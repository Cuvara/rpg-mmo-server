#!/usr/bin/env bash
# stack.sh — bring the WHOLE backend up locally with one command, and prove it.
#
# This is the newcomer entry point. It wires the five processes that the client
# flow needs — Nakama, PostgreSQL (meta + game state), Redis, the Go gateway and
# the C# game server — with matching secrets, and leaves them listening on
# documented ports.
#
#   ./stack.sh up        # build images + start everything (idempotent)
#   ./stack.sh check     # drive the full client flow through it (smoketest)
#   ./stack.sh health    # probe every health endpoint + the server registry
#   ./stack.sh logs      # tail gateway + gameserver
#   ./stack.sh ps        # service status
#   ./stack.sh down      # stop (add --wipe to delete data volumes too)
#
# Flags (any subcommand):
#   --scratch     run an ISOLATED second stack (own compose project, own
#                 container names, every published port offset) so it can
#                 coexist with a stack that is already up.
#   --no-build    skip the image builds in `up` (use the images already there).
#
# `make` is NOT required and is not installed on every dev box here; the
# Makefile's flow-* targets are thin wrappers around this script.
#
# Docs: docs/RUNBOOK-local-dev.md §"Run the whole thing locally".
set -euo pipefail

cd "$(dirname "$0")"

COMPOSE=${COMPOSE:-docker compose}
SCRATCH=0
NO_BUILD=0
CMD=""

for arg in "$@"; do
	case "$arg" in
	--scratch) SCRATCH=1 ;;
	--no-build) NO_BUILD=1 ;;
	--wipe) WIPE=1 ;;
	-h | --help)
		sed -n '2,30p' "$0" | sed 's/^# \{0,1\}//'
		exit 0
		;;
	-*)
		echo "unknown flag: $arg" >&2
		exit 2
		;;
	*)
		if [ -z "$CMD" ]; then CMD="$arg"; else
			echo "unexpected argument: $arg" >&2
			exit 2
		fi
		;;
	esac
done
WIPE=${WIPE:-0}
CMD=${CMD:-up}

# .env is what docker compose interpolates from; create it on first run.
if [ ! -f .env ]; then
	cp .env.example .env
	echo "created .env from .env.example (dev defaults — do not use in a shared env)"
fi

# ---------------------------------------------------------------- scratch mode
# A scratch stack must differ from the default one in THREE ways, or compose
# either collides on host ports or silently ADOPTS AND RECREATES the running
# containers (which is worse — it looks like success):
#   - project name        -> own network + own named volumes
#   - COMPOSE_NAME_PREFIX -> own container_name values
#   - every published port -> no host-port collision
#
# Both are passed to compose through a generated env file and an explicit -p,
# NOT through exported environment variables. Two reasons, both load-bearing:
#
#  1. On this project's dev box `docker` is a shell shim to the Windows
#     `docker.exe` (see docs/CICD.md §4a). WSL only forwards environment
#     variables to a Windows process when they are listed in $WSLENV, so
#     `export COMPOSE_PROJECT_NAME=... ; docker compose up` silently ignores
#     every override and operates on the DEFAULT project. That failure mode is
#     invisible: compose prints a normal, successful-looking recreate.
#  2. The compose file carries a top-level `name:`, which beats
#     COMPOSE_PROJECT_NAME anyway. `-p` is the only reliable override.
SCRATCH_ENV_FILE=.env.scratch
if [ "$SCRATCH" -eq 1 ]; then
	PROJECT=rpg-mmo-scratch
	{
		cat .env
		echo
		echo "# --- appended by stack.sh --scratch (regenerated every up) ---"
		echo "COMPOSE_NAME_PREFIX=rpgs"
		echo "POSTGRES_PORT=6432"
		echo "POSTGRES_GAME_PORT=6433"
		echo "REDIS_PORT=7379"
		echo "NAKAMA_GRPC_PORT=8349"
		echo "NAKAMA_HTTP_PORT=8350"
		echo "NAKAMA_CONSOLE_PORT=8351"
		echo "NAKAMA_METRICS_PORT=9500"
		echo "GATEWAY_CONTAINER_PORT=9000"
		echo "GAMESERVER_CONTAINER_PORT=9300"
		echo "GATEWAY_METRICS_PORT=9502"
		echo "GAMESERVER_METRICS_PORT=9501"
		# ADVERTISED to clients in MsgEnterWorldResp.ServerAddr — must be the
		# PUBLISHED port, not the in-container listen port.
		echo "GAMESERVER_PUBLIC_ADDR=:9300"
		echo "GAME_DB_URL=postgres://\${POSTGRES_GAME_USER:-game}:\${POSTGRES_GAME_PASSWORD:-localdev}@localhost:6433/\${POSTGRES_GAME_DB:-gamestate}?sslmode=disable"
	} >"$SCRATCH_ENV_FILE"
	ENV_FILE=$SCRATCH_ENV_FILE
else
	PROJECT=rpg-mmo-meta
	ENV_FILE=.env
fi

# Load the SAME file into this shell, so the script's own probes and the
# smoketest use the ports and secrets the containers were actually started with.
set -a
# shellcheck disable=SC1090,SC1091
. "./$ENV_FILE"
set +a

GATEWAY_PORT=${GATEWAY_CONTAINER_PORT:-8100}
GAME_PORT=${GAMESERVER_CONTAINER_PORT:-9200}
NAKAMA_PORT=${NAKAMA_HTTP_PORT:-7350}
MAP_ID=${GAMESERVER_MAP_ID:-map_01}

# Relative --env-file/-f paths only: the docker.exe shim cannot resolve absolute
# WSL paths (docs/CICD.md §4a), which is why this script cd's to its own dir.
dc() { $COMPOSE -p "$PROJECT" --env-file "$ENV_FILE" --profile realtime "$@"; }

redis_cli() {
	dc exec -T redis sh -c \
		"redis-cli \${REDIS_PASSWORD:+-a \"\$REDIS_PASSWORD\"} --no-auth-warning $*"
}

# ------------------------------------------------------------------ subcommands
do_up() {
	if [ "$NO_BUILD" -eq 0 ]; then
		# Nakama runtime plugin (gateway_token RPC). Built into ./modules,
		# which the nakama service bind-mounts.
		if [ ! -f modules/nakama.so ]; then
			echo "==> building nakama plugin (modules/nakama.so)"
			mkdir -p modules
			DOCKER_BUILDKIT=1 docker build \
				-f nakama-plugin.Dockerfile \
				--build-arg "NAKAMA_VERSION=${NAKAMA_VERSION:-3.40.0}" \
				--target export \
				--output type=local,dest=modules \
				..
		else
			echo "==> modules/nakama.so present — skipping plugin build"
		fi

		echo "==> building gateway image"
		docker build -f docker/Dockerfile.gateway \
			-t "${GATEWAY_IMAGE:-rpg-mmo/gateway:dev}" ..
		echo "==> building C# gameserver image (NativeAOT — slow on a cold cache)"
		docker build -f docker/Dockerfile.gameserver-dotnet \
			-t "${GAMESERVER_IMAGE:-rpg-mmo/gameserver-dotnet:dev}" ..
	fi

	echo "==> starting the stack"
	dc up -d

	echo "==> waiting for the game server to register itself in Redis"
	# The C# server self-registers into servers:map:<map_id> on boot and
	# heartbeats every 5s. Until that key exists the gateway answers
	# MsgEnterWorld with "no available server for map <id>", so this wait is
	# the difference between a usable stack and a confusing one.
	for _ in $(seq 1 60); do
		if [ -n "$(redis_cli "smembers servers:map:$MAP_ID" 2>/dev/null | tr -d '[:space:]')" ]; then
			break
		fi
		sleep 1
	done

	echo
	echo "stack up."
	echo "   gateway     tcp  localhost:$GATEWAY_PORT   <- point the Unity client here"
	echo "   game server tcp  localhost:$GAME_PORT   (dialed after MsgEnterWorldResp)"
	echo "   nakama      http localhost:$NAKAMA_PORT   console http://localhost:${NAKAMA_CONSOLE_PORT:-7351}"
	echo "   secrets     JWT_SECRET / JOIN_TOKEN_SECRET — read them from ./.env"
	echo
	echo "next: ./stack.sh check"
}

do_health() {
	rc=0
	curl -fsS "http://localhost:$NAKAMA_PORT/healthcheck" >/dev/null &&
		echo "nakama      OK" || { echo "nakama      FAIL"; rc=1; }
	curl -fsS "http://localhost:${GATEWAY_METRICS_PORT:-9102}/healthz" >/dev/null &&
		echo "gateway     OK" || { echo "gateway     FAIL"; rc=1; }
	curl -fsS "http://localhost:${GAMESERVER_METRICS_PORT:-9101}/healthz" >/dev/null &&
		echo "game server OK" || { echo "game server FAIL"; rc=1; }
	if redis_cli ping 2>/dev/null | grep -q PONG; then
		echo "redis       OK"
	else
		echo "redis       FAIL"
		rc=1
	fi
	members=$(redis_cli "smembers servers:map:$MAP_ID" 2>/dev/null | tr -d '\r')
	if [ -n "$(printf '%s' "$members" | tr -d '[:space:]')" ]; then
		echo "registry    OK ($(echo "$members" | tr '\n' ' ')serves map $MAP_ID)"
	else
		echo "registry    FAIL (no game server registered for map $MAP_ID)"
		rc=1
	fi
	return $rc
}

do_check() {
	# smoketest runs on the HOST: nakama device auth -> gateway_token RPC ->
	# gateway MsgAuth/MsgEnterWorld -> game server join -> input/snapshot ->
	# clean disconnect. Exactly the path a Unity client walks.
	#
	# Extra smoketest flags go in SMOKE_FLAGS, e.g.
	#   SMOKE_FLAGS='-skip-db' ./stack.sh check
	# `go` is frequently not on PATH in a non-login shell even when it is
	# installed — a plain "go: command not found" here reads as "Go is missing"
	# and sends people installing a toolchain they already have. Look in the
	# usual places first, and if it really is absent say so with the fix.
	if ! command -v go >/dev/null 2>&1; then
		for candidate in "$HOME/go/bin/go" /usr/local/go/bin/go /usr/lib/go/bin/go; do
			[ -x "$candidate" ] && PATH="$(dirname "$candidate"):$PATH" && export PATH && break
		done
	fi
	if ! command -v go >/dev/null 2>&1; then
		echo "error: 'go' is not on PATH and was not found in the usual locations." >&2
		echo "       The stack itself is running — only this check needs Go." >&2
		echo "       Fix: export PATH=\$PATH:/path/to/go/bin, then re-run ./stack.sh check" >&2
		return 1
	fi

	(
		cd ../smoketest
		# shellcheck disable=SC2086
		go run ./cmd/smoketest \
			-nakama-url "http://localhost:$NAKAMA_PORT" \
			-gateway-addr ":$GATEWAY_PORT" \
			-map-id "$MAP_ID" \
			${SMOKE_FLAGS:-}
	)
}

case "$CMD" in
up) do_up ;;
down)
	if [ "$WIPE" -eq 1 ]; then dc down -v; else dc down; fi
	;;
ps) dc ps ;;
logs) dc logs -f --tail=100 gateway gameserver-dotnet ;;
health) do_health ;;
check) do_check ;;
*)
	echo "unknown subcommand: $CMD (want up|down|ps|logs|health|check)" >&2
	exit 2
	;;
esac
