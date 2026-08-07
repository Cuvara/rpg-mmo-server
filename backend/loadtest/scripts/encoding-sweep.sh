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

# Refuse to start while a CD deploy is running on this host.
#
# The load generator and the self-hosted runner share this machine, so an
# overlapping deploy contaminates the sweep AND the sweep can make the deploy's
# post-deploy smoke flaky — which under the merge gate reads as a broken deploy
# rather than a busy box. Measured: p99 at a fixed level swung 72.9ms to 240.6ms
# depending on whether a CD build phase was in flight, a 3.3x range, while the
# tick mean moved only 1.7x.
#
# Only cd.yml is checked, and that is deliberate rather than an oversight: its
# deploy jobs are the ONLY ones on this host (cd.yml uses
# `runs-on: ${{ fromJSON(needs.resolve.outputs.runner_labels) }}` for them).
# ci.yml, _go-module.yml and ci-dotnet.yml are all `ubuntu-latest`, i.e.
# GitHub-hosted, so ordinary CI never contends with a sweep. Do not "fix" this by
# widening it to all workflows — CI runs on nearly every push, and gating on
# those would make sweeps effectively unrunnable.
#
# Set SKIP_CD_CHECK=1 to override (e.g. no gh, or a deliberately concurrent run).
if [ "${SKIP_CD_CHECK:-0}" != "1" ] && command -v gh >/dev/null 2>&1; then
  cd_status=$(gh run list --workflow=cd.yml -L 1 --json status --jq '.[0].status' 2>/dev/null || echo "")
  if [ "$cd_status" = "in_progress" ] || [ "$cd_status" = "queued" ]; then
    echo "error: a CD run is $cd_status on this host." >&2
    echo "  A sweep now would be contaminated by it, and could make its smoke test flaky." >&2
    echo "  Wait for it, or re-run with SKIP_CD_CHECK=1 and RECORD THE OVERLAP in the results." >&2
    exit 1
  fi
fi

start_server() {
  local image="$1"
  $DOCKER rm -f "$NAME" >/dev/null 2>&1 || true
  $DOCKER run -d --name "$NAME" \
    -p ${PORT}:9000 -p ${METRICS_PORT}:9101 \
    -e JWT_SECRET="$SECRET" -e GAMESERVER_ADDR=:9000 \
    -e GAMESERVER_MAP_ID=map_bench -e GAMESERVER_ID=gs-bench \
    -e GAMESERVER_CAPACITY=2000 -e METRICS_ADDR=:9101 \
    "$image" >/dev/null

  # Wait for the server to be live AND genuinely empty.
  #
  # BOTH gauges, not just entities: checking entities alone let a container that
  # was still carrying players through as "clean", and the level measured against
  # it reported a 97% bandwidth saving that was pure artefact. See
  # docs/BENCHMARK.md. The loadtest itself now also rejects such a level, but
  # this precondition is cheaper than discovering it 43 seconds later.
  for _ in $(seq 1 90); do
    m=$(curl -sf "http://localhost:${METRICS_PORT}/metrics" 2>/dev/null) || { sleep 1; continue; }
    e=$(echo "$m" | grep -E '^gameserver_entities' | awk '{print $2}')
    o=$(echo "$m" | grep -E '^gameserver_players_online' | awk '{print $2}')
    if [ "$e" = "0" ] && [ "$o" = "0" ]; then
      # /metrics being up proves nothing about the GAME port: Program.cs starts
      # the metrics endpoint (line ~193) well before the listener, so a level
      # that starts on the strength of a metrics scrape can race the listener
      # and lose a client to "connection reset by peer" during join. Measured at
      # roughly 1 cold start in 4. Probe the port that matters, then settle.
      for _ in $(seq 1 30); do
        if (exec 3<>/dev/tcp/127.0.0.1/${PORT}) 2>/dev/null; then
          sleep 1
          return 0
        fi
        sleep 1
      done
      echo "  game port ${PORT} never accepted" >&2
      return 1
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
    # Retry a level the tool itself reports INVALID. That verdict means the level
    # did not measure what it claims (see load/runner.go validityFailure), so
    # recording it would put a non-measurement into the results; rerunning is the
    # only correct response. A DEGRADED level is a real result and is kept.
    for attempt in 1 2 3; do
      echo "=== ${arm} @ ${players} players (attempt ${attempt}) ==="
      start_server "$image" || continue
      ./loadtest \
        -join direct -gameserver-addr "127.0.0.1:${PORT}" -server-id gs-bench \
        -gameserver-metrics "http://localhost:${METRICS_PORT}/metrics" -gateway-metrics "" \
        -players "$players" -duration "$DURATION" -warmup "$WARMUP" \
        -movement cluster -encoding "$encoding" \
        -label "${arm}" \
        -json "${OUT}/${arm}-${players}.json" || echo "LEVEL FAILED: ${arm}@${players}" >&2

      if [ -f "${OUT}/${arm}-${players}.json" ] && \
         ! grep -q '"invalid":true' "${OUT}/${arm}-${players}.json"; then
        break
      fi
      echo "  level INVALID, rerunning" >&2
      rm -f "${OUT}/${arm}-${players}.json"
      sleep 3
    done
  done
}

run_arm baseline-json "$BASELINE_IMAGE" json
run_arm new-json      "$NEW_IMAGE"      json
run_arm new-proto     "$NEW_IMAGE"      proto

$DOCKER rm -f "$NAME" >/dev/null 2>&1 || true
echo "results in ${OUT}/"
