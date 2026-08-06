# GameServer .NET — Wire API Reference

Authoritative reference for the client↔gameserver wire protocol as implemented by
`backend/gameserver-dotnet`. The Unity client implements against **this** document.

The Go mirror of these types lives in `backend/shared/messages` (`messages.go`,
`snapshot_state.go`); the C# types live in `GameServer/Net/WireProtocol.cs` with a
Unity-facing mirror in `Shared.GameLogic/Components/SnapshotData.cs`. All three must
stay in lockstep — a change to one is a change to all.

---

## Framing

```
[4-byte big-endian int32 length][UTF-8 JSON envelope of exactly that length]
```

Maximum frame body: 1 MB (`WireProtocol.MaxMessageSize`). Field names are
`snake_case`. Floats are 32-bit (`float` / `float32`).

```json
{ "type": 8, "payload": { ... } }
```

## Message types

| # | Name | Direction | Payload |
|---|------|-----------|---------|
| 1 | `auth` | client → gateway | `{ token }` |
| 2 | `auth_resp` | gateway → client | `{ ok, user_id?, error? }` |
| 3 | `enter_world` | client → gateway | `{ map_id }` |
| 4 | `enter_world_resp` | gateway → client | `{ server_addr?, join_token?, transport?, error? }` |
| 5 | `join_token` | client → gameserver | `{ token }` |
| 6 | `join_token_resp` | gameserver → client | `{ ok, user_id?, error? }` |
| 7 | `input` | client → gameserver | `{ tick, move_x, move_y, attack_target_id? }` |
| 8 | `snapshot` | gameserver → client | see below |
| 9 | `disconnect` | either | `{}` |
| 10 | `resync` | client → gameserver | `{}` (ignored) — request a full keyframe |

---

## `input` (7) — client → gameserver, once per client tick

```json
{ "tick": 41, "move_x": 1.0, "move_y": 0.0, "attack_target_id": "mob_3" }
```

- `tick` — the client's own monotonically increasing input sequence number. It is
  **not** the server tick. The server drops any input whose `tick` is not strictly
  greater than the last one it accepted for that player, and echoes the newest
  accepted value back as `ack_tick`.
- `move_x` / `move_y` — a **direction**, not a displacement. The server integrates
  `direction * speed * dt` once per server tick. Vectors with magnitude > 1 are
  normalized; magnitude > 1.5 is dropped. Sending more packets does not move further.
- `attack_target_id` — optional entity ID to attack this tick. Gated by range and by
  a tick-based cooldown (see below).

## `snapshot` (8) — gameserver → client, once per server tick per client

```json
{
  "tick": 128,
  "ack_tick": 41,
  "full": true,
  "entities": [
    { "id": "u1", "type": "player", "x": 12.5, "y": -3.0, "hp": 90, "max_hp": 100 }
  ],
  "removed": ["mob_7"]
}
```

| Field | Type | Present | Meaning |
|-------|------|---------|---------|
| `tick` | uint64 | always | Server simulation tick this snapshot describes. |
| `ack_tick` | uint64 | omitted when 0 | Highest `input.tick` the server has **accepted for this client's own entity**. The reconciliation anchor. |
| `full` | bool | omitted when false | `true` = keyframe: `entities` is the complete AOI set. `false`/absent = delta. |
| `entities` | array | always (may be `[]`) | Keyframe: everything in AOI. Delta: only entities whose visible state changed since the previous snapshot **sent to this connection**. |
| `removed` | string[] | omitted when empty | Delta only: entity IDs that left the AOI or the world. Never present on a keyframe. |

`entities[]` element: `id` (string), `type` (`player` \| `npc` \| `mob` \| `boss`),
`x`, `y` (float32), `hp`, `max_hp` (int). Visible state is exactly these fields —
a change in any of them puts the entity in the next delta; a change in a field the
client cannot see (e.g. cooldown) does not.

### Client merge algorithm (normative)

```
on snapshot s:
    if s.full:      world.clear()          # discard everything not re-listed
    for e in s.entities:  world[e.id] = e  # upsert
    for id in s.removed:  world.remove(id) # despawn
    tick     = max(tick, s.tick)
    ack_tick = max(ack_tick, s.ack_tick)   # monotonic; a 0 never lowers it
```

Reference implementations, both covered by tests:
`Shared.GameLogic.Systems.SnapshotMerger` (C#/Unity) and
`messages.SnapshotState` (Go — used by the smoke test and integration tests).

### Keyframe policy

A snapshot is a keyframe when **any** of the following holds:

1. It is the first snapshot on the connection (always — a reconnect starts fresh).
2. The client sent `resync` (10) since the last snapshot.
3. `keyframeInterval` snapshots have been sent since the last keyframe. Default 30
   (≈ every 2s at 15Hz); configured by `--keyframe-interval` /
   `GAMESERVER_KEYFRAME_INTERVAL`. **0 or less disables delta encoding entirely** —
   every snapshot becomes a keyframe, which is the escape hatch for a client that
   cannot merge deltas.

Delta correctness assumes an ordered, reliable transport (TCP today): the server
treats "last sent" as "last received". The periodic keyframe is the recovery path
for anything that breaks that assumption.

### Reconciliation

`ack_tick` is per-connection and derives only from the receiving player's own
entity — one client's ack never reflects another's inputs. A client should:

1. Keep unacknowledged inputs in a local ring buffer.
2. On each snapshot, discard buffered inputs with `tick <= ack_tick`.
3. Snap the local entity to the authoritative position from the snapshot, then
   replay the remaining buffered inputs through `MovementSystem` to re-predict.

`ack_tick = 0` means the server has accepted no input yet — do not treat it as an
ack of tick 0.

### Backward compatibility

`ack_tick`, `full` and `removed` are all omitted when default, so a keyframe-only
stream (`--keyframe-interval 0`) is byte-identical to the pre-delta protocol. A
client that reads only `tick` and `entities` still works against such a server.

## `resync` (10) — client → gameserver

Empty payload. Promotes this connection's next snapshot to a keyframe. Send it when
local reconstruction is lost or suspect (scene reload, merge error). It is cheap but
not free — it costs one full AOI snapshot; do not send it every tick.

---

## Combat timing

Attack cooldown is counted in **simulation ticks**, not wall-clock:
`EntityState.CooldownUntilTick`, and an attack is legal when
`currentTick >= CooldownUntilTick`. The length comes from
`GameConstants.AttackCooldownTicks(tickRate)` = `ceil(AttackCooldownMs * tickRate / 1000)`,
which is 8 ticks (533 ms) at the default 15 Hz. The Unity client must use the same
function so predicted attacks match the server's ruling exactly.
