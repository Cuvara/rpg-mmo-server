#!/usr/bin/env bash
# Dedicated synthetic PostgreSQL and Redis backup/restore rehearsal.
# Only exact rpgr-* resources created here are removed; archives and evidence persist.
set -euo pipefail

DB_DIR="$(cd -- "$(dirname -- "$BASH_SOURCE")" && pwd)"
MIGRATIONS_DIR="$DB_DIR/migrations/gamestate"
if [ "$#" -ne 1 ]; then
	echo "usage: rehearsal.sh <durable-artifact-root>" >&2
	exit 2
fi
REHEARSAL_ROOT="$1"
if [ -z "$REHEARSAL_ROOT" ]; then echo 'artifact root must not be empty' >&2; exit 2; fi
POSTGRES_IMAGE="$(printenv R1_POSTGRES_IMAGE 2>/dev/null || echo postgres:16.4-alpine)"
REDIS_IMAGE="$(printenv R1_REDIS_IMAGE 2>/dev/null || echo redis:7.4-alpine)"
RUN_ID="$(date -u +%Y%m%dT%H%M%SZ)-$$"
SAFE_ID="$(printf %s "$RUN_ID" | tr -c 'A-Za-z0-9' '_')"
PREFIX="rpgr-r1-$RUN_ID"
NETWORK="$PREFIX-net"
RUN_DIR="$REHEARSAL_ROOT/$RUN_ID"
META_SRC="$PREFIX-meta-src"
GAME_SRC="$PREFIX-game-src"
REDIS_SRC="$PREFIX-redis-src"
META_DST="$PREFIX-meta-restore"
GAME_DST="$PREFIX-game-restore"
META_SRC_VOL="$META_SRC-data"
GAME_SRC_VOL="$GAME_SRC-data"
REDIS_SRC_VOL="$REDIS_SRC-data"
META_DST_VOL="$META_DST-data"
GAME_DST_VOL="$GAME_DST-data"
PG_PASSWORD="rpgr-synthetic-$RANDOM-$(date -u +%s)"
CREATED_CONTAINERS=""
CREATED_VOLUMES=""
NETWORK_CREATED=0
STATE_CAPTURED=0
CLEANUP_FAILED=0
GITHUB_OUTPUT="$(printenv GITHUB_OUTPUT 2>/dev/null || true)"

die() { echo "[rehearsal] ERROR: $*" >&2; exit 1; }
log() { echo "[rehearsal] $*"; }
mkdir -p "$RUN_DIR"
chmod 700 "$RUN_DIR" 2>/dev/null || true
exec > >(tee -a "$RUN_DIR/rehearsal.log") 2>&1

save_normal_state() {
	local side="$1"
	docker ps -a --no-trunc \
		--format '{{.ID}}|{{.Image}}|{{.Names}}|{{.State}}|{{.Status}}' \
		> "$RUN_DIR/docker-ps-$side.txt"
	awk -F '|' '$3 ~ /^rpg-/ && $3 !~ /^rpgr-/' "$RUN_DIR/docker-ps-$side.txt" |
		sort > "$RUN_DIR/normal-rpg-$side.txt"
}
cleanup() {
	local result=$?
	trap - EXIT
	set +e
	local name
	for name in $(docker ps -a --format '{{.Names}}' | awk -v p="$PREFIX-redis-restore-scratch-" 'index($0,p)==1'); do
		CREATED_CONTAINERS="$CREATED_CONTAINERS $name"
	done
	for name in $(docker volume ls --format '{{.Name}}' | awk -v p="$PREFIX-redis-restore-vol-" 'index($0,p)==1'); do
		CREATED_VOLUMES="$CREATED_VOLUMES $name"
	done
	for name in $CREATED_CONTAINERS; do
		if docker inspect "$name" >/dev/null 2>&1; then
			docker rm -f "$name" >> "$RUN_DIR/cleanup.log" 2>&1 || CLEANUP_FAILED=1
		fi
	done
	for name in $CREATED_VOLUMES; do
		if docker volume inspect "$name" >/dev/null 2>&1; then
			docker volume rm "$name" >> "$RUN_DIR/cleanup.log" 2>&1 || CLEANUP_FAILED=1
		fi
	done
	if [ "$NETWORK_CREATED" -eq 1 ] && docker network inspect "$NETWORK" >/dev/null 2>&1; then
		docker network rm "$NETWORK" >> "$RUN_DIR/cleanup.log" 2>&1 || CLEANUP_FAILED=1
	fi
	if [ "$STATE_CAPTURED" -eq 1 ]; then
		save_normal_state after || CLEANUP_FAILED=1
		if ! cmp -s "$RUN_DIR/normal-rpg-before.txt" "$RUN_DIR/normal-rpg-after.txt"; then
			echo '[rehearsal] ERROR: normal rpg-* Docker state changed' >&2
			diff -u "$RUN_DIR/normal-rpg-before.txt" "$RUN_DIR/normal-rpg-after.txt" >&2 || true
			CLEANUP_FAILED=1
		fi
	fi
	[ "$CLEANUP_FAILED" -eq 0 ] || result=1
	log "run evidence: $RUN_DIR"
	exit "$result"
}
trap cleanup EXIT

command -v docker >/dev/null 2>&1 || die 'docker is required'
docker info >/dev/null 2>&1 || die 'Docker daemon is unavailable'
if command -v pgrep >/dev/null 2>&1 && pgrep -af '[t]estlab[.]py.*(S09|soak)' >/dev/null; then
	pgrep -af '[t]estlab[.]py.*(S09|soak)' >&2 || true
	die 'a testlab soak process is active; request a new no-soak window'
fi
for file in backup.sh restore.sh redis-backup.sh redis-restore.sh; do
	[ -f "$DB_DIR/$file" ] || die "missing repository script: $DB_DIR/$file"
done
[ -d "$MIGRATIONS_DIR" ] || die "missing repository migrations: $MIGRATIONS_DIR"

# Run resources join only an internal network; published ports bind loopback
# only and are allocated dynamically, so no normal service ports are reused.
for image in "$POSTGRES_IMAGE" "$REDIS_IMAGE"; do
	if ! docker image inspect "$image" >/dev/null 2>&1; then
		log "pulling rehearsal image $image"
		docker pull "$image"
	fi
done
for name in "$NETWORK" "$META_SRC" "$GAME_SRC" "$REDIS_SRC" "$META_DST" "$GAME_DST"; do
	if docker inspect "$name" >/dev/null 2>&1 || docker network inspect "$name" >/dev/null 2>&1; then
		die "resource already exists; refusing to reuse: $name"
	fi
done
for name in "$META_SRC_VOL" "$GAME_SRC_VOL" "$REDIS_SRC_VOL" "$META_DST_VOL" "$GAME_DST_VOL"; do
	docker volume inspect "$name" >/dev/null 2>&1 && die "volume already exists; refusing to reuse: $name"
done
if [ -n "$(docker ps -a --format '{{.Names}}' | awk -v p="$PREFIX-" 'index($0,p)==1')" ] ||
	[ -n "$(docker volume ls --format '{{.Name}}' | awk -v p="$PREFIX-" 'index($0,p)==1')" ]; then
	die "a resource with the unique run prefix already exists: $PREFIX"
fi

save_normal_state before
STATE_CAPTURED=1
log "run id: $RUN_ID"
log "Docker context: $(docker context show 2>/dev/null || echo default)"
log 'normal rpg-* Docker state captured before resource creation'
docker network create --internal --label org.rpg.rehearsal.task=R1 \
	--label "org.rpg.rehearsal.run=$RUN_ID" "$NETWORK" >/dev/null
NETWORK_CREATED=1

create_volume() {
	local volume="$1"
	docker volume create --label org.rpg.rehearsal.task=R1 \
		--label "org.rpg.rehearsal.run=$RUN_ID" "$volume" >/dev/null
	CREATED_VOLUMES="$CREATED_VOLUMES $volume"
}
create_postgres() {
	local name="$1" volume="$2" user="$3" database="$4"
	create_volume "$volume"
	docker run -d --name "$name" --network "$NETWORK" \
		--label org.rpg.rehearsal.task=R1 --label "org.rpg.rehearsal.run=$RUN_ID" \
		-p '127.0.0.1::5432' -e "POSTGRES_USER=$user" \
		-e "POSTGRES_PASSWORD=$PG_PASSWORD" -e "POSTGRES_DB=$database" \
		-v "$volume:/var/lib/postgresql/data" "$POSTGRES_IMAGE" >/dev/null
	CREATED_CONTAINERS="$CREATED_CONTAINERS $name"
	log "created $name on loopback port $(docker port "$name" 5432/tcp | sed 's/.*://')"
}
create_postgres "$META_SRC" "$META_SRC_VOL" nakama nakama
create_postgres "$GAME_SRC" "$GAME_SRC_VOL" game gamestate
create_postgres "$META_DST" "$META_DST_VOL" nakama nakama
create_postgres "$GAME_DST" "$GAME_DST_VOL" game gamestate
create_volume "$REDIS_SRC_VOL"
docker run -d --name "$REDIS_SRC" --network "$NETWORK" \
	--label org.rpg.rehearsal.task=R1 --label "org.rpg.rehearsal.run=$RUN_ID" \
	-p '127.0.0.1::6379' -v "$REDIS_SRC_VOL:/data" "$REDIS_IMAGE" \
	redis-server --appendonly yes --dir /data --dbfilename dump.rdb >/dev/null
CREATED_CONTAINERS="$CREATED_CONTAINERS $REDIS_SRC"
log "created $REDIS_SRC on loopback port $(docker port "$REDIS_SRC" 6379/tcp | sed 's/.*://')"

wait_pg() {
	local container="$1" user="$2" db="$3" deadline=$((SECONDS + 90))
	while [ "$SECONDS" -lt "$deadline" ]; do
		docker exec "$container" pg_isready -U "$user" -d "$db" >/dev/null 2>&1 && return 0
		sleep 1
	done
	die "PostgreSQL did not become ready: $container"
}
wait_redis() {
	local container="$1" deadline=$((SECONDS + 60))
	while [ "$SECONDS" -lt "$deadline" ]; do
		[ "$(docker exec "$container" redis-cli PING 2>/dev/null | tr -d '\r')" = PONG ] && return 0
		sleep 1
	done
	die "Redis did not become ready: $container"
}
wait_pg "$META_SRC" nakama nakama
wait_pg "$GAME_SRC" game gamestate
wait_pg "$META_DST" nakama nakama
wait_pg "$GAME_DST" game gamestate
wait_redis "$REDIS_SRC"

log 'applying repository game-state migrations to isolated source'
for migration in "$MIGRATIONS_DIR"/*.sql; do
	[ -f "$migration" ] || die 'no game-state migrations found'
	docker exec -i "$GAME_SRC" psql -v ON_ERROR_STOP=1 -U game -d gamestate < "$migration"
done
docker exec -i "$META_SRC" psql -v ON_ERROR_STOP=1 -U nakama -d nakama <<'SQL'
CREATE TABLE rehearsal_synthetic_accounts (account_key text PRIMARY KEY, account_value text NOT NULL);
INSERT INTO rehearsal_synthetic_accounts VALUES
('r1-account-001', 'synthetic-meta-value-001'),
('r1-account-002', 'synthetic-meta-value-002');
SQL
docker exec -i "$GAME_SRC" psql -v ON_ERROR_STOP=1 -U game -d gamestate <<'SQL'
INSERT INTO player_states (user_id, map_id, x, y, hp, max_hp)
VALUES ('r1-synthetic-user', 'r1-map', 12.5, -4.25, 80, 100);
INSERT INTO character_state (character_id, user_id, map_id, x, y, z, yaw, hp, max_hp, level, xp)
VALUES ('r1-synthetic-character', 'r1-synthetic-user', 'r1-map', 12.5, -4.25, 0, 1.5, 80, 100, 3, 1250);
INSERT INTO character_items (instance_id, character_id, item_id, quantity, container, bag_index)
VALUES ('r1-synthetic-item', 'r1-synthetic-character', 'r1-synthetic-potion', 2, 'bag', 0);
INSERT INTO item_grants (grant_id, character_id, kind, instance_id)
VALUES ('r1-synthetic-grant', 'r1-synthetic-character', 'grant', 'r1-synthetic-item');
SQL
docker exec "$REDIS_SRC" redis-cli SET "r1:$RUN_ID:string" synthetic-string-value >/dev/null
docker exec "$REDIS_SRC" redis-cli HSET "r1:$RUN_ID:hash" field-a synthetic-hash-value >/dev/null
docker exec "$REDIS_SRC" redis-cli XADD "r1:$RUN_ID:events" '*' event synthetic-event >/dev/null
docker exec "$REDIS_SRC" redis-cli SET "r1:$RUN_ID:ttl" synthetic-expiring-value EX 3600 >/dev/null

docker exec "$META_SRC" psql -At -U nakama -d nakama -c \
	'SELECT count(*) FROM rehearsal_synthetic_accounts' > "$RUN_DIR/meta-source-count.txt"
docker exec "$GAME_SRC" psql -At -U game -d gamestate -c \
	"SELECT count(*) FROM pg_tables WHERE schemaname='public' AND tablename IN ('player_states','character_state','character_items','item_grants')" > "$RUN_DIR/gamestate-source-schema-count.txt"
docker exec "$GAME_SRC" psql -At -U game -d gamestate -c \
	'SELECT count(*) FROM player_states; SELECT count(*) FROM character_state; SELECT count(*) FROM character_items; SELECT count(*) FROM item_grants' > "$RUN_DIR/gamestate-source-row-counts.txt"
docker exec "$REDIS_SRC" redis-cli DBSIZE > "$RUN_DIR/redis-source-key-count.txt"
[ "$(cat "$RUN_DIR/meta-source-count.txt")" = 2 ] || die 'source meta synthetic row count mismatch'
[ "$(cat "$RUN_DIR/gamestate-source-schema-count.txt")" = 4 ] || die 'source game-state schema is incomplete'
[ "$(tr '\n' ',' < "$RUN_DIR/gamestate-source-row-counts.txt" | sed 's/,$//')" = '1,1,1,1' ] || die 'source game-state row count mismatch'
[ "$(tr -d '\r' < "$RUN_DIR/redis-source-key-count.txt")" = 4 ] || die 'source Redis key count mismatch'

export META_CONTAINER="$META_SRC" GAME_CONTAINER="$GAME_SRC"
export POSTGRES_USER=nakama POSTGRES_DB=nakama POSTGRES_GAME_USER=game POSTGRES_GAME_DB=gamestate
log 'running repository PostgreSQL backup script against dedicated sources'
bash "$DB_DIR/backup.sh" --dir "$RUN_DIR/backups/postgres" --keep 1
export REDIS_CONTAINER="$REDIS_SRC"
log 'running repository Redis backup script against dedicated source'
bash "$DB_DIR/redis-backup.sh" --dir "$RUN_DIR/backups/redis" --keep 1

META_BACKUP="$(find "$RUN_DIR/backups/postgres/meta" -maxdepth 1 -type f -name 'meta-*.dump' -print -quit)"
GAME_BACKUP="$(find "$RUN_DIR/backups/postgres/gamestate" -maxdepth 1 -type f -name 'gamestate-*.dump' -print -quit)"
REDIS_BACKUP="$(find "$RUN_DIR/backups/redis" -maxdepth 1 -type f -name 'redis-*.rdb' -print -quit)"
[ -n "$META_BACKUP" ] && [ "$(find "$RUN_DIR/backups/postgres/meta" -maxdepth 1 -name 'meta-*.dump' | wc -l)" -eq 1 ] || die 'expected exactly one meta dump'
[ -n "$GAME_BACKUP" ] && [ "$(find "$RUN_DIR/backups/postgres/gamestate" -maxdepth 1 -name 'gamestate-*.dump' | wc -l)" -eq 1 ] || die 'expected exactly one game-state dump'
[ -n "$REDIS_BACKUP" ] && [ "$(find "$RUN_DIR/backups/redis" -maxdepth 1 -name 'redis-*.rdb' | wc -l)" -eq 1 ] || die 'expected exactly one Redis RDB'

export META_CONTAINER="$META_DST"
log 'restoring meta backup into fresh dedicated target database'
bash "$DB_DIR/restore.sh" --file "$META_BACKUP" --db meta --target "$SAFE_ID"_meta_restore --yes
export GAME_CONTAINER="$GAME_DST"
log 'restoring game-state backup into fresh dedicated target database'
bash "$DB_DIR/restore.sh" --file "$GAME_BACKUP" --db gamestate --target "$SAFE_ID"_game_restore --yes

META_DB="$SAFE_ID"_meta_restore
GAME_DB="$SAFE_ID"_game_restore
META_COUNT="$(docker exec "$META_DST" psql -At -U nakama -d "$META_DB" -c 'SELECT count(*) FROM rehearsal_synthetic_accounts')"
[ "$META_COUNT" = 2 ] || die "meta restore has $META_COUNT rows; expected 2"
META_VALUES="$(docker exec "$META_DST" psql -At -U nakama -d "$META_DB" -c \
	'SELECT string_agg(account_key || chr(61) || account_value, chr(44) ORDER BY account_key) FROM rehearsal_synthetic_accounts')"
[ "$META_VALUES" = 'r1-account-001=synthetic-meta-value-001,r1-account-002=synthetic-meta-value-002' ] || die 'meta restored values differ'
TABLE_COUNT="$(docker exec "$GAME_DST" psql -At -U game -d "$GAME_DB" -c \
	"SELECT count(*) FROM pg_tables WHERE schemaname='public' AND tablename IN ('player_states','character_state','character_items','item_grants')")"
[ "$TABLE_COUNT" = 4 ] || die "game-state schema table count is $TABLE_COUNT; expected 4"
ROW_COUNTS="$(docker exec "$GAME_DST" psql -At -U game -d "$GAME_DB" -c \
	"SELECT (SELECT count(*) FROM player_states) || ',' || (SELECT count(*) FROM character_state) || ',' || (SELECT count(*) FROM character_items) || ',' || (SELECT count(*) FROM item_grants)")"
[ "$ROW_COUNTS" = '1,1,1,1' ] || die "game-state row counts differ: $ROW_COUNTS"
GAME_VALUE="$(docker exec "$GAME_DST" psql -At -U game -d "$GAME_DB" -c 'SELECT user_id || chr(58) || map_id || chr(58) || hp FROM player_states')"
[ "$GAME_VALUE" = 'r1-synthetic-user:r1-map:80' ] || die 'game-state restored value differs'

# Preserve the helper's fresh Redis target briefly to check each synthetic value.
docker ps -a --format '{{.Names}}' | awk -v p="$PREFIX-redis-restore-scratch-" 'index($0,p)==1' | sort > "$RUN_DIR/redis-restore-containers-before.txt"
docker volume ls --format '{{.Name}}' | awk -v p="$PREFIX-redis-restore-vol-" 'index($0,p)==1' | sort > "$RUN_DIR/redis-restore-volumes-before.txt"
log 'running repository Redis restore in scratch mode'
REDIS_RESTORE_SCRATCH_PREFIX="$PREFIX-redis-restore" REDIS_RESTORE_NETWORK="$NETWORK" \
	bash "$DB_DIR/redis-restore.sh" --file "$REDIS_BACKUP" --mode scratch --keep-scratch
docker ps -a --format '{{.Names}}' | awk -v p="$PREFIX-redis-restore-scratch-" 'index($0,p)==1' | sort > "$RUN_DIR/redis-restore-containers-after.txt"
docker volume ls --format '{{.Name}}' | awk -v p="$PREFIX-redis-restore-vol-" 'index($0,p)==1' | sort > "$RUN_DIR/redis-restore-volumes-after.txt"
comm -13 "$RUN_DIR/redis-restore-containers-before.txt" "$RUN_DIR/redis-restore-containers-after.txt" > "$RUN_DIR/redis-restore-new-containers.txt"
comm -13 "$RUN_DIR/redis-restore-volumes-before.txt" "$RUN_DIR/redis-restore-volumes-after.txt" > "$RUN_DIR/redis-restore-new-volumes.txt"
[ "$(wc -l < "$RUN_DIR/redis-restore-new-containers.txt")" -eq 1 ] || die 'Redis restore did not create exactly one new scratch container'
[ "$(wc -l < "$RUN_DIR/redis-restore-new-volumes.txt")" -eq 1 ] || die 'Redis restore did not create exactly one new scratch volume'
REDIS_RESTORE_TARGET="$(cat "$RUN_DIR/redis-restore-new-containers.txt")"
REDIS_RESTORE_VOLUME="$(cat "$RUN_DIR/redis-restore-new-volumes.txt")"
KEY_PREFIX="r1:$RUN_ID:"
[ "$(docker exec "$REDIS_RESTORE_TARGET" redis-cli --raw GET "$KEY_PREFIX"string)" = synthetic-string-value ] || die 'Redis string value differs'
[ "$(docker exec "$REDIS_RESTORE_TARGET" redis-cli --raw HGET "$KEY_PREFIX"hash field-a)" = synthetic-hash-value ] || die 'Redis hash value differs'
[ "$(docker exec "$REDIS_RESTORE_TARGET" redis-cli --raw XRANGE "$KEY_PREFIX"events - + | grep -c '^synthetic-event$')" -eq 1 ] || die 'Redis stream value differs'
[ "$(docker exec "$REDIS_RESTORE_TARGET" redis-cli --raw GET "$KEY_PREFIX"ttl)" = synthetic-expiring-value ] || die 'Redis TTL value differs'
[ "$(docker exec "$REDIS_RESTORE_TARGET" redis-cli --raw TTL "$KEY_PREFIX"ttl)" -gt 0 ] || die 'Redis TTL did not survive restore'
[ "$(docker exec "$REDIS_RESTORE_TARGET" redis-cli DBSIZE | tr -d '\r')" -eq 4 ] || die 'Redis key count differs'
docker rm -f "$REDIS_RESTORE_TARGET" >/dev/null
docker volume rm "$REDIS_RESTORE_VOLUME" >/dev/null

{
	printf 'artifact\tbytes\tsha256\n'
	find "$RUN_DIR/backups" -type f \( -name '*.dump' -o -name '*.rdb' \) -print0 | sort -z |
	while IFS= read -r -d '' file; do
		printf '%s\t%s\t%s\n' "$(printf %s "$file" | sed "s|^$RUN_DIR/||")" \
			"$(wc -c < "$file" | tr -d ' ')" "$(sha256sum "$file" | awk '{print $1}')"
	done
} > "$RUN_DIR/metadata.tsv"
log "meta synthetic rows restored: $META_COUNT"
log "game-state migration tables restored: $TABLE_COUNT; table row counts: $ROW_COUNTS"
log 'Redis string, hash, stream, TTL and key count verified after restore'
log "archives and evidence retained in $RUN_DIR"
log 'rehearsal PASS'
if [ -n "$GITHUB_OUTPUT" ]; then printf 'run_dir=%s\n' "$RUN_DIR" >> "$GITHUB_OUTPUT"; fi
