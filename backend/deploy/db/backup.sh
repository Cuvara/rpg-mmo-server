#!/usr/bin/env bash
#
# backup.sh — pg_dump both PostgreSQL instances into timestamped custom-format
# archives, then prune old ones.
#
# Runs against the compose containers via `docker exec`, so it needs no psql
# client on the host. Works locally (WSL, where `docker` may only exist as
# `docker.exe`) and on the deploy runner.
#
# Usage:
#   db/backup.sh                        # back up both DBs, keep 7 per DB
#   db/backup.sh --db gamestate         # only the game-state DB
#   db/backup.sh --dir /tmp/b --keep 3  # custom destination + retention
#   db/backup.sh --skip-missing         # containers absent -> warn, exit 0
#   db/backup.sh --kube-context k3d-rpg-dev   # dump the IN-CLUSTER StatefulSets instead
#
# Environment overrides (flags win):
#   BACKUP_DIR              destination root          (default /var/backups/rpg-mmo)
#   BACKUP_KEEP             archives kept per DB      (default 7)
#   META_CONTAINER          meta container name       (default rpg-postgres)
#   GAME_CONTAINER          game container name       (default rpg-postgres-game)
#   POSTGRES_USER/DB        meta credentials          (default nakama/nakama)
#   POSTGRES_GAME_USER/DB   game credentials          (default game/gamestate)
#   BACKUP_KUBE_CONTEXT     kubectl context -> k8s mode  (default: unset = compose containers)
#   BACKUP_KUBE_NAMESPACE   namespace of the StatefulSets (default rpg-k8s-data)
#
# Output:
#   $BACKUP_DIR/<db>/<db>-<UTC timestamp>.dump    (pg_dump -Fc, restorable with restore.sh)
#
# Exit codes: 0 ok (or nothing to do with --skip-missing), 1 failure, 2 bad usage.
#
set -euo pipefail

BACKUP_DIR="${BACKUP_DIR:-/var/backups/rpg-mmo}"
BACKUP_KEEP="${BACKUP_KEEP:-7}"
META_CONTAINER="${META_CONTAINER:-rpg-postgres}"
GAME_CONTAINER="${GAME_CONTAINER:-rpg-postgres-game}"
WHICH_DB="all"
SKIP_MISSING=0
KUBE_CTX="${BACKUP_KUBE_CONTEXT:-}"
KUBE_NS="${BACKUP_KUBE_NAMESPACE:-rpg-k8s-data}"
DUMPED=0

# ---------------------------------------------------------------- arg parsing
while [ $# -gt 0 ]; do
	case "$1" in
	--dir)
		BACKUP_DIR="${2:?--dir needs a path}"
		shift 2
		;;
	--keep)
		BACKUP_KEEP="${2:?--keep needs a number}"
		shift 2
		;;
	--db)
		WHICH_DB="${2:?--db needs meta|gamestate|all}"
		shift 2
		;;
	--skip-missing)
		SKIP_MISSING=1
		shift
		;;
	--kube-context)
		KUBE_CTX="${2:?--kube-context needs a context name}"
		shift 2
		;;
	-h | --help)
		sed -n '2,28p' "${BASH_SOURCE[0]}"
		exit 0
		;;
	*)
		echo "ERROR: unknown flag: $1 (try --help)" >&2
		exit 2
		;;
	esac
done

case "$WHICH_DB" in
meta | gamestate | all) ;;
*)
	echo "ERROR: --db must be meta|gamestate|all (got '$WHICH_DB')" >&2
	exit 2
	;;
esac

if ! [[ "$BACKUP_KEEP" =~ ^[0-9]+$ ]] || [ "$BACKUP_KEEP" -lt 1 ]; then
	echo "ERROR: --keep must be a positive integer (got '$BACKUP_KEEP')" >&2
	exit 2
fi

# ---------------------------------------------------------------- log helpers
log() { echo "[backup] $*"; }
warn() { echo "[backup] WARNING: $*" >&2; }
die() {
	echo "[backup] ERROR: $*" >&2
	exit 1
}

# --------------------------------------------------------- toolchain: docker
# WSL: the Linux `docker` CLI may be absent while Docker Desktop exposes
# `docker.exe` on PATH. Try both.
# Retried. One `docker info` per candidate made a single Docker Desktop shim flake
# fatal: CD's "Back up databases" failed on develop with "docker not available (tried
# docker, docker.exe)" after nine consecutive successes on the same runner, and the
# PostgreSQL dump this gates is deliberately fatal. The daemon was up; one CLI call
# was not. A few attempts cost seconds and only on the failure path.
detect_docker() {
	local attempt
	for attempt in 1 2 3 4 5; do
		if command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1; then
			echo docker
			return 0
		fi
		if command -v docker.exe >/dev/null 2>&1 && docker.exe info >/dev/null 2>&1; then
			echo docker.exe
			return 0
		fi
		[ "$attempt" -lt 5 ] && sleep 3
	done
	return 1
}

# Two transports, one dump. In k8s mode the target is a StatefulSet reached through
# `kubectl exec`; otherwise a compose container through `docker exec`.
#
# k8s mode exists because the compose containers are no longer where dev and staging
# keep their data. Both run on k3d, and CD's k8s deploy STOPS the compose containers.
# So this script, pointed at compose, found nothing running and -- with
# --skip-missing, as CD calls it -- printed "skipping meta", "skipping gamestate",
# "done", and passed. The dump that exists to gate schema migrations backed up
# nothing on every dev deploy, green.
if [ -n "$KUBE_CTX" ]; then
	command -v kubectl >/dev/null 2>&1 || die "--kube-context given but kubectl is not on PATH"
	log "mode: k8s (context $KUBE_CTX, namespace $KUBE_NS)"
else
	DOCKER="$(detect_docker)" || die "docker not available (tried docker, docker.exe)"
fi

target_running() {
	if [ -n "$KUBE_CTX" ]; then
		[ "$(kubectl --context "$KUBE_CTX" -n "$KUBE_NS" get statefulset "$1" \
			-o jsonpath='{.status.readyReplicas}' 2>/dev/null)" = "1" ]
	else
		[ "$("$DOCKER" inspect -f '{{.State.Running}}' "$1" 2>/dev/null)" = "true" ]
	fi
}

# db_exec [-i] <target> <command...>
db_exec() {
	local interactive=""
	if [ "$1" = "-i" ]; then
		interactive="-i"
		shift
	fi
	local target="$1"
	shift
	if [ -n "$KUBE_CTX" ]; then
		kubectl --context "$KUBE_CTX" -n "$KUBE_NS" exec $interactive "statefulset/$target" -- "$@"
	else
		"$DOCKER" exec $interactive "$target" "$@"
	fi
}

# db_put <target> <local file> <path inside the target> -- stage a file, VERIFIED.
#
# k8s mode uses `kubectl cp`, never `kubectl exec -i ... cat >`. Measured on k3d-rpg-dev:
# streaming a 133030-byte archive through `kubectl exec -i` stdin arrived as 32768,
# 98304 and 131072 bytes on three tries -- always a multiple of 32 KiB, never complete --
# while `kubectl cp` was exact 5/5 and `docker exec -i` was exact 5/5. It is why the
# first restore drill of the meta database failed with "could not read from input file:
# end of file" and left 0 users in the scratch copy. A 16 KiB archive fits in one chunk,
# which is why the game-state drill passed and hid it.
#
# Either way the staged copy is compared to the local file by md5 before anything reads
# it: a transport that can drop bytes silently must not be trusted to have not.
db_put() {
	local target="$1" src="$2" dest="$3" want got
	if [ -n "$KUBE_CTX" ]; then
		kubectl --context "$KUBE_CTX" -n "$KUBE_NS" cp "$src" "${target}-0:${dest}" -c postgres >/dev/null ||
			return 1
	else
		db_exec -i "$target" sh -c "cat > '$dest'" <"$src" || return 1
	fi
	want="$(md5sum <"$src" | cut -c1-32)"
	got="$(db_exec "$target" md5sum "$dest" 2>/dev/null | cut -c1-32)"
	if [ "$want" != "$got" ]; then
		echo "staged copy of $(basename "$src") in $target differs from the local file (md5 $got, want $want)" >&2
		return 1
	fi
}

# ------------------------------------------------------------------- dump one
# dump_db <label> <container> <user> <dbname>
dump_db() {
	local label="$1" container="$2" user="$3" dbname="$4"

	if ! target_running "$container"; then
		if [ "$SKIP_MISSING" -eq 1 ]; then
			warn "'$container' not running -- skipping $label"
			return 0
		fi
		die "'$container' not running (use --skip-missing to tolerate)"
	fi

	local dest_dir="$BACKUP_DIR/$label"
	mkdir -p "$dest_dir"

	local stamp
	stamp="$(date -u +%Y%m%dT%H%M%SZ)"
	local out="$dest_dir/$label-$stamp.dump"

	log "dumping $label ($container: $user@$dbname) -> $out"

	# -Fc = custom format: compressed, and pg_restore can filter/parallelise it.
	# Write to a .partial first so an interrupted run never leaves a file that
	# looks like a usable backup.
	if ! db_exec "$container" pg_dump -U "$user" -d "$dbname" -Fc --no-password >"$out.partial" 2>"$out.err"; then
		warn "pg_dump failed for $label:"
		cat "$out.err" >&2 || true
		rm -f "$out.partial" "$out.err"
		die "backup of $label failed"
	fi
	rm -f "$out.err"

	# A dump that pg_restore cannot list is not a backup. Catch corruption now,
	# not during an incident.
	#
	# Retry with a sync in between: on WSL drvfs mounts (/mnt/*) a file read
	# immediately after the redirect closes can briefly appear truncated, which
	# made this check flake in CI while the dump itself was fine.
	#
	# The check reads the WHOLE archive (`pg_restore -f /dev/null` renders every data block
	# to a discarded script), not just its table of contents (`--list`, the old check),
	# which sits at the head of the file and passes on an archive truncated after it.
	local verify_ok=0 attempt remote="/tmp/rpg-backup-verify-$$.dump"
	for attempt in 1 2 3; do
		sync "$out.partial" 2>/dev/null || sync
		if db_put "$container" "$out.partial" "$remote" &&
			db_exec "$container" pg_restore -f /dev/null "$remote" >/dev/null 2>&1; then
			verify_ok=1
			break
		fi
		sleep "$attempt"
	done
	db_exec "$container" rm -f "$remote" >/dev/null 2>&1 || true
	if [ "$verify_ok" -ne 1 ]; then
		rm -f "$out.partial"
		die "verification failed: '$label' dump is not a readable pg_restore archive (3 attempts)"
	fi

	mv "$out.partial" "$out"

	local size
	size="$(du -h "$out" | cut -f1)"
	log "  ok: $(basename "$out") ($size)"
	DUMPED=$((DUMPED + 1))

	prune "$label" "$dest_dir"
}

# ------------------------------------------------------------------- retention
prune() {
	local label="$1" dir="$2"

	# Newest-first, drop everything past the keep count.
	local -a old
	mapfile -t old < <(ls -1t "$dir"/"$label"-*.dump 2>/dev/null | tail -n "+$((BACKUP_KEEP + 1))")

	if [ "${#old[@]}" -eq 0 ]; then
		log "  retention: $(ls -1 "$dir"/"$label"-*.dump 2>/dev/null | wc -l) kept (limit $BACKUP_KEEP)"
		return 0
	fi

	local f
	for f in "${old[@]}"; do
		log "  retention: removing $(basename "$f")"
		rm -f "$f"
	done
}

# ----------------------------------------------------------------------- main
mkdir -p "$BACKUP_DIR" 2>/dev/null ||
	die "cannot create '$BACKUP_DIR' (permission denied?) -- set BACKUP_DIR to a writable path or pre-create it"
[ -w "$BACKUP_DIR" ] || die "'$BACKUP_DIR' is not writable by $(id -un)"

log "destination: $BACKUP_DIR (keep $BACKUP_KEEP per database)"

if [ -n "$KUBE_CTX" ]; then
	META_CONTAINER="${META_STATEFULSET:-postgres-meta}"
	GAME_CONTAINER="${GAME_STATEFULSET:-postgres-game}"
fi

if [ "$WHICH_DB" = "meta" ] || [ "$WHICH_DB" = "all" ]; then
	dump_db "meta" "$META_CONTAINER" \
		"${POSTGRES_USER:-nakama}" "${POSTGRES_DB:-nakama}"
fi

if [ "$WHICH_DB" = "gamestate" ] || [ "$WHICH_DB" = "all" ]; then
	dump_db "gamestate" "$GAME_CONTAINER" \
		"${POSTGRES_GAME_USER:-game}" "${POSTGRES_GAME_DB:-gamestate}"
fi

# Say how much was actually backed up. "done" after skipping everything read exactly
# like "done" after backing everything up, and that is how an empty backup passed CD.
if [ "$DUMPED" -eq 0 ]; then
	warn "backed up NOTHING -- every target was skipped"
	echo "::warning title=backup.sh::no database was backed up (every target was skipped)"
fi
log "done: $DUMPED database(s) backed up"
