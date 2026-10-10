#!/usr/bin/env bash
# run-level.sh <players> <label> <outdir> [extra loadtest flags]
# Fresh game server per level, load generator on Windows, server in WSL docker.
set -u
P=$1; LABEL=$2; OUT=$3; shift 3
mkdir -p "$OUT"
wsl() { MSYS_NO_PATHCONV=1 wsl.exe -d Ubuntu -- bash -c "$1" | tr -d '\r'; }
wsl "docker restart rpg-gameserver >/dev/null"
# wait for registration + a stable entity count (the map's initial spawn)
for i in $(seq 1 60); do
  e=$(curl -s -m2 http://127.0.0.1:9101/status | grep -o '"entities":[0-9]*' | cut -d: -f2)
  [ -n "$e" ] && [ "$e" -gt 0 ] && break; sleep 1; done
sleep 4
BASE=$(curl -s http://127.0.0.1:9101/status | grep -o '"entities":[0-9]*' | cut -d: -f2)
echo "baseline_entities=$BASE" > "$OUT/$LABEL.baseline"
( while true; do
    ts=$(date +%s)
    s=$(wsl "docker stats --no-stream --format '{{.CPUPerc}} {{.MemUsage}}' rpg-gameserver")
    g=$(powershell -NoProfile -Command "\$p=Get-Process loadtest -ErrorAction SilentlyContinue; if(\$p){'{0:F2} {1}' -f \$p.CPU, \$p.WorkingSet64}" | tr -d '\r')
    echo "$ts server[$s] loadgen_cpu_s,ws[$g]"
    sleep 5
  done ) > "$OUT/$LABEL.samples" 2>&1 &
SAMPLER=$!
set -a; . "${KEY_FILE:?set KEY_FILE to a file holding TRANSPORT_KEY=...}"; set +a
JWT_SECRET=$(grep '^JWT_SECRET=' "${DEPLOY_ENV:-../../deploy/.env}" | cut -d= -f2- | tr -d '\r"') \
JOIN_TOKEN_SECRET=$(grep '^JOIN_TOKEN_SECRET=' "${DEPLOY_ENV:-../../deploy/.env}" | cut -d= -f2- | tr -d '\r"') \
"${LOADTEST_BIN:-./loadtest.exe}" -join direct -gameserver-addr "${GS_ADDR:?set GS_ADDR to the advertised host:port (WSL2: <wsl-ip>:9000)}" -server-id gs-dotnet-map_01 \
  -players "$P" -ramp 20 -duration 60s -movement cluster -baseline-entities "$BASE" \
  -gameserver-metrics http://127.0.0.1:9101/metrics -gateway-metrics "" \
  -label "$LABEL" -json "$OUT/$LABEL.json" "$@" > "$OUT/$LABEL.txt" 2>&1
kill $SAMPLER 2>/dev/null
grep -A2 "^players  joined" "$OUT/$LABEL.txt" | tail -1
