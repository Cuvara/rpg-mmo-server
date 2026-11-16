#!/usr/bin/env bash
# Layer 1 -- cluster invariants. Read-only kubectl, always against $KUBE_CONTEXT.

k() { kubectl --context "$KUBE_CONTEXT" "$@"; }

source "$(dirname "${BASH_SOURCE[0]}")/python_lf.sh"

check_cluster_reachable() {
  local out
  if ! out=$(k version -o json 2>&1); then
    fail "kubectl cannot reach the cluster" \
      "context $KUBE_CONTEXT answers" \
      "$(echo "$out" | head -2)" \
      "kubectl config get-contexts; k3d cluster list"
    return
  fi
  pass "context=$KUBE_CONTEXT server=$(k version -o json 2>/dev/null | cluster_python_lf -c 'import json,sys;print(json.load(sys.stdin).get("serverVersion",{}).get("gitVersion","?"))' 2>/dev/null)"
}

check_namespaces() {
  local missing=() ns
  for ns in $VERIFY_NAMESPACES; do
    k get ns "$ns" >/dev/null 2>&1 || missing+=("$ns")
  done
  if [ ${#missing[@]} -gt 0 ]; then
    fail "namespace(s) absent" "$VERIFY_NAMESPACES" "missing: ${missing[*]}" \
      "kubectl --context $KUBE_CONTEXT get ns"
    return
  fi
  pass "present: $VERIFY_NAMESPACES"
}

# Every Deployment/StatefulSet/DaemonSet in the target namespaces has all of its
# replicas Ready. Reads the status subresource, not the pod list, so a workload
# scaled to zero is visible as 0/0 rather than silently absent.
check_workloads_ready() {
  local bad=() ns line
  for ns in $VERIFY_NAMESPACES; do
    while read -r line; do
      [ -z "$line" ] && continue
      bad+=("$line")
    done < <(k get deploy,statefulset,daemonset -n "$ns" -o json 2>/dev/null | cluster_python_lf -c '
import json,sys
try: d=json.load(sys.stdin)
except Exception: sys.exit(0)
for it in d.get("items",[]):
    kind=it["kind"]; name=it["metadata"]["name"]; ns=it["metadata"]["namespace"]
    st=it.get("status",{}); sp=it.get("spec",{})
    if kind=="DaemonSet":
        want=st.get("desiredNumberScheduled",0); got=st.get("numberReady",0)
    else:
        want=sp.get("replicas",0); got=st.get("readyReplicas",0) or 0
    if got!=want or want==0:
        print(f"{ns}/{kind.lower()}/{name} ready={got}/{want}")
')
  done
  if [ ${#bad[@]} -gt 0 ]; then
    fail "workload(s) not fully Ready" "every replica Ready in: $VERIFY_NAMESPACES" \
      "$(printf '%s; ' "${bad[@]}")" \
      "kubectl --context $KUBE_CONTEXT get pods -n <ns>; kubectl describe <workload>"
    return
  fi
  local n
  n=$(for ns in $VERIFY_NAMESPACES; do k get deploy,statefulset,daemonset -n "$ns" --no-headers 2>/dev/null; done | wc -l)
  pass "$n workload(s) Ready across: $VERIFY_NAMESPACES"
}

check_pvcs_bound() {
  if [ -z "${VERIFY_PVCS:-}" ]; then
    skip "no PVCs declared for this target (VERIFY_PVCS empty). NOT a statement that storage is healthy -- it means this deployment claims no persistent volumes. Set VERIFY_PVCS='ns/name ...' once the data tier moves into the cluster."
    return
  fi
  local bad=() ref ns name phase
  for ref in $VERIFY_PVCS; do
    ns="${ref%%/*}"; name="${ref##*/}"
    phase=$(k get pvc "$name" -n "$ns" -o jsonpath='{.status.phase}' 2>/dev/null)
    [ "$phase" = "Bound" ] || bad+=("$ref=${phase:-ABSENT}")
  done
  if [ ${#bad[@]} -gt 0 ]; then
    fail "PVC(s) not Bound" "Bound" "${bad[*]}" \
      "kubectl --context $KUBE_CONTEXT get pvc -A; kubectl describe pvc <name> -n <ns>"
    return
  fi
  pass "Bound: $VERIFY_PVCS"
}

# Echoes the literal GAMESERVER_MAP_ID baked into a fleet's pod template, or
# nothing when the template does not pin one (per-pod map id, or a fleet that
# does not use the variable at all).
#
# This is the discriminator for every "may this fleet hold spare Ready pods?"
# question below, because the C# server self-registers into Redis at STARTUP --
# right after ReadyAsync, before any allocation (GameServer.cs) -- keyed by
# whatever GAMESERVER_MAP_ID it was given. A fleet-wide literal therefore makes
# every Ready pod a live server for the SAME map.
#
# Only a literal `value:` counts. A `valueFrom` (downward API, ConfigMap) is a
# map id the fleet does not itself fix, so it is reported as not-pinned and the
# stricter checks stand down rather than guess.
fleet_wide_map_id() {
  local ns="$1" name="$2"
  k get fleet "$name" -n "$ns" -o json 2>/dev/null | cluster_python_lf -c '
import json,sys
try: d=json.load(sys.stdin)
except Exception: sys.exit(0)
spec=d.get("spec",{}).get("template",{}).get("spec",{}).get("template",{}).get("spec",{})
for c in spec.get("containers",[]) or []:
    for e in c.get("env",[]) or []:
        if e.get("name")=="GAMESERVER_MAP_ID" and "value" in e:
            print(e["value"]); sys.exit(0)
'
}

# The whole rule, in one place, over DATA rather than over a cluster.
#
# Reads two JSON documents (a Fleet list and a FleetAutoscaler list, as
# `kubectl get -A -o json` returns them) out of the environment, and emits one
# line per fleet in the named namespaces:
#
#     <ns>/<fleet>|<fleet-wide map id or empty>|<autoscalers, comma-separated>
#
# The separator is `|` and NOT a tab, because tab is IFS whitespace: `IFS=$'\t'
# read` collapses two adjacent tabs into one, so a fleet with no map id would
# have its autoscaler list read back as its map id -- which reads as a PASS
# ("pins <autoscaler name>") on exactly the fleet this rule is about. Caught by
# verify/tests/autoscaler_rule_test.sh before it could be believed.
#
# Kept free of kubectl on purpose: this function is what verify/tests/
# autoscaler_rule_test.sh exercises offline, so the rule can be proven to say
# FAIL for one fleet shape and PASS for the other without a cluster and without
# creating a FleetAutoscaler anywhere.
#
# Only a literal `value:` counts as a pinned map id -- see fleet_wide_map_id.
classify_fleets() {
  cluster_python_lf -c '
import json,os,sys
want=set(sys.argv[1].split())
def load(name):
    try: return json.loads(os.environ.get(name,"") or "{}")
    except Exception: return {}
fleets=load("FLEETS_JSON"); fas=load("FAS_JSON")
byfleet={}
for it in fas.get("items",[]) or []:
    m=it.get("metadata",{}); s=it.get("spec",{})
    key=(m.get("namespace"), s.get("fleetName"))
    pol=s.get("policy",{}).get("type","?")
    byfleet.setdefault(key,[]).append("%s/%s(policy=%s)" % (m.get("namespace"), m.get("name"), pol))
for it in fleets.get("items",[]) or []:
    m=it.get("metadata",{}); ns=m.get("namespace"); name=m.get("name")
    if want and ns not in want: continue
    spec=it.get("spec",{}).get("template",{}).get("spec",{}).get("template",{}).get("spec",{})
    mapid=""
    for c in spec.get("containers",[]) or []:
        for e in c.get("env",[]) or []:
            if e.get("name")=="GAMESERVER_MAP_ID" and "value" in e:
                mapid=e["value"]; break
        if mapid: break
    print("%s/%s|%s|%s" % (ns, name, mapid, ",".join(sorted(byfleet.get((ns,name),[])))))
' "$1"
}

# NO FleetAutoscaler on a fleet whose pods all carry the same map id.
#
# This check exists because the trap it guards is invisible in every other
# signal: the manifest applies, the pod goes Ready, the fleet reads *healthier*
# than before (ready=1 instead of ready=0, so cluster.fleet stops warning), and
# the damage is a SECOND registry entry for one map_id.
# `registry.FindServer` then returns the least-loaded of the two -- i.e. the
# unallocated spare -- so live players are handed a pod Agones is free to delete
# on the next scale-down, and the two halves of the map cannot see each other
# (ADR-2, ADR-18).
#
# Measured on k3d 2026-08-18: scaling this fleet 1 -> 2 put a second member into
# `servers:map:map_01` 5.4s later, with no allocation involved.
#
# Deliberately a FAIL, not a WARN. Prose saying "do not add one" is already in
# 50-fleet-map.yaml, deploy/CLAUDE.md and docs/K3S.md, and prose did not stop it
# being proposed again.
#
# IT SWEEPS EVERY FLEET IN $VERIFY_NAMESPACES, not just $VERIFY_FLEET, since
# 2026-09-13 (ADR-14 stage 7). It used to inspect the one fleet a target names,
# which was the whole cluster's worth of fleets when there was one fleet. There
# are two now -- a map fleet that pins GAMESERVER_MAP_ID and a dungeon fleet
# that pins none (ADR-26 decision 8) -- and a rule that only looks at the fleet
# a target happens to name is a rule that would have missed an autoscaler put on
# the OTHER one. The map fleet is still the fleet the prohibition is about; the
# sweep is what makes "no autoscaler on a map-pinned fleet" a statement about
# the deployment rather than about one line in a target file.
#
# The map-less side is a PASS, not an exemption bolted on for the dungeon fleet:
# ADR-18 decision 4 already names "a Ready pod that is not yet claiming a world"
# as the condition that unlocks a buffer autoscaler, and a fleet that pins no
# map id is exactly that. Nothing was weakened to let the dungeon autoscaler
# through -- the map fleet's shape still fails, which
# verify/tests/autoscaler_rule_test.sh proves offline on both shapes.
check_no_single_map_autoscaler() {
  local fleets fas rows
  fleets=$(k get fleet -A -o json 2>/dev/null)
  fas=$(k get fleetautoscalers -A -o json 2>/dev/null)

  # VERIFY_FLEET is still asserted to exist: a target that names a fleet which
  # is not there must not read as "swept the namespaces, found nothing wrong".
  if [ -n "${VERIFY_FLEET:-}" ]; then
    local ns="${VERIFY_FLEET%%/*}" name="${VERIFY_FLEET##*/}"
    if ! FLEETS_JSON="$fleets" FAS_JSON="" classify_fleets "$ns" | grep -q "^${ns}/${name}|"; then
      fail "fleet not found" "$VERIFY_FLEET exists" "absent" \
        "kubectl --context $KUBE_CONTEXT get fleet -A"
      return
    fi
  fi

  rows=$(FLEETS_JSON="$fleets" FAS_JSON="$fas" classify_fleets "${VERIFY_NAMESPACES:-}")
  if [ -z "$rows" ]; then
    skip "no Agones Fleet in ${VERIFY_NAMESPACES:-<no namespaces declared>} -- autoscaler posture UNVERIFIED for this target. This is absence of coverage, not evidence that no autoscaler exists."
    return
  fi

  local violations=() summary=() fleet mapid autos
  while IFS='|' read -r fleet mapid autos; do
    [ -z "$fleet" ] && continue
    if [ -n "$mapid" ] && [ -n "$autos" ]; then
      violations+=("$fleet pins GAMESERVER_MAP_ID=$mapid and is targeted by $autos")
      continue
    fi
    if [ -n "$mapid" ]; then
      summary+=("$fleet pins $mapid, no autoscaler (correct)")
    elif [ -n "$autos" ]; then
      summary+=("$fleet pins no map id, autoscaler permitted: $autos")
    else
      summary+=("$fleet pins no map id, no autoscaler (permitted either way)")
    fi
  done <<<"$rows"

  if [ ${#violations[@]} -gt 0 ]; then
    fail "FleetAutoscaler on a single-map fleet" \
      "no FleetAutoscaler targeting a fleet whose template pins a fleet-wide GAMESERVER_MAP_ID" \
      "$(printf '%s; ' "${violations[@]}")" \
      "kubectl --context $KUBE_CONTEXT delete fleetautoscaler <name> -n <ns>  # every Ready pod of a map-pinned fleet self-registers as a second live server for that map (ADR-2/ADR-18); see backend/deploy/docs/K3S.md 'Why there is no autoscaler'"
    return
  fi
  pass "$(printf '%s; ' "${summary[@]}")"
}

# A fleet at its declared size. Agones counts Ready and Allocated separately;
# a fleet whose whole capacity is Allocated has zero spare, which is not an
# error but is worth naming, because an EnterWorld for an unserved map then
# fails with "no server available" instead of the map-mismatch refusal.
check_fleet_replicas() {
  if [ -z "${VERIFY_FLEET:-}" ]; then
    skip "no Agones fleet declared (VERIFY_FLEET empty) -- fleet sizing UNVERIFIED for this target"
    return
  fi
  local ns="${VERIFY_FLEET%%/*}" name="${VERIFY_FLEET##*/}" json
  if ! json=$(k get fleet "$name" -n "$ns" -o json 2>&1); then
    fail "fleet not found" "$VERIFY_FLEET exists" "$(echo "$json" | head -1)" \
      "kubectl --context $KUBE_CONTEXT get fleet -A"
    return
  fi
  local nums
  nums=$(echo "$json" | cluster_python_lf -c '
import json,sys
d=json.load(sys.stdin); st=d.get("status",{})
print(d.get("spec",{}).get("replicas",0), st.get("replicas",0), st.get("readyReplicas",0), st.get("allocatedReplicas",0))
')
  read -r want cur ready alloc <<<"$nums"
  local min="${VERIFY_FLEET_MIN_REPLICAS:-1}"
  if [ "$cur" -lt "$min" ]; then
    fail "fleet below its declared size" "current >= $min (spec.replicas=$want)" \
      "current=$cur ready=$ready allocated=$alloc" \
      "kubectl --context $KUBE_CONTEXT describe fleet $name -n $ns; kubectl get gameserver -n $ns"
    return
  fi
  if [ "$ready" -eq 0 ]; then
    # ready=0 with everything Allocated is two different situations, and the old
    # blanket WARN flattened them.
    #
    # On a fleet that pins GAMESERVER_MAP_ID fleet-wide it is the DESIGNED
    # steady state, not a shortfall: a spare Ready pod on such a fleet is a
    # second live server for that map, so "no spare capacity" is the invariant
    # holding (ADR-2, ADR-18). Warning about it trains the reader to expect a
    # warning here, which is exactly how the buffer autoscaler that breaks the
    # invariant gets proposed as the fix.
    #
    # The one thing the old WARN carried that is worth keeping -- that
    # refusal.unknown_map cannot reach its branch -- is stated by that check's
    # own SKIP, so nothing is lost by saying it once.
    local pinned
    pinned=$(fleet_wide_map_id "$ns" "$name")
    if [ -n "$pinned" ]; then
      pass "current=$cur ready=$ready allocated=$alloc (spec=$want, min=$min). ready=0 is CORRECT here: the template pins GAMESERVER_MAP_ID=$pinned fleet-wide, so a spare Ready pod would be a second live server for $pinned. refusal.unknown_map cannot run in this state and says so."
      return
    fi
    warn "fleet at size but with no spare capacity: current=$cur ready=$ready allocated=$alloc (spec=$want). Every pod is Allocated, so a further EnterWorld -- including the unknown-map refusal probe -- cannot obtain one. This fleet does NOT pin a fleet-wide map id, so spare capacity is meaningful for it and its absence is worth acting on."
    return
  fi
  pass "current=$cur ready=$ready allocated=$alloc (spec=$want, min=$min)"
}

# Restart classification, shared by check_no_restarts. Kept in a variable so the
# python does not have to survive two levels of shell quoting.
_RESTART_CLASSIFY_PY='
import datetime, json, os, sys, time

try:
    doc = json.load(sys.stdin)
except Exception:
    sys.exit(0)

window = int(os.environ.get("RESTART_WINDOW", "1800"))
now = time.time()


def epoch(stamp):
    if not stamp:
        return None
    try:
        return datetime.datetime.strptime(stamp, "%Y-%m-%dT%H:%M:%SZ").replace(
            tzinfo=datetime.timezone.utc).timestamp()
    except ValueError:
        return None


for pod in doc.get("items", []):
    meta = pod.get("metadata", {})
    status = pod.get("status", {})
    inits = status.get("initContainerStatuses") or []
    statuses = [(cs, True) for cs in inits] + [(cs, False) for cs in (status.get("containerStatuses") or [])]
    for cs, is_init in statuses:
        count = cs.get("restartCount", 0)
        if count <= 0:
            continue
        term = (cs.get("lastState") or {}).get("terminated") or {}
        state = cs.get("state") or {}
        term_now = state.get("terminated") or {}
        # An INIT container is supposed to end terminated: exit 0 is its healthy steady
        # state, not a crash. Reading it as "not running" failed every deploy, forever, on
        # any pod whose init container had restarted once -- a host reboot is enough -- which
        # is the permanent-red trap the restartCount note below exists to prevent. Its
        # current termination also stands in for the age when lastState was not kept.
        settled = is_init and term_now.get("exitCode") == 0
        finished = term.get("finishedAt") or (term_now.get("finishedAt") if settled else None)
        exit_code = term.get("exitCode")
        reason = term.get("reason") or "?"
        running = "running" in state
        if running:
            current = "Running since %s" % state["running"].get("startedAt", "?")
        elif settled:
            current = "Completed (init container, exit 0) at %s" % (term_now.get("finishedAt") or "?")
        elif "waiting" in state:
            current = "NOT running -- waiting: %s" % ((state["waiting"] or {}).get("reason") or "?")
        else:
            current = "NOT running -- terminated"
        age = None
        seconds = epoch(finished)
        if seconds is not None:
            age = int(now - seconds)
        age_text = "age unknown" if age is None else "%dm%02ds ago" % (age // 60, age % 60)
        detail = "%s/%s:%s restarts=%d, last exit=%s (%s) at %s [%s], now %s" % (
            meta.get("namespace", "?"), meta.get("name", "?"), cs.get("name", "?"),
            count, exit_code, reason, finished or "unknown", age_text, current)
        # Inside the window, of unknown age, or not running right now: the
        # deployment under test owns it. Anything else predates this run.
        fresh = (not running and not settled) or age is None or age <= window
        print(("RECENT " if fresh else "OLD ") + detail)
'

# restartCount is CUMULATIVE over a pod's whole life and is never reset, so a
# single host reboot -- which on k3d terminates every container in the cluster
# with exit 255 -- makes a bare "restartCount == 0" assertion fail forever, on
# every future deploy, until somebody deletes every pod. That is a check that
# stops describing the deployment under test and starts describing the box's
# uptime, so it is scoped by TIME instead:
#
#   FAIL  a container that restarted inside VERIFY_RESTART_WINDOW seconds, or
#         that is not Running right now (a crash loop, at any age).
#   WARN  a container whose only restarts are older than the window. Reported,
#         named, and explicitly labelled as coverage this check gave up.
#
# The narrowing is deliberate and is printed in the verdict: outside the window
# this check proves nothing, and says so rather than reading as green.
check_no_restarts() {
  local window="${VERIFY_RESTART_WINDOW:-1800}"
  local recent=() old=() ns line
  for ns in $VERIFY_NAMESPACES; do
    while IFS= read -r line; do
      case "$line" in
        "RECENT "*) recent+=("${line#RECENT }") ;;
        "OLD "*)    old+=("${line#OLD }") ;;
      esac
    done < <(k get pods -n "$ns" -o json 2>/dev/null \
      | RESTART_WINDOW="$window" cluster_python_lf -c "$_RESTART_CLASSIFY_PY")
  done

  # One entry per line, continuation lines indented under "observed:".
  _restart_lines() { printf '%s\n' "$@" | sed '2,$s/^/                /'; }

  if [ ${#recent[@]} -gt 0 ]; then
    # Diagnosis, not an exemption. When several containers across more than one
    # namespace all terminated at the SAME second, the cause is the node or the
    # container runtime going down (on k3d: the k3d node container, Docker
    # Desktop, or the WSL2 host), not a workload defect -- no application bug
    # kills the Agones controller and Postgres simultaneously. Saying so here
    # is the difference between a five-minute triage and an hour of it. It
    # still FAILS: a cluster that lost every container minutes before the
    # verification has not been shown to have settled.
    local hint="kubectl --context $KUBE_CONTEXT logs <pod> -n <ns> -c <container> --previous; kubectl --context $KUBE_CONTEXT describe pod <pod> -n <ns>"
    local stamps ns_count
    stamps=$(printf '%s\n' "${recent[@]}" | sed -n 's/.*) at \([^ ]*\) \[.*/\1/p' | sort -u | grep -c . )
    ns_count=$(printf '%s\n' "${recent[@]}" | cut -d/ -f1 | sort -u | wc -l)
    if [ ${#recent[@]} -gt 1 ] && [ "$stamps" = "1" ] && [ "$ns_count" -gt 1 ]; then
      hint="ALL ${#recent[@]} containers, across $ns_count namespaces, died at the SAME instant -- that is the NODE or the container runtime restarting (k3d node container / Docker / the host), not a crash loop. This clears on its own ${window}s after the event; it is not something to fix in a workload. ${hint}"
    fi
    fail "container(s) restarted inside the ${window}s window, or are not running now" \
      "every container Running, with no restart in the last ${window}s (VERIFY_RESTART_WINDOW)" \
      "$(_restart_lines "${recent[@]}")" \
      "$hint"
    return
  fi
  if [ ${#old[@]} -gt 0 ]; then
    warn "every container is Running and none restarted in the last ${window}s, but these carry OLDER restarts that this check deliberately does NOT judge -- outside the window it proves nothing about why they died, so treat the list as unexplained history, not as health: $(_restart_lines "${old[@]}")"
    return
  fi
  pass "no container restart in $VERIFY_NAMESPACES within the last ${window}s, and every container is Running"
}

# The log collector (k8s/logging: Alloy DaemonSet, namespace rpg-k8s-logging) is
# there, Ready on every node it is scheduled to, and attached to Loki.
#
# Opt-in per target, because the same layer-1 checks run against targets that
# predate the collector (dev-agones): VERIFY_LOG_COLLECTOR="<namespace>/<daemonset>"
# declares it, empty means this target has none and the check SKIPs saying so.
#
#   FAIL  the namespace or the DaemonSet is absent, or desired != ready, or desired
#         is 0 (a collector that runs on no node ships nothing)
#   WARN  Ready, but the Service `loki` in that namespace has no EndpointSlice
#         address: Loki is not attached, so Alloy's pushes fail and its buffer
#         drops. Re-run k8s/logging/connect-loki.sh. (A WARN, not a PASS: the
#         collector is healthy and the logs are still going nowhere.)
#   PASS  Ready and the Service has an address
check_log_collector() {
  if [ -z "${VERIFY_LOG_COLLECTOR:-}" ]; then
    skip "no log collector declared for this target (VERIFY_LOG_COLLECTOR empty) -- log shipping UNVERIFIED. NOT a statement that logs flow."
    return
  fi
  local ns="${VERIFY_LOG_COLLECTOR%%/*}" ds="${VERIFY_LOG_COLLECTOR##*/}" svc="${VERIFY_LOG_SERVICE:-loki}"
  local look="kubectl --context $KUBE_CONTEXT -n $ns get ds,pods; backend/deploy/k8s/dev-up.sh applies k8s/logging"
  if ! k get ns "$ns" >/dev/null 2>&1; then
    fail "log collector namespace is absent" "namespace $ns with daemonset/$ds" "namespace $ns not found" "$look"
    return
  fi
  local counts desired ready
  if ! counts=$(k get daemonset "$ds" -n "$ns" -o 'jsonpath={.status.desiredNumberScheduled} {.status.numberReady}' 2>/dev/null); then
    fail "log collector DaemonSet is absent" "daemonset/$ds in $ns" "daemonset/$ds not found in $ns" "$look"
    return
  fi
  # An empty field (a status not filled in yet) reads as 0.
  set -- $counts
  desired="${1:-0}"; ready="${2:-0}"
  case "$desired$ready" in
    *[!0-9]*)
      fail "log collector DaemonSet status is unreadable" "numeric desired/ready counts" "desired='$desired' ready='$ready'" "$look"
      return
      ;;
  esac
  if [ "$desired" -eq 0 ]; then
    fail "log collector is scheduled on no node" "daemonset/$ds desired >= 1 and ready == desired" "desired=0 ready=$ready" "$look"
    return
  fi
  if [ "$desired" -ne "$ready" ]; then
    fail "log collector is not fully Ready" "daemonset/$ds ready == desired" "desired=$desired ready=$ready" \
      "kubectl --context $KUBE_CONTEXT -n $ns describe ds/$ds; kubectl --context $KUBE_CONTEXT -n $ns logs ds/$ds"
    return
  fi
  local addrs
  addrs=$(k get endpointslices -n "$ns" -l "kubernetes.io/service-name=$svc" \
    -o 'jsonpath={range .items[*]}{range .endpoints[*]}{.addresses[*]}{"\n"}{end}{end}' 2>/dev/null | tr -d '[:space:]')
  if [ -z "$addrs" ]; then
    warn "collector daemonset/$ds is Ready ($ready/$desired) but Service $svc in $ns has no EndpointSlice address: Loki is not attached, so no log line leaves the cluster. Start the monitoring stack (lgtm), then run: KUBE_CONTEXT=$KUBE_CONTEXT backend/deploy/k8s/logging/connect-loki.sh"
    return
  fi
  pass "collector daemonset/$ds Ready ($ready/$desired) in $ns; Service $svc has an address (not asserted: that a line reached Loki)"
}

# The Studio hub's identity (k8s/hub, k3s/hub_contract.py) holds EXACTLY the rights
# the contract allows, in every namespace it is granted.
#
# Opt-in per target, like the log collector: VERIFY_HUB_RBAC="<namespace>/<serviceaccount>"
# (rpg-k8s-hub/studio-hub) declares it; empty means this target has no k8s/hub and the
# check SKIPs saying so.
#
# Expected namespaces: rpg-k8s-realtime and rpg-k8s-data, plus rpg-k8s-realtime-<game>
# for every game other than cuvara in gateway-config's `games`. That is the list dev-up.sh
# derives (parse_games / rendered_game_namespaces) from the same live ConfigMap, so there
# is no second list here; verify has no list of game namespaces of its own.
#
# It reads live objects as JSON with `k get` (get only) and compares them through
# k3s/hub_contract.py (`compare`, `bindings`): rules by (apiGroup, resource, verb)
# triples, never by Role name or rule order.
#
#   FAIL  the ServiceAccount is absent; the Role studio-hub is absent from an expected
#         namespace, or has an extra verb, an extra resource, a missing triple, a
#         resourceNames/wildcard rule; its RoleBinding is absent or has another subject
#         or roleRef; ANY other RoleBinding in an expected namespace, or any
#         ClusterRoleBinding, names the ServiceAccount (except studio-hub-nodes); the node
#         ClusterRole differs from the node allow-list by one triple; the node ClusterRole
#         and its binding exist without each other
#   PASS  everything exact. The node ClusterRole absent is a PASS that says node access
#         is not granted (opt-in); present and exact is a PASS that says it is enabled
#
# Read-only: no create, no patch, no delete, and nothing is read from a Secret.
_HUB_CONTRACT_PY="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../../k3s" 2>/dev/null && pwd)/hub_contract.py"
HUB_PROBLEMS=()

# _hub_get <kubectl get args...> -> 0 found (JSON in _HUB_OUT), 1 NotFound, 2 any other error
_hub_get() {
  local rc
  _HUB_OUT=$(k get "$@" -o json 2>&1); rc=$?
  [ "$rc" -eq 0 ] && return 0
  case "$_HUB_OUT" in
    *NotFound*|*"not found"*) return 1 ;;
  esac
  return 2
}

# _hub_py <label> <json> <hub_contract.py args...>: one problem per line the contract prints
_hub_py() {
  local label="$1" json="$2" out rc line
  shift 2
  out=$(printf '%s' "$json" | cluster_python_lf "$_HUB_CONTRACT_PY" "$@"); rc=$?
  if [ "$rc" -eq 0 ]; then
    return 0
  fi
  if [ "$rc" -ne 1 ] || [ -z "$out" ]; then
    HUB_PROBLEMS+=("$label: the comparison (hub_contract.py $1) could not run, exit $rc")
    return 0
  fi
  while IFS= read -r line; do
    [ -n "$line" ] && HUB_PROBLEMS+=("$label: $line")
  done <<<"$out"
}

check_hub_rbac() {
  if [ -z "${VERIFY_HUB_RBAC:-}" ]; then
    skip "no Studio hub declared for this target (VERIFY_HUB_RBAC empty) -- the hub's rights are UNVERIFIED. NOT a statement that they are right."
    return
  fi
  local hub_ns="${VERIFY_HUB_RBAC%%/*}" hub_sa="${VERIFY_HUB_RBAC##*/}"
  local look="kubectl --context $KUBE_CONTEXT -n $hub_ns get sa $hub_sa; backend/deploy/k8s/dev-up.sh applies k8s/hub; python backend/deploy/k8s/games/render.py renders the per-game Role"
  HUB_PROBLEMS=()
  if [ ! -f "$_HUB_CONTRACT_PY" ]; then
    fail "the hub contract is not on this host" "backend/deploy/k3s/hub_contract.py next to backend/deploy/k8s" "$_HUB_CONTRACT_PY not found" "$look"
    return
  fi

  # --- the ServiceAccount
  local rc
  _hub_get serviceaccount "$hub_sa" -n "$hub_ns"; rc=$?
  if [ "$rc" -eq 1 ]; then
    fail "the Studio hub ServiceAccount is absent" "serviceaccount/$hub_sa in namespace $hub_ns" "not found" "$look"
    return
  elif [ "$rc" -ne 0 ]; then
    fail "the Studio hub ServiceAccount could not be read" "serviceaccount/$hub_sa in namespace $hub_ns" "$(printf '%s' "$_HUB_OUT" | head -2)" "$look"
    return
  fi

  # --- the expected namespaces: realtime and data, then the games gateway-config lists
  local games_raw game seen other ns
  local -a namespaces=("rpg-k8s-realtime:realtime" "rpg-k8s-data:data") games=()
  games_raw=$(k get configmap gateway-config -n rpg-k8s-realtime -o 'jsonpath={.data.games}' 2>/dev/null || true)
  local IFS_SAVE="$IFS"
  IFS=',' read -r -a _hub_games <<<"$games_raw"
  IFS="$IFS_SAVE"
  for game in ${_hub_games[@]+"${_hub_games[@]}"}; do
    game="${game#"${game%%[![:space:]]*}"}"; game="${game%"${game##*[![:space:]]}"}"
    [ -n "$game" ] && [ "$game" != cuvara ] || continue
    case "$game" in
      *[!abcdefghijklmnopqrstuvwxyz0123456789]*) HUB_PROBLEMS+=("gateway-config games: '$game' is not a valid game id; its namespace cannot be derived"); continue ;;
    esac
    seen=0
    for other in ${games[@]+"${games[@]}"}; do [ "$other" = "$game" ] && seen=1; done
    [ "$seen" -eq 1 ] || games+=("$game")
  done
  for game in ${games[@]+"${games[@]}"}; do namespaces+=("rpg-k8s-realtime-$game:realtime"); done

  # --- one Role + RoleBinding pair per expected namespace
  local entry shape
  for entry in "${namespaces[@]}"; do
    ns="${entry%%:*}"; shape="${entry##*:}"
    _hub_get role studio-hub -n "$ns"; rc=$?
    if [ "$rc" -eq 1 ]; then
      HUB_PROBLEMS+=("$ns: Role studio-hub is absent")
    elif [ "$rc" -ne 0 ]; then
      HUB_PROBLEMS+=("$ns: Role studio-hub could not be read: $(printf '%s' "$_HUB_OUT" | head -1)")
    else
      _hub_py "$ns: Role studio-hub" "$_HUB_OUT" compare --shape "$shape"
    fi
    _hub_get rolebindings -n "$ns"; rc=$?
    if [ "$rc" -ne 0 ]; then
      HUB_PROBLEMS+=("$ns: RoleBindings could not be read: $(printf '%s' "$_HUB_OUT" | head -1)")
    else
      _hub_py "$ns: RoleBindings" "$_HUB_OUT" bindings --scope namespaced --namespace "$ns"
    fi
  done

  # --- the cluster scope: opt-in node access, and nothing else may name the account
  local node=absent
  _hub_get clusterrole studio-hub-nodes; rc=$?
  if [ "$rc" -eq 0 ]; then
    node=present
    _hub_py "ClusterRole studio-hub-nodes" "$_HUB_OUT" compare --shape node
  elif [ "$rc" -ne 1 ]; then
    HUB_PROBLEMS+=("ClusterRole studio-hub-nodes could not be read: $(printf '%s' "$_HUB_OUT" | head -1)")
  fi
  _hub_get clusterrolebindings; rc=$?
  if [ "$rc" -ne 0 ]; then
    HUB_PROBLEMS+=("ClusterRoleBindings could not be read: $(printf '%s' "$_HUB_OUT" | head -1)")
  else
    _hub_py "ClusterRoleBindings" "$_HUB_OUT" bindings --scope cluster --node "$node"
  fi

  if [ ${#HUB_PROBLEMS[@]} -gt 0 ]; then
    fail "the Studio hub's rights differ from the contract (${#HUB_PROBLEMS[@]} problem(s))" \
      "exactly the allow-list of backend/deploy/k3s/hub_contract.py in: $(printf '%s ' "${namespaces[@]%%:*}")" \
      "$(printf '%s; ' "${HUB_PROBLEMS[@]}")" \
      "$look; kubectl --context $KUBE_CONTEXT get role,rolebinding studio-hub -n <ns> -o yaml"
    return
  fi
  local node_note="node access NOT granted (opt-in: k8s/hub/optional/node-access.yaml is not applied)"
  [ "$node" = present ] && node_note="node access ENABLED (ClusterRole studio-hub-nodes exact, bound to the hub only)"
  pass "ServiceAccount $hub_ns/$hub_sa exists; Role studio-hub equals the allow-list in $(printf '%s ' "${namespaces[@]%%:*}")(rules compared, not names); no other binding names it; $node_note"
}

# Secrets exist and every key holds a non-empty value. Values are never printed
# or logged -- only key names and byte lengths.
check_secrets() {
  if [ -z "${VERIFY_SECRETS:-}" ]; then
    skip "no Secrets declared (VERIFY_SECRETS empty) -- secret presence UNVERIFIED"
    return
  fi
  local bad=() detail=() ref ns name out
  for ref in $VERIFY_SECRETS; do
    ns="${ref%%/*}"; name="${ref##*/}"
    if ! out=$(k get secret "$name" -n "$ns" -o json 2>&1); then
      bad+=("$ref=ABSENT"); continue
    fi
    # Keys that are legitimately empty must be named, one by one, in
    # VERIFY_SECRETS_ALLOW_EMPTY ("ns/name:key,key"). An empty value is
    # normally the failure -- an unset REDIS_PASSWORD looks identical to a
    # forgotten one -- so the exemption is per key and visible in the output,
    # never a blanket relaxation.
    # Append rather than assign: the same secret may legitimately be named in
    # more than one entry, and an assignment here silently drops every
    # exemption but the last -- which surfaces as a FAIL on a key the operator
    # believes they exempted.
    local allowed=""
    for a in ${VERIFY_SECRETS_ALLOW_EMPTY:-}; do
      [ "${a%%:*}" = "$ref" ] && allowed="${allowed:+$allowed,}${a#*:}"
    done
    local res
    res=$(echo "$out" | ALLOW_EMPTY="$allowed" cluster_python_lf -c '
import base64,json,os,sys
d=json.load(sys.stdin); data=d.get("data",{}) or {}
allow={k for k in os.environ.get("ALLOW_EMPTY","").split(",") if k}
if not data: print("EMPTY"); sys.exit(0)
bad=[k for k,v in data.items() if len(base64.b64decode(v or ""))==0 and k not in allow]
def label(k,v):
    n=len(base64.b64decode(v)); return f"{k}({n}B)"+(" empty-by-config" if n==0 else "")
print(("BAD:"+",".join(bad)) if bad else "OK:"+",".join(label(k,v) for k,v in sorted(data.items())))
')
    case "$res" in
      OK:*) detail+=("$ref[${res#OK:}]") ;;
      EMPTY) bad+=("$ref=no keys") ;;
      BAD:*) bad+=("$ref empty keys: ${res#BAD:}") ;;
    esac
  done
  if [ ${#bad[@]} -gt 0 ]; then
    fail "Secret(s) missing or holding an empty value" "every declared key present and non-empty" \
      "${bad[*]}" "kubectl --context $KUBE_CONTEXT get secret -n <ns>"
    return
  fi
  pass "${detail[*]} (key names and byte lengths only; values never read)"
}

register cluster.reachable  1 "cluster answers on the expected context" \
  "kubectl can talk to \$KUBE_CONTEXT and the API server responds" \
  "nothing about workload health; a reachable API server can front a dead cluster" \
  check_cluster_reachable
register cluster.namespaces 1 "expected namespaces exist" \
  "the namespaces the deployment declares are present" \
  "nothing about what is inside them" \
  check_namespaces
register cluster.workloads  1 "every workload fully Ready" \
  "each Deployment/StatefulSet/DaemonSet has all declared replicas Ready" \
  "readiness is the probe's opinion; a workload with no readiness probe passes while broken" \
  check_workloads_ready
register cluster.pvcs       1 "declared PVCs are Bound" \
  "each declared PVC has a bound PV" \
  "nothing about the data on it, nor about PVCs nobody declared" \
  check_pvcs_bound
register cluster.fleet      1 "Agones fleet at its declared size" \
  "the fleet exists and carries at least VERIFY_FLEET_MIN_REPLICAS GameServers" \
  "nothing about whether those pods serve the right map -- see registry.* for that" \
  check_fleet_replicas
register cluster.autoscaler 1 "no buffer autoscaler on a single-map fleet" \
  "no FleetAutoscaler targets a fleet whose pod template pins one GAMESERVER_MAP_ID for every replica -- on such a fleet a spare Ready pod is a second live server for that map, because the C# server self-registers at startup rather than on allocation (ADR-2, ADR-18)" \
  "nothing about fleets outside VERIFY_NAMESPACES, and nothing about a map id supplied per pod via valueFrom -- that case is reported as unpinned and the rule stands down. It does NOT say an autoscaler on a map-less fleet is correctly SIZED: buffer sizing is a capacity question and nothing here measures capacity" \
  check_no_single_map_autoscaler
register cluster.restarts   1 "no recent container restart, and nothing crash-looping" \
  "no container in the target namespaces restarted within the last VERIFY_RESTART_WINDOW seconds (default 1800), and every container is Running right now -- so an active crash loop fails at any age" \
  "restarts OLDER than the window are reported as a WARN with the container named, and are NOT judged: this check says nothing about why they died. It also cannot see a crash that predates the current pod, because restartCount is reset when a pod is recreated" \
  check_no_restarts
register cluster.secrets    1 "declared Secrets present and non-empty" \
  "each declared Secret exists and every key decodes to a non-empty value" \
  "nothing about whether the value is CORRECT -- data.nakama_plugin and flow.smoke prove that end to end. Keys listed in VERIFY_SECRETS_ALLOW_EMPTY are exempted from the non-empty rule and say so in the output" \
  check_secrets
register cluster.log_collector 1 "log collector Ready and attached to Loki" \
  "the Alloy DaemonSet in the logging namespace exists with desired >= 1 and every desired pod Ready, and the Service loki has an EndpointSlice address (Loki attached). FAIL when the namespace or DaemonSet is absent or not ready; WARN when ready but Loki is not attached" \
  "that a log line reached Loki. It does not query Loki, does not read Alloy's own log, and cannot see a collected namespace whose Role is missing (Alloy logs a 403 for it); the address is the one connect-loki.sh wrote, not proof that Loki answers on it. Declared per target by VERIFY_LOG_COLLECTOR; SKIPs when empty" \
  check_log_collector
register cluster.hub_rbac 1 "Studio hub holds exactly its contracted rights" \
  "the ServiceAccount studio-hub exists, and in rpg-k8s-realtime, rpg-k8s-data and every game namespace gateway-config lists its Role studio-hub equals the allow-list of k3s/hub_contract.py triple for triple (an extra verb, an extra resource or a missing one FAILs; rules are compared, not names), its RoleBinding has the one subject and roleRef, no other RoleBinding there and no ClusterRoleBinding names the account except studio-hub-nodes, and that opt-in node ClusterRole is either absent (PASS: node access not granted) or exact" \
  "that the token an operator minted is valid or unexpired, that anything else in the cluster cannot reach the hub's rights (it reads only RoleBindings of the expected namespaces and every ClusterRoleBinding; a RoleBinding naming the account in another namespace is not seen), or that the API server admits it (no kubectl auth can-i, no impersonation). Games come from gateway-config's games, so a rendered game namespace left out of it is not covered. Declared per target by VERIFY_HUB_RBAC; SKIPs when empty" \
  check_hub_rbac
