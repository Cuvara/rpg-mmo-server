#!/usr/bin/env bash
# Human-run ADR-33 D5 provisioning on the shared game PostgreSQL instance.
# Requires the db-schema-write gate and an administrative (superuser) connection.
# Never invoke this automatically during deploy or server startup.
#
# Usage: provision-gamestate.sh <game> [--connection-limit N] [--dry-run]
#                              [--revoke-public-connect]
# GAMESTATE_ROLE_PASSWORD is read by psql 15+ with \getenv, never passed in argv.
# PSQL_CMD is a whitespace-separated command, default psql; use a wrapper script
# for arguments needing shell quoting. Do not put passwords in PSQL_CMD.
# --revoke-public-connect removes PUBLIC CONNECT on every other non-template
# database currently accessible to this role. Roles relying on PUBLIC lose new
# connections there; owners and superusers retain access. Provision cuvara's
# existing gamestate database with provision-default-role.sh instead. No
# privileges are granted on that database by this per-game script.
# The flag is normally required: on stock PostgreSQL, PUBLIC holds CONNECT on
# postgres and gamestate, so a run without it creates the role and database and
# then fails the isolation check (exit 1). Rerunning with the flag converges.
# Default 20 connections per role AND database bounds each game's consumption
# of the shared instance's connection budget (ADR-33 D7); CPU remains shared.
# Exit codes: 0 success/dry run, 1 provisioning/isolation failure, 2 bad input.
set +x
set -euo pipefail
export LC_ALL=C

usage_error() { printf 'provision-gamestate: %s\n' "$1" >&2; exit 2; }

[[ $# -gt 0 ]] || usage_error 'a game id is required'
game="$1"
shift
[[ "$game" =~ ^[a-z][a-z0-9]{1,23}$ ]] ||
	usage_error 'invalid game id: expected ^[a-z][a-z0-9]{1,23}$'
case "$game" in
	gs|map|dungeon) usage_error "reserved game id: $game" ;;
	cuvara) usage_error 'cuvara is the default game; use provision-default-role.sh for its existing gamestate database' ;;
esac

connection_limit=20
dry_run=0
revoke_public=0
while [[ $# -gt 0 ]]; do
	case "$1" in
		--connection-limit)
			[[ $# -ge 2 ]] || usage_error '--connection-limit requires a positive integer'
			connection_limit="$2"
			shift 2
			;;
		--dry-run) dry_run=1; shift ;;
		--revoke-public-connect) revoke_public=1; shift ;;
		*) usage_error 'unknown option or extra argument' ;;
	esac
done
# PostgreSQL stores connection limits as signed int32; avoid shell arithmetic on
# untrusted/overflowing integers. Leading zeros are refused for clarity.
[[ "$connection_limit" =~ ^[1-9][0-9]*$ ]] || usage_error '--connection-limit must be a positive integer'
if [[ ${#connection_limit} -gt 10 ]] ||
	{ [[ ${#connection_limit} -eq 10 ]] && [[ "$connection_limit" > 2147483647 ]]; }; then
	usage_error '--connection-limit exceeds the PostgreSQL maximum (2147483647)'
fi
db="gamestate_$game"

emit_sql() {
	cat <<SQL
-- Stock PostgreSQL 16.4: disable session statement/error/duration logging before
-- reading the password. If these superuser settings fail, ON_ERROR_STOP stops
-- before any password-bearing statement. psql startup files/echo are disabled.
SET log_statement = 'none';
SET log_min_error_statement = 'panic';
SET log_min_duration_statement = -1;
SET log_min_duration_sample = -1;
SET log_transaction_sample_rate = 0;
SET log_duration = off;
SET log_parameter_max_length_on_error = 0;
SELECT 'CREATE ROLE $db LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOINHERIT CONNECTION LIMIT $connection_limit'
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = '$db')
\gexec
ALTER ROLE $db WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOINHERIT CONNECTION LIMIT $connection_limit;
\getenv role_password GAMESTATE_ROLE_PASSWORD
ALTER ROLE $db PASSWORD :'role_password';
\unset role_password
-- CREATE DATABASE must run outside a transaction/DO block.
SELECT 'CREATE DATABASE $db OWNER $db'
WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = '$db')
\gexec
ALTER DATABASE $db OWNER TO $db;
ALTER DATABASE $db CONNECTION LIMIT $connection_limit;
REVOKE CONNECT, TEMPORARY ON DATABASE $db FROM PUBLIC;
GRANT CONNECT ON DATABASE $db TO $db;
SQL
	if [[ "$revoke_public" -eq 1 ]]; then
		cat <<SQL
-- Explicit instance-wide hardening: affects other roles relying on PUBLIC.
SELECT format('REVOKE CONNECT ON DATABASE %I FROM PUBLIC', datname)
FROM pg_database
WHERE NOT datistemplate AND datname <> '$db'
  AND has_database_privilege('$db', datname, 'CONNECT')
\gexec
SQL
	fi
	cat <<SQL
-- Check effective privileges, including explicit grants and role memberships.
SELECT NOT EXISTS (
  SELECT FROM pg_database
  WHERE NOT datistemplate AND datname <> '$db'
    AND has_database_privilege('$db', datname, 'CONNECT')
) AS isolated
\gset
\if :isolated
\else
SELECT format('Isolation failed: role $db can CONNECT to database %I', datname)
FROM pg_database
WHERE NOT datistemplate AND datname <> '$db'
  AND has_database_privilege('$db', datname, 'CONNECT')
ORDER BY datname;
-- Deliberate error: ON_ERROR_STOP gives a nonzero exit on every supported psql.
SELECT 1 / 0;
\endif
SQL
}

dsn_shape() {
	printf 'DSN shape: postgres://%s:<password>@<host>:5432/%s?sslmode=...\n' "$db" "$db"
	printf "Store the completed DSN in that game's fleet Secret (key game-db-url); never commit it.\n"
}

if [[ "$dry_run" -eq 1 ]]; then
	emit_sql
	dsn_shape
	exit 0
fi
[[ -n "${GAMESTATE_ROLE_PASSWORD:+present}" ]] || usage_error 'GAMESTATE_ROLE_PASSWORD must be exported and nonempty (or use --dry-run)'
read -r -a psql_cmd <<< "${PSQL_CMD:-psql}"
[[ ${#psql_cmd[@]} -gt 0 ]] || usage_error 'PSQL_CMD must name a command'

# Suppress psql stderr: server error diagnostics can include interpolated SQL.
# stdout contains only non-secret isolation diagnostics; -q suppresses command
# tags, ECHO=none hides input, and -X prevents startup files enabling SQL echo.
if ! emit_sql | "${psql_cmd[@]}" -X -q -A -t \
	-v ON_ERROR_STOP=1 -v ECHO=none -v ECHO_HIDDEN=off 2>/dev/null; then
	printf 'Provisioning failed; check administrative access and any isolation diagnostics above.\n' >&2
	printf 'PUBLIC CONNECT may require --revoke-public-connect; direct grants/memberships require operator review.\n' >&2
	exit 1
fi
dsn_shape
