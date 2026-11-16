#!/usr/bin/env bash
# Offline default-role provisioning contracts: no database, Docker or network.
# Run: RPG_FACTORY_WORKSPACE=C:/Workspaces/UnityIndie bash backend/deploy/db/tests/default_role_test.sh
set -uo pipefail
HERE="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
DB_DIR="$(cd -- "$HERE/.." && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
PASS=0 FAIL=0
ok() { PASS=$((PASS + 1)); echo "ok   - $1"; }
bad() { FAIL=$((FAIL + 1)); echo "FAIL - $1"; }
check() { local desc="$1"; shift; if "$@"; then ok "$desc"; else bad "$desc"; fi; }
not_contains() { local needle="$1"; shift; ! grep -Fq -- "$needle" "$@"; }

cat > "$WORK/psql-stub.sh" <<'STUB'
#!/usr/bin/env bash
printf '%s\n' "$@" >> "$PROVISION_RECORD/argv"
cat > "$PROVISION_RECORD/stdin"
case "${PROVISION_STUB_MODE:-ok}" in
	password-error)
		printf 'ERROR: ALTER ROLE PASSWORD %s\n' "$GAMESTATE_ROLE_PASSWORD" >&2
		exit 3 ;;
	isolation-error)
		printf 'Isolation failed: role gamestate can CONNECT to database gamestate_ember\n'
		exit 3 ;;
	membership-error)
		printf 'Isolation failed: role gamestate is a member of role unsafe_role\n'
		exit 3 ;;
esac
STUB
export PSQL_CMD="bash $WORK/psql-stub.sh" PROVISION_RECORD="$WORK"
unset GAMESTATE_ROLE_PASSWORD PROVISION_STUB_MODE
run() {
	rm -f "$WORK/argv" "$WORK/stdin"
	STATUS=0
	bash "$DB_DIR/provision-default-role.sh" "$@" > "$WORK/stdout" 2> "$WORK/stderr" || STATUS=$?
}
refused() { [[ "$STATUS" -eq 2 && ! -e "$WORK/argv" ]]; }
for limit in 0 -1 1.5 abc '' 01 2147483648 99999999999999999999 '20; SELECT 1'; do
	run --connection-limit "$limit"
	check "refuses invalid connection limit [$limit] without psql" refused
done
run --connection-limit
check 'missing connection limit is refused without psql' refused
for arg in --unknown cuvara ember --revoke-public-connect; do
	run "$arg"
	check "unknown argument [$arg] is refused without psql" refused
done
run --dry-run extra
check 'dry run still refuses extra arguments without psql' refused

run --dry-run
check 'dry run succeeds without a password' test "$STATUS" -eq 0
check 'dry run never invokes psql' test ! -e "$WORK/argv"
check 'dry run creates a restricted default login' grep -Fq 'CREATE ROLE gamestate LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOINHERIT NOBYPASSRLS CONNECTION LIMIT 20' "$WORK/stdout"
check 'role creation is conditional' grep -Fq "WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'gamestate')" "$WORK/stdout"
check 'bootstrap login cannot be the application role being altered' grep -Fq "current_user <> 'gamestate' AND session_user <> 'gamestate' AS separate_admin" "$WORK/stdout"
check 'existing role attributes converge' grep -Fq 'ALTER ROLE gamestate WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOINHERIT NOBYPASSRLS CONNECTION LIMIT 20' "$WORK/stdout"
check 'default database ownership transfers in place' grep -Fq 'ALTER DATABASE gamestate OWNER TO gamestate;' "$WORK/stdout"
check 'database gets default connection budget' grep -Fq 'ALTER DATABASE gamestate CONNECTION LIMIT 20;' "$WORK/stdout"
check 'PUBLIC loses CONNECT and TEMPORARY on default database' grep -Fq 'REVOKE CONNECT, TEMPORARY ON DATABASE gamestate FROM PUBLIC;' "$WORK/stdout"
check 'default login gets CONNECT on default database' grep -Fq 'GRANT CONNECT ON DATABASE gamestate TO gamestate;' "$WORK/stdout"
check 'other reachable databases lose PUBLIC CONNECT unconditionally' grep -Fq "SELECT format('REVOKE CONNECT ON DATABASE %I FROM PUBLIC', datname)" "$WORK/stdout"
check 'revocation and isolation inspect effective database privileges' grep -Fq "has_database_privilege('gamestate', datname, 'CONNECT')" "$WORK/stdout"
check 'other-database query excludes own and template databases' grep -Fq "NOT datistemplate AND datname <> 'gamestate'" "$WORK/stdout"
check 'owner transfer connects only to gamestate' grep -Fxq '\connect gamestate' "$WORK/stdout"
check 'schemas are transferred with quoted identifiers' grep -Fq "format('ALTER SCHEMA %I OWNER TO gamestate', nspname)" "$WORK/stdout"
check 'system schemas are excluded' grep -Fq "nspname !~ '^pg_' AND nspname <> 'information_schema'" "$WORK/stdout"
check 'table and sequence owner transfers are included' grep -Fq "AND c.relkind IN ('r', 'p', 'S', 'v', 'm', 'f')" "$WORK/stdout"
check 'sequence command is emitted' grep -Fq "WHEN 'S' THEN 'SEQUENCE'" "$WORK/stdout"
check 'view command is emitted' grep -Fq "WHEN 'v' THEN 'VIEW'" "$WORK/stdout"
check 'materialized view command is emitted' grep -Fq "WHEN 'm' THEN 'MATERIALIZED VIEW'" "$WORK/stdout"
check 'sequences move after owning tables' grep -Fq "ORDER BY (c.relkind = 'S')" "$WORK/stdout"
check 'routines are transferred with quoted names and full signatures' grep -Fq 'n.nspname, p.proname, pg_get_function_identity_arguments(p.oid)' "$WORK/stdout"
check 'functions and procedures are included' grep -Fq "WHEN 'p' THEN 'PROCEDURE' WHEN 'a' THEN 'AGGREGATE' ELSE 'FUNCTION'" "$WORK/stdout"
check 'user types and domains are transferred' grep -Fq "CASE WHEN t.typtype = 'd' THEN 'DOMAIN' ELSE 'TYPE' END" "$WORK/stdout"
check 'standalone composite types are included' grep -Fq "AND (t.typrelid = 0 OR c.relkind = 'c')" "$WORK/stdout"
check 'array types follow base types instead of independent transfer' grep -Fq 'NOT EXISTS (SELECT FROM pg_type base WHERE base.typarray = t.oid)' "$WORK/stdout"
check 'multirange types are explicitly transferred instead of skipped' not_contains "AND t.typtype <> 'm'" "$WORK/stdout"
check 'already-owned objects are skipped' grep -Fq "AND c.relowner <> 'gamestate'::regrole" "$WORK/stdout"
check 'admin migration default table grants name current bootstrap user' grep -Fq "format('ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA public GRANT ALL ON TABLES TO gamestate', current_user)" "$WORK/stdout"
check 'admin migration default sequence grants name current bootstrap user' grep -Fq "format('ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA public GRANT ALL ON SEQUENCES TO gamestate', current_user)" "$WORK/stdout"
check 'isolation rejects any default-role membership' grep -Fq "SELECT FROM pg_auth_members WHERE member = 'gamestate'::regrole" "$WORK/stdout"
check 'isolation reports membership diagnostics' grep -Fq 'Isolation failed: role gamestate is a member of role %I' "$WORK/stdout"
check 'isolation check deliberately fails psql on unsafe access' grep -Fq 'SELECT 1 / 0;' "$WORK/stdout"
check 'script never globally reassigns bootstrap-owned objects' not_contains 'REASSIGN OWNED' "$WORK/stdout"
check 'script never recreates the populated default database' not_contains 'CREATE DATABASE' "$WORK/stdout"
check 'script never drops a database' not_contains 'DROP DATABASE' "$WORK/stdout"
check 'script never drops a role' not_contains 'DROP ROLE' "$WORK/stdout"

run --dry-run --connection-limit 2147483647
check 'PostgreSQL maximum connection limit is accepted' test "$STATUS" -eq 0
run --dry-run --connection-limit 1
check 'smallest positive connection limit is accepted' test "$STATUS" -eq 0
run
check 'missing role password is refused without psql' refused
export GAMESTATE_ROLE_PASSWORD=''
run
check 'empty role password is refused without psql' refused
export GAMESTATE_ROLE_PASSWORD="test-pw-xyz ' \$(ignored)"
run --dry-run --connection-limit 7
check 'custom budget dry run succeeds with password set' test "$STATUS" -eq 0
check 'custom role budget is emitted' grep -Fq 'NOBYPASSRLS CONNECTION LIMIT 7' "$WORK/stdout"
check 'custom database budget is emitted' grep -Fq 'ALTER DATABASE gamestate CONNECTION LIMIT 7;' "$WORK/stdout"
check 'password never appears in dry run output' not_contains "$GAMESTATE_ROLE_PASSWORD" "$WORK/stdout" "$WORK/stderr"

run --connection-limit 7
check 'stub psql execution succeeds' test "$STATUS" -eq 0
check 'SQL reaches psql on stdin' grep -Fq 'ALTER DATABASE gamestate OWNER TO gamestate;' "$WORK/stdin"
check 'password is read inside psql from the environment' grep -Fq '\getenv role_password GAMESTATE_ROLE_PASSWORD' "$WORK/stdin"
check 'password interpolation is performed only by psql' grep -Fq "PASSWORD :'role_password'" "$WORK/stdin"
check 'password variable is cleared' grep -Fxq '\unset role_password' "$WORK/stdin"
check 'psql ignores startup files' grep -Fxq -- '-X' "$WORK/argv"
for arg in -q -A -t ON_ERROR_STOP=1 ECHO=none ECHO_HIDDEN=off; do
	check "psql receives required safety argument [$arg]" grep -Fxq -- "$arg" "$WORK/argv"
done
for setting in "log_statement = 'none'" "log_min_error_statement = 'panic'" 'log_min_duration_statement = -1' 'log_min_duration_sample = -1' 'log_transaction_sample_rate = 0' 'log_duration = off' 'log_parameter_max_length_on_error = 0'; do
	check "session logging is disabled [$setting]" grep -Fq "SET $setting;" "$WORK/stdin"
done
check 'all logging settings precede the password read' awk '/^SET log_/ { last_setting = NR } /^\\getenv role_password/ { password_read = NR } END { exit !(last_setting > 0 && password_read > last_setting) }' "$WORK/stdin"
check 'password is absent from stdin, argv, stdout and stderr' not_contains "$GAMESTATE_ROLE_PASSWORD" "$WORK/stdin" "$WORK/argv" "$WORK/stdout" "$WORK/stderr"
check 'default DSN uses the restricted role' grep -Fq 'postgres://gamestate:<password>@<host>:5432/gamestate?sslmode=...' "$WORK/stdout"
export PSQL_CMD=' '
run
check 'empty whitespace-only psql command is refused' refused
export PSQL_CMD="bash $WORK/psql-stub.sh"

export PROVISION_STUB_MODE=password-error
run
check 'psql password error returns provisioning failure' test "$STATUS" -eq 1
check 'psql stderr cannot disclose the interpolated password' not_contains "$GAMESTATE_ROLE_PASSWORD" "$WORK/stdout" "$WORK/stderr"
check 'psql error details are suppressed' not_contains 'ERROR: ALTER ROLE' "$WORK/stderr"
export PROVISION_STUB_MODE=isolation-error
run
check 'cross-game isolation failure exits 1' test "$STATUS" -eq 1
check 'isolation failure names the offending database' grep -Fq 'can CONNECT to database gamestate_ember' "$WORK/stdout"
check 'isolation failure does not print a success DSN' not_contains 'DSN shape:' "$WORK/stdout"
export PROVISION_STUB_MODE=membership-error
run
check 'membership isolation failure exits 1' test "$STATUS" -eq 1
check 'membership failure names the offending role' grep -Fq 'is a member of role unsafe_role' "$WORK/stdout"
printf '\npassed: %s failed: %s\n' "$PASS" "$FAIL"
[[ "$FAIL" -eq 0 ]]
