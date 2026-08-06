#!/usr/bin/env bash
#
# deploy-local.sh — (re)start the realtime services on the machine this script
# runs on. Used by the CD workflow's deploy job on a self-hosted runner, and
# usable by hand on any VPS.
#
# Strategy: prefer systemd units when they exist (rpg-gateway.service /
# rpg-gameserver.service); otherwise fall back to nohup + pidfile supervision.
#
# Usage:
#   scripts/deploy-local.sh start|stop|restart|status|health
#
# Layout (override with RPG_DEPLOY_DIR):
#   $RPG_DEPLOY_DIR/bin/{gateway,gameserver}   binaries
#   $RPG_DEPLOY_DIR/run/*.pid                  pidfiles (nohup mode)
#   $RPG_DEPLOY_DIR/logs/*.log                 stdout+stderr (nohup mode)
#
# `health` also probes the meta stack (Nakama) and, when MONITORING_ENABLED is
# not "false", Grafana on GRAFANA_PORT. Both are WARN-ONLY: only the realtime
# services can fail a deploy.
#
# Env is sourced from the first file that exists:
#   /etc/rpg-mmo/env   then   $RPG_DEPLOY_DIR/deploy/.env   then   $RPG_DEPLOY_DIR/.env
# ($RPG_DEPLOY_DIR/deploy/.env is what the CD workflow writes — the same file
# docker compose reads, keeping realtime services and the meta stack in sync.)
# Never printed — the script only echoes which file was loaded.
#
set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
DEPLOY_DIR="${RPG_DEPLOY_DIR:-/opt/rpg-mmo}"
BIN_DIR="$DEPLOY_DIR/bin"
RUN_DIR="$DEPLOY_DIR/run"
LOG_DIR="$DEPLOY_DIR/logs"

ACTION="${1:-restart}"

step() { printf '\n\033[1;36m==> %s\033[0m\n' "$*"; }
info() { printf '    %s\n' "$*"; }
fail() {
	printf '\033[1;31mFAIL: %s\033[0m\n' "$*" >&2
	exit 1
}

mkdir -p "$BIN_DIR" "$RUN_DIR" "$LOG_DIR"

# ------------------------------------------------------------------- env load
load_env() {
	local f
	for f in /etc/rpg-mmo/env "$DEPLOY_DIR/deploy/.env" "$DEPLOY_DIR/.env"; do
		if [ -r "$f" ]; then
			# shellcheck disable=SC1090
			set -a && . "$f" && set +a
			info "env loaded from $f (values not echoed)"
			return 0
		fi
	done
	info "no env file found (/etc/rpg-mmo/env, $DEPLOY_DIR/deploy/.env, $DEPLOY_DIR/.env) — using process env + defaults"
}

# Loaded up-front: the service table below reads GATEWAY_ADDR/GAMESERVER_ADDR
# from it, and `status`/`health` need the same ports as `start`.
load_env

# Service definitions: name | binary | args | tcp port to healthcheck
GATEWAY_ADDR="${GATEWAY_ADDR:-:8000}"
GAMESERVER_ADDR="${GAMESERVER_ADDR:-:9000}"
GAMESERVER_MAP_ID="${GAMESERVER_MAP_ID:-map_01}"
# Exported so the gameserver process inherits the same values whether they came
# from deploy/.env or from these defaults. REDIS_* and GAMESERVER_PUBLIC_ADDR go
# with them: the server self-registers, so it needs the registry address and the
# address to advertise. In host mode there is no port mapping, so falling back to
# GAMESERVER_ADDR is correct.
export GAMESERVER_ADDR GAMESERVER_MAP_ID
export REDIS_ADDR REDIS_PASSWORD GAMESERVER_PUBLIC_ADDR

# The Go gameserver has been removed. C# .NET 10 NativeAOT binary is the default.
# Set GAMESERVER_RUNTIME=go only if you have a legacy Go binary to test against.
GAMESERVER_RUNTIME="${GAMESERVER_RUNTIME:-dotnet}"

# The gateway picks its redis backend from REDIS_ADDR automatically; the
# gameserver needs the explicit --redis flag, so add it when REDIS_ADDR is set.
GAMESERVER_ARGS="--addr=${GAMESERVER_ADDR} --map-id=${GAMESERVER_MAP_ID}"
if [ -n "${REDIS_ADDR:-}" ]; then
	GAMESERVER_ARGS="$GAMESERVER_ARGS --redis"
fi

# C# gameserver CLI flags. JWT_SECRET passed explicitly so the binary works
# even if env inheritance is incomplete (setsid/nohup). Server ID set for
# stable Redis registration.
GAMESERVER_DOTNET_ARGS="--addr=${GAMESERVER_ADDR} --map-id=${GAMESERVER_MAP_ID} --jwt-secret=${JWT_SECRET:-} --server-id=${GAMESERVER_ID:-gs-dotnet-${GAMESERVER_MAP_ID}}"

if [ "$GAMESERVER_RUNTIME" = "dotnet" ]; then
	GAMESERVER_BIN="gameserver-dotnet"
	GAMESERVER_FINAL_ARGS="$GAMESERVER_DOTNET_ARGS"
else
	GAMESERVER_BIN="gameserver"
	GAMESERVER_FINAL_ARGS="$GAMESERVER_ARGS"
fi

SERVICES=(
	"gateway|gateway|--addr=${GATEWAY_ADDR}|${GATEWAY_ADDR##*:}"
	"gameserver|${GAMESERVER_BIN}|${GAMESERVER_FINAL_ARGS}|${GAMESERVER_ADDR##*:}"
)

# --------------------------------------------------------------- systemd mode
has_systemd_unit() {
	command -v systemctl >/dev/null 2>&1 || return 1
	systemctl list-unit-files "rpg-$1.service" >/dev/null 2>&1 &&
		systemctl cat "rpg-$1.service" >/dev/null 2>&1
}

# ---------------------------------------------------------------- nohup mode
pidfile() { echo "$RUN_DIR/$1.pid"; }

is_running() {
	local pf
	pf="$(pidfile "$1")"
	[ -f "$pf" ] || return 1
	local pid
	pid="$(cat "$pf" 2>/dev/null || true)"
	[ -n "$pid" ] || return 1
	kill -0 "$pid" 2>/dev/null
}

stop_one() {
	local name="$1"
	if has_systemd_unit "$name"; then
		info "systemctl stop rpg-$name"
		sudo -n systemctl stop "rpg-$name" 2>/dev/null || systemctl stop "rpg-$name"
		return 0
	fi
	local pf pid
	pf="$(pidfile "$name")"
	if is_running "$name"; then
		pid="$(cat "$pf")"
		info "stopping $name (pid $pid)"
		kill "$pid" 2>/dev/null || true
		for _ in $(seq 1 30); do
			kill -0 "$pid" 2>/dev/null || break
			sleep 0.5
		done
		if kill -0 "$pid" 2>/dev/null; then
			info "$name did not exit in 15s — SIGKILL"
			kill -9 "$pid" 2>/dev/null || true
		fi
	else
		info "$name not running"
	fi
	rm -f "$pf"
}

start_one() {
	local name="$1" bin="$2" args="$3"
	if has_systemd_unit "$name"; then
		info "systemctl restart rpg-$name"
		sudo -n systemctl restart "rpg-$name" 2>/dev/null || systemctl restart "rpg-$name"
		return 0
	fi
	local exe="$BIN_DIR/$bin"
	[ -x "$exe" ] || fail "missing or non-executable binary: $exe"
	local log="$LOG_DIR/$name.log"
	info "starting $name: $exe $args  (log: $log)"
	# setsid + cleared RUNNER_TRACKING_ID: when invoked from a GitHub Actions
	# self-hosted runner, the runner's post-job cleanup kills every process it
	# can trace to the job (tracked via RUNNER_TRACKING_ID). Detach the session
	# and drop the marker so deployed services outlive the deploy job.
	# shellcheck disable=SC2086
	RUNNER_TRACKING_ID= setsid nohup "$exe" $args >>"$log" 2>&1 &
	echo $! >"$(pidfile "$name")"
	sleep 1
	is_running "$name" || fail "$name exited immediately — see $log"
}

# --------------------------------------------------------------- healthchecks
tcp_ok() {
	local host="$1" port="$2"
	if command -v nc >/dev/null 2>&1; then
		nc -z -w 2 "$host" "$port" >/dev/null 2>&1
	else
		timeout 2 bash -c "exec 3<>/dev/tcp/$host/$port" >/dev/null 2>&1
	fi
}

wait_tcp() {
	local name="$1" port="$2" i
	for i in $(seq 1 30); do
		if tcp_ok 127.0.0.1 "$port"; then
			info "$name healthy on tcp/$port (after ${i}s)"
			return 0
		fi
		sleep 1
	done
	return 1
}

health_nakama() {
	local url="${NAKAMA_HEALTH_URL:-http://127.0.0.1:7350/healthcheck}"
	if command -v curl >/dev/null 2>&1; then
		if curl -fsS --max-time 5 "$url" >/dev/null 2>&1; then
			info "nakama healthy ($url)"
		else
			info "WARN: nakama healthcheck failed ($url) — meta stack may still be starting"
		fi
	else
		info "curl not installed — skipping nakama healthcheck"
	fi
}

# Grafana (grafana/otel-lgtm, compose `monitoring` profile).
#
# WARN-ONLY BY DESIGN: observability is not on the gameplay critical path, so a
# dead Grafana must never fail a deploy that put a healthy game stack on the
# box. A red monitoring line in the deploy log is the signal; the deploy still
# succeeds. (Gateway/gameserver failures below are hard failures — they are the
# game.) Skipped entirely when MONITORING_ENABLED=false.
health_monitoring() {
	if [ "${MONITORING_ENABLED:-true}" = "false" ]; then
		info "monitoring disabled (MONITORING_ENABLED=false) — skipping Grafana healthcheck"
		return 0
	fi
	local port="${GRAFANA_PORT:-3000}"
	local url="${GRAFANA_HEALTH_URL:-http://127.0.0.1:${port}/api/health}"
	if ! command -v curl >/dev/null 2>&1; then
		info "curl not installed — skipping Grafana healthcheck"
		return 0
	fi
	if curl -fsS --max-time 5 "$url" >/dev/null 2>&1; then
		info "grafana healthy ($url)"
	else
		info "WARN: grafana healthcheck failed ($url) — monitoring stack down or still starting (not fatal)"
	fi
}

do_health() {
	step "Healthcheck"
	local rc=0 entry name bin args port
	for entry in "${SERVICES[@]}"; do
		IFS='|' read -r name bin args port <<<"$entry"
		wait_tcp "$name" "$port" || {
			printf '\033[1;31m    %s NOT healthy on tcp/%s\033[0m\n' "$name" "$port" >&2
			rc=1
		}
	done
	health_nakama
	health_monitoring
	return $rc
}

do_stop() {
	step "Stopping services"
	local entry name
	for entry in "${SERVICES[@]}"; do
		IFS='|' read -r name _ _ _ <<<"$entry"
		stop_one "$name"
	done
}

do_start() {
	step "Starting services"
	local entry name bin args
	for entry in "${SERVICES[@]}"; do
		IFS='|' read -r name bin args _ <<<"$entry"
		start_one "$name" "$bin" "$args"
	done

	# No registration step: the C# gameserver registers ITSELF in Redis on startup
	# and heartbeats every 5s (TTL 15s), so the entry is created, refreshed and
	# removed by the process that owns it. It reads REDIS_ADDR / REDIS_PASSWORD and
	# advertises GAMESERVER_PUBLIC_ADDR (falling back to GAMESERVER_ADDR, which is
	# correct in host mode where there is no port mapping).
	if [ -z "${REDIS_ADDR:-}" ]; then
		info "WARN: REDIS_ADDR is unset — the gameserver will not self-register and the gateway will not find it"
	fi
}

do_status() {
	step "Status"
	local entry name port
	for entry in "${SERVICES[@]}"; do
		IFS='|' read -r name _ _ port <<<"$entry"
		if has_systemd_unit "$name"; then
			info "$name: systemd -> $(systemctl is-active "rpg-$name" 2>/dev/null || echo unknown)"
		elif is_running "$name"; then
			info "$name: running (pid $(cat "$(pidfile "$name")"))"
		else
			info "$name: stopped"
		fi
	done
}

case "$ACTION" in
start)
	do_start
	do_health
	;;
stop) do_stop ;;
restart)
	do_stop
	do_start
	do_health
	;;
status) do_status ;;
health) do_health ;;
*) fail "unknown action '$ACTION' (start|stop|restart|status|health)" ;;
esac

step "deploy-local.sh $ACTION complete"
