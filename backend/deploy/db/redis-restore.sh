#!/usr/bin/env bash
#
# redis-restore.sh — load a redis-backup.sh RDB snapshot, either into a
# throwaway scratch container (default, safe) or over the live instance.
#
#   --mode scratch   spin a disposable redis on a disposable volume, load the
#                    dump, report DBSIZE + key breakdown, tear it down. The live
#                    container is never touched. THIS IS THE DEFAULT.
#   --mode live      DESTRUCTIVE: stop rpg-redis, wipe its dataset, inject the
#                    dump, start it again. Requires --yes.
#
# Usage:
#   # rehearse a backup (do this before you ever need it, and after every change)
#   db/redis-restore.sh --file /var/backups/rpg-mmo/redis/redis-20260806T041500Z.rdb
#
#   # real disaster recovery
#   db/redis-restore.sh --file <rdb> --mode live --yes
#
# Flags:
#   --file FILE      RDB produced by redis-backup.sh                  [required]
#   --mode M         scratch | live                            (default scratch)
#   --yes            required acknowledgement for --mode live
#   --keep-scratch   leave the scratch container/volume up for poking around
#   --container NAME live container name                     (default rpg-redis)
#
# WHY THE AOF HAS TO GO, AND WHY DELETING IT IS NOT ENOUGH (measured 2026-08-06):
#   The live server runs with `--appendonly yes`. On startup Redis prefers the
#   AOF over the RDB, so dropping a dump.rdb next to an existing appendonlydir
#   restores NOTHING — the server silently comes back with the old dataset and
#   the operator believes the restore worked. So both modes wipe
#   `appendonlydir`/`appendonly.aof` before injecting the RDB.
#
#   That wipe alone is still not a restore. With `appendonly yes` and NO AOF
#   manifest on disk, Redis 7 does not fall back to dump.rdb: it initialises an
#   EMPTY dataset and writes a fresh `appendonly.aof.1.base.rdb` from it. The
#   startup log goes straight from "Server initialized" to "Creating AOF base
#   file", with no "Done loading RDB" line, and the operator is left with an
#   empty Redis and an exit code of 0. This is the same reason the Redis manual
#   tells you to enable AOF with `CONFIG SET appendonly yes` at runtime rather
#   than by restarting into it.
#
#   --mode live therefore does not hand the RDB to the live container at all.
#   It first runs a short-lived SEED container over the live volume with
#   `--appendonly no` (so the RDB is actually loaded), then `CONFIG SET
#   appendonly yes` inside it, which rewrites appendonlydir FROM the loaded
#   dataset. Only then does the real container start — and it now finds an AOF
#   that contains the snapshot. The restored key count is compared against the
#   seed container's and a mismatch is a hard failure, because the failure mode
#   this whole comment describes was silent.
#
# Exit codes: 0 ok, 1 failure, 2 bad usage.
#
set -euo pipefail

FILE=""
MODE="scratch"
CONFIRMED=0
KEEP_SCRATCH=0
REDIS_CONTAINER="${REDIS_CONTAINER:-rpg-redis}"

# ---------------------------------------------------------------- arg parsing
while [ $# -gt 0 ]; do
	case "$1" in
	--file)
		FILE="${2:?--file needs a path}"
		shift 2
		;;
	--mode)
		MODE="${2:?--mode needs scratch|live}"
		shift 2
		;;
	--container)
		REDIS_CONTAINER="${2:?--container needs a name}"
		shift 2
		;;
	--yes)
		CONFIRMED=1
		shift
		;;
	--keep-scratch)
		KEEP_SCRATCH=1
		shift
		;;
	-h | --help)
		sed -n '2,38p' "${BASH_SOURCE[0]}"
		exit 0
		;;
	*)
		echo "ERROR: unknown flag: $1 (try --help)" >&2
		exit 2
		;;
	esac
done

[ -n "$FILE" ] || {
	echo "ERROR: --file is required (try --help)" >&2
	exit 2
}
case "$MODE" in
scratch | live) ;;
*)
	echo "ERROR: --mode must be scratch|live (got '$MODE')" >&2
	exit 2
	;;
esac

log() { echo "[redis-restore] $*"; }
warn() { echo "[redis-restore] WARNING: $*" >&2; }
die() {
	echo "[redis-restore] ERROR: $*" >&2
	exit 1
}

[ -f "$FILE" ] || die "no such file: $FILE"
[ "$(head -c 5 "$FILE")" = "REDIS" ] || die "'$FILE' is not an RDB file (missing REDIS magic)"

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
DOCKER="$(detect_docker)" || die "docker not available (tried docker, docker.exe)"

# Reuse the live container's image so the scratch instance is the same Redis
# version that wrote the dump (an older redis refuses a newer RDB version).
IMAGE="$("$DOCKER" inspect -f '{{.Config.Image}}' "$REDIS_CONTAINER" 2>/dev/null || true)"
[ -n "$IMAGE" ] || IMAGE="redis:7.4-alpine"

# rcli <container> <args...>
rcli() {
	local c="$1"
	shift
	if [ -n "${REDIS_PASSWORD:-}" ]; then
		"$DOCKER" exec "$c" redis-cli -a "$REDIS_PASSWORD" --no-auth-warning "$@"
	else
		"$DOCKER" exec "$c" redis-cli "$@"
	fi
}

# wait_ready <container> — poll PING for up to 60s.
wait_ready() {
	local c="$1" deadline=$((SECONDS + 60))
	while [ "$SECONDS" -lt "$deadline" ]; do
		if [ "$(rcli "$c" PING 2>/dev/null | tr -d '\r')" = "PONG" ]; then
			return 0
		fi
		sleep 1
	done
	return 1
}

# inject <volume> — wipe the dataset on <volume> and write $FILE as dump.rdb.
# Done from a helper container so the RDB never travels through a host path:
# docker.exe rejects absolute /mnt/* paths, which rules out `docker cp`.
inject() {
	local vol="$1"
	"$DOCKER" run --rm -i -v "$vol:/data" "$IMAGE" \
		sh -ec 'rm -rf /data/appendonlydir /data/appendonly.aof /data/dump.rdb; cat > /data/dump.rdb; ls -l /data/dump.rdb' \
		<"$FILE"
}

# report <container> — what actually came back.
report() {
	local c="$1"
	local n
	n="$(rcli "$c" DBSIZE | tr -d '\r')"
	log "restored dataset: $n keys"
	log "key breakdown (by prefix):"
	rcli "$c" --scan --count 1000 2>/dev/null | tr -d '\r' |
		awk -F: 'NF{print $1}' | sort | uniq -c | sort -rn | head -20 |
		while read -r count prefix; do log "    $count  ${prefix}:*"; done
	[ "$n" -gt 0 ] || warn "the restored dataset is EMPTY -- was the backup taken from an idle stack?"
}

# ------------------------------------------------------------------- scratch
if [ "$MODE" = "scratch" ]; then
	STAMP="$(date -u +%Y%m%d%H%M%S)"
	SC="rpg-redis-restore-scratch-$STAMP"
	VOL="rpg-redis-restore-vol-$STAMP"

	cleanup() {
		if [ "$KEEP_SCRATCH" -eq 1 ]; then
			log "keeping scratch container '$SC' and volume '$VOL' (--keep-scratch)"
			log "  clean up with: $DOCKER rm -f $SC && $DOCKER volume rm $VOL"
			return
		fi
		"$DOCKER" rm -f "$SC" >/dev/null 2>&1 || true
		"$DOCKER" volume rm "$VOL" >/dev/null 2>&1 || true
	}
	trap cleanup EXIT

	log "rehearsing '$FILE' in a scratch container (live '$REDIS_CONTAINER' untouched)"
	log "image: $IMAGE"
	"$DOCKER" volume create "$VOL" >/dev/null || die "cannot create scratch volume"
	inject "$VOL" >/dev/null || die "cannot write dump.rdb into scratch volume"

	# No published port and no AOF: this instance exists only to prove the file
	# parses and to count what is in it.
	"$DOCKER" run -d --name "$SC" -v "$VOL:/data" "$IMAGE" \
		redis-server --appendonly no --dbfilename dump.rdb --dir /data >/dev/null ||
		die "cannot start scratch container"

	wait_ready "$SC" || {
		warn "scratch redis never answered PING; last logs:"
		"$DOCKER" logs --tail 30 "$SC" >&2 || true
		die "restore rehearsal FAILED: redis could not load the RDB"
	}

	# A corrupt RDB makes redis exit before it serves, so PING above is already
	# the load check. Confirm explicitly anyway.
	if "$DOCKER" logs "$SC" 2>&1 | grep -qi "Bad file format\|Short read or OOM\|Internal error in RDB"; then
		"$DOCKER" logs --tail 30 "$SC" >&2 || true
		die "restore rehearsal FAILED: redis reported RDB corruption"
	fi

	log "RDB loaded cleanly"
	report "$SC"
	log "rehearsal OK -- this backup is restorable"
	exit 0
fi

# ---------------------------------------------------------------------- live
[ "$CONFIRMED" -eq 1 ] || {
	echo "ERROR: --mode live destroys the current Redis dataset. Re-run with --yes." >&2
	echo "       Rehearse first: db/redis-restore.sh --file $FILE" >&2
	exit 2
}

"$DOCKER" inspect "$REDIS_CONTAINER" >/dev/null 2>&1 ||
	die "container '$REDIS_CONTAINER' does not exist"

VOL="$("$DOCKER" inspect -f '{{range .Mounts}}{{if eq .Destination "/data"}}{{.Name}}{{end}}{{end}}' "$REDIS_CONTAINER" | tr -d '\r')"
[ -n "$VOL" ] || die "could not resolve the /data volume of '$REDIS_CONTAINER'"

warn "about to REPLACE the dataset of '$REDIS_CONTAINER' (volume $VOL) with $FILE"
log "stopping '$REDIS_CONTAINER'"
"$DOCKER" stop "$REDIS_CONTAINER" >/dev/null || die "cannot stop '$REDIS_CONTAINER'"

log "wiping AOF + RDB on volume '$VOL' and injecting the snapshot"
inject "$VOL" >/dev/null || {
	warn "injection failed -- restarting redis with whatever is on disk"
	"$DOCKER" start "$REDIS_CONTAINER" >/dev/null || true
	die "restore failed"
}

# Seed pass: load the RDB with AOF OFF, then turn AOF ON so Redis rebuilds
# appendonlydir from the loaded dataset. Without this the live container starts
# into an empty AOF and ignores dump.rdb entirely (see the header comment).
SEED="rpg-redis-restore-seed-$$"
seed_cleanup() { "$DOCKER" rm -f "$SEED" >/dev/null 2>&1 || true; }
trap seed_cleanup EXIT

log "seeding the AOF from the snapshot (temporary container, appendonly off)"
"$DOCKER" run -d --name "$SEED" -v "$VOL:/data" "$IMAGE" \
	redis-server --appendonly no --dbfilename dump.rdb --dir /data >/dev/null ||
	die "cannot start seed container"

wait_ready "$SEED" || {
	"$DOCKER" logs --tail 30 "$SEED" >&2 || true
	die "seed redis never answered PING: the RDB could not be loaded"
}
if "$DOCKER" logs "$SEED" 2>&1 | grep -qi "Bad file format\|Short read or OOM\|Internal error in RDB"; then
	"$DOCKER" logs --tail 30 "$SEED" >&2 || true
	die "seed redis reported RDB corruption -- live dataset NOT replaced cleanly"
fi

SEED_KEYS="$(rcli "$SEED" DBSIZE | tr -d '\r')"
log "snapshot contains $SEED_KEYS keys; rewriting AOF from it"
[ "$(rcli "$SEED" CONFIG SET appendonly yes | tr -d '\r')" = "OK" ] ||
	die "could not enable AOF on the seed container"

# CONFIG SET appendonly yes kicks off a background rewrite; the AOF on disk is
# only complete once it finishes. Starting the live container before then would
# race the rewrite and can leave a truncated AOF.
deadline=$((SECONDS + 120))
while [ "$SECONDS" -lt "$deadline" ]; do
	info="$(rcli "$SEED" INFO persistence | tr -d '\r')"
	case "$info" in *aof_rewrite_in_progress:0*)
		case "$info" in
		*aof_last_bgrewrite_status:ok*) break ;;
		*) die "AOF rewrite failed on the seed container" ;;
		esac
		;;
	esac
	sleep 1
done
[ "$SECONDS" -lt "$deadline" ] || die "AOF rewrite did not finish within 120s"

# SHUTDOWN so the final AOF is fsynced; redis-cli reports the closed connection
# as an error, which is expected here.
rcli "$SEED" SHUTDOWN NOSAVE >/dev/null 2>&1 || true
seed_cleanup
trap - EXIT
log "AOF seeded"

log "starting '$REDIS_CONTAINER'"
"$DOCKER" start "$REDIS_CONTAINER" >/dev/null || die "cannot start '$REDIS_CONTAINER'"
wait_ready "$REDIS_CONTAINER" || {
	"$DOCKER" logs --tail 30 "$REDIS_CONTAINER" >&2 || true
	die "'$REDIS_CONTAINER' did not come back up"
}

# Hard gate. A live restore that silently lands 0 keys is worse than a failed
# one: it destroys the dataset and reports success.
LIVE_KEYS="$(rcli "$REDIS_CONTAINER" DBSIZE | tr -d '\r')"
[ "$LIVE_KEYS" = "$SEED_KEYS" ] ||
	die "restore verification FAILED: snapshot had $SEED_KEYS keys, live '$REDIS_CONTAINER' came back with $LIVE_KEYS"

report "$REDIS_CONTAINER"

# The registry is rebuilt by game-server heartbeats, not by this restore; say so
# rather than letting an operator assume the world is whole again.
log "NOTE: sessions restored from the snapshot are stale — clients re-auth anyway."
log "NOTE: the server registry is only as good as the snapshot. Nothing in the"
log "      running code re-registers a game server today (DISASTER-RECOVERY.md"
log "      G1), so if the snapshot predates the live servers you must re-register"
log "      by hand. Verify with:"
log "      $DOCKER exec $REDIS_CONTAINER redis-cli --scan --pattern 'servers:*'"
log "done"
