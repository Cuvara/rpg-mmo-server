#!/usr/bin/env bash
# Safety helpers for rehearsal.sh, kept in a sourced file so that
# tests/rehearsal_test.sh can prove them offline (fake `docker`, no daemon).
# Sourcing this file has no side effects.
#
# Globals the callers set: PREFIX (rpgr-r1-<run-id>), RUN_ID, NETWORK.

# Variables (by exact name or prefix) that backup.sh, restore.sh, redis-backup.sh
# and redis-restore.sh read from the environment, plus the connection selectors
# of the tools they call. Any of them inherited from an operator shell or the
# self-hosted runner could redirect a child at the normal rpg-* stack, a host
# PostgreSQL/Redis or a k3d cluster.
# Exact names only where a prefix would collide with the caller's own variables.
R1_HONOURED_EXACT=(DATABASE_URL TARGET FSGROUP KUBECONFIG DB_CONNECTION_MODE
	META_CONTAINER GAME_CONTAINER META_STATEFULSET GAME_STATEFULSET)
R1_HONOURED_PATTERN='^(PG[A-Z]*|POSTGRES_.*|REDIS_.*|BACKUP_.*)$'

r1_die() {
	if declare -F die >/dev/null 2>&1; then die "$@"; fi
	echo "[rehearsal] ERROR: $*" >&2
	exit 1
}

# Unset every variable the child scripts honour. rehearsal.sh then sets only the
# rpgr-* targets it needs, explicitly, through r1_run_child.
r1_sanitise_env() {
	local name
	for name in "${R1_HONOURED_EXACT[@]}"; do unset "$name" 2>/dev/null || true; done
	for name in $(compgen -v | awk -v re="$R1_HONOURED_PATTERN" '$0 ~ re'); do
		unset "$name" 2>/dev/null || true
	done
}

# Run a repository script under an allowlisted environment (env -i). The only
# variables a child sees are PATH, HOME, locale/TMPDIR, the docker client
# variables needed to reach the same daemon, and the entries of R1_CHILD_ENV
# (NAME=value, set by the caller). Nothing inherited can change its target.
r1_run_child() {
	local passthrough=() name
	for name in LANG LC_ALL TMPDIR DOCKER_HOST DOCKER_CONTEXT DOCKER_CONFIG; do
		if [ -n "${!name:-}" ]; then passthrough+=("$name=${!name}"); fi
	done
	env -i PATH="$PATH" HOME="${HOME:-/tmp}" ${passthrough[@]+"${passthrough[@]}"} ${R1_CHILD_ENV[@]+"${R1_CHILD_ENV[@]}"} bash "$@"
}

# --- target assertions ------------------------------------------------------

# A target must start with the run prefix and must not be a normal rpg-* name.
# (The run label/network checks below tie it to this run; a running rpg-*
# container can never match since every rpgr-* name differs in its fourth
# character.)
r1_require_rpgr_name() {
	local name="$1" kind="${2:-resource}"
	[[ "$PREFIX" =~ ^rpgr-r1-[0-9]{8}T[0-9]{6}Z-[0-9]+$ ]] || r1_die "malformed run prefix '$PREFIX'"
	case "$name" in
		"$PREFIX"-*) ;;
		*) r1_die "refusing $kind '$name': not a $PREFIX-* rehearsal resource" ;;
	esac
	case "$name" in
		rpg-*) r1_die "refusing $kind '$name': matches a normal rpg-* name" ;;
	esac
}

r1_require_rpgr_container() {
	local name="$1" label networks
	r1_require_rpgr_name "$name" container
	label="$(docker inspect -f '{{index .Config.Labels "org.rpg.rehearsal.run"}}' "$name" 2>/dev/null || true)"
	[ "$label" = "$RUN_ID" ] || r1_die "refusing container '$name': run label is '$label', expected '$RUN_ID'"
	networks="$(docker inspect -f '{{range $k, $v := .NetworkSettings.Networks}}{{$k}} {{end}}' "$name" 2>/dev/null || true)"
	[ "$networks" = "$NETWORK " ] || r1_die "refusing container '$name': networks '$networks', expected only '$NETWORK'"
}

r1_require_rpgr_volume() {
	local name="$1" label
	r1_require_rpgr_name "$name" volume
	label="$(docker volume inspect -f '{{index .Labels "org.rpg.rehearsal.run"}}' "$name" 2>/dev/null || true)"
	[ "$label" = "$RUN_ID" ] || r1_die "refusing volume '$name': run label is '$label', expected '$RUN_ID'"
}

r1_require_rpgr_network() {
	local name="$1" label
	r1_require_rpgr_name "$name" network
	label="$(docker network inspect -f '{{index .Labels "org.rpg.rehearsal.run"}}' "$name" 2>/dev/null || true)"
	[ "$label" = "$RUN_ID" ] || r1_die "refusing network '$name': run label is '$label', expected '$RUN_ID'"
}

# The restore database must not exist before restore.sh runs: restore.sh would
# otherwise print "already exists -- restoring into it" and carry on.
r1_require_db_absent() {
	local container="$1" user="$2" db="$3" found
	case "$db" in
		"${SAFE_ID}"_*) ;;
		*) r1_die "refusing restore database '$db': not derived from run id $SAFE_ID" ;;
	esac
	found="$(docker exec "$container" psql -At -U "$user" -d postgres \
		-c "SELECT 1 FROM pg_database WHERE datname = '$db'")" ||
		r1_die "could not query databases in $container"
	[ -z "$found" ] || r1_die "refusing restore: database '$db' already exists in $container"
}

# --- normal-stack snapshot --------------------------------------------------

# Everything is captured without normalisation: container id, image id, status,
# StartedAt/FinishedAt, RestartCount and health, so that a `docker restart`
# changes the snapshot. (`index .State "Health"`: a plain `.State.Health` is a
# template error on containers without a healthcheck.) Volumes and networks named rpg-* are listed too.
# Uptime text (docker ps) is saved separately and is informational only.
r1_snapshot_normal_state() {
	local out="$1" names
	names="$(docker ps -a --format '{{.Names}}' | awk '/^rpg-/' | sort)"
	{
		if [ -n "$names" ]; then
			# shellcheck disable=SC2086
			docker inspect --format 'container|{{.Id}}|{{.Name}}|{{.Image}}|{{.State.Status}}|{{.State.StartedAt}}|{{.State.FinishedAt}}|{{.RestartCount}}|{{with index .State "Health"}}{{.Status}}{{else}}none{{end}}' $names
		fi
		docker volume ls --format 'volume|{{.Name}}' | awk '/^volume\|rpg-/'
		docker network ls --no-trunc --format 'network|{{.ID}}|{{.Name}}' | awk -F '|' '$3 ~ /^rpg-/'
	} | sort > "$out"
}

# Exact comparison; returns 1 and prints the diff on any difference.
r1_compare_normal_state() {
	local before="$1" after="$2"
	if cmp -s "$before" "$after"; then return 0; fi
	echo '[rehearsal] ERROR: normal rpg-* Docker state changed (id, StartedAt, RestartCount, health, volume or network)' >&2
	diff -u "$before" "$after" >&2 || true
	return 1
}
