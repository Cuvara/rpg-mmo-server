# GameServer .NET — Runbook

Operational procedures for the C# game server. Deployment, configuration and the
metric catalogue live in `README.md` and `METRICS.md`; this file holds the
*procedures* — things an operator runs against a live server.

## Probing admission hardening (`scripts/admission-probe.py`)

**Owner rule: a feature is not done when its tests pass; it must be exercised for
real.** This probe is the live-path check for the admission hardening shipped for
workspace audit F03/F04 (bounded, deadlined handshake; atomic capacity; bounded
input ingestion). It talks to a **running** game server over real TCP sockets and
its `/metrics` endpoint — python3 stdlib only, no build, no third-party modules.

### What it checks

| Check | What it does | PASS means |
|-------|--------------|------------|
| `idle` | Connects, sends nothing, times the close | Closed within `[0.5×, +3s]` of `GAMESERVER_HANDSHAKE_TIMEOUT_MS` |
| `pool` / `pool-drain` | Opens `N+1` idle connections where `N = GAMESERVER_MAX_PENDING_HANDSHAKES` | ≥1 closed at accept (within 50 ms); `gameserver_handshakes_pending == N`; `gameserver_handshakes_rejected_total{reason="pool_full"}` moved; gauge drains to 0 after the sockets close |
| `partial-prefix` | Sends 2 of the 4 length-prefix bytes, then silence | Closed at the deadline |
| `malformed-frame` | Sends a complete frame with an undecodable body | Closed immediately (well under the deadline) |
| `malformed-counters` | Re-reads `/metrics` | Pending gauge back to baseline; `reason="timeout"` and `reason="malformed"` each +1 |
| `flood-honest-acked` | Joins two players (mints HS256 join tokens itself), floods 6000 `MsgInput` from one, sends one input from the other | The second player's input is acknowledged (`ack_tick`) in its snapshot stream |
| `flood-counters` | Polls `/metrics` | `gameserver_inputs_dropped_total` and `gameserver_inputs_coalesced_total` both moved |

Every check prints `[PASS]`/`[FAIL]` with the measured values; exit code is
non-zero if any check fails. `--only <check>` (repeatable) runs a subset.

### Running it

The probe must be told the server's own settings — it cannot read them — and the
flood check needs the join-token secret and server id to mint tokens.

**Against a throwaway local instance** (safe anywhere; pick ports that are not the
shared stack's 9000/9101):

```bash
cd backend/gameserver-dotnet

JWT_SECRET=probesecret JOIN_TOKEN_SECRET=probesecret GAMESERVER_ID=gs-probe GAMESERVER_ENEMIES=false \
  dotnet run --project GameServer -- \
    --addr=127.0.0.1:19000 --metrics-addr=127.0.0.1:19101 \
    --max-pending-handshakes=4 --handshake-timeout-ms=2000 --capacity=10 &

# wait for "Game server listening on 127.0.0.1:19000"
python3 scripts/admission-probe.py \
  --port 19000 --metrics http://127.0.0.1:19101 \
  --max-pending 4 --timeout-ms 2000 \
  --secret probesecret --server-id gs-probe
```

Small `--max-pending` and `--handshake-timeout-ms` make the run take seconds; the
production defaults (256 / 5000) work too but the pool check then opens 257
sockets and the deadline checks wait 5 s each.

**Against the docker stack**: point `--port`/`--metrics` at the game server's
published ports and pass the stack's `GAMESERVER_MAX_PENDING_HANDSHAKES`,
`GAMESERVER_HANDSHAKE_TIMEOUT_MS`, `JOIN_TOKEN_SECRET` and `GAMESERVER_ID`. The flood
check joins two real players (`probe-flooder`, `probe-honest`) and leaves them to
the 30 s reconnect hold when it disconnects — run it on a quiet server, not during
a load test whose numbers you intend to keep.

### Reference output

Recorded 2026-09-07 against `dotnet run` on this branch with the flags above:

```
[PASS] idle: closed after 2.00s (deadline 2.0s)
[PASS] pool: 5 idle conns: 1 closed at accept, pending gauge=4 (want 4), pool_full counter +1
[PASS] pool-drain: pending gauge back to 0
[PASS] partial-prefix: closed after 2.0s (deadline 2.0s)
[PASS] malformed-frame: closed after 0.001s (must be well under 2.0s)
[PASS] malformed-counters: pending back to 0 (baseline 0), timeout +1, malformed +1
[PASS] flood-honest-acked: second player's input tick 5 acked as ack_tick=5 after 1 frames, while 6000 inputs were flooded in 0.00s
[PASS] flood-counters: inputs_dropped_total +3872, inputs_coalesced_total +1968

all checks PASSED
```

### Reading a failure

- `idle` / `partial-prefix` closed **late or never** — the handshake deadline is not
  armed (check the startup log line `Handshake: N pending max, Tms deadline`) or the
  read is not honouring cancellation on this transport.
- `pool`: `0 closed at accept` — the pool bound is not enforced, or `--max-pending`
  does not match the server's value (the probe opens exactly `N+1`).
- `malformed-frame` closed only at the deadline — the decode exception is being
  swallowed into the timeout path instead of refused at once.
- `flood-honest-acked` fails while `flood-counters` passes — the bound holds but
  the second player is starved: look at `gameserver_tick_duration_seconds` and
  `gameserver_tick_overruns_total` during the flood.
- `flood-counters` shows `+0` on one counter — a `+0` coalesced with non-zero drops
  means movement is not being coalesced at ingest (every packet is taking a slot);
  `+0` drops with non-zero coalesced means the per-connection budget is unbounded.
  Note the exporter caches `/metrics` briefly; the probe already polls for up to 10 s.

### Related metrics

`gameserver_handshakes_pending`, `gameserver_handshakes_rejected_total{reason}`,
`gameserver_inputs_dropped_total{reason}`, `gameserver_inputs_coalesced_total`,
`gameserver_transfers_rejected_total`; `/status` fields `handshakes_pending`,
`handshakes_rejected`, `inputs_dropped`, `transfers_rejected`. Full definitions in
`METRICS.md`; design rationale in `DESIGN.md`, "Admission hardening".
