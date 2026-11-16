#!/usr/bin/env bash
# Offline proof of the cluster.hub_rbac check (k8s/hub, k3s/hub_contract.py).
#
# WHY THIS EXISTS. The check has to FAIL when the Studio hub's ServiceAccount is
# missing or its Roles grant one verb or one resource beyond the allow-list. Showing
# that on a live cluster means widening a Role on a cluster other people use. This runs
# the same decision function against a fake `k` that answers from fixture files, so
# every state is shown in one run with no cluster, no kubectl and no mutation.
#
# The fixtures are written by hub_rbac_fixture.py FROM the contract's own tables, so
# the exact state cannot drift from the allow-list; each mutation is one named
# departure from it. The fake answers only `get` reads of the hub's own objects and
# gateway-config's `games`; anything else is logged and fails the run, and the run
# also fails when a call is not a read.
#
# Run:  bash backend/deploy/k8s/verify/tests/hub_rbac_test.sh
set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$HERE/../lib/common.sh"
KUBE_CONTEXT="offline-fixture"
source "$HERE/../lib/checks_cluster.sh"

WORK=$(mktemp -d)
trap 'rm -rf "$WORK"' EXIT
FIX="$WORK/fixture"
CALLS="$WORK/calls"
: > "$CALLS"

notfound() { echo "Error from server (NotFound): $1 not found"; return 1; }
from_file() { # file kind-and-name: print the fixture, or NotFound when the file is absent
  [ -f "$FIX/$1" ] && { cat "$FIX/$1"; return 0; }
  notfound "$2"
}

k() {
  echo "$*" >> "$CALLS"
  case "$*" in
    "get serviceaccount studio-hub -n rpg-k8s-hub -o json") from_file sa.json 'serviceaccounts "studio-hub"' ;;
    "get configmap gateway-config -n rpg-k8s-realtime -o jsonpath={.data.games}") cat "$FIX/games.txt" ;;
    "get role studio-hub -n "*" -o json")
      local cmd="$*" ns; ns=${cmd#get role studio-hub -n }; ns=${ns% -o json}
      from_file "role.$ns.json" "roles.rbac.authorization.k8s.io \"studio-hub\"" ;;
    "get rolebindings -n "*" -o json")
      local cmd="$*" ns; ns=${cmd#get rolebindings -n }; ns=${ns% -o json}
      from_file "rb.$ns.json" "namespace $ns" ;;
    "get clusterrole studio-hub-nodes -o json") from_file cr.json 'clusterroles.rbac.authorization.k8s.io "studio-hub-nodes"' ;;
    "get clusterrolebindings -o json") from_file crb.json 'clusterrolebindings' ;;
    *) echo "hub_rbac_test: unexpected kubectl read: $*" >&2; echo "$*" >> "$WORK/unexpected"; return 1 ;;
  esac
}

failures=0
# run_case <name> <want-verdict> <fixture args...> -- <expected substring>...
run_case() {
  local name="$1" want="$2" result=OK needle
  shift 2
  local -a fargs=() needles=()
  while [ $# -gt 0 ] && [ "$1" != -- ]; do fargs+=("$1"); shift; done
  [ "${1:-}" = -- ] && shift
  needles=("$@")
  if ! python3 "$HERE/hub_rbac_fixture.py" "$FIX" "${fargs[@]}"; then
    echo "fixture generation failed for: $name" >&2
    failures=$((failures+1)); return
  fi
  _verdict=""; _msg=""
  check_hub_rbac || true
  if [ "${_verdict:-}" != "$want" ]; then
    result=MISMATCH; failures=$((failures+1))
  else
    for needle in "${needles[@]+"${needles[@]}"}"; do
      if [[ "$_msg" != *"$needle"* ]]; then
        result="MISMATCH(message lacks '$needle')"; failures=$((failures+1)); break
      fi
    done
  fi
  printf '%-66s want=%-4s got=%-4s %s\n' "$name" "$want" "${_verdict:-NONE}" "$result"
  printf '    %s\n\n' "${_msg//$'\n'/$'\n'    }"
}

echo "== cluster.hub_rbac, run against fixtures (no cluster touched) =="
echo

VERIFY_HUB_RBAC="rpg-k8s-hub/studio-hub"

# ---- PASS: exact ------------------------------------------------------------
run_case "exact, cuvara only, no node ClusterRole"                 PASS --games cuvara -- "NOT granted (opt-in" "rpg-k8s-realtime rpg-k8s-data"
run_case "exact, no gateway games key at all (cuvara only)"        PASS --mutate no-gateway-games
run_case "exact with the node ClusterRole enabled"                 PASS --games cuvara --node -- "node access ENABLED"
run_case "exact, rendered non-default games ember and frost"       PASS --games cuvara,ember,frost -- "rpg-k8s-realtime-ember" "rpg-k8s-realtime-frost"
run_case "exact, non-default game, node ClusterRole enabled"       PASS --games ember --node -- "rpg-k8s-realtime-ember" "ENABLED"

# ---- FAIL: the ServiceAccount ----------------------------------------------
run_case "ServiceAccount missing"                                  FAIL --games cuvara --mutate no-sa -- "ServiceAccount is absent" "serviceaccount/studio-hub"

# ---- FAIL: rules, compared by triples, in either direction ------------------
run_case "extra verb (pods create) in rpg-k8s-realtime"            FAIL --games cuvara --mutate extra-verb -- "rpg-k8s-realtime: Role studio-hub" "extra: core/pods create"
run_case "extra resource (secrets get) in rpg-k8s-realtime"        FAIL --games cuvara --mutate extra-resource -- "extra: core/secrets get"
run_case "missing rule (pods/log) in rpg-k8s-realtime"             FAIL --games cuvara --mutate missing-rule -- "missing: core/pods/log get"
run_case "data namespace gains the Agones rows"                    FAIL --games cuvara --mutate data-gets-agones -- "rpg-k8s-data: Role studio-hub" "extra: agones.dev/fleets/scale patch"
run_case "wildcard resource in the data Role"                      FAIL --games cuvara --mutate wildcard -- "wildcard"
run_case "resourceNames in the data Role"                          FAIL --games cuvara --mutate resource-names -- "resourceNames"

# ---- FAIL: the rendered non-default game namespace --------------------------
run_case "Role missing in a rendered non-default game namespace"   FAIL --games cuvara,ember,frost --mutate game-role-missing -- "rpg-k8s-realtime-ember: Role studio-hub is absent"
run_case "extra verb in a rendered game namespace Role"            FAIL --games cuvara,ember --mutate game-extra-verb -- "rpg-k8s-realtime-ember: Role studio-hub" "extra: core/pods patch"
run_case "RoleBinding missing in a rendered game namespace"        FAIL --games cuvara,ember --mutate game-binding-missing -- "rpg-k8s-realtime-ember: RoleBindings: missing: RoleBinding rpg-k8s-realtime-ember/studio-hub"

# ---- FAIL: bindings ---------------------------------------------------------
run_case "RoleBinding with another subject"                        FAIL --games cuvara --mutate binding-wrong-subject -- "rpg-k8s-data: RoleBindings" "subjects are"
run_case "RoleBinding to cluster-admin"                            FAIL --games cuvara --mutate binding-cluster-admin -- "roleRef is"
run_case "a foreign RoleBinding names the ServiceAccount"          FAIL --games cuvara --mutate foreign-rb -- "foreign: RoleBinding rpg-k8s-realtime/hub-edit"
run_case "a foreign ClusterRoleBinding names the ServiceAccount"   FAIL --games cuvara --mutate foreign-crb -- "ClusterRoleBindings" "foreign: ClusterRoleBinding hub-admin"

# ---- FAIL: the opt-in node ClusterRole --------------------------------------
run_case "node ClusterRole with one verb too many (nodes delete)"  FAIL --games cuvara --mutate node-extra-verb -- "ClusterRole studio-hub-nodes" "extra: core/nodes delete"
run_case "node ClusterRole missing a rule"                         FAIL --games cuvara --mutate node-missing-rule -- "missing: core/pods/eviction create"
run_case "node ClusterRole without its binding"                    FAIL --games cuvara --mutate node-role-no-binding -- "missing: ClusterRoleBinding studio-hub-nodes"
run_case "node binding without its ClusterRole"                    FAIL --games cuvara --mutate node-binding-no-role -- "exists but the ClusterRole studio-hub-nodes does not"
run_case "node binding pointing at cluster-admin"                  FAIL --games cuvara --mutate node-binding-cluster-admin -- "roleRef is"

# ---- SKIP: a target that declares no hub ------------------------------------
VERIFY_HUB_RBAC=""
run_case "target declares no hub"                                  SKIP --games cuvara -- "VERIFY_HUB_RBAC"
VERIFY_HUB_RBAC="rpg-k8s-hub/studio-hub"

# ---- the invariants of the run itself ---------------------------------------
if [ -f "$WORK/unexpected" ]; then
  echo "the check made a read the fake does not answer:"; sed 's/^/    /' "$WORK/unexpected"
  failures=$((failures+1))
fi
write_calls=$(grep -vc '^get ' "$CALLS" || true)
printf '%-66s %s\n\n' "every kubectl call was a get (read-only)" "$([ "${write_calls:-0}" = 0 ] && echo OK || echo "NOT-READ-ONLY($write_calls)")"
[ "${write_calls:-0}" = 0 ] || failures=$((failures+1))
secret_reads=$(grep -ci 'secret' "$CALLS" || true)
printf '%-66s %s\n\n' "no Secret was read" "$([ "${secret_reads:-0}" = 0 ] && echo OK || echo "READ($secret_reads)")"
[ "${secret_reads:-0}" = 0 ] || failures=$((failures+1))

# ---- registration and targets ------------------------------------------------
reg_ok=OK
[ "${CHECK_FN[cluster.hub_rbac]:-}" = check_hub_rbac ] || reg_ok=MISSING
[ "${CHECK_LAYER[cluster.hub_rbac]:-}" = 1 ] || reg_ok=MISSING
case "${CHECK_LIMITS[cluster.hub_rbac]:-}" in
  *"token an operator minted"*) ;;
  *) reg_ok=MISSING ;;
esac
printf '%-66s %s\n' "registered as cluster.hub_rbac (layer 1)" "$reg_ok"
[ "$reg_ok" = OK ] || failures=$((failures+1))
for t in k8s-dev k8s-stg; do
  ok=OK; grep -Fxq 'VERIFY_HUB_RBAC="rpg-k8s-hub/studio-hub"' "$HERE/../targets/$t.env" || ok=MISSING
  printf '%-66s %s\n' "target $t declares VERIFY_HUB_RBAC" "$ok"
  [ "$ok" = OK ] || failures=$((failures+1))
done
ok=OK; ! grep -q 'VERIFY_HUB_RBAC' "$HERE/../targets/dev-agones.env" || ok=DECLARED
printf '%-66s %s\n\n' "target dev-agones (no k8s/hub) declares nothing" "$ok"
[ "$ok" = OK ] || failures=$((failures+1))

if [ "$failures" -ne 0 ]; then
  echo "RESULT=FAIL ($failures case(s) wrong)"
  exit 1
fi
echo "RESULT=PASS (an extra verb, an extra resource, a missing rule, a missing ServiceAccount, a missing game Role, a foreign binding and a widened node role each FAIL; exact PASSes with and without node access)"
