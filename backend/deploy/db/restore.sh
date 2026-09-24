#!/usr/bin/env bash
#
# restore.sh — restore a backup.sh archive into a PostgreSQL database.
#
# DESTRUCTIVE by default: restoring over a live database drops and recreates
# every object the dump contains. The script refuses to run without --yes.
#
# Usage:
#   # rehearse into a scratch database (safe, recommended before any real restore)
#   db/restore.sh --file /var/backups/rpg-mmo/gamestate/gamestate-20260805T041500Z.dump \
#                 --db gamestate --target gamestate_scratch --yes
#
#   # real disaster recovery, over the live database
#   db/restore.sh --file <dump> --db gamestate --yes
#
# Flags:
#   --file FILE     archive produced by backup.sh (pg_dump -Fc)         [required]
#   --db NAME       which instance: meta | gamestate                    [required]
#   --target DB     database name to restore into (default: that instance's live DB)
#   --create        create --target if it does not exist (implied for non-live targets)
#   --jobs N        parallel restore workers (default 2)
#   --yes           required acknowledgement; without it nothing runs
#
# Environment overrides:
#   META_CONTAINER / GAME_CONTAINER, POSTGRES_USER/DB, POSTGRES_GAME_USER/DB
#   BACKUP_KUBE_CONTEXT / BACKUP_KUBE_NAMESPACE   (same as --kube-context; see below)
#
#   --kube-context CTX   restore into the IN-CLUSTER StatefulSet (postgres-meta /
#                        postgres-game in rpg-k8s-data) through `kubectl exec`, instead of
#                        a compose container. Dev and staging keep their data on k3d; a
#                        restore that can only reach compose cannot recover them.
#
# Exit codes: 0 ok, 1 failure, 2 bad usage.
#
set -euo pipefail

META_CONTAINER="${META_CONTAINER:-rpg-postgres}"
GAME_CONTAINER="${GAME_CONTAINER:-rpg-postgres-game}"

FILE=""
WHICH_DB=""
TARGET=""
CONFIRMED=0
CREATE=0
JOBS=2
KUBE_CTX="${BACKUP_KUBE_CONTEXT:-}"
KUBE_NS="${BACKUP_KUBE_NAMESPACE:-rpg-k8s-data}"

# ---------------------------------------------------------------- arg parsing
while [ $# -gt 0 ]; do
	case "$1" in
	--file)
		FILE="${2:?--file needs a path}"
		shift 2
		;;
	--db)
		WHICH_DB="${2:?--db needs meta|gamestate}"
		shift 2
		;;
	--target)
		TARGET="${2:?--target needs a database name}"
		shift 2
		;;
	--jobs)
		JOBS="${2:?--jobs needs a number}"
		shift 2
		;;
	--kube-context)
		KUBE_CTX="${2:?--kube-context needs a context name}"
		shift 2
		;;
	--create)
		CREATE=1
		shift
		;;
	--yes)
		CONFIRMED=1
		shift
		;;
	-h | --help)
		sed -n '2,30p' "${BASH_SOURCE[0]}"
		exit 0
		;;
	*)
		echo "ERROR: unknown flag: $1 (try --help)" >&2
		exit 2
		;;
	esac
done

log() { echo "[restore] $*"; }
die() {
	echo "[restore] ERROR: $*" >&2
	exit 1
}
usage_error() {
	echo "[restore] ERROR: $*" >&2
	echo "Try --help." >&2
	exit 2
}

[ -n "$FILE" ] || usage_error "--file is required"
[ -n "$WHICH_DB" ] || usage_error "--db is required (meta|gamestate)"
[ -f "$FILE" ] || die "no such file: $FILE"
[[ "$JOBS" =~ ^[0-9]+$ ]] && [ "$JOBS" -ge 1 ] || usage_error "--jobs must be a positive integer"

case "$WHICH_DB" in
meta)
	CONTAINER="$META_CONTAINER"
	PGUSER_NAME="${POSTGRES_USER:-nakama}"
	LIVE_DB="${POSTGRES_DB:-nakama}"
	;;
gamestate)
	CONTAINER="$GAME_CONTAINER"
	PGUSER_NAME="${POSTGRES_GAME_USER:-game}"
	LIVE_DB="${POSTGRES_GAME_DB:-gamestate}"
	;;
*)
	usage_error "--db must be meta|gamestate (got '$WHICH_DB')"
	;;
esac

if [ -n "$KUBE_CTX" ]; then
	case "$WHICH_DB" in
	meta) CONTAINER="${META_STATEFULSET:-postgres-meta}" ;;
	gamestate) CONTAINER="${GAME_STATEFULSET:-postgres-game}" ;;
	esac
fi

TARGET="${TARGET:-$LIVE_DB}"
[ "$TARGET" = "$LIVE_DB" ] || CREATE=1

# --------------------------------------------------------- toolchain: docker
detect_docker() {
	if command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1; then
		echo docker
		return 0
	fi
	if command -v docker.exe >/dev/null 2>&1 && docker.exe info >/dev/null 2>&1; then
		echo docker.exe
		return 0
	fi
	return 1
}

# db_exec [-i] <target> <command...> -- kubectl exec into a StatefulSet in k8s mode,
# docker exec into a compose container otherwise. See backup.sh for why both exist.
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

if [ -n "$KUBE_CTX" ]; then
	command -v kubectl >/dev/null 2>&1 || die "--kube-context given but kubectl is not on PATH"
	[ "$(kubectl --context "$KUBE_CTX" -n "$KUBE_NS" get statefulset "$CONTAINER" \
		-o jsonpath='{.status.readyReplicas}' 2>/dev/null)" = "1" ] ||
		die "statefulset '$CONTAINER' in $KUBE_NS ($KUBE_CTX) is not ready"
	log "mode: k8s (context $KUBE_CTX, namespace $KUBE_NS)"
else
	DOCKER="$(detect_docker)" || die "docker not available (tried docker, docker.exe)"
	[ "$("$DOCKER" inspect -f '{{.State.Running}}' "$CONTAINER" 2>/dev/null)" = "true" ] ||
		die "container '$CONTAINER' is not running"
fi

# ------------------------------------------------------------ safety gate
if [ "$TARGET" = "$LIVE_DB" ]; then
	log "TARGET IS THE LIVE '$WHICH_DB' DATABASE ('$TARGET' in $CONTAINER)."
	log "Existing objects in it will be DROPPED and replaced by the archive."
else
	log "target is scratch database '$TARGET' in $CONTAINER (live '$LIVE_DB' untouched)"
fi

if [ "$CONFIRMED" -ne 1 ]; then
	die "refusing to restore without --yes"
fi

# --------------------------------------------------------------- verify input
log "archive: $FILE ($(du -h "$FILE" | cut -f1))"

# pg_restore refuses to parallelise when the archive arrives on stdin, so stage
# the file inside the container and restore from there.
REMOTE_DUMP="/tmp/rpg-restore-$$.dump"
cleanup() { db_exec "$CONTAINER" rm -f "$REMOTE_DUMP" >/dev/null 2>&1 || true; }
trap cleanup EXIT

# Staged by db_put: streamed through `docker exec` in compose mode (Docker Desktop on
# WSL cannot see /tmp paths the Linux side hands `docker cp`), `kubectl cp` in k8s mode,
# and md5-verified either way. The archive is checked AFTER staging, against the staged
# copy -- checking the local file and then trusting the transport is what let a
# truncated upload through.
log "staging archive into $CONTAINER:$REMOTE_DUMP"
db_put "$CONTAINER" "$FILE" "$REMOTE_DUMP" ||
	die "could not stage the archive inside '$CONTAINER' intact"

# The whole archive, not its head: `--list` reads only the table of contents.
db_exec "$CONTAINER" pg_restore -f /dev/null "$REMOTE_DUMP" >/dev/null 2>&1 ||
	die "not a complete, readable pg_restore archive: $FILE"

# ------------------------------------------------------------ create target
psql_admin() {
	# Connect to the maintenance DB so the target can be created/dropped.
	db_exec -i "$CONTAINER" psql -U "$PGUSER_NAME" -d postgres -tAc "$1"
}

if [ "$CREATE" -eq 1 ]; then
	exists="$(psql_admin "SELECT 1 FROM pg_database WHERE datname = '$TARGET'")"
	if [ "$exists" != "1" ]; then
		log "creating database '$TARGET'"
		psql_admin "CREATE DATABASE \"$TARGET\"" >/dev/null
	else
		log "database '$TARGET' already exists -- restoring into it"
	fi
fi

# ------------------------------------------------------------------- restore
log "restoring into '$TARGET' (jobs=$JOBS)"

# --clean --if-exists so a repeat restore is deterministic rather than colliding
# with objects left by the previous attempt. --exit-on-error to fail loudly.
if ! db_exec "$CONTAINER" pg_restore \
	-U "$PGUSER_NAME" -d "$TARGET" \
	--clean --if-exists --no-owner --no-privileges \
	--jobs "$JOBS" --exit-on-error "$REMOTE_DUMP"; then
	die "pg_restore failed -- '$TARGET' may be partially restored"
fi

# ---------------------------------------------------------------- verify out
log "restore complete; table row counts in '$TARGET':"
db_exec -i "$CONTAINER" psql -U "$PGUSER_NAME" -d "$TARGET" -c "
    SELECT relname AS table, n_live_tup AS approx_rows
    FROM pg_stat_user_tables
    ORDER BY n_live_tup DESC, relname
    LIMIT 20;"

log "done"
