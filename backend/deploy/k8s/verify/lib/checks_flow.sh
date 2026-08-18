#!/usr/bin/env bash
# Layer 4 -- the full flow, driven by the existing headless client.
#
# backend/smoketest already drives Nakama auth -> gateway_token -> MsgAuth ->
# MsgEnterWorld -> direct game-server dial -> MsgInput/MsgSnapshot -> the
# player_states row -> the reload after the reconnect hold. This layer does not
# reimplement any of that; it runs it with the flags that make it strict.
#
#   --strict-addr    a listen-style ServerAddr is a hard failure instead of
#                    being rewritten to loopback. The rewrite once hid exactly
#                    the defect that made the Agones path unusable.
#   --require-db     a persistence check that cannot run FAILS instead of
#                    skipping, so "no DSN configured" cannot read as green.

check_flow_smoke() {
  local bin="${VERIFY_SMOKETEST_BIN:-}"
  if [ -z "$bin" ] || [ ! -x "$bin" ]; then
    # Build it from source rather than skipping: a missing binary is a property
    # of the runner, not of the deployment, and must not turn into a gap in
    # coverage. Only a build that FAILS is reported as a failure.
    bin="${TMPDIR:-/tmp}/rpg-verify-smoketest"
    local berr
    if ! berr=$( cd "$HERE/../../../smoketest" && go build -o "$bin" ./cmd/smoketest 2>&1 ); then
      fail "smoketest binary unavailable and could not be built" \
        "an executable at VERIFY_SMOKETEST_BIN, or a buildable backend/smoketest" \
        "$(echo "$berr" | tail -2)" \
        "cd backend/smoketest && go build ./cmd/smoketest (is go on PATH?)"
      return
    fi
  fi
  local -a args=(
    --nakama-url "$VERIFY_NAKAMA_URL"
    --server-key "$VERIFY_NAKAMA_SERVER_KEY"
    --gateway-addr "$VERIFY_GATEWAY_ADDR"
    --map-id "$VERIFY_MAP_ID"
    --strict-addr
    --expect-migration-version "${VERIFY_GAME_MIGRATION:-1}"
  )
  local db_mode
  if [ -n "${VERIFY_GAME_DB_URL:-}" ]; then
    args+=(--game-db-url "$VERIFY_GAME_DB_URL" --require-db)
    db_mode="persistence checks REQUIRED"
  else
    args+=(--skip-db)
    db_mode="persistence checks SKIPPED (VERIFY_GAME_DB_URL unset)"
  fi
  [ -n "${VERIFY_HOLD_TTL:-}" ] && args+=(--hold-ttl "$VERIFY_HOLD_TTL")

  local out rc
  out=$(JWT_SECRET="$VERIFY_JWT_SECRET" "$bin" "${args[@]}" 2>&1); rc=$?
  echo "$out" | sed 's/^/      | /'
  if [ $rc -ne 0 ] || [[ "$out" != *"SMOKE=PASS"* ]]; then
    fail "the end-to-end flow did not complete" \
      "SMOKE=PASS and exit 0" "exit=$rc; last line: $(echo "$out" | tail -1)" \
      "the transcript above -- the first failing step names the hop"
    return
  fi
  if [ -z "${VERIFY_GAME_DB_URL:-}" ]; then
    warn "flow passed but WITHOUT persistence: $db_mode. Movement and snapshots are proven; the player_states write and the reload after the hold are NOT."
    return
  fi
  VERIFY_SMOKE_OUTPUT="$out"
  pass "SMOKE=PASS with --strict-addr and $db_mode"
}

# Attribute the GATEWAY, not just the registry. registry.stack_identity proves
# the Redis being read is this deployment's; this proves the gateway actually
# DIALED belongs to it too, which is the half that a leftover stack on the
# conventional ports would otherwise satisfy silently.
VERIFY_SMOKE_OUTPUT=""
check_flow_stack_identity() {
  if [ -z "${VERIFY_FLEET:-}" ]; then
    skip "no VERIFY_FLEET declared -- the gateway that answered cannot be attributed to this deployment"
    return
  fi
  if [ -z "$VERIFY_SMOKE_OUTPUT" ]; then
    skip "flow.smoke did not run or did not complete -- nothing to attribute (this is NOT a pass: the gateway that answered $VERIFY_GATEWAY_ADDR is unidentified)"
    return
  fi
  # The smoke test prints: PASS gateway_auth ... server=<host:port> (tcp)
  local got
  got=$(printf '%s\n' "$VERIFY_SMOKE_OUTPUT" | sed -n 's/.*[[:space:]]server=\([^[:space:]]*\).*/\1/p' | head -1)
  if [ -z "$got" ]; then
    skip "could not read the assigned server address out of the smoke transcript -- gateway UNATTRIBUTED"
    return
  fi
  local ns="${VERIFY_FLEET%%/*}" fleet="${VERIFY_FLEET##*/}" want=""
  # Every address this fleet's GameServers can advertise: the Agones-assigned
  # port composed with the advertise host the fleet is configured with.
  want=$(k get gs -n "$ns" -l "agones.dev/fleet=$fleet" \
    -o jsonpath='{range .items[*]}{.status.ports[0].port}{"\n"}{end}' 2>/dev/null)
  local port="${got##*:}" ok=0 p
  for p in $want; do [ "$p" = "$port" ] && ok=1; done
  if [ "$ok" != "1" ]; then
    fail "the gateway that answered does not belong to this deployment" \
      "an assigned server on a port of fleet $VERIFY_FLEET (ports: $(echo $want | tr '\n' ' '))" \
      "gateway $VERIFY_GATEWAY_ADDR assigned $got" \
      "another stack is answering on $VERIFY_GATEWAY_ADDR -- check for a leftover compose gateway on that port"
    return
  fi
  pass "gateway $VERIFY_GATEWAY_ADDR assigned $got, whose port belongs to fleet $VERIFY_FLEET -- the stack under test is the one that answered"
}

register flow.smoke 4 "full client flow end to end, strict address" \
  "auth, gateway token, EnterWorld, a DIRECT dial of the advertised game-server address, input->snapshot, the player_states write and the reload after the reconnect hold" \
  "one client on one map; it says nothing about concurrency, capacity or any map other than \$VERIFY_MAP_ID" \
  check_flow_smoke

register flow.stack_identity 4 "the gateway that answered IS this deployment" \
  "the server the dialed gateway assigned is a GameServer of the fleet under test, so the whole flow ran against this stack and not a leftover one on the same port" \
  "it cannot distinguish two deployments that share a fleet; it distinguishes THIS stack from any other one answering the same address" \
  check_flow_stack_identity
