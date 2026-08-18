#!/usr/bin/env bash
# Layer 3 -- the server registry contract.
#
# Redis layout (backend/shared/storage/redisstore/registry.go):
#   servers:id:{server_id}  HASH, TTL = ServerHeartbeatTTL   <- source of truth
#   servers:map:{map_id}    SET of server ids, no TTL         <- index, pruned lazily
# The index outlives dead servers, so liveness must be read from the hashes.

redis_cli() { $VERIFY_REDIS_EXEC redis-cli ${VERIFY_REDIS_AUTH:+-a "$VERIFY_REDIS_AUTH"} "$@" 2>/dev/null | tr -d '\r'; }

# live_servers -> one "server_id addr transport" line per LIVE registration for
# $VERIFY_MAP_ID. Sets REG_LIVE to the array and REG_STALE to index members
# whose hash has expired.
REG_LIVE=(); REG_STALE=()
collect_registry() {
  REG_LIVE=(); REG_STALE=()
  local id addr trans mid
  # Read every index member FIRST, into an array. Looping directly over the
  # SMEMBERS output while calling redis-cli inside the loop loses members: the
  # inner process inherits the loop's stdin and consumes the remaining lines,
  # so a split world reads back as a single server -- the exact defect this
  # file exists to catch.
  local -a members=()
  mapfile -t members < <(redis_cli SMEMBERS "servers:map:$VERIFY_MAP_ID")
  for id in "${members[@]}"; do
    [ -z "$id" ] && continue
    mid=$(redis_cli HGET "servers:id:$id" map_id </dev/null)
    if [ -z "$mid" ]; then REG_STALE+=("$id"); continue; fi
    addr=$(redis_cli HGET "servers:id:$id" addr </dev/null)
    trans=$(redis_cli HGET "servers:id:$id" transport </dev/null)
    REG_LIVE+=("$id $addr ${trans:-tcp} $mid")
  done
}

check_registry_one_server() {
  if [ -z "${VERIFY_REDIS_EXEC:-}" ]; then
    skip "no Redis exec prefix -- registry contents UNVERIFIED (an in-memory registry cannot be inspected from outside the gateway at all)"
    return
  fi
  collect_registry
  if [ ${#REG_LIVE[@]} -eq 0 ]; then
    fail "no live server registered for the map under test" \
      "exactly one live servers:id:* hash with map_id=$VERIFY_MAP_ID" \
      "0 live (index members with an expired hash: ${REG_STALE[*]:-none})" \
      "the game server pod's registration + heartbeat; kubectl logs on the fleet pod"
    return
  fi
  if [ ${#REG_LIVE[@]} -gt 1 ]; then
    fail "SPLIT WORLD: more than one live server for one map_id (ADR-2 violation)" \
      "exactly one live server for map_id=$VERIFY_MAP_ID" \
      "$(printf '[%s] ' "${REG_LIVE[@]}")" \
      "gateway logs for the duplicate-registration warning; backend/docs/ARCHITECTURE-DECISIONS.md ADR-2"
    return
  fi
  pass "1 live: ${REG_LIVE[0]}${REG_STALE[*]:+ (stale index members pruned lazily: ${REG_STALE[*]})}"
}

# The defect this exists for: a game server that advertised its LISTEN address
# (":9000" / "0.0.0.0:9000") instead of the address a client can reach. It works
# on a single host by accident -- the client rewrites it to loopback -- and is
# unusable the moment the server is a pod. A hostless address must fail here.
check_registry_addr_qualified() {
  if [ -z "${VERIFY_REDIS_EXEC:-}" ]; then
    skip "no Redis exec prefix -- advertised address UNVERIFIED"
    return
  fi
  [ ${#REG_LIVE[@]} -eq 0 ] && collect_registry
  if [ ${#REG_LIVE[@]} -eq 0 ]; then
    skip "no live registration to inspect (see registry.one_server) -- address UNVERIFIED"
    return
  fi
  local bad=() loop=() entry addr host
  for entry in "${REG_LIVE[@]}"; do
    addr=$(echo "$entry" | awk '{print $2}')
    host="${addr%:*}"
    case "$host" in
      ""|"0.0.0.0"|"[::]"|"::"|"*") bad+=("$addr") ;;
      "127.0.0.1"|"localhost"|"::1") loop+=("$addr") ;;
    esac
  done
  if [ ${#bad[@]} -gt 0 ]; then
    fail "advertised address is a LISTEN address, not a reachable one" \
      "host-qualified addr, e.g. 10.42.0.44:7033" \
      "${bad[*]}" \
      "the game server's advertise/host resolution (Agones status.address vs its bind address)"
    return
  fi
  if [ ${#loop[@]} -gt 0 ]; then
    if [ "${VERIFY_ADDR_ALLOW_LOOPBACK:-0}" = "1" ]; then
      warn "advertised address is loopback (${loop[*]}). Accepted for this target because VERIFY_ADDR_ALLOW_LOOPBACK=1 -- correct for a k3d node-port mapped onto the host, WRONG for any deployment where the client is not on the node."
      return
    fi
    fail "advertised address is loopback -- only this host can dial it" \
      "an address reachable from where clients actually are" "${loop[*]}" \
      "the game server's advertise address; set VERIFY_ADDR_ALLOW_LOOPBACK=1 only for single-host dev"
    return
  fi
  pass "host-qualified: $(printf '%s ' "${REG_LIVE[@]}" | awk '{print $2}')"
}

# A registered address that nothing is listening on is the other half of the
# same defect: the registry can be perfectly formed and still point nowhere.
check_registry_addr_dialable() {
  if [ ${#REG_LIVE[@]} -eq 0 ]; then
    skip "no live registration to dial -- reachability UNVERIFIED"
    return
  fi
  local addr host port
  addr=$(echo "${REG_LIVE[0]}" | awk '{print $2}')
  host="${addr%:*}"; port="${addr##*:}"
  [ -z "$host" ] && host=127.0.0.1
  if ! timeout 5 bash -c "exec 3<>/dev/tcp/$host/$port" 2>/dev/null; then
    fail "nothing accepts a TCP connection on the advertised address" \
      "a listener on $addr" "connection refused/timed out" \
      "the game server pod's port mapping (Agones hostPort vs containerPort), and any node firewall"
    return
  fi
  pass "TCP connect to $addr succeeded"
}

register registry.one_server    3 "exactly one live server for the map (ADR-2)" \
  "the Redis registry holds exactly one non-expired server hash for the map under test" \
  "nothing about an in-memory registry (--backend=memory is invisible here), and nothing about servers for OTHER maps" \
  check_registry_one_server
register registry.addr_qualified 3 "advertised address is host-qualified" \
  "the address clients are handed carries a real host, not a listen address like :9000" \
  "it does not prove the host is reachable from the CLIENT's network -- only that an address was formed" \
  check_registry_addr_qualified
register registry.addr_dialable 3 "advertised address accepts a connection" \
  "something is listening on the advertised host:port from where the suite runs" \
  "not that it speaks the game protocol -- flow.smoke proves that" \
  check_registry_addr_dialable
