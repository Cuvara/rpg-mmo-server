#!/usr/bin/env bash
# ab.sh <image-tag> <label> <players> <enemies:true|false>
# One level against rpg-mmo/gameserver-dotnet:<image-tag> on the local compose stack in WSL2.
# Retags the image as :dev, recreates the stack with GAMESERVER_ENEMIES, runs
# ../2026-10-10-kcp-only/run-level.sh, then prints one summary line. AI on declares
# 30+45P+10 baseline entities (the per-player enemy caps), AI off declares 0.
#
# Needs: KEY_FILE and KEY_FILE_WSL (one file holding TRANSPORT_KEY=..., Windows and WSL
# paths), GS_ADDR (<wsl-ip>:9000), LOADTEST_BIN, DEPLOY_ENV, DEPLOY_DIR (WSL path of
# backend/deploy) and OUT.
set -u
TAG=$1; LABEL=$2; P=$3; EN=$4
HERE=$(cd "$(dirname "$0")" && pwd)
OUT=${OUT:?set OUT to the results directory}
wsl() { MSYS_NO_PATHCONV=1 wsl.exe -d Ubuntu -- bash -c "$1" | tr -d '\r'; }
wsl "docker tag rpg-mmo/gameserver-dotnet:$TAG rpg-mmo/gameserver-dotnet:dev; export GAMESERVER_ENEMIES=$EN; set -a; . '${KEY_FILE_WSL:?set KEY_FILE_WSL to the WSL path of KEY_FILE}'; set +a; cd '${DEPLOY_DIR:?}' && ./stack.sh up --no-build >/dev/null 2>&1"
img=$(wsl "docker inspect rpg-gameserver --format '{{.Image}}'")
en=$(wsl "docker inspect rpg-gameserver --format '{{range .Config.Env}}{{println .}}{{end}}'" | grep '^GAMESERVER_ENEMIES=' | cut -d= -f2)
if [ "$EN" = true ]; then B=$((30 + 45 * P + 10)); else B=0; fi
"$HERE/../2026-10-10-kcp-only/run-level.sh" "$P" "$LABEL" "$OUT" -baseline-entities "$B" >/dev/null
rcv=$(wsl "docker exec rpg-gameserver cat /proc/net/snmp" | awk '/^Udp:/{n++; if(n==2) print $6}')
j=$OUT/$LABEL.json
g() { grep -o "\"$1\": [0-9.e-]*" "$j" | head -1 | awk '{print $2}'; }
ack() { grep -A6 '"ack_latency"' "$j" | grep -o "\"$1\": [0-9.]*" | awk '{print $2}'; }
echo "$LABEL img=${img:7:12} players=$P enemies=$en failed=$(g players_failed) rcvbufErr=$rcv retx=$(g retrans_ratio) ackp50=$(ack p50_ms) ackp95=$(ack p95_ms) ackp99=$(ack p99_ms) tickp99=$(g tick_p99_sec) overbudget=$(g tick_over_budget_ratio) recvBusy=$(g kcp_receive_busy_ratio) closed=$(g kcp_sessions_closed_window) cpu=$(g cpu_cores)"
