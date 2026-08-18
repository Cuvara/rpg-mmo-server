#!/usr/bin/env bash
# Layer 1 -- cluster invariants. Read-only kubectl, always against $KUBE_CONTEXT.

k() { kubectl --context "$KUBE_CONTEXT" "$@"; }

check_cluster_reachable() {
  local out
  if ! out=$(k version -o json 2>&1); then
    fail "kubectl cannot reach the cluster" \
      "context $KUBE_CONTEXT answers" \
      "$(echo "$out" | head -2)" \
      "kubectl config get-contexts; k3d cluster list"
    return
  fi
  pass "context=$KUBE_CONTEXT server=$(k version -o json 2>/dev/null | python3 -c 'import json,sys;print(json.load(sys.stdin).get("serverVersion",{}).get("gitVersion","?"))' 2>/dev/null)"
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
    done < <(k get deploy,statefulset,daemonset -n "$ns" -o json 2>/dev/null | python3 -c '
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
  k get fleet "$name" -n "$ns" -o json 2>/dev/null | python3 -c '
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

# Names any FleetAutoscaler in the cluster whose fleetName is this fleet.
# Cluster-wide on purpose: an autoscaler in the wrong namespace does nothing,
# and one in the right namespace is the whole hazard, so the namespace is
# compared rather than pre-filtered.
fleet_autoscalers() {
  local ns="$1" name="$2"
  k get fleetautoscalers -A -o json 2>/dev/null | python3 -c '
import json,sys
ns,fleet=sys.argv[1],sys.argv[2]
try: d=json.load(sys.stdin)
except Exception: sys.exit(0)
for it in d.get("items",[]):
    m=it.get("metadata",{}); s=it.get("spec",{})
    if m.get("namespace")==ns and s.get("fleetName")==fleet:
        pol=s.get("policy",{}).get("type","?")
        print("%s/%s(policy=%s)" % (m.get("namespace"), m.get("name"), pol))
' "$ns" "$name"
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
check_no_single_map_autoscaler() {
  if [ -z "${VERIFY_FLEET:-}" ]; then
    skip "no Agones fleet declared (VERIFY_FLEET empty) -- autoscaler posture UNVERIFIED for this target"
    return
  fi
  local ns="${VERIFY_FLEET%%/*}" name="${VERIFY_FLEET##*/}"
  if ! k get fleet "$name" -n "$ns" >/dev/null 2>&1; then
    fail "fleet not found" "$VERIFY_FLEET exists" "absent" \
      "kubectl --context $KUBE_CONTEXT get fleet -A"
    return
  fi

  local mapid autos
  mapid=$(fleet_wide_map_id "$ns" "$name")
  autos=$(fleet_autoscalers "$ns" "$name" | tr '\n' ' ')
  autos="${autos% }"

  if [ -z "$mapid" ]; then
    if [ -n "$autos" ]; then
      pass "fleet pins no fleet-wide GAMESERVER_MAP_ID, so a spare Ready pod is genuinely spare; autoscaler(s) permitted: $autos"
    else
      pass "fleet pins no fleet-wide GAMESERVER_MAP_ID; no autoscaler present (permitted either way)"
    fi
    return
  fi

  if [ -n "$autos" ]; then
    fail "FleetAutoscaler on a single-map fleet" \
      "no FleetAutoscaler targeting $VERIFY_FLEET while its template pins GAMESERVER_MAP_ID=$mapid" \
      "$autos" \
      "kubectl --context $KUBE_CONTEXT delete fleetautoscaler <name> -n $ns  # every Ready pod self-registers as a second live server for $mapid (ADR-2/ADR-18); see backend/deploy/docs/K3S.md 'Why there is no autoscaler'"
    return
  fi
  pass "no FleetAutoscaler targets $VERIFY_FLEET, which pins GAMESERVER_MAP_ID=$mapid fleet-wide -- a buffer of Ready pods would be a buffer of live servers for $mapid"
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
  nums=$(echo "$json" | python3 -c '
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

check_no_restarts() {
  local bad=() ns line
  for ns in $VERIFY_NAMESPACES; do
    while read -r line; do
      [ -n "$line" ] && bad+=("$line")
    done < <(k get pods -n "$ns" -o json 2>/dev/null | python3 -c '
import json,sys
try: d=json.load(sys.stdin)
except Exception: sys.exit(0)
for p in d.get("items",[]):
    for cs in p.get("status",{}).get("containerStatuses",[]) or []:
        if cs.get("restartCount",0) > 0:
            print(f'"'"'{p["metadata"]["namespace"]}/{p["metadata"]["name"]}:{cs["name"]}={cs["restartCount"]}'"'"')
')
  done
  if [ ${#bad[@]} -gt 0 ]; then
    fail "container(s) have restarted" "restartCount == 0 for every container" \
      "${bad[*]}" \
      "kubectl --context $KUBE_CONTEXT logs <pod> -n <ns> --previous"
    return
  fi
  pass "no container restart in $VERIFY_NAMESPACES since pod creation"
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
    res=$(echo "$out" | ALLOW_EMPTY="$allowed" python3 -c '
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
  "nothing about fleets it does not name, and nothing about a map id supplied per pod via valueFrom -- that case is reported as unpinned and the rule stands down" \
  check_no_single_map_autoscaler
register cluster.restarts   1 "no container has restarted" \
  "no container in the target namespaces has restartCount > 0 since it was created" \
  "the window is pod lifetime, not a fixed period; a pod recreated a minute ago hides yesterday's crash loop" \
  check_no_restarts
register cluster.secrets    1 "declared Secrets present and non-empty" \
  "each declared Secret exists and every key decodes to a non-empty value" \
  "nothing about whether the value is CORRECT -- data.nakama_plugin and flow.smoke prove that end to end. Keys listed in VERIFY_SECRETS_ALLOW_EMPTY are exempted from the non-empty rule and say so in the output" \
  check_secrets
