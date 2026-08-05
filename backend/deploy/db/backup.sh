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
#
# Environment overrides (flags win):
#   BACKUP_DIR              destination root          (default /var/backups/rpg-mmo)
#   BACKUP_KEEP             archives kept per DB      (default 7)
#   META_CONTAINER          meta container name       (default rpg-postgres)
#   GAME_CONTAINER          game container name       (default rpg-postgres-game)
#   POSTGRES_USER/DB        meta credentials          (default nakama/nakama)
#   POSTGRES_GAME_USER/DB   game credentials          (default game/gamestate)
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

DOCKER="$(detect_docker)" || die "docker not available (tried docker, docker.exe)"

container_running() {
	[ "$("$DOCKER" inspect -f '{{.State.Running}}' "$1" 2>/dev/null)" = "true" ]
}

# ------------------------------------------------------------------- dump one
# dump_db <label> <container> <user> <dbname>
dump_db() {
	local label="$1" container="$2" user="$3" dbname="$4"

	if ! container_running "$container"; then
		if [ "$SKIP_MISSING" -eq 1 ]; then
			warn "container '$container' not running -- skipping $label"
			return 0
		fi
		die "container '$container' not running (use --skip-missing to tolerate)"
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
	if ! "$DOCKER" exec "$container" pg_dump -U "$user" -d "$dbname" -Fc --no-password >"$out.partial" 2>"$out.err"; then
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
	local verify_ok=0 attempt
	for attempt in 1 2 3; do
		sync "$out.partial" 2>/dev/null || sync
		if "$DOCKER" exec -i "$container" pg_restore --list >/dev/null 2>&1 <"$out.partial"; then
			verify_ok=1
			break
		fi
		sleep "$attempt"
	done
	if [ "$verify_ok" -ne 1 ]; then
		rm -f "$out.partial"
		die "verification failed: '$label' dump is not a readable pg_restore archive (3 attempts)"
	fi

	mv "$out.partial" "$out"

	local size
	size="$(du -h "$out" | cut -f1)"
	log "  ok: $(basename "$out") ($size)"

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

if [ "$WHICH_DB" = "meta" ] || [ "$WHICH_DB" = "all" ]; then
	dump_db "meta" "$META_CONTAINER" \
		"${POSTGRES_USER:-nakama}" "${POSTGRES_DB:-nakama}"
fi

if [ "$WHICH_DB" = "gamestate" ] || [ "$WHICH_DB" = "all" ]; then
	dump_db "gamestate" "$GAME_CONTAINER" \
		"${POSTGRES_GAME_USER:-game}" "${POSTGRES_GAME_DB:-gamestate}"
fi

log "done"
