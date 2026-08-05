#!/usr/bin/env bash
#
# register-gameserver.sh — write the game server's entry into the Redis server
# registry that the gateway reads (gateway/registry -> shared/storage/redisstore).
#
# WHY THIS EXISTS: the C# .NET game server has no Redis client yet
# (GameServer/GameServer.csproj carries no StackExchange.Redis, and
# GameServer/Program.cs never registers or heartbeats). Until it does, whoever
# starts the server has to publish the registry entry, or the gateway answers
# MsgEnterWorld with "no available server for map <id>". Both deploy modes call
# this script:
#   DEPLOY_MODE=host        -> scripts/deploy-local.sh start
#   DEPLOY_MODE=containers  -> the CD deploy job, after `compose up`
# Delete this script the day the C# server registers itself.
#
# Layout written (see backend/shared/storage/redisstore/registry.go):
#   servers:id:{server_id}  HASH  server_id, map_id, addr, transport,
#                                 capacity, player_count      (+ TTL)
#   servers:map:{map_id}    SET   server ids on that map
#
# Usage:
#   scripts/register-gameserver.sh            # register (default)
#   scripts/register-gameserver.sh deregister
#
# Configuration is entirely environmental — the same deploy/.env both modes use:
#
#   REDIS_ADDR              host:port of Redis as THIS script sees it (default localhost:6379)
#   REDIS_PASSWORD          optional AUTH password
#   GAMESERVER_ID           registry id       (default gs-dotnet-<map>)
#   GAMESERVER_MAP_ID       map id            (default map_01)
#   GAMESERVER_PUBLIC_ADDR  address ADVERTISED TO CLIENTS. This is the one that
#                           matters on a VPS: it is handed back verbatim in
#                           MsgEnterWorldResp.ServerAddr, so it must be dialable
#                           BY THE CLIENT, not by the server.
#                           Default: ":${GAMESERVER_CONTAINER_PORT}" in
#                           containers mode / "$GAMESERVER_ADDR" on the host —
#                           a listen-style ":9200" which clients normalize to
#                           127.0.0.1:9200. Set it to <public-host>:<port> on a
#                           VPS.
#   GAMESERVER_CAPACITY     default 100
#   GAMESERVER_TRANSPORT    default tcp
#   REGISTRY_TTL            seconds, default 3600 (long, because nothing
#                           heartbeats this entry yet)
#
set -euo pipefail

ACTION="${1:-register}"

REDIS_ADDR="${REDIS_ADDR:-localhost:6379}"
MAP_ID="${GAMESERVER_MAP_ID:-map_01}"
SERVER_ID="${GAMESERVER_ID:-gs-dotnet-${MAP_ID}}"
PUBLIC_ADDR="${GAMESERVER_PUBLIC_ADDR:-${GAMESERVER_ADDR:-:9000}}"
CAPACITY="${GAMESERVER_CAPACITY:-100}"
TRANSPORT="${GAMESERVER_TRANSPORT:-tcp}"
TTL="${REGISTRY_TTL:-3600}"

info() { printf '    %s\n' "$*"; }
warn() { printf '\033[1;33m    WARN: %s\033[0m\n' "$*" >&2; }

# ------------------------------------------------------------ redis-cli probe
# Native redis-cli first; otherwise go through the Redis container (the usual
# case on a box where Redis only exists as a compose service).
REDIS_HOST="${REDIS_ADDR%%:*}"
REDIS_PORT="${REDIS_ADDR##*:}"
RCLI=()
if command -v redis-cli >/dev/null 2>&1; then
	RCLI=(redis-cli -h "${REDIS_HOST:-localhost}" -p "${REDIS_PORT:-6379}")
	[ -n "${REDIS_PASSWORD:-}" ] && RCLI+=(-a "$REDIS_PASSWORD" --no-auth-warning)
elif command -v docker >/dev/null 2>&1 &&
	container="$(docker ps --format '{{.Names}}' 2>/dev/null | grep -m1 redis || true)" &&
	[ -n "${container:-}" ]; then
	RCLI=(docker exec "$container" redis-cli)
	[ -n "${REDIS_PASSWORD:-}" ] && RCLI+=(-a "$REDIS_PASSWORD" --no-auth-warning)
	info "using redis-cli inside container '$container'"
else
	warn "no redis-cli available (neither native nor in a running container) — the gateway will not find the game server"
	exit 0
fi

case "$ACTION" in
register)
	info "registering ${SERVER_ID}: map=${MAP_ID} addr=${PUBLIC_ADDR} transport=${TRANSPORT} ttl=${TTL}s"
	if "${RCLI[@]}" HSET "servers:id:${SERVER_ID}" \
		server_id "$SERVER_ID" \
		map_id "$MAP_ID" \
		addr "$PUBLIC_ADDR" \
		transport "$TRANSPORT" \
		capacity "$CAPACITY" \
		player_count 0 >/dev/null; then
		"${RCLI[@]}" SADD "servers:map:${MAP_ID}" "$SERVER_ID" >/dev/null
		"${RCLI[@]}" EXPIRE "servers:id:${SERVER_ID}" "$TTL" >/dev/null
		info "registry OK — gateway will hand clients '${PUBLIC_ADDR}' for map ${MAP_ID}"
	else
		warn "redis HSET failed — gateway may not find the game server"
		exit 1
	fi
	;;
deregister)
	info "deregistering ${SERVER_ID} from map ${MAP_ID}"
	"${RCLI[@]}" DEL "servers:id:${SERVER_ID}" >/dev/null || true
	"${RCLI[@]}" SREM "servers:map:${MAP_ID}" "$SERVER_ID" >/dev/null || true
	;;
*)
	printf 'usage: %s [register|deregister]\n' "$0" >&2
	exit 2
	;;
esac
