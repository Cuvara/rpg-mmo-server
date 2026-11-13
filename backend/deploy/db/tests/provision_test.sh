#!/usr/bin/env bash
# Offline provisioning contract tests. No database, docker, cluster or network.
# Run: RPG_FACTORY_WORKSPACE=C:/Workspaces/UnityIndie bash backend/deploy/db/tests/provision_test.sh
set -uo pipefail
HERE="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
DB_DIR="$(cd -- "$HERE/.." && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
PASS=0
FAIL=0
ok() { PASS=$((PASS + 1)); echo "ok   - $1"; }
bad() { FAIL=$((FAIL + 1)); echo "FAIL - $1"; }
check() {
	local desc="$1"; shift
	if "$@"; then ok "$desc"; else bad "$desc"; fi
}
not_contains() { local needle="$1"; shift; ! grep -Fq -- "$needle" "$@"; }

cat > "$WORK/psql-stub.sh" <<'STUB'
#!/usr/bin/env bash
printf '%s\n' "$@" >> "$PROVISION_RECORD/argv"
cat > "$PROVISION_RECORD/stdin"
# Simulate a PostgreSQL error containing interpolated SQL on stderr.
if [[ "${PROVISION_STUB_MODE:-ok}" = password-error ]]; then
	printf 'ERROR: ALTER ROLE PASSWORD %s\n' "$GAMESTATE_ROLE_PASSWORD" >&2
	exit 3
fi
if [[ "${PROVISION_STUB_MODE:-ok}" = isolation-error ]]; then
	printf 'Isolation failed: role gamestate_foo can CONNECT to database postgres\n'
	exit 3
fi
STUB
export PSQL_CMD="bash $WORK/psql-stub.sh" PROVISION_RECORD="$WORK"
unset GAMESTATE_ROLE_PASSWORD PROVISION_STUB_MODE

run() {
	rm -f "$WORK/argv" "$WORK/stdin"
	STATUS=0
	bash "$DB_DIR/provision-gamestate.sh" "$@" > "$WORK/stdout" 2> "$WORK/stderr" || STATUS=$?
}
refused() { [[ "$STATUS" -eq 2 && ! -e "$WORK/argv" ]]; }
for game in Foo 1foo abcdefghijklmnopqrstuvwxy foo-bar foo_bar '' gs map dungeon cuvara; do
	run "$game"
	check "refuses invalid/reserved/default id [$game] without psql" refused
	case "$game" in
		gs|map|dungeon) check "reserved id [$game] has a named reason" grep -q "reserved game id: $game" "$WORK/stderr" ;;
	esac
done
check 'default game refusal names its existing database and user' \
	grep -q 'keeps the existing gamestate database and its existing user' "$WORK/stderr"
run
check 'missing game id is refused without psql' refused
for limit in 0 -1 1.5 abc '' 01 2147483648 99999999999999999999; do
	run foo --connection-limit "$limit"
	check "refuses invalid connection limit [$limit] without psql" refused
done
run foo --connection-limit
check 'missing connection limit is refused without psql' refused
run foo --unknown
check 'unknown option is refused without psql' refused

run foo --dry-run
check 'dry run succeeds without a password or psql' test "$STATUS" -eq 0
check 'dry run never invokes psql' test ! -e "$WORK/argv"
check 'dry run shows database with its own owner' grep -q 'CREATE DATABASE gamestate_foo OWNER gamestate_foo' "$WORK/stdout"
check 'dry run creates a restricted login role' grep -q 'CREATE ROLE gamestate_foo LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOINHERIT CONNECTION LIMIT 20' "$WORK/stdout"
check 'dry run shows database connection budget' grep -q 'ALTER DATABASE gamestate_foo CONNECTION LIMIT 20' "$WORK/stdout"
check 'dry run revokes PUBLIC on its own database' grep -q 'REVOKE CONNECT, TEMPORARY ON DATABASE gamestate_foo FROM PUBLIC' "$WORK/stdout"
check 'dry run reads password inside psql' grep -Fq '\getenv role_password GAMESTATE_ROLE_PASSWORD' "$WORK/stdout"
check 'dry run checks effective access to every other non-template database' grep -q "has_database_privilege('gamestate_foo', datname, 'CONNECT')" "$WORK/stdout"
check 'dry run excludes own and template databases from isolation check' grep -q "NOT datistemplate AND datname <> 'gamestate_foo'" "$WORK/stdout"
check 'default mode does not revoke PUBLIC on other databases' not_contains "SELECT format('REVOKE CONNECT" "$WORK/stdout"

run ab --dry-run --connection-limit 2147483647
check 'shortest valid id and PostgreSQL maximum limit are accepted' test "$STATUS" -eq 0
run abcdefghijklmnopqrstuvwx --dry-run
check '24-character game id is accepted' test "$STATUS" -eq 0

run foo
check 'missing role password is refused without psql' refused
export GAMESTATE_ROLE_PASSWORD=''
run foo
check 'empty role password is refused without psql' refused
export GAMESTATE_ROLE_PASSWORD=test-pw-xyz
run foo --dry-run --connection-limit 7 --revoke-public-connect
check 'custom budget and hardening dry run succeeds' test "$STATUS" -eq 0
check 'dry run still never invokes psql with a password set' test ! -e "$WORK/argv"
check 'custom budget is applied to database' grep -q 'ALTER DATABASE gamestate_foo CONNECTION LIMIT 7' "$WORK/stdout"
check 'opt-in revocation quotes database identifiers with format' grep -Fq "SELECT format('REVOKE CONNECT ON DATABASE %I FROM PUBLIC', datname)" "$WORK/stdout"
check 'dry run never contains the password' not_contains test-pw-xyz "$WORK/stdout" "$WORK/stderr"

run foo --connection-limit 7 --revoke-public-connect
check 'fake psql execution succeeds' test "$STATUS" -eq 0
check 'SQL reaches psql on stdin' grep -q 'CREATE DATABASE gamestate_foo OWNER gamestate_foo' "$WORK/stdin"
check 'role budget converges on rerun' grep -q 'ALTER ROLE gamestate_foo WITH LOGIN.*CONNECTION LIMIT 7' "$WORK/stdin"
check 'role creation is conditional' grep -q "WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'gamestate_foo')" "$WORK/stdin"
check 'database creation is conditional' grep -q "WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'gamestate_foo')" "$WORK/stdin"
check 'password is interpolated only by psql' grep -Fq "PASSWORD :'role_password'" "$WORK/stdin"
check 'psql stops on SQL errors' grep -qx 'ON_ERROR_STOP=1' "$WORK/argv"
check 'psql ignores startup files' grep -qx -- '-X' "$WORK/argv"
check 'psql input echo is disabled' grep -qx 'ECHO=none' "$WORK/argv"
check 'SQL, argv, stdout and stderr contain no password' not_contains test-pw-xyz "$WORK/stdin" "$WORK/argv" "$WORK/stdout" "$WORK/stderr"
check 'output gives only the fleet Secret DSN shape' grep -Fq 'postgres://gamestate_foo:<password>@<host>:5432/gamestate_foo?sslmode=...' "$WORK/stdout"

export PROVISION_STUB_MODE=password-error
run foo
check 'psql failure propagates' test "$STATUS" -eq 1
check 'SQL error diagnostics cannot disclose password' not_contains test-pw-xyz "$WORK/stdout" "$WORK/stderr"
export PROVISION_STUB_MODE=isolation-error
run foo
check 'isolation failure propagates' test "$STATUS" -eq 1
check 'isolation failure names offending database' grep -q 'can CONNECT to database postgres' "$WORK/stdout"

echo
echo "passed: $PASS failed: $FAIL"
[[ "$FAIL" -eq 0 ]]
