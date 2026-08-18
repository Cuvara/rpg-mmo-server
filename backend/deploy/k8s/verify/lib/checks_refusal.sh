#!/usr/bin/env bash
# Layer 6 -- the refusals. A deployment that only serves the happy path is not
# verified; these assert that the failures fail in the RIGHT way.

# --- an unserved map must be refused NON-retryably ------------------------
#
# Four distinct client-facing strings, and the difference is load-bearing
# (backend/gateway/server/server.go):
#   "map is not available"              terminal   -- no fleet hosts this map
#   "server is starting, retry shortly" retryable  -- allocated, still booting
#   "no server available for map"       terminal   -- the map is full, or the
#                                                     allocation failed for a
#                                                     reason other than an
#                                                     empty fleet
#   "all servers busy, retry shortly"   retryable  -- the fleet has no Ready pod
#
# The last one is retryable and safe to retry (#157): it is returned when the
# allocation API answers UnAllocated, a decoded 2xx body stating that NO
# GameServer was handed out, so the retry it invites leaks nothing. It is also
# the string this probe sees when the fleet is empty, which is precisely when
# the branch under test was never reached -- so it is INCONCLUSIVE here, not a
# failure. Do not "fix" that into a fail: it would make an empty fleet look like
# a gateway bug.
#
# A client told "retry shortly" for a map no fleet serves retries, and every
# retry permanently consumes a GameServer: Agones has no un-allocate and the
# gateway has no Deallocate. That is the leak rememberMismatch bounds.
#
# COST: this probe itself can allocate one GameServer that is never reclaimed,
# so it is opt-in via VERIFY_ALLOW_ALLOCATION=1 and skips loudly otherwise.
check_refusal_unknown_map() {
  if [ "${VERIFY_ALLOW_ALLOCATION:-0}" != "1" ]; then
    skip "unknown-map refusal NOT exercised. Enabling it (VERIFY_ALLOW_ALLOCATION=1) makes the gateway attempt one Agones allocation for a map no fleet serves; that GameServer is Allocated and never reclaimed (no un-allocate exists), so on a replicas:1 fleet it consumes the fleet. Run it against a scratch deployment, or a fleet with spare Ready replicas."
    return
  fi
  local map="${VERIFY_UNKNOWN_MAP:-map_does_not_exist_verify}"
  local out
  out=$(run_probe enterworld --map-id "$map" 2>&1)
  case "$out" in
    *'RESULT=ok'*)
      fail "the gateway ADMITTED a map no fleet serves" \
        "a refusal for map=$map" "$(echo "$out" | head -1)" \
        "ALLOCATOR_FLEET_MAP vs the fleet's GAMESERVER_MAP_ID"
      ;;
    *'message="map is not available"'*)
      pass "map=$map refused terminally: \"map is not available\" (non-retryable, as required)"
      ;;
    *'message="server is starting, retry shortly"'*)
      fail "an unserved map was refused RETRYABLY -- every retry leaks a GameServer" \
        '"map is not available" (terminal)' \
        '"server is starting, retry shortly" (retryable)' \
        "RegistryService.rememberMismatch / clientSafeAssignError in backend/gateway"
      ;;
    *'message="all servers busy, retry shortly"'*)
      skip "INCONCLUSIVE, not a pass: the gateway answered \"all servers busy, retry shortly\", which means the fleet had no Ready GameServer to allocate, so the map-mismatch branch was never reached. This is the expected answer for an empty fleet since #157 and is NOT a gateway fault -- retrying it allocates nothing. Re-run with spare Ready replicas in the fleet (see cluster.fleet)."
      ;;
    *'message="no server available for map"'*)
      skip "INCONCLUSIVE, not a pass: the gateway answered \"no server available for map\", which since #157 means the allocation failed for a reason OTHER than an empty fleet (transport error, non-2xx, undecodable body), so the map-mismatch branch was never reached. Check the gateway logs for the allocator error before re-running."
      ;;
    *)
      fail "unexpected answer to the unknown-map probe" \
        "one of the four known refusal strings" "$(echo "$out" | head -2)" \
        "backend/gateway/server/server.go clientSafeAssignError"
      ;;
  esac
}

# --- the gateway must refuse to start on a suicidal allocation wait -------
#
# EnterWorld blocks the connection's read loop for the allocation wait, and that
# same loop records the client's MsgPong. A wait at or above
# MaxHandlerBlockingWait (pongTimeout - pingInterval) therefore makes the
# gateway heartbeat-disconnect the very client it is waiting for -- a symptom
# that points nowhere near its cause. The binary is expected to exit non-zero
# rather than start.
#
# This runs a THROWAWAY container: no published ports, no network attachment to
# the live stack, an in-memory backend, and it is expected to die on startup. It
# cannot disturb the running deployment.
check_refusal_alloc_wait() {
  if [ -z "${VERIFY_GATEWAY_IMAGE:-}" ]; then
    skip "no gateway image declared (VERIFY_GATEWAY_IMAGE empty) -- the start-up guard is UNVERIFIED"
    return
  fi
  if ! have docker; then
    skip "docker not on PATH -- cannot run the throwaway gateway, guard UNVERIFIED"
    return
  fi
  local wait="${VERIFY_BAD_ALLOC_WAIT:-60s}" out rc
  out=$(docker run --rm --network none \
      -e JWT_SECRET=verify-only-not-a-real-secret \
      -e JOIN_TOKEN_SECRET=verify-only-not-a-real-secret \
      "$VERIFY_GATEWAY_IMAGE" \
      --allocator=agones --backend=memory --allocation-wait-timeout="$wait" 2>&1); rc=$?
  if [ $rc -eq 0 ]; then
    fail "the gateway STARTED with an allocation wait that outlives the client heartbeat" \
      "non-zero exit and a refusal to start at --allocation-wait-timeout=$wait" \
      "exit 0" \
      "backend/gateway/cmd/gateway/main.go, the MaxHandlerBlockingWait guard"
    return
  fi
  if [[ "$out" != *"refusing to start"* ]]; then
    fail "the gateway exited non-zero, but not for the reason under test" \
      "a log line containing 'allocation wait would starve the client heartbeat; refusing to start'" \
      "exit=$rc; $(echo "$out" | tail -2)" \
      "the container output above -- it may have died on an unrelated misconfiguration"
    return
  fi
  pass "refused to start at --allocation-wait-timeout=$wait (exit $rc), naming the heartbeat as the reason"
}

# --- a split world must be DETECTED, not merely absent --------------------
#
# ADR-2: exactly one live server per map_id. The gateway warns when it sees more
# ("map served by multiple game servers; the world is split..."). Two failure
# modes matter and they are not the same: a split world, and a split world
# nobody noticed.
check_refusal_split_world() {
  if [ -z "${VERIFY_REDIS_EXEC:-}" ]; then
    skip "no Redis exec prefix -- cannot count registrations, split-world detection UNVERIFIED"
    return
  fi
  [ ${#REG_LIVE[@]} -eq 0 ] && collect_registry
  local warned=0 logs=""
  if [ -n "${VERIFY_GATEWAY_LOG_CMD:-}" ]; then
    logs=$($VERIFY_GATEWAY_LOG_CMD 2>&1 | grep -c "map served by multiple game servers" || true)
    [ "${logs:-0}" -gt 0 ] && warned=1
  fi
  if [ ${#REG_LIVE[@]} -gt 1 ]; then
    if [ "$warned" = "1" ]; then
      fail "SPLIT WORLD: ${#REG_LIVE[@]} live servers for map_id=$VERIFY_MAP_ID (the gateway did warn)" \
        "one live server (ADR-2)" "$(printf '[%s] ' "${REG_LIVE[@]}")" \
        "gateway logs: 'map served by multiple game servers'"
    else
      fail "SPLIT WORLD, UNDETECTED: ${#REG_LIVE[@]} live servers for map_id=$VERIFY_MAP_ID and no gateway warning" \
        "one live server, or -- failing that -- a gateway warning naming the split" \
        "$(printf '[%s] ' "${REG_LIVE[@]}") ; warning lines found: ${logs:-0}" \
        "RegistryService.FindServer's duplicate branch; and whether VERIFY_GATEWAY_LOG_CMD reaches the right gateway"
    fi
    return
  fi
  if [ -z "${VERIFY_GATEWAY_LOG_CMD:-}" ]; then
    warn "one live server (no split), but the gateway's split-world WARNING could not be checked: VERIFY_GATEWAY_LOG_CMD is unset. The detector is unproven; only the current absence of a split is."
    return
  fi
  if [ "$warned" = "1" ]; then
    fail "the gateway warned about a split world that the registry does not currently show" \
      "no warning while exactly one server is registered" \
      "$logs warning line(s) in the gateway log, 1 live registration now" \
      "the gateway log -- a split existed earlier in this window and may recur"
    return
  fi
  pass "1 live server for map_id=$VERIFY_MAP_ID and no split-world warning in the gateway log"
}

register refusal.unknown_map  6 "an unserved map is refused non-retryably" \
  "a map no fleet hosts gets the terminal refusal, so a client cannot retry-loop the allocation leak" \
  "it costs one unreclaimable GameServer to run, and it is INCONCLUSIVE when the fleet has no Ready replica to allocate" \
  check_refusal_unknown_map
register refusal.alloc_wait   6 "gateway refuses a suicidal allocation wait" \
  "the binary exits rather than starting with an allocation wait that outlives the client heartbeat" \
  "it tests the IMAGE, not the running gateway's actual configured value -- a deployment can still be misconfigured below the guard" \
  check_refusal_alloc_wait
register refusal.split_world  6 "split world detected and reported (ADR-2)" \
  "at most one live server per map_id, and that the gateway's own duplicate-detection warning agrees with the registry" \
  "it cannot MANUFACTURE a split, so a clean run proves the detector was silent when it should be, not that it fires when it should" \
  check_refusal_split_world
