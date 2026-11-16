#!/usr/bin/env bash
# Offline proof of the cluster.log_collector check (k8s/logging, Alloy DaemonSet).
#
# WHY THIS EXISTS. The check has to FAIL when the collector is absent or not
# ready, and WARN (not pass) when it is ready but Loki is not attached. Showing
# that on a live cluster means deleting the collector or detaching Loki on a
# cluster other people use. This runs the same decision function against a fake
# `k` instead: no cluster, no kubectl, no mutation, every answer in one run.
#
# The fake answers only the three reads the check makes, from a per-case state:
#   get ns <ns>                        exists / absent
#   get daemonset <ds> -n <ns> -o jsonpath=...   "<desired> <ready>" / absent
#   get endpointslices -n <ns> -l kubernetes.io/service-name=loki -o jsonpath=...
#                                      an address / nothing
# Anything else returns 1, so a new read in the check fails these cases loudly.
#
# Run:  bash backend/deploy/k8s/verify/tests/log_collector_test.sh
set -uo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
source "$HERE/../lib/common.sh"
KUBE_CONTEXT="offline-fixture"
source "$HERE/../lib/checks_cluster.sh"

# Per-case state, set by run_case.
FIX_NS=present        # present | absent
FIX_DS=""             # "" (absent) | "<desired> <ready>"
FIX_ADDR=""           # "" (no EndpointSlice address) | an IPv4 address

k() {
  case "$*" in
    "get ns rpg-k8s-logging")
      [ "$FIX_NS" = present ] ;;
    "get daemonset alloy -n rpg-k8s-logging -o jsonpath={.status.desiredNumberScheduled} {.status.numberReady}")
      [ -n "$FIX_DS" ] && printf '%s' "$FIX_DS" ;;
    "get endpointslices -n rpg-k8s-logging -l kubernetes.io/service-name=loki -o jsonpath={range .items[*]}{range .endpoints[*]}{.addresses[*]}{\"\\n\"}{end}{end}")
      [ -n "$FIX_ADDR" ] && printf '%s\n' "$FIX_ADDR"
      return 0 ;;
    *) echo "log_collector_test: unexpected kubectl read: $*" >&2; return 1 ;;
  esac
}

failures=0
run_case() { # name expected-verdict ns ds addr [expected-substring]
  local name="$1" want="$2" result=OK
  FIX_NS="$3"; FIX_DS="$4"; FIX_ADDR="$5"
  local needle="${6:-}"
  _verdict=""; _msg=""
  check_log_collector || true
  if [ "${_verdict:-}" != "$want" ]; then
    result=MISMATCH
    failures=$((failures+1))
  elif [ -n "$needle" ] && [[ "$_msg" != *"$needle"* ]]; then
    result="MISMATCH(message lacks '$needle')"
    failures=$((failures+1))
  fi
  printf '%-52s want=%-4s got=%-4s %s\n' "$name" "$want" "${_verdict:-NONE}" "$result"
  printf '    %s\n\n' "${_msg//$'\n'/$'\n'    }"
}

echo "== cluster.log_collector, run against fixtures (no cluster touched) =="
echo

VERIFY_LOG_COLLECTOR="rpg-k8s-logging/alloy"
run_case "namespace absent"                       FAIL absent  ""     ""          "namespace"
run_case "namespace present, DaemonSet absent"    FAIL present ""     ""          "daemonset/alloy"
run_case "DaemonSet desired 0"                    FAIL present "0 0"  "10.0.0.5"  "no node"
run_case "not ready (1 of 2)"                     FAIL present "2 1"  "10.0.0.5"  "desired=2 ready=1"
run_case "not ready (0 of 1)"                     FAIL present "1 0"  "10.0.0.5"  "desired=1 ready=0"
run_case "status not filled in yet (empty fields)" FAIL present " "    "10.0.0.5"  "no node"
run_case "ready, no EndpointSlice address"        WARN present "1 1"  ""          "connect-loki.sh"
run_case "ready, with an EndpointSlice address"   PASS present "1 1"  "172.20.0.9" "has an address"

# A target that declares no collector SKIPs, saying so: it must never read as PASS.
VERIFY_LOG_COLLECTOR=""
run_case "target declares no collector"           SKIP present "1 1"  "172.20.0.9" "VERIFY_LOG_COLLECTOR"

# The registration: layer 1, and the `cannot` text says it does not prove a line
# reached Loki (the acceptance wording).
reg_ok=OK
[ "${CHECK_FN[cluster.log_collector]:-}" = check_log_collector ] || reg_ok=MISSING
[ "${CHECK_LAYER[cluster.log_collector]:-}" = 1 ] || reg_ok=MISSING
case "${CHECK_LIMITS[cluster.log_collector]:-}" in
  *"a log line reached Loki"*) ;;
  *) reg_ok=MISSING ;;
esac
printf '%-52s %s\n\n' "registered as cluster.log_collector (layer 1)" "$reg_ok"
[ "$reg_ok" = OK ] || failures=$((failures+1))

if [ "$failures" -ne 0 ]; then
  echo "RESULT=FAIL ($failures case(s) wrong)"
  exit 1
fi
echo "RESULT=PASS (absent or not-ready collector FAILs, a collector with no Loki WARNs, ready and attached PASSes)"
