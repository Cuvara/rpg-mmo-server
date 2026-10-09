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
