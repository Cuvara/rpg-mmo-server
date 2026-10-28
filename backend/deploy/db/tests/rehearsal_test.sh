#!/usr/bin/env bash
# Offline tests for rehearsal.sh safety: no docker daemon, no network, no volumes.
# A stateful fake `docker` (tests/fake-docker.sh) is put first on PATH.
#
# Run:  bash backend/deploy/db/tests/rehearsal_test.sh
set -uo pipefail
HERE="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
DB_DIR="$(cd -- "$HERE/.." && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
PASS=0
FAIL=0

ok() { PASS=$((PASS + 1)); echo "ok   - $1"; }
bad() { FAIL=$((FAIL + 1)); echo "FAIL - $1"; }
check() { # description command...
	local desc="$1"; shift
	if "$@"; then ok "$desc"; else bad "$desc"; fi
}
refuses() { # description command... : must exit non-zero
	local desc="$1"; shift
	if ( "$@" ) >/dev/null 2>&1; then bad "$desc (was accepted)"; else ok "$desc"; fi
}

new_fake() { # fresh fake docker state; echoes its dir
	local d="$WORK/fake-$1"
	rm -rf "$d"; mkdir -p "$d/bin" "$d/objects"
	cp "$HERE/fake-docker.sh" "$d/bin/docker"
	chmod +x "$d/bin/docker"
	printf 'rpg-postgres\nrpg-redis\n' > "$d/normal-names.txt"
	printf 'rpg-pgdata\n' > "$d/normal-volumes.txt"
	printf 'abc123 rpg-net\n' > "$d/normal-networks.txt"
	cat > "$d/inspect.txt" <<'EOF'
container|sha256:aaa|/rpg-postgres|sha256:img1|running|2026-10-28T10:00:00.000000000Z|0001-01-01T00:00:00Z|0|healthy
container|sha256:bbb|/rpg-redis|sha256:img2|running|2026-10-28T10:00:01.000000000Z|0001-01-01T00:00:00Z|0|none
EOF
	echo "$d"
}

RUN_ID=20261029T010203Z-4242
SAFE_ID=20261029T010203Z_4242
PREFIX="rpgr-r1-$RUN_ID"
NETWORK="$PREFIX-net"

HOSTILE_NAMES=(BACKUP_KUBE_CONTEXT BACKUP_KUBE_NAMESPACE DB_CONNECTION_MODE REDIS_CONNECTION_MODE
	PGHOST PGPORT PGUSER PGPASSWORD PGDATABASE DATABASE_URL REDIS_URL REDIS_HOST REDIS_PORT
	REDIS_PASSWORD REDIS_IMAGE REDIS_BACKUP_DIR REDIS_RESTORE_NETWORK REDIS_RESTORE_SCRATCH_PREFIX
	REDIS_STATEFULSET POSTGRES_USER POSTGRES_PASSWORD POSTGRES_DB POSTGRES_GAME_USER POSTGRES_GAME_DB
	META_CONTAINER GAME_CONTAINER META_STATEFULSET GAME_STATEFULSET BACKUP_DIR BACKUP_KEEP TARGET
	FSGROUP KUBECONFIG)
set_hostile_env() {
	local name
	for name in "${HOSTILE_NAMES[@]}"; do export "$name=HOSTILE-rpg-$name"; done
	export DB_CONNECTION_MODE=host REDIS_CONNECTION_MODE=host BACKUP_KUBE_CONTEXT=k3d-rpg-dev
	export PGHOST=rpg-postgres PGPORT=5432 REDIS_HOST=rpg-redis REDIS_PORT=6379
	export META_CONTAINER=rpg-postgres GAME_CONTAINER=rpg-postgres-game REDIS_CONTAINER=rpg-redis
	export DATABASE_URL=postgres://nakama@rpg-postgres:5432/nakama REDIS_URL=redis://rpg-redis:6379
}

# --- 1. sanitised environment ------------------------------------------------
t_sanitise() (
	set_hostile_env
	export REDIS_CONTAINER=rpg-redis
	# shellcheck source=../rehearsal-lib.sh
	source "$DB_DIR/rehearsal-lib.sh"
	r1_sanitise_env
	local name left=""
	for name in "${HOSTILE_NAMES[@]}" REDIS_CONTAINER; do
		if [ -n "${!name+x}" ]; then left="$left $name"; fi
	done
	[ -z "$left" ] || { echo "still set:$left" >&2; exit 1; }
)
check 'r1_sanitise_env unsets every variable the backup/restore scripts honour' t_sanitise

# --- 2. children see only the explicit rpgr-* targets ------------------------
t_child_env() (
	set_hostile_env
	source "$DB_DIR/rehearsal-lib.sh"
	cat > "$WORK/dump-env.sh" <<'EOF'
env | sort
EOF
	R1_CHILD_ENV=("META_CONTAINER=$PREFIX-meta-src" "REDIS_CONTAINER=$PREFIX-redis-src" BACKUP_KEEP=1)
	# even WITHOUT r1_sanitise_env the allowlist alone must hide the hostile values
	r1_run_child "$WORK/dump-env.sh" > "$WORK/child-env.txt"
	! grep -q 'HOSTILE' "$WORK/child-env.txt" || exit 1
	! grep -Eq '^(PG|DB_|REDIS_(HOST|PORT|URL|CONNECTION)|BACKUP_KUBE|DATABASE_URL)' "$WORK/child-env.txt" || exit 1
	grep -qx "META_CONTAINER=$PREFIX-meta-src" "$WORK/child-env.txt" || exit 1
	grep -qx "REDIS_CONTAINER=$PREFIX-redis-src" "$WORK/child-env.txt" || exit 1
	grep -qx 'BACKUP_KEEP=1' "$WORK/child-env.txt" || exit 1
	! grep -q 'rpg-postgres\|rpg-redis' "$WORK/child-env.txt" || exit 1
)
check 'children run under env -i: hostile inherited values never reach them' t_child_env

# --- 3. full script with hostile env, fake docker, stub children -------------
# Copy rehearsal.sh + lib next to stub backup/restore scripts that only record
# their environment and fail. The run must reach the backup step with only
# rpgr-* targets, and must never mutate a normal rpg-* object.
t_full_script_hostile() (
	local fake; fake="$(new_fake full)"
	local sandbox="$WORK/sandbox-full"
	rm -rf "$sandbox"; mkdir -p "$sandbox"
	cp "$DB_DIR/rehearsal.sh" "$DB_DIR/rehearsal-lib.sh" "$sandbox/"
	cp -r "$DB_DIR/migrations" "$sandbox/migrations"
	local s
	for s in backup.sh restore.sh redis-backup.sh redis-restore.sh; do
		cat > "$sandbox/$s" <<EOF
#!/usr/bin/env bash
{ echo "SCRIPT=$s"; echo "ARGS=\$*"; env | sort; } > "$fake/child-$s.env"
exit 42
EOF
	done
	set_hostile_env
	export FAKE_DOCKER_DIR="$fake" PATH="$fake/bin:$PATH"
	if bash "$sandbox/rehearsal.sh" "$WORK/artifacts-full" > "$WORK/full.out" 2>&1; then
		echo 'rehearsal unexpectedly succeeded with a failing backup stub' >&2; exit 1
	fi
	local envf="$fake/child-backup.sh.env"
	[ -f "$envf" ] || { echo 'backup.sh stub never ran' >&2; cat "$WORK/full.out" >&2; exit 1; }
	! grep -q 'HOSTILE' "$envf" || exit 1
	! grep -Eq '^(PGHOST|PGPORT|DB_CONNECTION_MODE|REDIS_CONNECTION_MODE|BACKUP_KUBE_CONTEXT|REDIS_HOST|DATABASE_URL)=' "$envf" || exit 1
	grep -Eq '^META_CONTAINER=rpgr-r1-.*-meta-src$' "$envf" || exit 1
	grep -Eq '^GAME_CONTAINER=rpgr-r1-.*-game-src$' "$envf" || exit 1
	grep -Eq '^BACKUP_DIR=.*artifacts-full' "$envf" || exit 1
	! grep -Eq '=rpg-(postgres|redis)' "$envf" || exit 1
	# no mutating docker call may mention a normal rpg-* object
	! grep -E '^(rm|stop|restart|kill|exec|run|volume (rm|create)|network (rm|create)) .*(^|[ =/])rpg-' "$fake/calls.log" || exit 1
	grep -q 'target assertion passed' "$WORK/full.out" || exit 1
)
check 'rehearsal.sh with hostile inherited env reaches children with rpgr-* targets only' t_full_script_hostile

# --- 4. target assertions -----------------------------------------------------
t_assertions() (
	local fake; fake="$(new_fake assert)"
	export FAKE_DOCKER_DIR="$fake" PATH="$fake/bin:$PATH"
	source "$DB_DIR/rehearsal-lib.sh"
	local good="$PREFIX-meta-src"
	printf '%s\n%s \n' "$RUN_ID" "$NETWORK" > "$fake/objects/c_$good"
	printf '%s\n\n' "$RUN_ID" > "$fake/objects/v_$good-data"
	printf '%s\n\n' "$RUN_ID" > "$fake/objects/n_$NETWORK"

	r1_require_rpgr_container "$good" && r1_require_rpgr_volume "$good-data" && r1_require_rpgr_network "$NETWORK" || exit 1

	# a normal rpg-* name
	( r1_require_rpgr_name rpg-postgres container ) >/dev/null 2>&1 && exit 11
	( r1_require_rpgr_container rpg-postgres ) >/dev/null 2>&1 && exit 12
	# right shape, wrong run
	( r1_require_rpgr_name rpgr-r1-20250101T000000Z-1-meta-src container ) >/dev/null 2>&1 && exit 13
	# prefix right, but object missing its run label
	printf 'other-run\n%s \n' "$NETWORK" > "$fake/objects/c_$PREFIX-unlabelled"
	( r1_require_rpgr_container "$PREFIX-unlabelled" ) >/dev/null 2>&1 && exit 14
	# attached to a normal network as well
	printf '%s\n%s rpg-net \n' "$RUN_ID" "$NETWORK" > "$fake/objects/c_$PREFIX-bridged"
	( r1_require_rpgr_container "$PREFIX-bridged" ) >/dev/null 2>&1 && exit 15
	# volume / network without the run label
	printf 'x\n\n' > "$fake/objects/v_$PREFIX-foreign-vol"
	( r1_require_rpgr_volume "$PREFIX-foreign-vol" ) >/dev/null 2>&1 && exit 16
	printf 'x\n\n' > "$fake/objects/n_$PREFIX-foreign-net"
	( r1_require_rpgr_network "$PREFIX-foreign-net" ) >/dev/null 2>&1 && exit 17
	# a malformed prefix is itself refused
	( PREFIX=rpgr-r1 r1_require_rpgr_name rpgr-r1-meta container ) >/dev/null 2>&1 && exit 18
	# restore db: absent passes, present is refused, name outside the run is refused
	SAFE_ID="$SAFE_ID" r1_require_db_absent "$good" nakama "${SAFE_ID}_meta_restore" || exit 19
	( SAFE_ID="$SAFE_ID" r1_require_db_absent "$good" nakama nakama ) >/dev/null 2>&1 && exit 20
	exit 0
)
check 'target assertions accept run objects and refuse everything else' t_assertions

t_db_present() (
	local fake; fake="$(new_fake dbpresent)"
	cp "$fake/bin/docker" "$fake/bin/docker.orig"
	# fake psql answers "1" for the pg_database probe: the database already exists
	sed -i 's/\*pg_database\*) : ;;/*pg_database*) echo 1 ;;/' "$fake/bin/docker"
	export FAKE_DOCKER_DIR="$fake" PATH="$fake/bin:$PATH"
	source "$DB_DIR/rehearsal-lib.sh"
	SAFE_ID="$SAFE_ID"
	r1_require_db_absent "$PREFIX-meta-restore" nakama "${SAFE_ID}_meta_restore"
)
refuses 'restore is refused when the target database already exists' t_db_present

# --- 5. normal-stack comparison detects a restart -----------------------------
snapshot() { # fakedir out
	( export FAKE_DOCKER_DIR="$1" PATH="$1/bin:$PATH"; source "$DB_DIR/rehearsal-lib.sh"; r1_snapshot_normal_state "$2" )
}
compare() { ( source "$DB_DIR/rehearsal-lib.sh"; r1_compare_normal_state "$1" "$2" ); }

F="$(new_fake cmp)"
snapshot "$F" "$WORK/before.txt"
snapshot "$F" "$WORK/same.txt"
check 'snapshot contains containers, volumes and networks' \
	bash -c "grep -q '^container|sha256:aaa' '$WORK/before.txt' && grep -qx 'volume|rpg-pgdata' '$WORK/before.txt' && grep -q '^network|abc123|rpg-net' '$WORK/before.txt'"
check 'unchanged normal stack compares equal' compare "$WORK/before.txt" "$WORK/same.txt"

# docker restart: same id, same image, same state, same normalised uptime text; StartedAt moves
sed -i 's/2026-10-28T10:00:01.000000000Z/2026-10-29T01:02:03.000000000Z/' "$F/inspect.txt"
snapshot "$F" "$WORK/restarted.txt"
refuses 'a restart (State.StartedAt changed) is detected' compare "$WORK/before.txt" "$WORK/restarted.txt"

F2="$(new_fake cmp2)"
sed -i 's/|0|healthy/|1|healthy/' "$F2/inspect.txt"
snapshot "$F2" "$WORK/restartcount.txt"
refuses 'RestartCount change is detected' compare "$WORK/before.txt" "$WORK/restartcount.txt"

F3="$(new_fake cmp3)"
sed -i 's/|healthy/|unhealthy/' "$F3/inspect.txt"
snapshot "$F3" "$WORK/health.txt"
refuses 'health change is detected' compare "$WORK/before.txt" "$WORK/health.txt"

F4="$(new_fake cmp4)"
echo 'rpg-extra' >> "$F4/normal-volumes.txt"
snapshot "$F4" "$WORK/vol.txt"
refuses 'a new rpg-* volume is detected' compare "$WORK/before.txt" "$WORK/vol.txt"

F5="$(new_fake cmp5)"
echo 'def456 rpg-other-net' >> "$F5/normal-networks.txt"
snapshot "$F5" "$WORK/net.txt"
refuses 'a new rpg-* network is detected' compare "$WORK/before.txt" "$WORK/net.txt"

F6="$(new_fake cmp6)"
mkdir -p "$F6/objects"; printf 'run\n\n' > "$F6/objects/c_rpgr-r1-x-meta-src"
snapshot "$F6" "$WORK/scratch.txt"
check 'rpgr-* scratch objects are not part of the normal-stack comparison' compare "$WORK/before.txt" "$WORK/scratch.txt"

# --- 6. cleanup deletes only manifest-owned AND labelled objects -------------
t_cleanup_ownership() (
	local fake; fake="$(new_fake cleanup)"
	export FAKE_DOCKER_DIR="$fake" PATH="$fake/bin:$PATH"
	source "$DB_DIR/rehearsal-lib.sh"
	MANIFEST="$WORK/manifest-cleanup.tsv"
	local o="$fake/objects" clog="$WORK/cleanup-cleanup.log"
	: > "$MANIFEST"
	# owned: recorded in the manifest and labelled with this run
	printf '%s\n%s \n' "$RUN_ID" "$NETWORK" > "$o/c_$PREFIX-meta-src"; r1_record_owned container "$PREFIX-meta-src"
	printf '%s\n%s \n' "$RUN_ID" "$NETWORK" > "$o/c_$PREFIX-redis-restore-scratch-1"; r1_record_owned container "$PREFIX-redis-restore-scratch-1"
	printf '%s\n\n' "$RUN_ID" > "$o/v_$PREFIX-meta-src-data"; r1_record_owned volume "$PREFIX-meta-src-data"
	printf '%s\n\n' "$RUN_ID" > "$o/v_$PREFIX-redis-restore-vol-1"; r1_record_owned volume "$PREFIX-redis-restore-vol-1"
	printf '%s\n\n' "$RUN_ID" > "$o/n_$NETWORK"; r1_record_owned network "$NETWORK"
	# unowned: matching prefix, no label, not in the manifest
	printf 'none\n%s \n' "$NETWORK" > "$o/c_$PREFIX-redis-restore-scratch-unowned"
	printf 'none\n\n' > "$o/v_$PREFIX-redis-restore-vol-unowned"
	printf 'none\n\n' > "$o/n_$PREFIX-other-net"
	# labelled with this run but never recorded
	printf '%s\n%s \n' "$RUN_ID" "$NETWORK" > "$o/c_$PREFIX-redis-restore-scratch-unrecorded"
	printf '%s\n\n' "$RUN_ID" > "$o/v_$PREFIX-redis-restore-vol-unrecorded"
	# recorded but carrying somebody else's label (name collision)
	printf 'other-run\n%s \n' "$NETWORK" > "$o/c_$PREFIX-game-src"; r1_record_owned container "$PREFIX-game-src"
	printf 'other-run\n\n' > "$o/v_$PREFIX-game-src-data"; r1_record_owned volume "$PREFIX-game-src-data"
	# recorded, but outside the run prefix: a normal stack name must never go
	printf '%s\n\n' "$RUN_ID" > "$o/c_rpg-redis"; r1_record_owned container rpg-redis
	r1_cleanup_owned "$MANIFEST" "$clog" >/dev/null 2>&1 || { echo 'cleanup reported failure' >&2; exit 1; }
	local gone survive
	for gone in "c_$PREFIX-meta-src" "c_$PREFIX-redis-restore-scratch-1" "v_$PREFIX-meta-src-data" "v_$PREFIX-redis-restore-vol-1" "n_$NETWORK"; do
		[ ! -e "$o/$gone" ] || { echo "owned object survived: $gone" >&2; exit 2; }
	done
	for survive in "c_$PREFIX-redis-restore-scratch-unowned" "v_$PREFIX-redis-restore-vol-unowned" "n_$PREFIX-other-net" \
		"c_$PREFIX-redis-restore-scratch-unrecorded" "v_$PREFIX-redis-restore-vol-unrecorded" \
		"c_$PREFIX-game-src" "v_$PREFIX-game-src-data" c_rpg-redis; do
		[ -e "$o/$survive" ] || { echo "foreign object was removed: $survive" >&2; exit 3; }
	done
	grep -q "scratch-unowned' matches the run prefix but is not in the ownership manifest" "$clog" || { echo 'unowned object not reported' >&2; exit 4; }
	grep -q "scratch-unrecorded' matches the run prefix but is not in the ownership manifest" "$clog" || { echo 'unrecorded object not reported' >&2; exit 5; }
	grep -q "carries run label 'other-run'" "$clog" || { echo 'wrong-label object not reported' >&2; exit 6; }
	grep -q "container 'rpg-redis' is outside run prefix" "$clog" || { echo 'outside-prefix entry not reported' >&2; exit 7; }
	# nothing outside the owned set was ever passed to a removal call
	! grep -E '^(rm|volume rm|network rm) .*(unowned|unrecorded|other-net|game-src|rpg-redis)' "$fake/calls.log" || exit 8
)
check 'cleanup removes manifest+label objects and leaves unowned, unrecorded, mislabelled and rpg-* ones' t_cleanup_ownership

# Look-alike resources that appear under the run prefix mid-run must survive the
# real EXIT trap of the full script (a failing backup stub forces the cleanup).
t_full_script_unowned_survives() (
	local fake; fake="$(new_fake fullunowned)"
	local sandbox="$WORK/sandbox-fullunowned"
	rm -rf "$sandbox"; mkdir -p "$sandbox"
	cp "$DB_DIR/rehearsal.sh" "$DB_DIR/rehearsal-lib.sh" "$sandbox/"
	cp -r "$DB_DIR/migrations" "$sandbox/migrations"
	local s
	for s in backup.sh restore.sh redis-backup.sh redis-restore.sh; do
		# backup.sh plants look-alikes under the run prefix, then fails
		cat > "$sandbox/$s" <<EOF
#!/usr/bin/env bash
if [ "$s" = backup.sh ]; then
	p="\$(printenv META_CONTAINER | sed 's/-meta-src\$//')"
	printf 'none\n\n' > "$fake/objects/c_\$p-redis-restore-scratch-unowned"
	printf 'none\n\n' > "$fake/objects/v_\$p-redis-restore-vol-unowned"
	printf '%s\n\n' "\${p#rpgr-r1-}" > "$fake/objects/v_\$p-labelled-unrecorded"
fi
exit 42
EOF
	done
	export FAKE_DOCKER_DIR="$fake" PATH="$fake/bin:$PATH"
	if bash "$sandbox/rehearsal.sh" "$WORK/artifacts-fullunowned" > "$WORK/fullunowned.out" 2>&1; then exit 1; fi
	# the planted -labelled-unrecorded volume carries THIS run's label but is not in the manifest
	local o="$fake/objects" f
	ls "$o" | grep -q -- '-redis-restore-scratch-unowned$' || { echo 'unowned container removed' >&2; cat "$WORK/fullunowned.out" >&2; exit 2; }
	ls "$o" | grep -q -- '-redis-restore-vol-unowned$' || { echo 'unowned volume removed' >&2; exit 3; }
	ls "$o" | grep -q -- '-labelled-unrecorded$' || { echo 'planted volume removed' >&2; exit 4; }
	# every object the run created itself is gone
	for f in "$o"/*; do
		case "$(basename "$f")" in
			*-redis-restore-scratch-unowned|*-redis-restore-vol-unowned|*-labelled-unrecorded) ;;
			*) echo "owned object left behind: $(basename "$f")" >&2; exit 5 ;;
		esac
	done
	grep -q 'left untouched:.*unowned' "$WORK/fullunowned.out" || { echo 'survivor not reported' >&2; exit 6; }
	ls "$WORK"/artifacts-fullunowned/*/ownership-manifest.tsv >/dev/null || exit 7
)
check 'full script: look-alike resources created mid-run survive the EXIT cleanup' t_full_script_unowned_survives

# --- 7. redis-restore.sh labels and records exactly what it creates ----------
t_redis_restore_manifest() (
	local fake; fake="$(new_fake redisrestore)"
	export FAKE_DOCKER_DIR="$fake" PATH="$fake/bin:$PATH"
	local rdb="$WORK/test.rdb"; printf 'REDIS0011' > "$rdb"
	local m="$WORK/manifest-redis.tsv"; : > "$m"
	printf '%s\n\n' "$RUN_ID" > "$fake/objects/n_$NETWORK"
	env -i PATH="$PATH" HOME="$HOME" FAKE_DOCKER_DIR="$fake" REDIS_CONTAINER="$PREFIX-redis-src" REDIS_IMAGE=redis:test \
		REDIS_RESTORE_SCRATCH_PREFIX="$PREFIX-redis-restore" REDIS_RESTORE_NETWORK="$NETWORK" \
		REDIS_RESTORE_RUN_LABEL="org.rpg.rehearsal.run=$RUN_ID" REDIS_RESTORE_MANIFEST="$m" \
		bash "$DB_DIR/redis-restore.sh" --file "$rdb" --mode scratch --keep-scratch >/dev/null 2>&1 || true
	grep -qE "^volume	$PREFIX-redis-restore-vol-" "$m" || { echo 'scratch volume not recorded' >&2; cat "$m" >&2; exit 1; }
	grep -qE "^container	$PREFIX-redis-restore-scratch-[0-9]+$" "$m" || { echo 'scratch container not recorded' >&2; exit 2; }
	# the transient inject helper is recorded too (and must not be taken for the scratch target)
	grep -qE "^container	$PREFIX-redis-restore-scratch-[0-9]+-inject$" "$m" || { echo 'inject helper not recorded' >&2; exit 6; }
	# every create call carries the run label
	local create
	create="$(grep -E '^(volume create|run) ' "$fake/calls.log")"
	[ -n "$create" ] || { echo 'no create calls seen' >&2; exit 3; }
	! printf '%s\n' "$create" | grep -v -- "--label org.rpg.rehearsal.run=$RUN_ID" || { echo 'a create call lacks the run label' >&2; exit 4; }
	local f
	for f in "$fake"/objects/c_"$PREFIX"-redis-restore-scratch-* "$fake"/objects/v_"$PREFIX"-redis-restore-vol-*; do
		[ "$(sed -n 1p "$f")" = "$RUN_ID" ] || { echo "unlabelled: $f" >&2; exit 5; }
	done
)
check 'redis-restore.sh passes the run label to every scratch object and records exact names' t_redis_restore_manifest

echo
echo "passed: $PASS failed: $FAIL"
[ "$FAIL" -eq 0 ]
