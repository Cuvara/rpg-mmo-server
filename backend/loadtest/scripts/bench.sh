#!/usr/bin/env bash
# Run one loadtest level while sampling process-level resource use, so the
# write-up can report RAM/CPU per process alongside the latency numbers.
#
# Two things are sampled that the load generator itself cannot see:
#   - `docker stats` for the server containers (RAM is the unbounded-growth check)
#   - the generator's own CPU, which is the confound: this box runs the load
#     generator, Docker Desktop, Kubernetes and the servers all at once.
#
# Usage:
#   scripts/bench.sh <players> <duration> <movement> <outdir> [extra loadtest flags...]
#
# Requires JWT_SECRET in the environment and a built ./loadtest binary (or set
# LOADTEST_BIN). On WSL, Docker is reached through docker.exe.
set -euo pipefail

PLAYERS="${1:?usage: bench.sh <players> <duration> <movement> <outdir> [flags...]}"
DURATION="${2:?}"
MOVEMENT="${3:?}"
OUTDIR="${4:?}"
shift 4

LOADTEST_BIN="${LOADTEST_BIN:-./loadtest}"
DOCKER="${DOCKER:-docker.exe}"
CONTAINERS="${CONTAINERS:-rpg-gameserver rpg-gateway rpg-redis}"
TAG="${PLAYERS}-${MOVEMENT}"

mkdir -p "$OUTDIR"
: >"$OUTDIR/stats-$TAG.txt"

"$LOADTEST_BIN" \
	-players "$PLAYERS" \
	-duration "$DURATION" \
	-movement "$MOVEMENT" \
	-label "n=$PLAYERS movement=$MOVEMENT" \
	-json "$OUTDIR/run-$TAG.json" \
	"$@" >"$OUTDIR/run-$TAG.log" 2>&1 &
RUN_PID=$!

# Sample until the run exits. `docker stats --no-stream` costs ~1s per call, so
# this samples roughly every 5s rather than continuously.
while kill -0 "$RUN_PID" 2>/dev/null; do
	{
		echo "--- $(date -Is) ---"
		# shellcheck disable=SC2086
		$DOCKER stats --no-stream --format \
			'{{.Name}}\t{{.CPUPerc}}\t{{.MemUsage}}' $CONTAINERS 2>/dev/null || true
		# The generator's own footprint, for the confound section.
		ps -o pid,pcpu,pmem,rss,etime,comm -C "$(basename "$LOADTEST_BIN")" --no-headers 2>/dev/null || true
	} >>"$OUTDIR/stats-$TAG.txt"
	sleep 4
done

wait "$RUN_PID" || true
echo "=== players=$PLAYERS movement=$MOVEMENT ==="
tail -n 5 "$OUTDIR/run-$TAG.log"
