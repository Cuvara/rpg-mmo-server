#!/usr/bin/env bash
#
# Protobuf vs JSON capacity sweep.
#
# Runs three arms at matched player counts against a dedicated game server:
#
#   baseline-json : develop's server image, legacy JSON      (the BENCHMARK.md baseline)
#   new-json      : this branch's image, legacy JSON         (isolates the JSON-path cleanup)
#   new-proto     : this branch's image, protobuf            (isolates the encoding change)
#
# The SAME loadtest binary drives all three: `-encoding json` emits byte-identical
# legacy frames, so the generator is held constant and only the server under test
# changes. Splitting new-json out matters for honesty — this branch also removed a
# JsonDocument.Parse round-trip from the JSON path, and folding that saving into
# "what Protobuf bought" would flatter the result.
#
# Per BENCHMARK.md §2, the container is restarted before every level and the run
# waits for gameserver_entities to read 0, because entities leak on disconnect
# (§7.1) and would otherwise contaminate each level with the previous one's ghosts.
#
# Usage: JWT_SECRET=... ./encoding-sweep.sh [players...]

set -euo pipefail

cd "$(dirname "$0")/.."

DOCKER="${DOCKER:-docker.exe}"
SECRET="${JWT_SECRET:?JWT_SECRET is required}"
LEVELS=("${@:-50 100 150 200}")
read -r -a LEVELS <<< "${LEVELS[*]}"

BASELINE_IMAGE="${BASELINE_IMAGE:-rpg-mmo/gameserver-dotnet:f4d5561c15163c230fa3b08f6b6d827f41fab531}"
NEW_IMAGE="${NEW_IMAGE:-rpg-mmo/gameserver-dotnet:proto}"
NAME=rpg-gs-encbench
PORT=9300
METRICS_PORT=9301
OUT="results/encoding"

DURATION="${DURATION:-35s}"
WARMUP="${WARMUP:-8s}"

mkdir -p "$OUT"
go build -o loadtest ./cmd/loadtest

start_server() {
  local image="$1"
  $DOCKER rm -f "$NAME" >/dev/null 2>&1 || true
  $DOCKER run -d --name "$NAME" \
    -p ${PORT}:9000 -p ${METRICS_PORT}:9101 \
    -e JWT_SECRET="$SECRET" -e GAMESERVER_ADDR=:9000 \
    -e GAMESERVER_MAP_ID=map_bench -e GAMESERVER_ID=gs-bench \
    -e GAMESERVER_CAPACITY=2000 -e METRICS_ADDR=:9101 \
    "$image" >/dev/null

  # Wait for the server to be live AND for entity count to read 0.
  for _ in $(seq 1 60); do
    if curl -sf "http://localhost:${METRICS_PORT}/metrics" 2>/dev/null \
        | grep -qE '^gameserver_entities({})? 0(\.0)?$'; then
      return 0
    fi
    sleep 1
  done
  echo "server did not come up clean" >&2
  $DOCKER logs "$NAME" 2>&1 | tail -20 >&2
  return 1
}

run_arm() {
  local arm="$1" image="$2" encoding="$3"
  for players in "${LEVELS[@]}"; do
    echo "=== ${arm} @ ${players} players ==="
    start_server "$image"
    ./loadtest \
      -join direct -gameserver-addr "127.0.0.1:${PORT}" -server-id gs-bench \
      -gameserver-metrics "http://localhost:${METRICS_PORT}/metrics" -gateway-metrics "" \
      -players "$players" -duration "$DURATION" -warmup "$WARMUP" \
      -movement cluster -encoding "$encoding" \
      -label "${arm}" \
      -json "${OUT}/${arm}-${players}.json" || echo "LEVEL FAILED: ${arm}@${players}" >&2
  done
}

run_arm baseline-json "$BASELINE_IMAGE" json
run_arm new-json      "$NEW_IMAGE"      json
run_arm new-proto     "$NEW_IMAGE"      proto

$DOCKER rm -f "$NAME" >/dev/null 2>&1 || true
echo "results in ${OUT}/"
