#!/usr/bin/env bash
# MG-01 upgrade of cuvara's existing gamestate database to a restricted login.
# Requires an administrative (superuser) connection. Idempotent: the local
# compose stack runs it as the one-shot postgres-game-roles service before the
# game servers; on k8s it is the manual job-default-role Job, and on a host it
# is human-run under the db-schema-write gate. Never run it from server startup.
#
# Usage: provision-default-role.sh [--connection-limit N] [--dry-run]
# GAMESTATE_ROLE_PASSWORD is read by psql 15+ with \getenv, never passed in argv.
# PSQL_CMD is a whitespace-separated command, default psql; use a wrapper script
# for arguments needing shell quoting. Do not put passwords in PSQL_CMD.
# The restricted login gamestate owns only the existing gamestate database and
# its non-system objects. No database is dropped, recreated, or copied. The
# bootstrap superuser is unchanged and remains for provisioning and migrations.
# PUBLIC CONNECT is revoked on every other reachable non-template database;
# roles relying on PUBLIC lose new connections there. Direct grants, ownership
# of another database, and any role membership fail the final isolation check.
# Admin-created public tables/sequences grant ALL to gamestate by default.
# Default 20 connections per role AND database matches provision-gamestate.sh.
# Exit codes: 0 success/dry run, 1 provisioning/isolation failure, 2 bad input.
set +x
set -euo pipefail
export LC_ALL=C

usage_error() { printf 'provision-default-role: %s\n' "$1" >&2; exit 2; }
connection_limit=20
dry_run=0
while [[ $# -gt 0 ]]; do
	case "$1" in
		--connection-limit)
			[[ $# -ge 2 ]] || usage_error '--connection-limit requires a positive integer'
			connection_limit="$2"
			shift 2
			;;
		--dry-run) dry_run=1; shift ;;
		*) usage_error 'unknown option or extra argument' ;;
	esac
done
# PostgreSQL stores connection limits as signed int32. Validate as text to
# avoid shell arithmetic overflow; refuse leading zeros like the sibling script.
[[ "$connection_limit" =~ ^[1-9][0-9]*$ ]] || usage_error '--connection-limit must be a positive integer'
if [[ ${#connection_limit} -gt 10 ]] ||
	{ [[ ${#connection_limit} -eq 10 ]] && [[ "$connection_limit" > 2147483647 ]]; }; then
	usage_error '--connection-limit exceeds the PostgreSQL maximum (2147483647)'
fi

emit_sql() {
	cat <<SQL
-- Stock PostgreSQL 16.4: stop if session logging cannot be disabled before
-- reading the password. psql startup files and input echo are disabled too.
SET log_statement = 'none';
SET log_min_error_statement = 'panic';
SET log_min_duration_statement = -1;
SET log_min_duration_sample = -1;
SET log_transaction_sample_rate = 0;
SET log_duration = off;
SET log_parameter_max_length_on_error = 0;
-- Never demote or change the password of the bootstrap login itself.
SELECT current_user <> 'gamestate' AND session_user <> 'gamestate' AS separate_admin
\gset
\if :separate_admin
\else
SELECT 'Provisioning failed: bootstrap/admin login must differ from gamestate';
SELECT 1 / 0;
\endif
SELECT 'CREATE ROLE gamestate LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOINHERIT NOBYPASSRLS CONNECTION LIMIT $connection_limit'
WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'gamestate')
\gexec
ALTER ROLE gamestate WITH LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOINHERIT NOBYPASSRLS CONNECTION LIMIT $connection_limit;
\getenv role_password GAMESTATE_ROLE_PASSWORD
ALTER ROLE gamestate PASSWORD :'role_password';
\unset role_password
-- An absent gamestate is an operator error: this upgrades an existing volume.
ALTER DATABASE gamestate OWNER TO gamestate;
ALTER DATABASE gamestate CONNECTION LIMIT $connection_limit;
REVOKE CONNECT, TEMPORARY ON DATABASE gamestate FROM PUBLIC;
GRANT CONNECT ON DATABASE gamestate TO gamestate;
-- Unconditional instance-wide hardening: affects roles relying on PUBLIC.
SELECT format('REVOKE CONNECT ON DATABASE %I FROM PUBLIC', datname)
FROM pg_database
WHERE NOT datistemplate AND datname <> 'gamestate'
  AND has_database_privilege('gamestate', datname, 'CONNECT')
\gexec
\connect gamestate
-- Work only inside gamestate. Quote identifiers from catalogs, including
-- legacy schemas/object names; skip PostgreSQL's reserved system schemas.
SELECT format('ALTER SCHEMA %I OWNER TO gamestate', nspname)
FROM pg_namespace
WHERE nspname !~ '^pg_' AND nspname <> 'information_schema'
  AND nspowner <> 'gamestate'::regrole
ORDER BY nspname
\gexec
-- Table owner changes also transfer their indexes, row types and owned
-- sequences. Process standalone sequences last to satisfy OWNED BY checks.
SELECT format('ALTER %s %I.%I OWNER TO gamestate',
  CASE c.relkind
    WHEN 'S' THEN 'SEQUENCE' WHEN 'v' THEN 'VIEW'
    WHEN 'm' THEN 'MATERIALIZED VIEW' WHEN 'f' THEN 'FOREIGN TABLE'
    ELSE 'TABLE' END, n.nspname, c.relname)
FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE n.nspname !~ '^pg_' AND n.nspname <> 'information_schema'
  AND c.relkind IN ('r', 'p', 'S', 'v', 'm', 'f')
  AND c.relowner <> 'gamestate'::regrole
ORDER BY (c.relkind = 'S'), n.nspname, c.relname
\gexec
-- Array types and relation row types follow their owning type/relation;
-- PostgreSQL forbids transferring them independently. PostgreSQL 16 does
-- not transfer a range's multirange owner: transfer it explicitly here,
-- which also transfers its array type, rather than leaving bootstrap owners.
-- Standalone composite types have relkind c and need explicit transfer.
SELECT format('ALTER %s %I.%I OWNER TO gamestate',
  CASE WHEN t.typtype = 'd' THEN 'DOMAIN' ELSE 'TYPE' END,
  n.nspname, t.typname)
FROM pg_type t JOIN pg_namespace n ON n.oid = t.typnamespace
LEFT JOIN pg_class c ON c.oid = t.typrelid
WHERE n.nspname !~ '^pg_' AND n.nspname <> 'information_schema'
  AND (t.typrelid = 0 OR c.relkind = 'c')
  AND NOT EXISTS (SELECT FROM pg_type base WHERE base.typarray = t.oid)
  AND t.typowner <> 'gamestate'::regrole
ORDER BY n.nspname, t.typname
\gexec
SELECT format('ALTER %s %I.%I(%s) OWNER TO gamestate',
  CASE p.prokind WHEN 'p' THEN 'PROCEDURE' WHEN 'a' THEN 'AGGREGATE' ELSE 'FUNCTION' END,
  n.nspname, p.proname, pg_get_function_identity_arguments(p.oid))
FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
WHERE n.nspname !~ '^pg_' AND n.nspname <> 'information_schema'
  AND p.proowner <> 'gamestate'::regrole
ORDER BY n.nspname, p.proname, p.oid
\gexec
-- Admin-run migrations must not lock the app role out of future objects.
SELECT format('ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA public GRANT ALL ON TABLES TO gamestate', current_user)
\gexec
SELECT format('ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA public GRANT ALL ON SEQUENCES TO gamestate', current_user)
\gexec
-- Check effective privileges, including explicit grants and memberships.
SELECT NOT EXISTS (
  SELECT FROM pg_database
  WHERE NOT datistemplate AND datname <> 'gamestate'
    AND has_database_privilege('gamestate', datname, 'CONNECT')
) AND NOT EXISTS (
  SELECT FROM pg_auth_members WHERE member = 'gamestate'::regrole
) AS isolated
\gset
\if :isolated
\else
SELECT format('Isolation failed: role gamestate can CONNECT to database %I', datname)
FROM pg_database
WHERE NOT datistemplate AND datname <> 'gamestate'
  AND has_database_privilege('gamestate', datname, 'CONNECT')
ORDER BY datname;
SELECT format('Isolation failed: role gamestate is a member of role %I', r.rolname)
FROM pg_auth_members m JOIN pg_roles r ON r.oid = m.roleid
WHERE m.member = 'gamestate'::regrole
ORDER BY r.rolname;
-- Deliberate error: ON_ERROR_STOP gives a nonzero exit on supported psql.
SELECT 1 / 0;
\endif
SQL
}

dsn_shape() {
	printf 'DSN shape: postgres://gamestate:<password>@<host>:5432/gamestate?sslmode=...\n'
	printf "Store the completed DSN in cuvara's game-db-url Secret; never commit it.\n"
}
if [[ "$dry_run" -eq 1 ]]; then
	emit_sql
	dsn_shape
	exit 0
fi
[[ -n "${GAMESTATE_ROLE_PASSWORD:+present}" ]] || usage_error 'GAMESTATE_ROLE_PASSWORD must be exported and nonempty (or use --dry-run)'
read -r -a psql_cmd <<< "${PSQL_CMD:-psql}"
[[ ${#psql_cmd[@]} -gt 0 ]] || usage_error 'PSQL_CMD must name a command'
# Server diagnostics can include interpolated password SQL: suppress stderr.
if ! emit_sql | "${psql_cmd[@]}" -X -q -A -t \
	-v ON_ERROR_STOP=1 -v ECHO=none -v ECHO_HIDDEN=off 2>/dev/null; then
	printf 'Default-role provisioning failed; check administrative access and any isolation diagnostics above.\n' >&2
	printf 'Direct database grants, ownership and role memberships require operator review.\n' >&2
	exit 1
fi
dsn_shape
