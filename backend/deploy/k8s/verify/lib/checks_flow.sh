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
  # A VERIFY_SMOKETEST_BIN that is set but not runnable is a configuration
  # error, not a missing toolchain: falling through to a source build here would
  # hide a stale bundle path behind a "go: command not found".
  if [ -n "$bin" ] && [ ! -x "$bin" ]; then
    fail "VERIFY_SMOKETEST_BIN does not point at an executable" \
      "an executable file at the configured path" \
      "VERIFY_SMOKETEST_BIN=$bin ($([ -e "$bin" ] && echo "exists but is not executable" || echo "does not exist"))" \
      "the deployment bundle: CI exports this from dist/bin/smoketest, built by the build-smoketest job"
    return
  fi
  if [ -z "$bin" ]; then
    # No prebuilt binary configured. Build it from source rather than skipping:
    # a missing binary is a property of the runner, not of the deployment, and
    # must not turn into a gap in coverage. Only a build that FAILS is a failure.
    bin="${TMPDIR:-/tmp}/rpg-verify-smoketest"
    local berr
    if ! berr=$( cd "$HERE/../../../smoketest" && go build -o "$bin" ./cmd/smoketest 2>&1 ); then
      fail "smoketest binary unavailable and could not be built" \
        "an executable at VERIFY_SMOKETEST_BIN, or a buildable backend/smoketest" \
        "$(echo "$berr" | tail -2)" \
        "set VERIFY_SMOKETEST_BIN to a prebuilt binary (this is what CI does -- the deploy runner has no Go toolchain), or: cd backend/smoketest && go build ./cmd/smoketest"
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
  # The meta hop's pin (ADR-24). The smoketest refuses an https URL without it
  # rather than failing inside an x509 message several steps from the cause.
  if [ -n "${VERIFY_NAKAMA_TLS_CERT:-}" ]; then
    args+=(--nakama-tls-cert "$VERIFY_NAKAMA_TLS_CERT")
  fi
  local db_mode
  if [ -n "${VERIFY_GAME_DB_URL:-}" ]; then
    args+=(--game-db-url "$VERIFY_GAME_DB_URL" --require-db)
    db_mode="persistence checks REQUIRED"
  else
    args+=(--skip-db)
    db_mode="persistence checks SKIPPED (VERIFY_GAME_DB_URL unset)"
  fi
  [ -n "${VERIFY_HOLD_TTL:-}" ] && args+=(--hold-ttl "$VERIFY_HOLD_TTL")

  # Sealed gameplay hop. This layer does NOT read deploy/.env -- that file
  # describes the compose stack and k8s mode did not deploy it -- so the
  # SMOKE_SEALED/SMOKE_ENCODING derivation in cd.yml's generator does not reach
  # here. The target file is this path's equivalent, and VERIFY_SEALED is its
  # one input.
  #
  # Both flags, never one. A sealed run is necessarily a protobuf run: the JSON
  # codec has no sealed frame, so a `require` server refuses a JSON client at
  # the join with `encoding_cannot_seal`. The binary refuses `-sealed` without
  # `-encoding proto` for exactly this reason, so getting it wrong here is a
  # startup error rather than a confusing mid-run failure.
  #
  # Unset means unsealed. That is NO LONGER what every environment is: as of
  # 2026-09-11 the k8s targets (k8s-dev.env, k8s-stg.env) default VERIFY_SEALED
  # to 1, matching GAMESERVER_SEALED=require in backend/deploy/k8s/app/50-fleet-map.yaml.
  # The dev-agones target still defaults to unset/0, matching the separate
  # agones/fleet-map-dotnet-dev.yaml fleet, which stays "off". This must match
  # the fleet manifest's GAMESERVER_SEALED for the environment being verified,
  # and a mismatch is a REAL failure: the server refusing a client it is
  # configured to refuse is the check working.
  local sealed_mode="plaintext gameplay hop"
  if [ "${VERIFY_SEALED:-0}" != "0" ]; then
    args+=(--sealed --encoding proto)
    sealed_mode="SEALED gameplay hop (chacha20-poly1305 over protobuf)"
  fi

  # Gateway-hop TLS (ADR-23). Same shape as VERIFY_SEALED above and the same
  # rule: this must match the deployment, and a mismatch is a REAL failure.
  #
  # It has to be here or this check becomes the thing that breaks when TLS is
  # turned on. A TLS listener cannot answer a plaintext client in a language it
  # understands, so an unpinned smoke run against a TLS gateway fails with a
  # bare `read length: EOF` -- no mention of TLS, from either end. Measured on
  # k3d-rpg-dev 2026-09-13, alongside the two cases that DO name themselves: the
  # correct pin passes, and a different valid self-signed certificate is refused
  # with "gateway certificate does not match the pin".
  #
  # The pin is a FILE PATH, and dev-up.sh writes it out of the cluster's own
  # gateway-tls Secret, so the verifier pins what the gateway actually serves
  # rather than a copy someone remembered to update.
  # THE CLUSTER decides whether TLS is on, not a target file and not whether a
  # pin file happens to exist. Both of those can disagree with the deployment,
  # and each disagreement fails in a way that blames the wrong thing: a stale
  # pin against a plaintext gateway fails the handshake, and a missing pin
  # against a TLS gateway fails with a bare EOF.
  local tls_mode="plaintext gateway hop"
  local gw_tls_path
  gw_tls_path=$(k get configmap gateway-config -n rpg-k8s-realtime \
    -o 'jsonpath={.data.tls-cert-path}' 2>/dev/null || true)

  if [ -n "$gw_tls_path" ]; then
    # Read out of THIS cluster's gateway-tls Secret, not from a run directory.
    #
    # It used to fall back to ${RPG_K8S_RUN_DIR:-/tmp/claude-1000/rpg-k8s-dev}/gateway-tls.crt.
    # CD sets RPG_K8S_RUN_DIR on the deploy step only, so the verify step always took
    # the default -- DEV's directory. That is correct on dev by accident and wrong on
    # every other cluster: the first staging deploy with gateway TLS failed
    #   gateway certificate does not match the pin (presented 930 bytes, pinned 930)
    # because it pinned dev's certificate (sha256 5A:AA:6F...) against staging's
    # (39:45:51...). Same size, different key, and nothing named the cause. The
    # certificate the gateway actually serves comes from this Secret, so the pin does.
    local pin="${VERIFY_GATEWAY_TLS_CERT:-}"
    if [ -z "$pin" ]; then
      pin="$(mktemp "${TMPDIR:-/tmp}/verify-gateway-pin.XXXXXX")"
      k get secret gateway-tls -n rpg-k8s-realtime \
        -o 'jsonpath={.data.tls\.crt}' 2>/dev/null | base64 -d >"$pin" 2>/dev/null || true
    fi
    if ! grep -q "BEGIN CERTIFICATE" "$pin" 2>/dev/null; then
      fail "the gateway terminates TLS but no pinned certificate is readable" \
        "a PEM the gateway's certificate must match" \
        "gateway-config names $gw_tls_path; no certificate at $pin" \
        "kubectl --context $KUBE_CONTEXT -n rpg-k8s-realtime get secret gateway-tls, or set VERIFY_GATEWAY_TLS_CERT"
      return
    fi
    args+=(--gateway-tls-cert "$pin")
    tls_mode="TLS gateway hop, certificate PINNED ($pin)"
  elif [ -n "${VERIFY_GATEWAY_TLS_CERT:-}" ]; then
    # A pin was supplied for a gateway that is not serving TLS. Refusing rather
    # than ignoring it: somebody believes this hop is encrypted and it is not,
    # which is exactly the state nothing else in this deployment would reveal.
    fail "a gateway pin was supplied but the gateway is NOT terminating TLS" \
      "either TLS on (gateway-config tls-cert-path) or no pin" \
      "pin=$VERIFY_GATEWAY_TLS_CERT; gateway-config names no tls-cert-path" \
      "kubectl --context $KUBE_CONTEXT -n rpg-k8s-realtime get configmap gateway-config -o yaml"
    return
  fi

  local out rc
  out=$(JWT_SECRET="$VERIFY_JWT_SECRET" "$bin" "${args[@]}" 2>&1); rc=$?
  echo "$out" | sed 's/^/      | /'
  if [ $rc -ne 0 ] || [[ "$out" != *"SMOKE=PASS"* ]]; then
    fail "the end-to-end flow did not complete" \
      "SMOKE=PASS and exit 0 ($sealed_mode; $tls_mode)" "exit=$rc; last line: $(echo "$out" | tail -1)" \
      "the transcript above -- the first failing step names the hop"
    return
  fi
  if [ -z "${VERIFY_GAME_DB_URL:-}" ]; then
    warn "flow passed but WITHOUT persistence: $db_mode ($sealed_mode; $tls_mode). Movement and snapshots are proven; the player_states write and the reload after the hold are NOT."
    return
  fi
  VERIFY_SMOKE_OUTPUT="$out"
  pass "SMOKE=PASS with --strict-addr, $db_mode, $sealed_mode, $tls_mode"
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
