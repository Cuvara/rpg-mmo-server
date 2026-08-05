#!/usr/bin/env bash
#
# setup-github-env.sh — create a GitHub Environment and populate every secret
# and variable the CD pipeline reads.
#
# This is the scripted form of backend/deploy/docs/VPS-SETUP.md §2. The
# authoritative list of names lives in .github/workflows/cd.yml; keep the two in
# sync (the doc shows the grep that proves it).
#
# Usage:
#   scripts/setup-github-env.sh <env-name> [flags]
#
#   scripts/setup-github-env.sh staging --generate            # strong secrets, invented for you
#   scripts/setup-github-env.sh dev                           # prompts, dev defaults offered
#   scripts/setup-github-env.sh production --generate --dry-run
#
# Secrets come from (first wins): a flag, the environment, an interactive
# prompt, or --generate. They are never echoed and never written to disk; only
# `gh secret set` sees them, on stdin.
#
# Flags — secrets:
#   --jwt-secret V            --postgres-password V     --nakama-console-password V
#   --grafana-admin-password V --nakama-server-key V    --redis-password V
#   --generate                invent strong values for any secret not supplied
#
# Flags — variables (each maps 1:1 to a vars.* read by cd.yml):
#   --deploy-dir V            RPG_DEPLOY_DIR            (default /opt/rpg-mmo)
#   --deploy-mode V           DEPLOY_MODE               host | containers
#   --gateway-addr V          GATEWAY_ADDR              (default :8000)
#   --gameserver-addr V       GAMESERVER_ADDR           (default :9200)
#   --gameserver-public-addr V GAMESERVER_PUBLIC_ADDR   host:port CLIENTS dial
#   --map-id V                GAMESERVER_MAP_ID         (default map_01)
#   --redis-addr V            REDIS_ADDR                (default localhost:6379)
#   --game-db-url V           GAME_DB_URL               empty = in-memory store
#   --nakama-version V        NAKAMA_VERSION            (default 3.40.0)
#   --postgres-db V / --postgres-user V / --nakama-console-user V
#   --monitoring true|false   MONITORING_ENABLED        (default true)
#   --grafana-bind V          GRAFANA_BIND              (default 127.0.0.1 here)
#   --grafana-port V          GRAFANA_PORT              (default 3000)
#
# Other flags:
#   --repo owner/name         default: the repo of the current directory
#   --non-interactive         never prompt; missing required secret = error
#   --dry-run                 print every gh command, execute nothing
#   -h | --help
#
# PRODUCTION GUARDRAILS: when <env-name> is "production" (or --strict is
# passed), every secret must be >= 32 characters and must not contain a known
# placeholder ("dev-secret", "localdev", "password", "changeme", "admin",
# "defaultkey"). The script refuses rather than publishing a weak secret to a
# production environment. Use --generate to satisfy it instantly.
#
set -euo pipefail

# ------------------------------------------------------------------ defaults
ENV_NAME=""
REPO=""
DRY_RUN=0
INTERACTIVE=1
GENERATE=0
STRICT=0

JWT_SECRET="${JWT_SECRET:-}"
POSTGRES_PASSWORD="${POSTGRES_PASSWORD:-}"
NAKAMA_CONSOLE_PASSWORD="${NAKAMA_CONSOLE_PASSWORD:-}"
GRAFANA_ADMIN_PASSWORD="${GRAFANA_ADMIN_PASSWORD:-}"
NAKAMA_SERVER_KEY="${NAKAMA_SERVER_KEY:-}"
REDIS_PASSWORD="${REDIS_PASSWORD:-}"

RPG_DEPLOY_DIR="${RPG_DEPLOY_DIR:-/opt/rpg-mmo}"
DEPLOY_MODE="${DEPLOY_MODE:-containers}"
GATEWAY_ADDR="${GATEWAY_ADDR:-:8000}"
GAMESERVER_ADDR="${GAMESERVER_ADDR:-:9200}"
GAMESERVER_PUBLIC_ADDR="${GAMESERVER_PUBLIC_ADDR:-}"
GAMESERVER_MAP_ID="${GAMESERVER_MAP_ID:-map_01}"
REDIS_ADDR="${REDIS_ADDR:-localhost:6379}"
GAME_DB_URL="${GAME_DB_URL:-}"
NAKAMA_VERSION="${NAKAMA_VERSION:-3.40.0}"
POSTGRES_DB="${POSTGRES_DB:-nakama}"
POSTGRES_USER="${POSTGRES_USER:-nakama}"
NAKAMA_CONSOLE_USER="${NAKAMA_CONSOLE_USER:-admin}"
MONITORING_ENABLED="${MONITORING_ENABLED:-true}"
GRAFANA_BIND="${GRAFANA_BIND:-127.0.0.1}"
GRAFANA_PORT="${GRAFANA_PORT:-3000}"
# Remaining vars cd.yml reads. They all have working defaults in the workflow;
# we set them anyway so the environment is explicit and self-documenting rather
# than depending on a default someone has to go read the YAML to discover.
# Override any of them through the environment before invoking.
GRAFANA_USER="${GRAFANA_USER:-admin}"
GRAFANA_ANONYMOUS="${GRAFANA_ANONYMOUS:-false}"
PROMETHEUS_PORT="${PROMETHEUS_PORT:-9090}"
PROMETHEUS_BIND="${PROMETHEUS_BIND:-127.0.0.1}"
OTLP_GRPC_PORT="${OTLP_GRPC_PORT:-4317}"
OTLP_HTTP_PORT="${OTLP_HTTP_PORT:-4318}"
OTLP_BIND="${OTLP_BIND:-127.0.0.1}"
OTEL_LGTM_VERSION="${OTEL_LGTM_VERSION:-0.11.15}"
GATEWAY_METRICS_PORT="${GATEWAY_METRICS_PORT:-9102}"
GAMESERVER_METRICS_PORT="${GAMESERVER_METRICS_PORT:-9101}"
GAMESERVER_METRICS_ADDR="${GAMESERVER_METRICS_ADDR:-gameserver-dotnet:9101}"
GATEWAY_CONTAINER_PORT="${GATEWAY_CONTAINER_PORT:-}"
GAMESERVER_CONTAINER_PORT="${GAMESERVER_CONTAINER_PORT:-}"
BACKUP_DIR="${BACKUP_DIR:-}"
BACKUP_KEEP="${BACKUP_KEEP:-7}"

# Placeholders that must never reach a production environment.
WEAK_PATTERNS='dev-secret|localdev|changeme|defaultkey|password|admin|secret123|test'
MIN_SECRET_LEN=32

# ------------------------------------------------------------------ plumbing
step() { printf '\n\033[1;36m==> %s\033[0m\n' "$*"; }
info() { printf '    %s\n' "$*"; }
warn() { printf '\033[1;33m    WARN: %s\033[0m\n' "$*" >&2; }
fail() {
	printf '\033[1;31mFAIL: %s\033[0m\n' "$*" >&2
	exit 1
}

usage() {
	sed -n '3,60p' "$0" | sed 's/^# \{0,1\}//'
	exit "${1:-0}"
}

need_arg() { [ -n "${2:-}" ] || fail "flag $1 needs a value"; }

# run_gh ARGS... — execute gh, or print it under --dry-run.
run_gh() {
	if [ "$DRY_RUN" = "1" ]; then
		printf '    [dry-run] gh %s\n' "$*"
		return 0
	fi
	gh "$@"
}

# set_secret NAME VALUE — value goes in on stdin so it never lands in argv
# (argv is visible to every other process on the box via /proc).
set_secret() {
	local name="$1" value="$2"
	if [ -z "$value" ]; then
		info "secret $name: (empty — skipped)"
		return 0
	fi
	if [ "$DRY_RUN" = "1" ]; then
		printf '    [dry-run] gh secret set %s --env %s --repo %s   (value: %d chars, hidden)\n' \
			"$name" "$ENV_NAME" "$REPO" "${#value}"
		return 0
	fi
	printf '%s' "$value" | gh secret set "$name" --env "$ENV_NAME" --repo "$REPO" --body-file -
	info "secret $name set (${#value} chars)"
}

set_var() {
	local name="$1" value="$2"
	run_gh variable set "$name" --env "$ENV_NAME" --repo "$REPO" --body "$value" >/dev/null &&
		info "var $name=$value"
}

gen_secret() { openssl rand -base64 48 | tr -d '\n=+/' | cut -c1-48; }

# ------------------------------------------------------------- arg parsing
parse_args() {
	[ $# -gt 0 ] || usage 1
	case "$1" in
	-h | --help) usage 0 ;;
	-*) fail "the first argument must be the environment name (e.g. staging)" ;;
	*)
		ENV_NAME="$1"
		shift
		;;
	esac

	while [ $# -gt 0 ]; do
		case "$1" in
		--jwt-secret) need_arg "$1" "${2:-}" && JWT_SECRET="$2" && shift 2 ;;
		--postgres-password) need_arg "$1" "${2:-}" && POSTGRES_PASSWORD="$2" && shift 2 ;;
		--nakama-console-password) need_arg "$1" "${2:-}" && NAKAMA_CONSOLE_PASSWORD="$2" && shift 2 ;;
		--grafana-admin-password) need_arg "$1" "${2:-}" && GRAFANA_ADMIN_PASSWORD="$2" && shift 2 ;;
		--nakama-server-key) need_arg "$1" "${2:-}" && NAKAMA_SERVER_KEY="$2" && shift 2 ;;
		--redis-password) need_arg "$1" "${2:-}" && REDIS_PASSWORD="$2" && shift 2 ;;
		--deploy-dir) need_arg "$1" "${2:-}" && RPG_DEPLOY_DIR="$2" && shift 2 ;;
		--deploy-mode) need_arg "$1" "${2:-}" && DEPLOY_MODE="$2" && shift 2 ;;
		--gateway-addr) need_arg "$1" "${2:-}" && GATEWAY_ADDR="$2" && shift 2 ;;
		--gameserver-addr) need_arg "$1" "${2:-}" && GAMESERVER_ADDR="$2" && shift 2 ;;
		--gameserver-public-addr) need_arg "$1" "${2:-}" && GAMESERVER_PUBLIC_ADDR="$2" && shift 2 ;;
		--map-id) need_arg "$1" "${2:-}" && GAMESERVER_MAP_ID="$2" && shift 2 ;;
		--redis-addr) need_arg "$1" "${2:-}" && REDIS_ADDR="$2" && shift 2 ;;
		--game-db-url) need_arg "$1" "${2:-}" && GAME_DB_URL="$2" && shift 2 ;;
		--nakama-version) need_arg "$1" "${2:-}" && NAKAMA_VERSION="$2" && shift 2 ;;
		--postgres-db) need_arg "$1" "${2:-}" && POSTGRES_DB="$2" && shift 2 ;;
		--postgres-user) need_arg "$1" "${2:-}" && POSTGRES_USER="$2" && shift 2 ;;
		--nakama-console-user) need_arg "$1" "${2:-}" && NAKAMA_CONSOLE_USER="$2" && shift 2 ;;
		--monitoring) need_arg "$1" "${2:-}" && MONITORING_ENABLED="$2" && shift 2 ;;
		--grafana-bind) need_arg "$1" "${2:-}" && GRAFANA_BIND="$2" && shift 2 ;;
		--grafana-port) need_arg "$1" "${2:-}" && GRAFANA_PORT="$2" && shift 2 ;;
		--repo) need_arg "$1" "${2:-}" && REPO="$2" && shift 2 ;;
		--generate) GENERATE=1 && shift ;;
		--strict) STRICT=1 && shift ;;
		--non-interactive) INTERACTIVE=0 && shift ;;
		--dry-run) DRY_RUN=1 && shift ;;
		-h | --help) usage 0 ;;
		*) fail "unknown argument '$1' (try --help)" ;;
		esac
	done

	case "$DEPLOY_MODE" in host | containers) ;; *) fail "--deploy-mode must be 'host' or 'containers' (got '$DEPLOY_MODE')" ;; esac
	case "$MONITORING_ENABLED" in true | false) ;; *) fail "--monitoring must be 'true' or 'false' (got '$MONITORING_ENABLED')" ;; esac
	[ "$ENV_NAME" = "production" ] && STRICT=1
	# The default advertised address follows the game server port, which is
	# right on a single-machine dev box and wrong anywhere a client is remote.
	[ -n "$GAMESERVER_PUBLIC_ADDR" ] || GAMESERVER_PUBLIC_ADDR=":${GAMESERVER_ADDR##*:}"
}

# ------------------------------------------------------------------ preflight
preflight() {
	step "Preflight"
	command -v gh >/dev/null 2>&1 || fail "gh CLI not found — https://cli.github.com"
	if [ "$DRY_RUN" != "1" ]; then
		gh auth status >/dev/null 2>&1 || fail "gh is not authenticated — run: gh auth login"
	fi
	if [ -z "$REPO" ]; then
		REPO="$(gh repo view --json nameWithOwner -q .nameWithOwner 2>/dev/null || true)"
		[ -n "$REPO" ] || fail "could not detect the repository — pass --repo owner/name"
	fi
	if [ "$GENERATE" = "1" ]; then
		command -v openssl >/dev/null 2>&1 || fail "--generate needs openssl"
	fi
	info "repo:        $REPO"
	info "environment: $ENV_NAME$([ "$STRICT" = 1 ] && echo '  (STRICT: production-grade secrets required)')"
	info "deploy mode: $DEPLOY_MODE"
	[ "$DRY_RUN" = "1" ] && info "MODE: dry-run — nothing will be created or changed"
	return 0
}

# --------------------------------------------------------------- secret input
# prompt_secret VARNAME "description" "dev default"
prompt_secret() {
	local varname="$1" desc="$2" devdefault="${3:-}"
	local current="${!varname}"

	if [ -z "$current" ] && [ "$GENERATE" = "1" ]; then
		current="$(gen_secret)"
		info "$varname: generated (48 chars)"
	fi

	if [ -z "$current" ] && [ "$INTERACTIVE" = "1" ]; then
		local hint=""
		[ -n "$devdefault" ] && [ "$STRICT" = "0" ] && hint=" [enter = ${devdefault}]"
		printf '    %s\n      %s%s: ' "$desc" "$varname" "$hint" >&2
		read -rs current
		printf '\n' >&2
		[ -z "$current" ] && [ "$STRICT" = "0" ] && current="$devdefault"
	fi

	printf -v "$varname" '%s' "$current"
}

# validate_secret NAME VALUE REQUIRED
validate_secret() {
	local name="$1" value="$2" required="$3"
	if [ -z "$value" ]; then
		[ "$required" = "yes" ] && fail "$name is required and was not provided (use --generate, a flag, or drop --non-interactive)"
		return 0
	fi
	[ "$STRICT" = "1" ] || return 0
	if [ "${#value}" -lt "$MIN_SECRET_LEN" ]; then
		fail "$name is ${#value} characters; $ENV_NAME requires >= $MIN_SECRET_LEN. Use --generate."
	fi
	# Case-insensitive placeholder check.
	if printf '%s' "$value" | tr '[:upper:]' '[:lower:]' | grep -qE "$WEAK_PATTERNS"; then
		fail "$name looks like a placeholder (matched /$WEAK_PATTERNS/); $ENV_NAME requires a real generated secret."
	fi
}

collect_secrets() {
	step "Secrets"
	# GRAFANA_ADMIN_PASSWORD is required exactly when monitoring is on — the
	# deploy job fails the same way (a Grafana published with a default password
	# is an open door).
	prompt_secret JWT_SECRET "HS256 secret shared by Nakama, gateway and game server" "dev-secret-change-me"
	prompt_secret POSTGRES_PASSWORD "Nakama meta DB password" "localdev"
	prompt_secret NAKAMA_CONSOLE_PASSWORD "Nakama admin console password" "password"
	if [ "$MONITORING_ENABLED" = "true" ]; then
		prompt_secret GRAFANA_ADMIN_PASSWORD "Grafana admin password" "localdev"
	fi
	prompt_secret NAKAMA_SERVER_KEY "Nakama client server key (optional; blank = defaultkey)" ""
	prompt_secret REDIS_PASSWORD "Redis password (optional; blank = no auth)" ""

	validate_secret JWT_SECRET "$JWT_SECRET" yes
	validate_secret POSTGRES_PASSWORD "$POSTGRES_PASSWORD" yes
	validate_secret NAKAMA_CONSOLE_PASSWORD "$NAKAMA_CONSOLE_PASSWORD" yes
	if [ "$MONITORING_ENABLED" = "true" ]; then
		validate_secret GRAFANA_ADMIN_PASSWORD "$GRAFANA_ADMIN_PASSWORD" yes
	fi
	validate_secret NAKAMA_SERVER_KEY "$NAKAMA_SERVER_KEY" no
	validate_secret REDIS_PASSWORD "$REDIS_PASSWORD" no
}

# ------------------------------------------------------------- apply to GitHub
create_environment() {
	step "Environment '$ENV_NAME'"
	# Idempotent: PUT creates or leaves it alone. `gh secret set --env` fails
	# with 404 if the environment does not exist yet, so this must come first.
	run_gh api -X PUT "repos/$REPO/environments/$ENV_NAME" --silent &&
		info "environment ready"
}

apply_secrets() {
	step "Applying secrets"
	set_secret JWT_SECRET "$JWT_SECRET"
	set_secret POSTGRES_PASSWORD "$POSTGRES_PASSWORD"
	set_secret NAKAMA_CONSOLE_PASSWORD "$NAKAMA_CONSOLE_PASSWORD"
	set_secret GRAFANA_ADMIN_PASSWORD "$GRAFANA_ADMIN_PASSWORD"
	set_secret NAKAMA_SERVER_KEY "$NAKAMA_SERVER_KEY"
	set_secret REDIS_PASSWORD "$REDIS_PASSWORD"
}

apply_vars() {
	step "Applying variables"
	set_var RPG_DEPLOY_DIR "$RPG_DEPLOY_DIR"
	set_var DEPLOY_MODE "$DEPLOY_MODE"
	set_var GATEWAY_ADDR "$GATEWAY_ADDR"
	set_var GAMESERVER_ADDR "$GAMESERVER_ADDR"
	set_var GAMESERVER_PUBLIC_ADDR "$GAMESERVER_PUBLIC_ADDR"
	set_var GAMESERVER_MAP_ID "$GAMESERVER_MAP_ID"
	set_var REDIS_ADDR "$REDIS_ADDR"
	set_var NAKAMA_VERSION "$NAKAMA_VERSION"
	set_var POSTGRES_DB "$POSTGRES_DB"
	set_var POSTGRES_USER "$POSTGRES_USER"
	set_var NAKAMA_CONSOLE_USER "$NAKAMA_CONSOLE_USER"
	set_var MONITORING_ENABLED "$MONITORING_ENABLED"
	set_var GRAFANA_BIND "$GRAFANA_BIND"
	set_var GRAFANA_PORT "$GRAFANA_PORT"
	# Empty means "in-memory player store"; setting an empty variable is legal
	# and keeps the environment self-documenting.
	set_var GAME_DB_URL "$GAME_DB_URL"

	# Monitoring + observability ports.
	set_var GRAFANA_USER "$GRAFANA_USER"
	set_var GRAFANA_ANONYMOUS "$GRAFANA_ANONYMOUS"
	set_var PROMETHEUS_PORT "$PROMETHEUS_PORT"
	set_var PROMETHEUS_BIND "$PROMETHEUS_BIND"
	set_var OTLP_GRPC_PORT "$OTLP_GRPC_PORT"
	set_var OTLP_HTTP_PORT "$OTLP_HTTP_PORT"
	set_var OTLP_BIND "$OTLP_BIND"
	set_var OTEL_LGTM_VERSION "$OTEL_LGTM_VERSION"
	set_var GATEWAY_METRICS_PORT "$GATEWAY_METRICS_PORT"
	set_var GAMESERVER_METRICS_PORT "$GAMESERVER_METRICS_PORT"
	set_var GAMESERVER_METRICS_ADDR "$GAMESERVER_METRICS_ADDR"

	# Container published ports: empty lets cd.yml derive them from the listen
	# addresses above, which is what keeps :8000 / :9200 true in both modes.
	[ -n "$GATEWAY_CONTAINER_PORT" ] && set_var GATEWAY_CONTAINER_PORT "$GATEWAY_CONTAINER_PORT"
	[ -n "$GAMESERVER_CONTAINER_PORT" ] && set_var GAMESERVER_CONTAINER_PORT "$GAMESERVER_CONTAINER_PORT"

	# Backups: empty BACKUP_DIR defaults to $RPG_DEPLOY_DIR/backups in cd.yml.
	[ -n "$BACKUP_DIR" ] && set_var BACKUP_DIR "$BACKUP_DIR"
	set_var BACKUP_KEEP "$BACKUP_KEEP"
	return 0
}

print_summary() {
	step "Summary"
	cat <<EOF
    Environment    : $ENV_NAME  ($REPO)
    Deploy dir     : $RPG_DEPLOY_DIR
    Deploy mode    : $DEPLOY_MODE
    Gateway        : $GATEWAY_ADDR
    Game server    : $GAMESERVER_ADDR   advertised to clients as: $GAMESERVER_PUBLIC_ADDR
    Monitoring     : $MONITORING_ENABLED   (Grafana on $GRAFANA_BIND:$GRAFANA_PORT)
    Game DB        : ${GAME_DB_URL:-<empty — in-memory player store, state lost on restart>}

    Checks worth doing before the first deploy:
      - GAMESERVER_PUBLIC_ADDR must be dialable BY A CLIENT. "$GAMESERVER_PUBLIC_ADDR"
        resolves to the client's own loopback unless it carries a real host.
      - A runner with the label '$ENV_NAME' must be online:
          gh api repos/$REPO/actions/runners --jq '.runners[]|"\(.name) \(.status) \([.labels[].name]|join(","))"'

    Then deploy:
      gh workflow run cd.yml --repo $REPO --ref <branch> -f environment=$ENV_NAME

    Full guide: backend/deploy/docs/VPS-SETUP.md
EOF
}

main() {
	parse_args "$@"
	preflight
	collect_secrets
	create_environment
	apply_secrets
	apply_vars
	print_summary
	step "setup-github-env.sh complete"
}

main "$@"
