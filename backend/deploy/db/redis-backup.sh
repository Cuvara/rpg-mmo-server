#!/usr/bin/env bash
#
# redis-backup.sh — snapshot the Redis system-of-record into a timestamped RDB
# file, then prune old ones.
#
# Redis here is NOT a cache (ADR-4): it holds the server registry (servers:*),
# gateway sessions (session:*) and the cross-server event stream (events:*).
# Losing it makes every live game server invisible to matchmaking, so it gets
# the same backup treatment PostgreSQL does.
#
# Method: BGSAVE (fork + write RDB, does not block the event loop) then stream
# /data/dump.rdb out of the container over stdout. `docker cp` is deliberately
# avoided: under WSL, docker.exe translates host paths and an absolute /mnt/*
# destination fails.
#
# Usage:
#   db/redis-backup.sh                       # back up, keep 7
#   db/redis-backup.sh --dir /tmp/b --keep 3 # custom destination + retention
#   db/redis-backup.sh --skip-missing        # container absent -> warn, exit 0
#
# Environment overrides (flags win):
#   REDIS_BACKUP_DIR  destination root       (default $BACKUP_DIR/redis, else /var/backups/rpg-mmo/redis)
#   BACKUP_KEEP       archives kept          (default 7)
#   REDIS_CONTAINER   container name         (default rpg-redis)
#   REDIS_PASSWORD    AUTH password          (default empty = no auth)
#
# Output:
#   $REDIS_BACKUP_DIR/redis-<UTC timestamp>.rdb   (restorable with redis-restore.sh)
#
# Exit codes: 0 ok (or nothing to do with --skip-missing), 1 failure, 2 bad usage.
#
set -euo pipefail

BACKUP_DIR="${BACKUP_DIR:-/var/backups/rpg-mmo}"
REDIS_BACKUP_DIR="${REDIS_BACKUP_DIR:-$BACKUP_DIR/redis}"
BACKUP_KEEP="${BACKUP_KEEP:-7}"
REDIS_CONTAINER="${REDIS_CONTAINER:-rpg-redis}"
SKIP_MISSING=0

# ---------------------------------------------------------------- arg parsing
while [ $# -gt 0 ]; do
	case "$1" in
	--dir)
		REDIS_BACKUP_DIR="${2:?--dir needs a path}"
		shift 2
		;;
	--keep)
		BACKUP_KEEP="${2:?--keep needs a number}"
		shift 2
		;;
	--container)
		REDIS_CONTAINER="${2:?--container needs a name}"
		shift 2
		;;
	--skip-missing)
		SKIP_MISSING=1
		shift
		;;
	-h | --help)
		sed -n '2,32p' "${BASH_SOURCE[0]}"
		exit 0
		;;
	*)
		echo "ERROR: unknown flag: $1 (try --help)" >&2
		exit 2
		;;
	esac
done

if ! [[ "$BACKUP_KEEP" =~ ^[0-9]+$ ]] || [ "$BACKUP_KEEP" -lt 1 ]; then
	echo "ERROR: --keep must be a positive integer (got '$BACKUP_KEEP')" >&2
	exit 2
fi

# ---------------------------------------------------------------- log helpers
log() { echo "[redis-backup] $*"; }
warn() { echo "[redis-backup] WARNING: $*" >&2; }
die() {
	echo "[redis-backup] ERROR: $*" >&2
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

# redis_cli <args...> — run redis-cli inside the container, with AUTH if set.
redis_cli() {
	if [ -n "${REDIS_PASSWORD:-}" ]; then
		"$DOCKER" exec "$REDIS_CONTAINER" redis-cli -a "$REDIS_PASSWORD" --no-auth-warning "$@"
	else
		"$DOCKER" exec "$REDIS_CONTAINER" redis-cli "$@"
	fi
}

# ----------------------------------------------------------------------- main
if ! container_running "$REDIS_CONTAINER"; then
	if [ "$SKIP_MISSING" -eq 1 ]; then
		warn "container '$REDIS_CONTAINER' not running -- skipping redis backup"
		exit 0
	fi
	die "container '$REDIS_CONTAINER' not running (use --skip-missing to tolerate)"
fi

mkdir -p "$REDIS_BACKUP_DIR" 2>/dev/null ||
	die "cannot create '$REDIS_BACKUP_DIR' (permission denied?) -- set REDIS_BACKUP_DIR to a writable path"
[ -w "$REDIS_BACKUP_DIR" ] || die "'$REDIS_BACKUP_DIR' is not writable by $(id -un)"

log "destination: $REDIS_BACKUP_DIR (keep $BACKUP_KEEP)"

# The dataset is small (sessions + registry + a trimmed stream); record its size
# so a suspiciously empty backup is obvious in the log.
KEYS="$(redis_cli DBSIZE 2>/dev/null | tr -d '\r' || echo '?')"
log "live dataset: $KEYS keys"

# --------------------------------------------------------------------- BGSAVE
# LASTSAVE is a unix timestamp of the last successful save. Snapshot it first so
# we can prove the BGSAVE we asked for is the one that completed, rather than
# copying a stale dump.rdb left over from the `save 60 1000` rule.
BEFORE="$(redis_cli LASTSAVE | tr -d '\r')"
[[ "$BEFORE" =~ ^[0-9]+$ ]] || die "LASTSAVE returned '$BEFORE' (redis unreachable or AUTH wrong?)"

log "issuing BGSAVE (lastsave=$BEFORE)"
if ! redis_cli BGSAVE >/dev/null 2>&1; then
	# BGSAVE is refused while another fork (BGSAVE/BGREWRITEAOF) is in flight.
	warn "BGSAVE was refused -- another save may be in progress; waiting for it"
fi

# Wait for LASTSAVE to advance. 60s is generous: this dataset saves in well
# under a second, and the ceiling only matters if the fork is starved.
DEADLINE=$((SECONDS + 60))
saved=0
while [ "$SECONDS" -lt "$DEADLINE" ]; do
	NOW="$(redis_cli LASTSAVE | tr -d '\r')"
	if [[ "$NOW" =~ ^[0-9]+$ ]] && [ "$NOW" -gt "$BEFORE" ]; then
		saved=1
		break
	fi
	sleep 1
done
[ "$saved" -eq 1 ] || die "BGSAVE did not complete within 60s (lastsave still $BEFORE)"

# rdb_last_bgsave_status is `ok` or `err`; a failed fork still bumps nothing but
# is worth surfacing explicitly.
STATUS="$(redis_cli INFO persistence | tr -d '\r' | awk -F: '/^rdb_last_bgsave_status:/{print $2}')"
[ "$STATUS" = "ok" ] || die "rdb_last_bgsave_status=$STATUS -- the snapshot on disk is not trustworthy"
log "BGSAVE ok"

# ------------------------------------------------------------------ copy out
STAMP="$(date -u +%Y%m%dT%H%M%SZ)"
OUT="$REDIS_BACKUP_DIR/redis-$STAMP.rdb"

# Write to .partial so an interrupted run never leaves a file that looks like a
# usable backup. `docker exec cat` keeps the bytes on stdout: no host-path
# translation, which is what breaks `docker cp` under WSL/docker.exe.
if ! "$DOCKER" exec "$REDIS_CONTAINER" cat /data/dump.rdb >"$OUT.partial" 2>"$OUT.err"; then
	warn "copy of /data/dump.rdb failed:"
	cat "$OUT.err" >&2 || true
	rm -f "$OUT.partial" "$OUT.err"
	die "redis backup failed"
fi
rm -f "$OUT.err"

# ---------------------------------------------------------------- verify
# An RDB file starts with the ASCII magic "REDIS" followed by a 4-digit version.
# This catches truncation and the classic "we copied an error message" failure.
#
# Retry with a sync in between: on WSL drvfs mounts (/mnt/*) a file read
# immediately after the redirect closes can briefly appear truncated. The
# PostgreSQL backup hit exactly this (see backup.sh) — same mitigation here.
verify_ok=0
for attempt in 1 2 3; do
	sync "$OUT.partial" 2>/dev/null || sync
	magic="$(head -c 5 "$OUT.partial" 2>/dev/null || true)"
	size="$(wc -c <"$OUT.partial" 2>/dev/null || echo 0)"
	if [ "$magic" = "REDIS" ] && [ "$size" -gt 16 ]; then
		verify_ok=1
		break
	fi
	sleep "$attempt"
done
if [ "$verify_ok" -ne 1 ]; then
	rm -f "$OUT.partial"
	die "verification failed: not an RDB file (magic='${magic:-}', size=${size:-0}, 3 attempts)"
fi

mv "$OUT.partial" "$OUT"
log "  ok: $(basename "$OUT") ($(du -h "$OUT" | cut -f1), $KEYS keys)"

# ------------------------------------------------------------------ retention
mapfile -t old < <(ls -1t "$REDIS_BACKUP_DIR"/redis-*.rdb 2>/dev/null | tail -n "+$((BACKUP_KEEP + 1))")
if [ "${#old[@]}" -eq 0 ]; then
	log "  retention: $(ls -1 "$REDIS_BACKUP_DIR"/redis-*.rdb 2>/dev/null | wc -l) kept (limit $BACKUP_KEEP)"
else
	for f in "${old[@]}"; do
		log "  retention: removing $(basename "$f")"
		rm -f "$f"
	done
fi

log "done"
