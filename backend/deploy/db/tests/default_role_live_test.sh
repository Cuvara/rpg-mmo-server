#!/usr/bin/env bash
# MG-01 regression: upgrade a populated default database without cross-game access.
# Run: RPG_FACTORY_WORKSPACE=C:/Workspaces/UnityIndie bash backend/deploy/db/tests/default_role_live_test.sh
# Uses only a private PostgreSQL 16 cluster/container; never the shared stack.
# Exit 77 means neither PG_BIN_DIR nor a reachable Docker daemon was available.
set -euo pipefail
export LC_ALL=C
HERE="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
DB_DIR="$(cd -- "$HERE/.." && pwd)"
WORK="$(mktemp -d)"
PASS=0 FAIL=0
backend=none
container="mg-default-role-$$-$RANDOM-$RANDOM"
container_created=0
pg_started=0
cleanup() {
	local status=$?
	local local_stopped=1 created_container_id
	trap - EXIT
	# Release the temporary working directory before removing it on Windows.
	cd -- "$DB_DIR"
	# The cidfile is written only for our creation, even if docker run fails
	# during start. A name collision can never cause removal of someone else's DB.
	if [[ -s "$WORK/container-id" ]]; then
		# Docker writes the id without a trailing newline, so read returns 1 at
		# EOF; under set -e that would abort this trap before the removal.
		read -r created_container_id < "$WORK/container-id" || true
		docker rm -f "$created_container_id" >/dev/null 2>&1 || status=1
	fi
	if [[ "$pg_started" -eq 1 ]]; then
		if ! "$PG_BIN_DIR/pg_ctl" -D "$WORK/data" -m immediate -w stop >/dev/null 2>&1; then
			status=1
			local_stopped=0
		fi
	fi
	# If stopping a local cluster failed, keep its data directory for diagnosis.
	if [[ "$local_stopped" -eq 1 ]]; then rm -rf "$WORK"; fi
	exit "$status"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
ok() { PASS=$((PASS + 1)); printf 'ok   - %s\n' "$1"; }
bad() { FAIL=$((FAIL + 1)); printf 'FAIL - %s\n' "$1"; }
check() { local desc="$1"; shift; if "$@"; then ok "$desc"; else bad "$desc"; fi; }
skip() { printf 'SKIP - %s\npassed: 0 failed: 0 skipped: 1\n' "$1"; exit 77; }

# Do not inherit connection/service settings pointing at an operator's stack.
unset PGHOST PGHOSTADDR PGPORT PGUSER PGDATABASE PGPASSWORD PGSERVICE PGSERVICEFILE PGOPTIONS
unset PGSSLMODE PGSSLROOTCERT PGSSLCERT PGSSLKEY PGTARGETSESSIONATTRS
export PGPASSFILE="$WORK/no-pgpass" PGCONNECT_TIMEOUT=5
admin_password="mg-bootstrap-$$-$RANDOM"
export GAMESTATE_ROLE_PASSWORD="mg-default-$$-$RANDOM"
ember_password="mg-ember-$$-$RANDOM"

if [[ -n "${PG_BIN_DIR:-}" ]]; then
	for bin in initdb pg_ctl psql; do
		[[ -x "$PG_BIN_DIR/$bin" ]] || { echo "FAIL - PG_BIN_DIR lacks $bin"; exit 1; }
	done
	"$PG_BIN_DIR/psql" --version | grep -Eq 'PostgreSQL\) 16\.' || { echo 'FAIL - PG_BIN_DIR must contain PostgreSQL 16'; exit 1; }
	backend=local
	printf '%s\n' "$admin_password" > "$WORK/password"
	"$PG_BIN_DIR/initdb" -D "$WORK/data" -U game --pwfile="$WORK/password" --auth-host=scram-sha-256 --auth-local=scram-sha-256 > "$WORK/init.log" 2>&1
	# pg_ctl attempts the bind itself. Retry collisions on random private ports.
	for attempt in {1..30}; do
		port=$((20000 + (RANDOM % 40000)))
		[[ "$port" != 5432 && "$port" != 5433 && "$port" != 6433 && "$port" != 15433 ]] || continue
		pg_started=1
		if "$PG_BIN_DIR/pg_ctl" -D "$WORK/data" -l "$WORK/postgres.log" -o "-h 127.0.0.1 -p $port -k ''" -w start > "$WORK/start.log" 2>&1; then
			break
		fi
		[[ ! -f "$WORK/data/postmaster.pid" ]] || { cat "$WORK/start.log"; exit 1; }
		pg_started=0
	done
	[[ "$pg_started" -eq 1 ]] || { cat "$WORK/start.log"; exit 1; }
	printf '#!/usr/bin/env bash\nexec "%s/psql" "$@"\n' "$PG_BIN_DIR" > "$WORK/psql-wrapper"
elif command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1; then
	backend=docker
	# Docker allocates a free localhost port; retry the forbidden shared ports.
	for attempt in {1..30}; do
		if ! docker run -d --name "$container" --cidfile "$WORK/container-id" -e POSTGRES_USER=game -e POSTGRES_DB=postgres \
			-e "POSTGRES_PASSWORD=$admin_password" -p 127.0.0.1::5432 postgres:16.4-alpine > /dev/null; then
			echo 'FAIL - could not start private PostgreSQL container'; exit 1
		fi
		container_created=1
		port="$(docker port "$container" 5432/tcp)"
		port="${port##*:}"
		if [[ "$port" != 5432 && "$port" != 5433 && "$port" != 6433 && "$port" != 15433 ]]; then break; fi
		docker rm -f "$container" >/dev/null
		rm -f "$WORK/container-id"
		container_created=0
	done
	[[ "$container_created" -eq 1 ]] || { echo 'FAIL - no permitted private port'; exit 1; }
	ready=0
	for attempt in {1..60}; do
		if docker exec "$container" pg_isready -h 127.0.0.1 -U game -d postgres >/dev/null 2>&1; then ready=1; break; fi
		sleep 1
	done
	[[ "$ready" -eq 1 ]] || { echo 'FAIL - private PostgreSQL did not become ready'; exit 1; }
	# Client TCP authentication is enforced even though psql runs in the container.
	cat > "$WORK/psql-wrapper" <<'WRAPPER'
#!/usr/bin/env bash
exec docker exec -i -e PGUSER -e PGDATABASE -e PGPASSWORD -e PGCONNECT_TIMEOUT \
	-e GAMESTATE_ROLE_PASSWORD "$DEFAULT_ROLE_TEST_CONTAINER" psql -h 127.0.0.1 -p 5432 "$@"
WRAPPER
	export DEFAULT_ROLE_TEST_CONTAINER="$container"
else
	skip 'set PG_BIN_DIR to PostgreSQL 16 tools or start Docker for a private postgres:16.4-alpine'
fi
chmod +x "$WORK/psql-wrapper"
# The script splits PSQL_CMD on whitespace; use a safe relative wrapper path.
cd -- "$WORK"
export PSQL_CMD='bash ./psql-wrapper' PGHOST=127.0.0.1 PGPORT="$port" PGUSER=game PGDATABASE=postgres PGPASSWORD="$admin_password"
psql_test() { bash ./psql-wrapper -X -q -A -t -v ON_ERROR_STOP=1 "$@"; }
sql() { psql_test -c "$1"; }
truth() { [[ "$(sql "$1")" == t ]]; }
as_role() {
	local login="$1" database="$2" password="$3"; shift 3
	PGUSER="$login" PGDATABASE="$database" PGPASSWORD="$password" psql_test "$@"
}
refused() {
	if as_role "$@" -c 'SELECT 1' > "$WORK/denied.out" 2>&1; then return 1; fi
	# A failed password, missing database, or unavailable server is not isolation.
	grep -q 'permission denied for database' "$WORK/denied.out"
}

printf 'Private PostgreSQL backend: %s, localhost port: %s\n' "$backend" "$port"
sql 'CREATE DATABASE gamestate OWNER game'
export PGDATABASE=gamestate
psql_test < "$DB_DIR/init-gamestate.sql"
for migration in "$DB_DIR/migrations/gamestate/001_init.sql" "$DB_DIR/migrations/gamestate/002_characters.sql"; do psql_test < "$migration"; done
sql "INSERT INTO player_states (user_id, map_id, hp) VALUES ('legacy-one', 'map_01', 37), ('legacy-two', 'map_02', 42)"
# Exercise every owner-transfer category, including quoted names and overloads.
psql_test <<'SQL'
CREATE SCHEMA "legacy schema";
CREATE TABLE "legacy schema"."old table" (id serial PRIMARY KEY);
CREATE SEQUENCE "legacy schema"."standalone sequence";
CREATE VIEW "legacy schema"."old view" AS SELECT id FROM "legacy schema"."old table";
CREATE MATERIALIZED VIEW "legacy schema"."old materialized" AS SELECT id FROM "legacy schema"."old table";
CREATE TYPE "legacy schema".mood AS ENUM ('calm');
CREATE DOMAIN "legacy schema".positive AS integer CHECK (VALUE > 0);
CREATE TYPE "legacy schema".pair AS (x integer, y integer);
CREATE TYPE "legacy schema".legacy_range AS RANGE (subtype = integer);
CREATE FUNCTION "legacy schema".identity_value(integer) RETURNS integer LANGUAGE SQL AS 'SELECT $1';
CREATE FUNCTION "legacy schema".identity_value(text) RETURNS text LANGUAGE SQL AS 'SELECT $1';
CREATE PROCEDURE "legacy schema".noop() LANGUAGE SQL AS 'SELECT 1';
SQL
export PGDATABASE=postgres
GAMESTATE_ROLE_PASSWORD="$ember_password" bash "$DB_DIR/provision-gamestate.sh" ember --revoke-public-connect
as_role gamestate_ember gamestate_ember "$ember_password" -c "CREATE TABLE ember_secret (value text); INSERT INTO ember_secret VALUES ('ember-only')"

# Before the fix this is the failing seam: the default-role converger is absent.
if [[ ! -f "$DB_DIR/provision-default-role.sh" ]]; then
	bad 'default-role converger exists (MG-01 regression)'
	printf 'passed: %s failed: %s skipped: 0\n' "$PASS" "$FAIL"
	exit 1
fi
if ! bash "$DB_DIR/provision-default-role.sh"; then
	bad 'default-role converge succeeds'; printf 'passed: %s failed: %s skipped: 0\n' "$PASS" "$FAIL"; exit 1
fi
check 'default login has every restricted role attribute' truth "SELECT rolcanlogin AND NOT rolsuper AND NOT rolcreatedb AND NOT rolcreaterole AND NOT rolreplication AND NOT rolinherit AND NOT rolbypassrls AND rolconnlimit = 20 FROM pg_roles WHERE rolname = 'gamestate'"
check 'default role owns only gamestate' truth "SELECT array_agg(datname ORDER BY datname) = ARRAY['gamestate']::name[] FROM pg_database WHERE datdba = 'gamestate'::regrole"
check 'bootstrap superuser and its other database owners are unchanged' truth "SELECT rolsuper AND rolcreatedb AND rolcreaterole AND rolreplication AND rolinherit AND (SELECT datdba = 'game'::regrole FROM pg_database WHERE datname = 'postgres') AND (SELECT datdba = 'gamestate_ember'::regrole FROM pg_database WHERE datname = 'gamestate_ember') FROM pg_roles WHERE rolname = 'game'"
check 'default role has no role memberships' truth "SELECT NOT EXISTS (SELECT FROM pg_auth_members WHERE member = 'gamestate'::regrole)"
check 'default login cannot connect to ember' refused gamestate gamestate_ember "$GAMESTATE_ROLE_PASSWORD"
check 'default login cannot connect to postgres' refused gamestate postgres "$GAMESTATE_ROLE_PASSWORD"
check 'ember login cannot connect to default database' refused gamestate_ember gamestate "$ember_password"
export PGDATABASE=gamestate_ember
check 'default role lacks SELECT on ember table even without a connection gate' truth "SELECT NOT has_table_privilege('gamestate', 'public.ember_secret', 'SELECT')"
check 'ember data survives default-role convergence' truth "SELECT (SELECT value FROM ember_secret) = 'ember-only'"
export PGDATABASE=gamestate
check 'every non-system schema belongs to default role' truth "SELECT NOT EXISTS (SELECT FROM pg_namespace WHERE nspname !~ '^pg_' AND nspname <> 'information_schema' AND nspowner <> 'gamestate'::regrole)"
check 'every non-system relation belongs to default role' truth "SELECT NOT EXISTS (SELECT FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname !~ '^pg_' AND n.nspname <> 'information_schema' AND c.relowner <> 'gamestate'::regrole)"
check 'every non-system routine belongs to default role' truth "SELECT NOT EXISTS (SELECT FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname !~ '^pg_' AND n.nspname <> 'information_schema' AND p.proowner <> 'gamestate'::regrole)"
types_owned() {
	local mismatches
	mismatches="$(sql "SELECT format('%I.%I (kind=%s, owner=%I, relation=%s)', n.nspname, t.typname, t.typtype, r.rolname, coalesce(c.relkind::text, 'none')) FROM pg_type t JOIN pg_namespace n ON n.oid = t.typnamespace JOIN pg_roles r ON r.oid = t.typowner LEFT JOIN pg_class c ON c.oid = t.typrelid WHERE n.nspname !~ '^pg_' AND n.nspname <> 'information_schema' AND t.typowner <> 'gamestate'::regrole ORDER BY n.nspname, t.typname")" || return 1
	if [[ -n "$mismatches" ]]; then printf 'Unexpected type owners:\n%s\n' "$mismatches"; return 1; fi
}
check 'every non-system type belongs to default role' types_owned
check 'populated player states remain readable after upgrade' test "$(as_role gamestate gamestate "$GAMESTATE_ROLE_PASSWORD" -c "SELECT string_agg(user_id || ':' || hp, ',' ORDER BY user_id) FROM player_states")" = 'legacy-one:37,legacy-two:42'
check 'default login can update preserved data' as_role gamestate gamestate "$GAMESTATE_ROLE_PASSWORD" -c "UPDATE player_states SET hp = 38 WHERE user_id = 'legacy-one'"
check 'default login can insert new data' as_role gamestate gamestate "$GAMESTATE_ROLE_PASSWORD" -c "INSERT INTO player_states (user_id, hp) VALUES ('new-player', 99)"
check 'gameserver migration can create tables as default login' as_role gamestate gamestate "$GAMESTATE_ROLE_PASSWORD" -c "CREATE TABLE public.schema_migrations (version integer PRIMARY KEY, checksum text NOT NULL); INSERT INTO schema_migrations VALUES (1, 'migration-checksum')"
sql 'CREATE TABLE public.admin_migration (id serial PRIMARY KEY)'
check 'admin migration tables remain writable by default login' as_role gamestate gamestate "$GAMESTATE_ROLE_PASSWORD" -c 'INSERT INTO admin_migration DEFAULT VALUES; SELECT id FROM admin_migration'
check 'admin migration sequences remain usable by default login' truth "SELECT has_sequence_privilege('gamestate', 'admin_migration_id_seq', 'USAGE,SELECT,UPDATE')"
# Compare owners, memberships, ACLs and data across the second converge.
snapshot() {
	sql "SELECT 'db:' || datname || ':' || datdba || ':' || datconnlimit || ':' || coalesce(datacl::text, '') FROM pg_database ORDER BY datname"
	sql "SELECT 'role:' || rolname || ':' || rolcanlogin || ':' || rolsuper || ':' || rolcreatedb || ':' || rolcreaterole || ':' || rolreplication || ':' || rolinherit || ':' || rolbypassrls || ':' || rolconnlimit FROM pg_roles WHERE rolname IN ('game', 'gamestate', 'gamestate_ember') ORDER BY rolname"
	sql "SELECT 'member:' || roleid || ':' || member || ':' || admin_option FROM pg_auth_members ORDER BY roleid, member"
	sql "SELECT 'schema:' || nspname || ':' || nspowner || ':' || coalesce(nspacl::text, '') FROM pg_namespace WHERE nspname !~ '^pg_' AND nspname <> 'information_schema' ORDER BY nspname"
	sql "SELECT 'rel:' || c.oid || ':' || c.relowner || ':' || coalesce(c.relacl::text, '') FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname !~ '^pg_' AND n.nspname <> 'information_schema' ORDER BY c.oid"
	sql "SELECT 'routine:' || p.oid || ':' || p.proowner || ':' || coalesce(p.proacl::text, '') FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace WHERE n.nspname !~ '^pg_' AND n.nspname <> 'information_schema' ORDER BY p.oid"
	sql "SELECT 'type:' || t.oid || ':' || t.typowner || ':' || coalesce(t.typacl::text, '') FROM pg_type t JOIN pg_namespace n ON n.oid = t.typnamespace WHERE n.nspname !~ '^pg_' AND n.nspname <> 'information_schema' ORDER BY t.oid"
	# Catalog "char" has ambiguous concatenation overloads; serialize as text.
	sql "SELECT 'defaults:' || defaclrole::text || ':' || defaclnamespace::text || ':' || defaclobjtype::text || ':' || defaclacl::text FROM pg_default_acl ORDER BY oid"
	sql 'SELECT user_id, hp FROM player_states ORDER BY user_id'
}
# First converge also transfers objects added by the admin migration above.
export PGDATABASE=postgres
check 'converge handles admin-created migration objects' bash "$DB_DIR/provision-default-role.sh"
export PGDATABASE=gamestate
snapshot > "$WORK/before"
export PGDATABASE=postgres
check 'rerunning converge exits zero' bash "$DB_DIR/provision-default-role.sh"
export PGDATABASE=gamestate
snapshot > "$WORK/after"
check 'rerunning converge leaves owners, ACLs and data unchanged' cmp -s "$WORK/before" "$WORK/after"
check 'rerun keeps other databases inaccessible' refused gamestate gamestate_ember "$GAMESTATE_ROLE_PASSWORD"
check 'preserved data remains writable after rerun' as_role gamestate gamestate "$GAMESTATE_ROLE_PASSWORD" -c "UPDATE player_states SET hp = hp + 1 WHERE user_id = 'legacy-one'"
printf 'passed: %s failed: %s skipped: 0\n' "$PASS" "$FAIL"
[[ "$FAIL" -eq 0 ]]
