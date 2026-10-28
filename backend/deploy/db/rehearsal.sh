#!/usr/bin/env bash
# Dedicated synthetic PostgreSQL and Redis backup/restore rehearsal.
# Only exact rpgr-* resources created here are removed; archives and evidence persist.
set -euo pipefail

DB_DIR="$(cd -- "$(dirname -- "$BASH_SOURCE")" && pwd)"
# shellcheck source=rehearsal-lib.sh
source "$DB_DIR/rehearsal-lib.sh"
# Start from a sanitised environment: nothing inherited may select a target for
# backup.sh / restore.sh / redis-backup.sh / redis-restore.sh. The rpgr-* targets
# are passed to each child explicitly through r1_run_child (env -i allowlist).
r1_sanitise_env
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
RUN_LABEL="org.rpg.rehearsal.run=$RUN_ID"
MANIFEST="$RUN_DIR/ownership-manifest.tsv"
STATE_CAPTURED=0
CLEANUP_FAILED=0
GITHUB_OUTPUT="$(printenv GITHUB_OUTPUT 2>/dev/null || true)"

die() { echo "[rehearsal] ERROR: $*" >&2; exit 1; }
log() { echo "[rehearsal] $*"; }
if [ -e "$RUN_DIR" ]; then
	echo "[rehearsal] ERROR: run directory already exists; refusing to reuse: $RUN_DIR" >&2
	exit 1
fi
mkdir -p "$RUN_DIR"
chmod 700 "$RUN_DIR" 2>/dev/null || true
: > "$MANIFEST"
exec > >(tee -a "$RUN_DIR/rehearsal.log") 2>&1

save_normal_state() {
	local side="$1"
	# Raw docker ps text: informational only (uptime text hides restarts).
	docker ps -a --no-trunc \
		--format '{{.ID}}|{{.Image}}|{{.Names}}|{{.State}}|{{.Status}}' \
		> "$RUN_DIR/docker-ps-$side.txt"
	# The comparison input: ids, StartedAt, RestartCount, health, volumes, networks.
	r1_snapshot_normal_state "$RUN_DIR/normal-rpg-$side.txt"
}
cleanup() {
	local result=$?
	trap - EXIT
	set +e
	# Delete only what is BOTH in the ownership manifest AND carries this run's
	# label; anything else with a matching prefix is left alone and reported.
	r1_cleanup_owned "$MANIFEST" "$RUN_DIR/cleanup.log" || CLEANUP_FAILED=1
	if [ "$STATE_CAPTURED" -eq 1 ]; then
		save_normal_state after || CLEANUP_FAILED=1
		r1_compare_normal_state "$RUN_DIR/normal-rpg-before.txt" "$RUN_DIR/normal-rpg-after.txt" || CLEANUP_FAILED=1
	fi
	[ "$CLEANUP_FAILED" -eq 0 ] || result=1
	log "run evidence: $RUN_DIR"
	exit "$result"
}
trap cleanup EXIT

command -v docker >/dev/null 2>&1 || die 'docker is required'
docker info >/dev/null 2>&1 || die 'Docker daemon is unavailable'
# Best effort until testlab publishes a lock file: refuse on any testlab run,
# soak or scratch-stack process visible here, or on an operator-provided lock.
# Process visibility differs per host, so a granted no-soak window is still needed.
SOAK_RE='[t]estlab[.]py.*(run|suite|sweep|build-client|S09|soak)|[s]tack[.]sh.*--scratch'
if command -v pgrep >/dev/null 2>&1 && pgrep -af "$SOAK_RE" >/dev/null; then
	pgrep -af "$SOAK_RE" >&2 || true
	die 'a testlab process is active; request a new no-soak window'
fi
R1_LAB_LOCK="$(printenv R1_LAB_LOCK 2>/dev/null || true)"
if [ -n "$R1_LAB_LOCK" ] && [ -e "$R1_LAB_LOCK" ]; then
	die "testlab lock file present: $R1_LAB_LOCK"
fi
for file in backup.sh restore.sh redis-backup.sh redis-restore.sh; do
	[ -f "$DB_DIR/$file" ] || die "missing repository script: $DB_DIR/$file"
done
[ -d "$MIGRATIONS_DIR" ] || die "missing repository migrations: $MIGRATIONS_DIR"

# Run resources join only an internal network and publish no ports, so no
# normal service port is touched.
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
# Every created object is recorded in the ownership manifest immediately before
# its create call. The preflight above proved the name free, and cleanup still
# requires this run's label, so a recorded name can only ever delete our own object.
r1_record_owned network "$NETWORK"
docker network create --internal --label org.rpg.rehearsal.task=R1 \
	--label "$RUN_LABEL" "$NETWORK" >/dev/null

create_volume() {
	local volume="$1"
	r1_record_owned volume "$volume"
	docker volume create --label org.rpg.rehearsal.task=R1 \
		--label "$RUN_LABEL" "$volume" >/dev/null
}
create_postgres() {
	local name="$1" volume="$2" user="$3" database="$4"
	create_volume "$volume"
	r1_record_owned container "$name"
	docker run -d --name "$name" --network "$NETWORK" \
		--label org.rpg.rehearsal.task=R1 --label "$RUN_LABEL" \
		-e "POSTGRES_USER=$user" \
		-e "POSTGRES_PASSWORD=$PG_PASSWORD" -e "POSTGRES_DB=$database" \
		-v "$volume:/var/lib/postgresql/data" "$POSTGRES_IMAGE" >/dev/null
	log "created $name on private network $NETWORK at service port 5432"
}
create_postgres "$META_SRC" "$META_SRC_VOL" nakama nakama
create_postgres "$GAME_SRC" "$GAME_SRC_VOL" game gamestate
create_postgres "$META_DST" "$META_DST_VOL" nakama nakama
create_postgres "$GAME_DST" "$GAME_DST_VOL" game gamestate
create_volume "$REDIS_SRC_VOL"
r1_record_owned container "$REDIS_SRC"
docker run -d --name "$REDIS_SRC" --network "$NETWORK" \
	--label org.rpg.rehearsal.task=R1 --label "$RUN_LABEL" \
	-v "$REDIS_SRC_VOL:/data" "$REDIS_IMAGE" \
	redis-server --appendonly yes --dir /data --dbfilename dump.rdb >/dev/null
log "created $REDIS_SRC on private network $NETWORK at service port 6379"

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
# Full object signature (tables, indexes, constraints) of each source database,
# compared with the restored copy, so a partial schema restore fails.
schema_signature() {
	docker exec "$1" psql -At -U "$2" -d "$3" -c "SELECT s FROM (
		SELECT 'table|' || tablename FROM pg_tables WHERE schemaname = 'public'
		UNION ALL SELECT 'index|' || indexname || '|' || indexdef FROM pg_indexes WHERE schemaname = 'public'
		UNION ALL SELECT 'constraint|' || conrelid::regclass || '|' || conname || '|' || pg_get_constraintdef(oid)
			FROM pg_constraint WHERE connamespace = 'public'::regnamespace
		) x(s) ORDER BY s"
}
schema_signature "$META_SRC" nakama nakama > "$RUN_DIR/meta-source-schema.txt"
schema_signature "$GAME_SRC" game gamestate > "$RUN_DIR/gamestate-source-schema.txt"
[ "$(grep -c '^table|' "$RUN_DIR/gamestate-source-schema.txt")" -ge 4 ] || die 'source game-state schema signature is incomplete'
[ "$(cat "$RUN_DIR/meta-source-count.txt")" = 2 ] || die 'source meta synthetic row count mismatch'
[ "$(cat "$RUN_DIR/gamestate-source-schema-count.txt")" = 4 ] || die 'source game-state schema is incomplete'
[ "$(tr '\n' ',' < "$RUN_DIR/gamestate-source-row-counts.txt" | sed 's/,$//')" = '1,1,1,1' ] || die 'source game-state row count mismatch'
[ "$(tr -d '\r' < "$RUN_DIR/redis-source-key-count.txt")" = 4 ] || die 'source Redis key count mismatch'

# Target assertion: every container, network and volume the children touch must
# be a labelled rpgr-r1-<run-id>-* object of this run, attached to this run's
# network only, and must not be a running rpg-* object.
assert_targets() {
	local c v
	r1_require_rpgr_network "$NETWORK"
	for c in "$@"; do r1_require_rpgr_container "$c"; done
	for v in "$META_SRC_VOL" "$GAME_SRC_VOL" "$REDIS_SRC_VOL" "$META_DST_VOL" "$GAME_DST_VOL"; do
		r1_require_rpgr_volume "$v"
	done
}
assert_targets "$META_SRC" "$GAME_SRC" "$REDIS_SRC" "$META_DST" "$GAME_DST"
log "target assertion passed: all targets are $PREFIX-* objects of this run"

R1_CHILD_ENV=(
	"META_CONTAINER=$META_SRC" "GAME_CONTAINER=$GAME_SRC"
	POSTGRES_USER=nakama POSTGRES_DB=nakama POSTGRES_GAME_USER=game POSTGRES_GAME_DB=gamestate
	"BACKUP_DIR=$RUN_DIR/backups" BACKUP_KEEP=1
)
log 'running repository PostgreSQL backup script against dedicated sources'
r1_run_child "$DB_DIR/backup.sh" --dir "$RUN_DIR/backups/postgres" --keep 1
R1_CHILD_ENV=(
	"REDIS_CONTAINER=$REDIS_SRC" "REDIS_IMAGE=$REDIS_IMAGE"
	"BACKUP_DIR=$RUN_DIR/backups" BACKUP_KEEP=1
)
log 'running repository Redis backup script against dedicated source'
r1_run_child "$DB_DIR/redis-backup.sh" --dir "$RUN_DIR/backups/redis" --keep 1

META_BACKUP="$(find "$RUN_DIR/backups/postgres/meta" -maxdepth 1 -type f -name 'meta-*.dump' -print -quit)"
GAME_BACKUP="$(find "$RUN_DIR/backups/postgres/gamestate" -maxdepth 1 -type f -name 'gamestate-*.dump' -print -quit)"
REDIS_BACKUP="$(find "$RUN_DIR/backups/redis" -maxdepth 1 -type f -name 'redis-*.rdb' -print -quit)"
[ -n "$META_BACKUP" ] && [ "$(find "$RUN_DIR/backups/postgres/meta" -maxdepth 1 -name 'meta-*.dump' | wc -l)" -eq 1 ] || die 'expected exactly one meta dump'
[ -n "$GAME_BACKUP" ] && [ "$(find "$RUN_DIR/backups/postgres/gamestate" -maxdepth 1 -name 'gamestate-*.dump' | wc -l)" -eq 1 ] || die 'expected exactly one game-state dump'
[ -n "$REDIS_BACKUP" ] && [ "$(find "$RUN_DIR/backups/redis" -maxdepth 1 -name 'redis-*.rdb' | wc -l)" -eq 1 ] || die 'expected exactly one Redis RDB'

META_DB="$SAFE_ID"_meta_restore
GAME_DB="$SAFE_ID"_game_restore
assert_targets "$META_DST" "$GAME_DST"
r1_require_db_absent "$META_DST" nakama "$META_DB"
r1_require_db_absent "$GAME_DST" game "$GAME_DB"
R1_CHILD_ENV=(
	"META_CONTAINER=$META_DST" "GAME_CONTAINER=$GAME_DST"
	POSTGRES_USER=nakama POSTGRES_DB=nakama POSTGRES_GAME_USER=game POSTGRES_GAME_DB=gamestate
)
log 'restoring meta backup into fresh dedicated target database'
r1_run_child "$DB_DIR/restore.sh" --file "$META_BACKUP" --db meta --target "$META_DB" --yes
log 'restoring game-state backup into fresh dedicated target database'
r1_run_child "$DB_DIR/restore.sh" --file "$GAME_BACKUP" --db gamestate --target "$GAME_DB" --yes

META_COUNT="$(docker exec "$META_DST" psql -At -U nakama -d "$META_DB" -c 'SELECT count(*) FROM rehearsal_synthetic_accounts')"
[ "$META_COUNT" = 2 ] || die "meta restore has $META_COUNT rows; expected 2"
META_VALUES="$(docker exec "$META_DST" psql -At -U nakama -d "$META_DB" -c \
	'SELECT string_agg(account_key || chr(61) || account_value, chr(44) ORDER BY account_key) FROM rehearsal_synthetic_accounts')"
[ "$META_VALUES" = 'r1-account-001=synthetic-meta-value-001,r1-account-002=synthetic-meta-value-002' ] || die 'meta restored values differ'
schema_signature "$META_DST" nakama "$META_DB" > "$RUN_DIR/meta-restored-schema.txt"
schema_signature "$GAME_DST" game "$GAME_DB" > "$RUN_DIR/gamestate-restored-schema.txt"
cmp -s "$RUN_DIR/meta-source-schema.txt" "$RUN_DIR/meta-restored-schema.txt" || die 'meta restored schema differs from source'
cmp -s "$RUN_DIR/gamestate-source-schema.txt" "$RUN_DIR/gamestate-restored-schema.txt" || die 'game-state restored schema differs from source'
TABLE_COUNT="$(docker exec "$GAME_DST" psql -At -U game -d "$GAME_DB" -c \
	"SELECT count(*) FROM pg_tables WHERE schemaname='public' AND tablename IN ('player_states','character_state','character_items','item_grants')")"
[ "$TABLE_COUNT" = 4 ] || die "game-state schema table count is $TABLE_COUNT; expected 4"
ROW_COUNTS="$(docker exec "$GAME_DST" psql -At -U game -d "$GAME_DB" -c \
	"SELECT (SELECT count(*) FROM player_states) || ',' || (SELECT count(*) FROM character_state) || ',' || (SELECT count(*) FROM character_items) || ',' || (SELECT count(*) FROM item_grants)")"
[ "$ROW_COUNTS" = '1,1,1,1' ] || die "game-state row counts differ: $ROW_COUNTS"
GAME_VALUE="$(docker exec "$GAME_DST" psql -At -U game -d "$GAME_DB" -c 'SELECT user_id || chr(58) || map_id || chr(58) || hp FROM player_states')"
[ "$GAME_VALUE" = 'r1-synthetic-user:r1-map:80' ] || die 'game-state restored value differs'

# redis-restore.sh creates its own scratch container and volume. It is handed this
# run's label and the ownership manifest, labels what it creates and appends the
# exact names to the manifest; they are not discovered by prefix afterwards.
log 'running repository Redis restore in scratch mode'
r1_require_rpgr_name "$PREFIX-redis-restore" 'redis restore prefix'
R1_CHILD_ENV=(
	"REDIS_CONTAINER=$REDIS_SRC" "REDIS_IMAGE=$REDIS_IMAGE"
	"REDIS_RESTORE_SCRATCH_PREFIX=$PREFIX-redis-restore" "REDIS_RESTORE_NETWORK=$NETWORK"
	"REDIS_RESTORE_RUN_LABEL=$RUN_LABEL" "REDIS_RESTORE_MANIFEST=$MANIFEST"
)
r1_run_child "$DB_DIR/redis-restore.sh" --file "$REDIS_BACKUP" --mode scratch --keep-scratch
awk -F '\t' -v p="$PREFIX-redis-restore-scratch-" '$1=="container" && index($2,p)==1 && $2 !~ /-inject$/ {print $2}' "$MANIFEST" > "$RUN_DIR/redis-restore-new-containers.txt"
awk -F '\t' -v p="$PREFIX-redis-restore-vol-" '$1=="volume" && index($2,p)==1 {print $2}' "$MANIFEST" > "$RUN_DIR/redis-restore-new-volumes.txt"
[ "$(wc -l < "$RUN_DIR/redis-restore-new-containers.txt")" -eq 1 ] || die 'Redis restore did not record exactly one scratch container'
[ "$(wc -l < "$RUN_DIR/redis-restore-new-volumes.txt")" -eq 1 ] || die 'Redis restore did not record exactly one scratch volume'
REDIS_RESTORE_TARGET="$(cat "$RUN_DIR/redis-restore-new-containers.txt")"
REDIS_RESTORE_VOLUME="$(cat "$RUN_DIR/redis-restore-new-volumes.txt")"
r1_require_rpgr_container "$REDIS_RESTORE_TARGET"
r1_require_rpgr_volume "$REDIS_RESTORE_VOLUME"
KEY_PREFIX="r1:$RUN_ID:"
[ "$(docker exec "$REDIS_RESTORE_TARGET" redis-cli --raw GET "$KEY_PREFIX"string)" = synthetic-string-value ] || die 'Redis string value differs'
[ "$(docker exec "$REDIS_RESTORE_TARGET" redis-cli --raw HGET "$KEY_PREFIX"hash field-a)" = synthetic-hash-value ] || die 'Redis hash value differs'
[ "$(docker exec "$REDIS_RESTORE_TARGET" redis-cli --raw XRANGE "$KEY_PREFIX"events - + | grep -c '^synthetic-event$')" -eq 1 ] || die 'Redis stream value differs'
[ "$(docker exec "$REDIS_RESTORE_TARGET" redis-cli --raw GET "$KEY_PREFIX"ttl)" = synthetic-expiring-value ] || die 'Redis TTL value differs'
[ "$(docker exec "$REDIS_RESTORE_TARGET" redis-cli --raw TTL "$KEY_PREFIX"ttl)" -gt 0 ] || die 'Redis TTL did not survive restore'
[ "$(docker exec "$REDIS_RESTORE_TARGET" redis-cli DBSIZE | tr -d '\r')" -eq 4 ] || die 'Redis key count differs'
# Removed by the EXIT cleanup with every other manifest entry.

{
	printf 'artifact\tbytes\tsha256\n'
	find "$RUN_DIR/backups" -type f \( -name '*.dump' -o -name '*.rdb' \) -print0 | sort -z |
	while IFS= read -r -d '' file; do
		printf '%s\t%s\t%s\n' "$(printf %s "$file" | sed "s|^$RUN_DIR/||")" \
			"$(wc -c < "$file" | tr -d ' ')" "$(sha256sum "$file" | awk '{print $1}')"
	done
} > "$RUN_DIR/metadata.tsv"
log "meta synthetic rows restored: $META_COUNT"
log "schema signatures (tables, indexes, constraints) identical; game-state objects: $(wc -l < "$RUN_DIR/gamestate-source-schema.txt" | tr -d ' ')"
log "game-state migration tables restored: $TABLE_COUNT; table row counts: $ROW_COUNTS"
log 'Redis string, hash, stream, TTL and key count verified after restore'
log "archives and evidence retained in $RUN_DIR"
log 'rehearsal PASS'
if [ -n "$GITHUB_OUTPUT" ]; then printf 'run_dir=%s\n' "$RUN_DIR" >> "$GITHUB_OUTPUT"; fi
