# GameServer .NET — Runbook

Operational procedures for the C# game server. Deployment, configuration and the
metric catalogue live in `README.md` and `METRICS.md`; this file holds the
*procedures* — things an operator runs against a live server.

## Transport: KCP over UDP only

The gameplay port (default `:9000`) is **UDP**. There is no TCP gameplay listener
(ADR-32): `GAMESERVER_TRANSPORT` / `--transport` may be unset or `kcp`; any other
value stops the server at boot with

```
GAMESERVER_TRANSPORT=<value> is not supported: realtime gameplay is KCP/UDP only
```

The registry always advertises `transport=kcp`. The metrics/status HTTP port stays
TCP. Firewalls, Docker port mappings (`9000/udp`) and Agones ports (`protocol: UDP`)
must allow UDP to the game port; a TCP-only rule produces a server that is Ready,
registered and unreachable.

`TRANSPORT_KEY` (32-byte hex) turns on kcp-go-compatible AES per datagram and must
match the clients' key; with it unset the boot log and
`gameserver_transport_encrypted` say the hop is cleartext. A key mismatch shows up
as `gameserver_kcp_datagrams_dropped_total{reason="bad_crypto"}` climbing while no
session forms.

### KCP listener limits

| Variable | Default | What it bounds |
|---|---|---|
| `GAMESERVER_KCP_MAX_SESSIONS` | 4096 | Live sessions, pre-authentication included |
| `GAMESERVER_KCP_MAX_SESSIONS_PER_IP` | 16 | Live sessions per source IP (loopback exempt). Raise it behind carrier-grade NAT |
| `GAMESERVER_KCP_NEW_SESSIONS_PER_SEC` | 200 (burst 2x) | New sessions per second, listener-wide |
| `GAMESERVER_KCP_DATAGRAMS_PER_SEC` | 500 (burst 2x) | Inbound datagrams per second per session; the excess is dropped, never fatal |

A set value that is not a positive number is fatal at boot. The values in force are
logged as `KCP limits:` in the startup banner. Fixed (not configurable): 60 s idle
timeout, dead link after 20 unacknowledged transmissions (~3.5 s on a live link),
256 KiB of buffered input per session before the receive window closes, and a send
queue at which writers wait (256 segments) and past which the session is closed as
a slow consumer (1024 segments).

**Reading the counters** (`METRICS.md`, `gameserver_kcp_*`):

- `sessions_rejected_total{reason="global_cap"|"per_ip_cap"}` rising with real
  players failing to connect: the cap is below real load — raise it. Rising with
  `gameserver_kcp_sessions` pinned at the cap and `handshakes_pending` high: a
  session flood; the caps are doing their job and the source is in the
  `KCP session refused from ...` warnings (logged once per 1000 refusals).
- `sessions_rejected_total{reason="rate"}` during a mass reconnect (a restart with a
  full map) is expected and self-clearing: refused openers are retransmitted.
- `datagrams_dropped_total{reason="rate"}` from one session is a client sending far
  above any legitimate rate; KCP recovers the dropped datagrams.
- `sessions_closed_total{reason="slow_consumer"}` should stay at zero: writers wait
  at the soft limit, so only a writer that ignores it can reach the hard one.

### Shutdown

On shutdown the server sends `disconnect{reason=server_shutdown}` to every client,
waits 2 s, closes the connections and only then closes the UDP socket — every
session shares that socket, so closing it first would discard the notice.

## Logs

The server writes **one JSON object per line on stdout** and nothing else. A log
shipper can read stdout line by line and parse every line.

```json
{"ts":"2026-11-16T08:41:53.217Z","level":"INFO","category":"Program","msg":"  MapId:     map_01","game":"cuvara","map_id":"map_01","server_id":"gs-3f9c1a7e5b2d"}
```

(The line shape is exactly what `JsonLinesConsoleFormatter` writes; the values are
illustrative.)

| Field | Meaning |
|-------|---------|
| `ts` | Event time, UTC, RFC 3339 with milliseconds and a `Z` suffix (`2026-11-16T08:41:53.217Z`) |
| `level` | `TRACE`, `DEBUG`, `INFO`, `WARN`, `ERROR` or `CRITICAL` |
| `category` | The logger category (usually the class name, e.g. `Program`, `GameServerHost`) |
| `msg` | The formatted message. Always present; an empty string when an exception was logged with no message |
| `exception` | `Exception.ToString()` (type, message, stack trace). **Only present when the event has an exception** |
| `game` | The game this server serves (`GAMESERVER_GAME_ID`, default `cuvara`) |
| `map_id` | The map this server owns (`GAMESERVER_MAP_ID`, default `map_01`) |
| `server_id` | The server id (`GAMESERVER_ID` / `POD_NAME` / generated `gs-<hex>`) |

`game`, `map_id` and `server_id` are fixed for the life of the process, so they are
on **every** line, written once at startup into the formatter rather than looked up
per call. Fields are written in the order above. A message or exception that spans
several lines is JSON-escaped (`\n`) and stays on one line.

### Level and format

| Variable | Values | Default |
|----------|--------|---------|
| `GAMESERVER_LOG_LEVEL` (`--log-level`) | `Trace`, `Debug`, `Information`, `Warning`, `Error`, `Critical` (ASCII case-insensitive) | `Information` |
| `GAMESERVER_LOG_FORMAT` (`--log-format`) | `json`, `text` (ASCII case-insensitive) | `json` |

Unset or empty selects the default. At the default level the set of events logged is
the same as before these knobs existed. The flag wins over the variable.

**Raising the level.** Set `GAMESERVER_LOG_LEVEL=Debug` (or `Trace`) on the pod or
process and restart it; the level is read once at startup and there is no runtime
switch. A lower level only writes more lines, it never changes what is logged at a
higher one.

**An invalid value exits 2 at startup**, before migrations, registration or any
connection, like every other `GAMESERVER_*` knob. `None`, numbers and typos are
refused rather than guessed. The refusal is logged as one Critical line, written with
the default level and format so a shipper still receives it:

```
invalid GAMESERVER_LOG_LEVEL='verbose': expected one of Trace, Debug, Information, Warning, Error, Critical
invalid GAMESERVER_LOG_FORMAT='yaml': expected one of json, text
```

### Reading logs locally

- `GAMESERVER_LOG_FORMAT=text` (or `--log-format text`) restores the previous console
  output (plain, multi-line). It has no `game` / `map_id` / `server_id` fields: those
  are a feature of the JSON format only.
- Or keep JSON and filter it: `dotnet run --project GameServer | jq -r '"\(.ts) \(.level) \(.category): \(.msg)"'`,
  or `... | jq 'select(.level == "WARN" or .level == "ERROR")'`. Show an exception
  with `jq -r '.exception // empty'`.

### Compared with the Go gateway's slog JSON

The gateway logs through Go's `log/slog` JSON handler (`backend/shared/logger`) to
stdout. The two are close but not identical, so a collector needs a small mapping.

| Concept | Gateway (`slog.NewJSONHandler`) | Game server (this module) | Mapping |
|---------|---------------------------------|---------------------------|---------|
| Timestamp key | `time` | `ts` | Rename one to the other |
| Timestamp format | RFC 3339 with nanoseconds, in **local time zone** (`2026-11-16T15:41:53.217123456+07:00`; `Z` only when the host is UTC) | RFC 3339, **always UTC**, milliseconds, `Z` | Parse both as RFC 3339; both are accepted by an RFC 3339 parser |
| Level key | `level` | `level` | Same key |
| Level values | `DEBUG`, `INFO`, `WARN`, `ERROR` (offsets such as `INFO+2` for custom levels; none are used here) | `TRACE`, `DEBUG`, `INFO`, `WARN`, `ERROR`, `CRITICAL` | The four shared levels are spelled identically; the game server adds `TRACE` and `CRITICAL`. Map `CRITICAL` to the collector's highest severity (fatal) |
| Message key | `msg` | `msg` | Same key |
| Logger name | none (no `category`; the gateway adds no logger name unless a call adds an attribute) | `category` | Present only on the game server |
| Error | An attribute chosen by the call site (the gateway uses `err`), as a string | `exception`, the full `Exception.ToString()` including the stack trace | Map the gateway's error attribute and `exception` to one error field if one is wanted |
| Other attributes | Each `slog` attribute is its own top-level key | **None**: structured arguments are folded into `msg` | See limits below |
| Game / map / server identity | Whatever the call site attaches | `game`, `map_id`, `server_id` on every line | Select on these three for the game server; the gateway does not stamp them |

Both default to level `info`. The gateway takes its level from its own configuration
(`debug`, `info`, `warn`, `error`; anything else is `info`), whereas
`GAMESERVER_LOG_LEVEL` refuses an unknown value instead of falling back.

### Limits

- **Structured message arguments are not separate fields.** A call such as
  `LogInformation("Player {UserId} joined", id)` produces `msg` = `"Player u1 joined"`;
  `UserId` is not a key. Search `msg` as text.
- **Logger scopes are not emitted.** Only the three process-level identity fields are
  added; any `BeginScope` content is dropped.
- `text` format has no identity fields.
- The level is fixed at startup (no runtime change).
- Lone UTF-16 surrogates in a message are replaced with U+FFFD so the line stays valid
  JSON; if a line still cannot be encoded the server writes a short fallback `ERROR`
  line (`log line could not be encoded`, without the identity fields) rather than lose
  the event or throw out of a log call.

## Probing admission hardening

`scripts/admission-probe.py` was removed with the TCP gameplay listener (ADR-32): it
dialled the game server over raw TCP, which the server no longer accepts, and a stdlib
Python script has no KCP. The behaviours it checked are covered as follows:

| Behaviour | Where it is exercised now |
|-----------|---------------------------|
| Idle / partial-prefix / malformed connection closed at the handshake deadline; pending pool bounded | `HandshakeHardeningTests` (in-process server, real KCP client) |
| Session flood: global / per-IP caps, new-session rate, datagram budget, oversize / undersize / bad-conv datagrams | `KcpListenerHardeningTests`, counters `gameserver_kcp_*` on `/metrics` |
| Input flood: drop and coalesce counters, an honest player's input still acknowledged | `KcpGameplayTests`, `gameserver_inputs_dropped_total` / `gameserver_inputs_coalesced_total` |
| Hostile datagrams against a **running** server, then a real join | `backend/integration_test` `TestKCP_HostileDatagramsDoNotDisturbTheListener` |
| Live join over KCP against a deployed stack | `smoketest` (`./stack.sh check`), `deploy/k8s/verify/probe enterworld` (`kcp_join=ok`) |
