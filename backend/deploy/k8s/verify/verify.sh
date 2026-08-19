#!/usr/bin/env bash
# Deployment verification suite -- "is this deployment actually working?"
#
# One command, one pass/fail, and a diagnostic on every failure. Layered:
#   1 cluster    kubectl invariants (namespaces, workloads, PVCs, fleet, restarts, secrets)
#   2 data       Postgres x2, Redis (incl. ADR-4 noeviction), Nakama health + plugin
#   3 registry   exactly one live server per map, host-qualified address
#   4 flow       backend/smoketest with --strict-addr
#   5 client     Unity PlayMode NUnit XML (this suite never launches Unity)
#   6 refusal    the failures fail correctly
#
# Read-only against the deployment. It starts nothing, restarts nothing,
# deletes nothing. The one exception is opt-in and named: refusal.unknown_map
# (VERIFY_ALLOW_ALLOCATION=1) can cost one Agones GameServer.
#
# Usage:
#   ./verify.sh --target dev-agones
#   ./verify.sh --target dev-agones --layer 3 --layer 4
#   ./verify.sh --target dev-agones --unity-results /abs/playmode.xml
#   ./verify.sh --list
set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$HERE/lib/common.sh"

TARGET=""; STRICT=0; LIST=0; declare -a LAYERS=()
while [ $# -gt 0 ]; do
  case "$1" in
    --target|-t)        TARGET="$2"; shift 2 ;;
    --layer|-l)         LAYERS+=("$2"); shift 2 ;;
    --unity-results)    export VERIFY_UNITY_RESULTS="$2"; shift 2 ;;
    --allow-allocation) export VERIFY_ALLOW_ALLOCATION=1; shift ;;
    --strict)           STRICT=1; shift ;;
    --list)             LIST=1; shift ;;
    -h|--help)          sed -n '2,25p' "$0"; exit 0 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

# ---- target configuration ------------------------------------------------
# A target file is plain shell. Anything already exported in the environment
# wins, so a one-off run can override a single value without editing the file.
if [ -n "$TARGET" ]; then
  TFILE="$TARGET"
  [ -f "$TFILE" ] || TFILE="$HERE/targets/$TARGET.env"
  if [ ! -f "$TFILE" ]; then
    echo "no such target: $TARGET (looked for $TFILE)" >&2
    echo "available: $(ls "$HERE/targets" | sed 's/\.env$//' | tr '\n' ' ')" >&2
    exit 2
  fi
  # An environment variable set by the caller wins over the target file: a
  # one-off run ("VERIFY_MAP_ID=map_02 ./verify.sh -t dev-agones") must not be
  # silently overwritten by the file it is overriding. Snapshot the exported
  # VERIFY_*/KUBE_CONTEXT values, source the target, then re-apply them.
  _saved_env="$(export -p | grep -E '^(declare -x |export )(VERIFY_[A-Za-z0-9_]+|KUBE_CONTEXT)=' || true)"
  # shellcheck disable=SC1090
  source "$TFILE"
  eval "$_saved_env"
fi

: "${VERIFY_TARGET_NAME:=${TARGET:-unnamed}}"
: "${KUBE_CONTEXT:=k3d-rpg-dev}"
: "${VERIFY_NAMESPACES:=rpg-realtime}"
: "${VERIFY_MAP_ID:=map_01}"
: "${VERIFY_NAKAMA_URL:=http://127.0.0.1:7350}"
: "${VERIFY_NAKAMA_SERVER_KEY:=defaultkey}"
: "${VERIFY_GATEWAY_ADDR:=127.0.0.1:8000}"
: "${VERIFY_JWT_SECRET:=${JWT_SECRET:-}}"
: "${VERIFY_GAME_MIGRATION:=1}"
: "${VERIFY_UNITY_GATEWAY_HOST:=${VERIFY_GATEWAY_ADDR%:*}}"
: "${VERIFY_UNITY_GATEWAY_PORT:=${VERIFY_GATEWAY_ADDR##*:}}"
: "${VERIFY_UNITY_NAKAMA_HOST:=127.0.0.1}"
: "${VERIFY_UNITY_NAKAMA_PORT:=7350}"

# run_probe -- the protocol-level observer.
#
# VERIFY_PROBE_BIN points at a prebuilt binary and is the shape CI uses: the
# deploy runner applies artifacts to a cluster and has no business carrying a Go
# toolchain, so cd.yml builds probe/ on the build runner, ships it in the
# deployment bundle and exports the path here. Building from probe/ on demand is
# the fallback for an operator running this by hand.
PROBE_BIN="${VERIFY_PROBE_BIN:-}"
if [ -n "$PROBE_BIN" ] && [ ! -x "$PROBE_BIN" ]; then
  echo "VERIFY_PROBE_BIN is set to '$PROBE_BIN', which is not an executable file." >&2
  echo "Refusing to silently fall back to a source build: a stale or mistyped path" >&2
  echo "must not read as 'the probe could not be built'." >&2
  exit 2
fi
run_probe() {
  if [ -z "$PROBE_BIN" ]; then
    PROBE_BIN="${TMPDIR:-/tmp}/rpg-verify-probe"
    if [ ! -x "$PROBE_BIN" ] || [ "$HERE/probe/main.go" -nt "$PROBE_BIN" ]; then
      local berr
      if ! berr=$( cd "$HERE/probe" && go build -o "$PROBE_BIN" . 2>&1 ); then
        echo "RESULT=error probe build failed: $(echo "$berr" | tail -2 | tr '\n' ' ')"
        echo "RESULT=error set VERIFY_PROBE_BIN to a prebuilt probe, or put go on PATH and run: cd $HERE/probe && go build"
        return 1
      fi
    fi
  fi
  NAKAMA_URL="$VERIFY_NAKAMA_URL" NAKAMA_SERVER_KEY="$VERIFY_NAKAMA_SERVER_KEY" \
  GATEWAY_ADDR="$VERIFY_GATEWAY_ADDR" JWT_SECRET="$VERIFY_JWT_SECRET" \
    "$PROBE_BIN" "$@"
}

source "$HERE/lib/checks_cluster.sh"
source "$HERE/lib/checks_data.sh"
source "$HERE/lib/checks_registry.sh"
source "$HERE/lib/checks_flow.sh"
source "$HERE/lib/checks_client.sh"
source "$HERE/lib/checks_refusal.sh"

if [ "$LIST" = "1" ]; then
  for id in "${CHECK_ORDER[@]}"; do
    printf '%s  [layer %s]  %s\n' "$id" "${CHECK_LAYER[$id]}" "${CHECK_TITLE[$id]}"
    printf '    proves:  %s\n' "${CHECK_PROVES[$id]}"
    printf '    cannot:  %s\n\n' "${CHECK_LIMITS[$id]}"
  done
  exit 0
fi

want_layer() {
  [ ${#LAYERS[@]} -eq 0 ] && return 0
  local l
  for l in "${LAYERS[@]}"; do [ "$l" = "$1" ] && return 0; done
  return 1
}

if [ -z "$VERIFY_JWT_SECRET" ]; then
  echo "VERIFY_JWT_SECRET (or JWT_SECRET) is unset. Without it the token and flow" >&2
  echo "checks cannot distinguish a real token from a forged one, so the suite refuses" >&2
  echo "to run rather than report green on unverifiable evidence." >&2
  exit 2
fi

echo "================================================================"
echo "deployment verification -- target: $VERIFY_TARGET_NAME"
echo "  kube context : $KUBE_CONTEXT"
echo "  namespaces   : $VERIFY_NAMESPACES"
echo "  gateway      : $VERIFY_GATEWAY_ADDR   nakama: $VERIFY_NAKAMA_URL"
echo "  map under test: $VERIFY_MAP_ID"
echo "  started      : $(date '+%Y-%m-%d %H:%M:%S %Z')"
echo "================================================================"

LAST_LAYER=""
for id in "${CHECK_ORDER[@]}"; do
  layer="${CHECK_LAYER[$id]}"
  want_layer "$layer" || continue
  if [ "$layer" != "$LAST_LAYER" ]; then
    echo
    echo "-- layer $layer --------------------------------------------------"
    LAST_LAYER="$layer"
  fi
  run_check "$id"
done

summary "$STRICT"
