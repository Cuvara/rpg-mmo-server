#!/usr/bin/env bash
# Offline proof of the cluster.autoscaler rule (ADR-18, ADR-26, ADR-14 stage 7).
#
# WHY THIS EXISTS. The rule it tests is a prohibition, and the only honest way
# to show a prohibition still works is to show it FAILING on the case it exists
# to catch. Doing that on a live cluster means creating a FleetAutoscaler on the
# map fleet -- i.e. manufacturing the ADR-2 split-world hazard on purpose, on a
# cluster other people are using -- and then remembering to delete it. This runs
# the same decision function against canned `kubectl get -o json` documents
# instead: no cluster, no mutation, both answers in one run.
#
# The fleet shapes below are REAL, captured from k3d-rpg-dev on 2026-09-13:
#   rpg-k8s-realtime/map-servers-dotnet-k8s      env GAMESERVER_MAP_ID=map_01
#   rpg-k8s-realtime/dungeon-servers-dotnet-k8s  no GAMESERVER_MAP_ID at all
# The FleetAutoscaler documents are synthetic, because neither exists on that
# cluster -- the dungeon one is what app/70-fleetautoscaler-dungeon.yaml creates,
# and the map one is the thing that must never be created.
#
# Run:  bash verify/tests/autoscaler_rule_test.sh
set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$HERE/../lib/common.sh"
KUBE_CONTEXT="offline-fixture"
source "$HERE/../lib/checks_cluster.sh"

FLEETS_JSON_FIXTURE='{"items":[
 {"metadata":{"name":"map-servers-dotnet-k8s","namespace":"rpg-k8s-realtime"},
  "spec":{"replicas":1,"template":{"spec":{"template":{"spec":{"containers":[
    {"name":"gameserver","env":[
      {"name":"AGONES_ENABLED","value":"true"},
      {"name":"GAMESERVER_MAP_ID","value":"map_01"},
      {"name":"GAMESERVER_ADDR","value":":9000"}]}]}}}}}},
 {"metadata":{"name":"dungeon-servers-dotnet-k8s","namespace":"rpg-k8s-realtime"},
  "spec":{"replicas":2,"template":{"spec":{"template":{"spec":{"containers":[
    {"name":"gameserver","env":[
      {"name":"AGONES_ENABLED","value":"true"},
      {"name":"GAMESERVER_MODE","value":"dungeon"},
      {"name":"GAMESERVER_ADDR","value":":9000"}]}]}}}}}}
]}'

FAS_NONE='{"items":[]}'
FAS_ON_DUNGEON='{"items":[{"metadata":{"name":"dungeon-servers-dotnet-k8s-buffer","namespace":"rpg-k8s-realtime"},
  "spec":{"fleetName":"dungeon-servers-dotnet-k8s","policy":{"type":"Buffer"}}}]}'
FAS_ON_MAP='{"items":[{"metadata":{"name":"map-servers-dotnet-k8s-buffer","namespace":"rpg-k8s-realtime"},
  "spec":{"fleetName":"map-servers-dotnet-k8s","policy":{"type":"Buffer"}}}]}'

# Replace the cluster with the fixtures. `k` is the ONLY way checks_cluster.sh
# reaches kubectl, so overriding it here is what makes the run offline.
FIXTURE_FAS="$FAS_NONE"
k() {
  case "$*" in
    "get fleet -A -o json")            printf '%s' "$FLEETS_JSON_FIXTURE" ;;
    "get fleetautoscalers -A -o json") printf '%s' "$FIXTURE_FAS" ;;
    *) return 1 ;;
  esac
}

VERIFY_NAMESPACES="rpg-k8s-realtime"
VERIFY_FLEET="rpg-k8s-realtime/map-servers-dotnet-k8s"

failures=0
run_case() { # name expected-verdict fas-json
  local name="$1" want="$2"
  FIXTURE_FAS="$3"
  _verdict=""; _msg=""
  check_no_single_map_autoscaler || true
  printf '%-52s want=%-4s got=%-4s %s\n' "$name" "$want" "${_verdict:-NONE}" \
    "$([ "${_verdict:-}" = "$want" ] && echo OK || { echo MISMATCH; failures=$((failures+1)); })"
  printf '    %s\n\n' "${_msg//$'\n'/$'\n'    }"
}

echo "== cluster.autoscaler, run against fixtures (no cluster touched) =="
echo
run_case "no autoscaler anywhere"                 PASS "$FAS_NONE"
run_case "autoscaler on the MAP fleet (map_01)"   FAIL "$FAS_ON_MAP"
run_case "autoscaler on the DUNGEON fleet"        PASS "$FAS_ON_DUNGEON"

if [ "$failures" -ne 0 ]; then
  echo "RESULT=FAIL ($failures case(s) wrong)"
  exit 1
fi
echo "RESULT=PASS (the rule fails the map-pinned fleet and passes the map-less one)"
