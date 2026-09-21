# GameServer .NET

C# port of the Go game server. Shares game logic with Unity DOTS client via `Shared.GameLogic`.

## Architecture

The solution contains three projects:

```
gameserver-dotnet/
  Shared.GameLogic/      Pure C# game logic library (shared with Unity client)
  GameServer/            Server application (.NET 10 console, NativeAOT)
  GameServer.Tests/      xUnit test suite
```

### Shared.GameLogic

A standard .NET 10 class library with **zero Unity dependencies**. Contains all
deterministic game logic: movement validation, combat calculations, cooldown
checks, AOI (Area of Interest) queries, and game constants.

This library is designed to be imported by the Unity DOTS client as a local
package or Git submodule, enabling client-side prediction with identical code
paths on both server and client.

### GameServer

A .NET 10 console application that hosts the authoritative game world. It speaks
the same wire protocol as the Go game server, so the Go gateway cannot
distinguish between Go and C# backends.

### GameServer.Tests

xUnit-based test suite covering shared logic, server systems, protocol encoding,
and integration scenarios.

## Quick Start

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- For NativeAOT publishing: `clang` and `zlib` development headers

### Build & Run

```bash
# Build the entire solution
dotnet build

# Run the game server (from repo root or gameserver-dotnet/)
# NOTE: flags are SPACE-separated. `--addr=:9000` is parsed as an unknown token
# and silently ignored — Program.cs GetArg only matches `--name value` pairs.
dotnet run --project GameServer -- --addr :9000 --map-id map_01

# Run with all flags
dotnet run --project GameServer -- \
  --addr :9000 \
  --map-id map_01 \
  --sim-critical-hz 60 \
  --sim-world-hz 15 \
  --sim-background-hz 5 \
  --map-width 1000 \
  --map-height 1000 \
  --jwt-secret dev-secret-change-me \
  --agones \
  --redis localhost:6379
```

### Configuration

Every flag has an environment-variable equivalent; the flag wins when both are
set. Flags are **space-separated** (`--addr :9000`).

| Flag | Environment variable | Default | Description |
|------|----------------------|---------|-------------|
| `--mode` | `GAMESERVER_MODE` | `map` | `map` or `dungeon`. Dungeon mode changes **three** things, all from ADR-26: a **60s** reconnect hold instead of 30s; the pod writes its `servers:id:` registry hash but **never joins the `servers:map:` index**, so `FindServer` cannot hand one party's instance to an unrelated player (decision 8); and the pod **persists only the map-independent player fields** — HP and max HP — leaving `map_id`, `x` and `y` as the origin map wrote them (decision 5). A dungeon instance also **ends its own process** once the last member has left and their hold has expired with no reconnect (decision 6). See `docs/DESIGN.md`, "Dungeon mode" |
| `--addr` | `GAMESERVER_ADDR` | `:9000` | Game traffic listen address |
| `--map-id` | `GAMESERVER_MAP_ID` | `map_01` | Map identifier, also the `map_id` metric label |
| `--server-id` | `GAMESERVER_ID` / `POD_NAME` | random | Server identity checked against the join token |
| `--capacity` | `GAMESERVER_CAPACITY` | `100` | Maximum concurrent players. A join beyond it is refused with `"Server is full"` **and logged at Warning** with the count, the limit and the user — this refusal was silent until #145, so a server turning players away and a broken one produced identical logs. The value is published into the registry and enforced by the gateway too, so it is a fleet-wide admission limit, not a pod-local one. Also reported as `capacity` on `/status`. Admission is **atomic**: the slot is reserved before the player-store load and released if the join fails, so N concurrent joins against one free slot admit exactly one; a user rejoining over their own still-open connection *replaces* it and takes no second slot; a user in the reconnect hold window is not an occupant (see `docs/DESIGN.md`, "Admission hardening") |
| `--max-pending-handshakes` | `GAMESERVER_MAX_PENDING_HANDSHAKES` | `256` | Most accepted sockets allowed in the join handshake at once. **Separate from `--capacity`**, which counts authenticated players only. An accept beyond it is closed on the spot with no reply and counted as `gameserver_handshakes_rejected_total{reason="pool_full"}`. Published as `handshakes_pending` on `/status` |
| `--handshake-timeout-ms` | `GAMESERVER_HANDSHAKE_TIMEOUT_MS` | `5000` | Deadline, from accept, for a peer to deliver a complete, valid `MsgJoinToken` (and to read the reply to a rejected one). An idle socket, a partial length prefix or a partial body is closed at the deadline and counted as `reason="timeout"`; a frame that is not a join is closed immediately as `reason="malformed"`. Linked to shutdown, so pending reads unwind on stop. Covers only what the peer controls — the player-store load after verification runs on the host token |
| `--max-inputs-per-tick` | `GAMESERVER_MAX_INPUTS_PER_TICK` | `32` | Most inputs one connection may queue between two tick drains. Movement-only inputs **coalesce in place** (newest wins) and occupy one slot however often they are sent; inputs carrying an attack target are kept distinct and budgeted by this. Beyond it inputs are dropped and counted as `gameserver_inputs_dropped_total{reason="connection_budget"}` |
| `--max-pending-inputs` | `GAMESERVER_MAX_PENDING_INPUTS` | `0` → capacity × per-tick budget | World-wide cap on the pending input queue between two drains. `0` derives it as `GAMESERVER_CAPACITY × GAMESERVER_MAX_INPUTS_PER_TICK` — every admitted player spending their whole budget at once still fits. Beyond it inputs are dropped and counted as `reason="queue_full"` |
| `--max-snapshot-bytes` | `GAMESERVER_MAX_SNAPSHOT_BYTES` | `8192` | **Per-connection downlink budget**: most bytes of snapshot *payload* one client may be sent per snapshot. The counterpart to `--max-inputs-per-tick` on the other direction of the wire — before it, the AOI radius was the only bound on a snapshot, and a radius bounds *area*, not how many entities stand inside it. `0` disables it and restores the pre-budget encoder exactly. **Protobuf connections only** (every byte figure in the encoder is a protobuf size; a JSON frame for the same snapshot is several times larger, so a JSON stream is bounded only by the AOI radius). It is a **tail cap, not a shaper** — but it engages earlier than first documented: measured live, a stock server starts shedding at **~200 entities in one observer's AOI** (keyframes only; the steady delta stream is clipped from ~317). The original "~2.7× the mean snapshot, never engages" derivation sized the cap against a *delta-weighted* mean and was wrong; a keyframe costs ~41 B/entity against a delta's ~26. See "Downlink budget" in `docs/DESIGN.md` for the corrected derivation and the measured table. When it does, entities are **deferred, never dropped** — see "Downlink budget" in `docs/DESIGN.md`. Reported as `max_snapshot_bytes` on `/status`, with `snapshot_bytes`, `snapshot_entities_shed`, `snapshot_removals_deferred` and `snapshot_max_shed_age` |
| `--aoi-radius` | `GAMESERVER_AOI_RADIUS` | `50` | **Area-of-interest radius in world units.** The single largest lever on downstream bandwidth: population inside a circle grows with the SQUARE of the radius, so halving it quarters the expected entity count per snapshot — a bigger effect than anything downstream of it, including `--max-snapshot-bytes`, which is a tail cap on what the radius already selected. It is also the spatial index's **cell size** (`EcsWorld`), so the two cannot be configured apart. **Refused at startup, never defaulted:** an unparseable, zero, negative, non-finite or absurd value exits 2 with a named reason (`GameServer/Server/AoiSettings.cs`) — a typo that silently ran a fleet at 50 while its manifest said 5 is the exact divergence this refuses to allow. Parsed with InvariantCulture, so `12.5` and never `12,5`. A radius that reaches the map's **diagonal** logs a warning at startup: interest management then filters nothing and every entity is in every snapshot, which is legitimate in a small dungeon instance and a mistake on an open map, and the server cannot tell which it is looking at. **Not on the wire** — a client cannot ask for it; read `aoi_radius` (and `aoi_covers_whole_map`) from `/status` |
| `--importance` | `GAMESERVER_IMPORTANCE` | `legacy` | **Which entities are emitted FIRST when the per-connection downlink budget bites.** Not how many bytes go out (`--max-snapshot-bytes`) and not which entities are candidates at all (`--aoi-radius`) — purely the order among candidates. `legacy` is every factor zero: self, then longest-deferred, then nearest, which is what shipped before importance existed. `balanced` additionally ranks on visible-state change (10), combat (6), entity type (3) and distance (2). **Deferral age stays dominant in both** — it sits ABOVE the score in the comparison, because the starvation bound (max deferral is the size of the dirty set, independent of session length) is a consequence of the comparison being strictly oldest-first; folding age into the weighted sum would make fairness a function of the weights. Per-factor overrides `GAMESERVER_IMPORTANCE_W_{DISTANCE,CHANGE,TYPE,COMBAT}`; weighting a factor this server has no data for (`PARTY`, `PVP`, `BOSS`, `QUEST`, `VISIBILITY`, `ZONE`, `INTERACTION`) **exits 2** rather than being accepted and ignored. Reported as `importance_profile` and `importance_weights` on `/status`; the label reads `custom` whenever a weight was overridden, so it never claims a profile it is not running |
| `--replication-schedule` | `GAMESERVER_REPLICATION_SCHEDULE` | `off` | **How often an entity of a given importance is re-sent.** `off` (default) means every dirty entity is due every world tick. `tiered` withholds lower-importance entities for a configured interval: score ≥ 8 every tick, score ≥ 3 for 133ms, otherwise 266ms. **Intervals are configured in milliseconds and converted through `SIM_WORLD_HZ`** — a tick count means nothing without the rate that advances it, so the same setting is 2 and 4 ticks at 15Hz and 4 and 8 at 30Hz, holding the same wall time. `tiered` without importance weights **exits 2**: every score would be zero, every entity would land in the slowest band, and the result would be a uniform staleness increase rather than a policy. **An edge is never deferred** — a health change or an action retrigger goes out immediately, because withholding one is a dropped event rather than a late update. **A keyframe never applies intervals**, since the client discards anything a keyframe does not list. Applies whether or not the byte budget is on. Reported as `replication_schedule` on `/status` with `snapshot_deferred_by_interval` and `snapshot_max_state_age` — the last of which is NOT `snapshot_max_shed_age`: that counts budget deferrals, this counts schedule ones. See ADR-27 |
| `--sim-critical-hz` | `SIM_CRITICAL_HZ` | `60` | Frequency of the **critical** group (input, movement, combat). This is also the **base tick rate** of the loop — every other group is derived from it |
| `--sim-world-hz` | `SIM_WORLD_HZ` | `15` | Frequency of the **world** group (AI, spawning, despawning) **and of the snapshot broadcast**. Must divide `SIM_CRITICAL_HZ` exactly and must not exceed it, or the server exits with code 2 |
| `--sim-background-hz` | `SIM_BACKGROUND_HZ` | `5` | Frequency of the **background** group (work that tolerates a whole interval of delay). Must divide `SIM_CRITICAL_HZ` exactly and must not exceed `SIM_WORLD_HZ` |
| `--tick-rate` | `GAMESERVER_TICK_RATE` | *(unset → `60/15/5`)* | **Legacy single-rate switch.** Sets *every* group to this one rate, i.e. base = world = background, snapshots every tick — the pre-multi-rate server exactly. Only applies when no `SIM_*_HZ` environment variable is set; any of them present wins and the tick rate is ignored |
| `--sealed` | `GAMESERVER_SEALED` | `require` | **Sealed session on the gameplay hop.** `require` runs an authenticated X25519 exchange after the join reply and encrypts every frame after it with ChaCha20-Poly1305 (ADR-22; normative format in `backend/docs/SEALED-FRAMING.md`). `off` restores the pre-sealing server exactly. **Two values, not three** — there is no "preferred" mode, because a negotiable encryption setting is a downgrade attack with a friendly name; any other value exits with code 2 rather than guessing. A `require` server **refuses** every client that cannot seal: a JSON client is closed after the join reply and **no setting fixes it** (the JSON codec has no sealed frame), and a protobuf client that never sends `MsgSealedClientHello` is closed at `--handshake-timeout-ms`. A client must therefore set `NetworkSettings.RequireSealedSession` in the same rollout, and that ships in a built player, not in a deployment variable |
| `--keyframe-interval` | `GAMESERVER_KEYFRAME_INTERVAL` | `30` | Delta snapshots between full keyframes; `0` disables delta encoding (see `docs/API.md`) |
| `--gather-workers` | `GAMESERVER_GATHER_WORKERS` | `1` | Threads the AOI gather may use. `1` is serial. Above `1` it applies only from 500 viewers up — measured gain is 2.0-2.7x at 500 viewers / 4 workers, inside the noise at 200, a loss at 50 (see `docs/DESIGN.md`, "Where the tick budget goes") |
| `--map-width` | `GAMESERVER_MAP_WIDTH` | `1000` | Map width in world units |
| `--map-height` | `GAMESERVER_MAP_HEIGHT` | `1000` | Map height in world units |
| *(none)* | `GAMESERVER_ENEMIES` | on | Enemy AI on/off. `false` disables the spawner entirely; `/status` then reports `enemy_ai: "off"` and `enemies_alive: 0` |
| *(none)* | `GAMESERVER_ENEMY_CHASE` | `on` | **Enemies chase the nearest LIVE player.** `off` restores the pre-change AI in full: every enemy walks to the origin and despawns on arrival. Same vocabulary as `GAMESERVER_FIELD_DELTA` (`on/true/1/yes`, `off/false/0/no`); anything else **exits 2** |
| *(none)* | `GAMESERVER_ENEMY_MAX` | `30` | Enemy population cap with **nobody online**. The pre-change cap, unchanged |
| *(none)* | `GAMESERVER_ENEMY_MAX_PER_PLAYER` | `45` | Extra enemies allowed **per live player**, added to `GAMESERVER_ENEMY_MAX`. The cap in force is `MAX + MAX_PER_PLAYER × livePlayers`, bounded at 20 000. Additive rather than multiplicative so an empty server is exactly the pre-change server while a four-player group gets a 210-enemy world |
| *(none)* | `GAMESERVER_ENEMY_WAVE_SIZE` | `2` | Enemies per wave with nobody online |
| *(none)* | `GAMESERVER_ENEMY_WAVE_SIZE_PER_PLAYER` | `6` | Extra enemies per wave per live player, bounded at 2 000. Scales **with the cap, not by taste**: filling a 75-enemy cap two at a time takes 56s, so a bigger cap without a bigger wave is decoration |
| *(none)* | `GAMESERVER_ENEMY_WAVE_INTERVAL` | `1.5` | Seconds between waves |
| *(none)* | `GAMESERVER_ENEMY_SPAWN_DISTANCE` | `13` | World units from the anchor an enemy is placed at — a **randomly chosen live player**, or the origin when nobody is online (which is the ring the pre-change spawner always used). Choosing the anchor per enemy is what spreads a wave across the whole player population instead of onto one ring |
| *(none)* | `GAMESERVER_ENEMY_MIN_SPAWN_DISTANCE` | `8` | Closest an enemy is placed to **any** live player. The spawn distance only guarantees separation from the anchor; in a crowd a placement can land in a bystander's lap, and map-edge clamping can pull one back across it. Best-effort: 6 samples of the circle, then the last candidate is taken anyway, because a wave that silently spawns fewer entities the tighter the crowd gets is the threadbare fight returning through the door marked "safety check". **Must be strictly less than `GAMESERVER_ENEMY_SPAWN_DISTANCE`** or the server exits 2 — at or above it, every candidate including the one it was measured from is rejected |
| *(none)* | `GAMESERVER_ENEMY_CONTACT_RANGE` | `1` | How close a chaser closes before it stops advancing. Inside `GameConstants.AttackRange` (3.0) so the player can hit what is standing on them, and non-zero so a ring of chasers does not jitter across the target every tick |
| *(none)* | `GAMESERVER_ENEMY_HP` / `_ATTACK` / `_DEFENSE` / `_SPEED` | `16` / `5` / `2` / `2.5` | Per-enemy stats, unchanged from the compiled-in values |
| *(none)* | `GAMESERVER_ENEMY_ATTACKS` | `on` | **Enemies attack back.** `off` restores the one-directional fight: enemies close to the contact range and stand there while players hit them. Same vocabulary as `GAMESERVER_FIELD_DELTA`; anything else **exits 2**. Enemy attacks go through the ordinary input path (`EcsWorld.PushInput` → `InputHandler` → `CombatLogic`), exactly as bot attacks do, so damage, range, cooldown, the `Damage`/`Death` game events and the kill reward are all the one combat path this server has — there is no second damage formula |
| *(none)* | `GAMESERVER_ENEMY_ATTACKERS_PER_TARGET` | `3` | **The survivability cap, and the only thing that bounds what one player can take.** At most this many enemy attacks LAND on one player per `GAMESERVER_ENEMY_ATTACK_INTERVAL`, however many enemies are standing on them — a player surrounded by 327 enemies takes exactly what a player surrounded by 3 takes. Bounded at 1 000. `0` means enemies never land a hit (a valid setting, not a refused one). **Read the arithmetic below before raising it** |
| *(none)* | `GAMESERVER_ENEMY_ATTACK_INTERVAL` | `0.5` | Seconds between one player's incoming-damage windows. The period the cap above is expressed over, rounded **up** to a whole number of world ticks (at the default 15Hz world rate, 0.5s → 8 world ticks → 0.533s). Values below the server's own 500ms attack cooldown (`GameConstants.AttackCooldownMs`) do **not** produce more damage — the enemy's own cooldown is charged by the same `InputHandler` a player's is — so the effective interval is `max(this, 0.5s)` |
| *(none)* | `GAMESERVER_PLAYER_RESPAWN` | `on` | **A player whose HP reaches 0 is returned to the map spawn point at full HP on the next world tick.** Named for the player rather than the enemy because it governs a player rule; it lives in this family because enemy attacks are what make player death reachable. `off` restores the pre-existing behaviour, which is not a design but an absence — see "Death and respawn" below |
| *(none)* | `GAMESERVER_BOTS` | `0` (**off**) | **Synthetic players.** `N > 0` spawns N bots: real player-type entities that move and attack through the ordinary input path, so clients render them, enemies chase them, and they are legitimate targets. A battle royale is a crowd of *players* as much as of enemies, and this project can only put three real clients on a map. **They are development/demo scaffolding**: not persisted, no connection, no capacity, and not an AI to ship to players. Logged as a startup WARNING whenever non-zero, because a busy map with `players_online: 3` otherwise reads as a broken counter |
| *(none)* | `GAMESERVER_BOT_SPREAD` | `120` | Radius of the disc bots are scattered over at startup. Deliberately wide: bots are what the enemy spawner anchors on, so where the bots are is where the fight is, and clustering them rebuilds the conveyor belt with extra steps |
| *(none)* | `GAMESERVER_BOT_ENGAGE_RANGE` | `40` | How far a bot travels to engage an enemy. Beyond it the bot wanders — a "nearest enemy anywhere" rule collapses every bot onto whichever corner is busiest |
| *(none)* | `GAMESERVER_BOT_HP` / `_ATTACK` / `_DEFENSE` / `_SPEED` | `100` / `10` / `5` / `4` | Per-bot stats. The defaults match a real player's except for speed, which is higher so bots visibly circulate |
| `--jwt-secret` | `JWT_SECRET` | *(empty)* | HS256 secret for the Nakama→client auth token. Only used here as the `JOIN_TOKEN_SECRET` fallback |
| `--join-token-secret` | `JOIN_TOKEN_SECRET` | *(empty → `JWT_SECRET`)* | HS256 secret the **gateway** signs join tokens with. Comma-separated (`current,previous`) to rotate — see below |
| `--metrics-addr` | `METRICS_ADDR` | `:9101` | Prometheus `/metrics` + `/healthz`. Empty, `off`, `none` or `disabled` turns it off — same vocabulary as the Go gateway. An address that parses as none of those disables the endpoint and logs an error; it does not stop the server |
| `--game-db-url` | `GAME_DB_URL` | *(unset)* | Game-state PostgreSQL DSN — see below |
| `--migrate-only` | `GAMESERVER_MIGRATE_ONLY=true` | off | Apply pending migrations, then exit — see below |
| `--agones` | `AGONES_ENABLED=true` | off | Report Ready / Health / Allocate / Shutdown to the Agones sidecar, and take the **advertised address** from the GameServer status instead of `--public-addr` — see below |
| *(none)* | `AGONES_SDK_HTTP_PORT` | `9358` | Agones sidecar HTTP port. Only read when Agones is enabled; an unparsable value warns and falls back |
| `--transport` | `GAMESERVER_TRANSPORT` | `tcp` | Realtime transport: `tcp` or `kcp` — see below |
| *(none)* | `TRANSPORT_KEY` | *(empty)* | Pre-shared AES-256 key for KCP. Empty = cleartext (start-up WARNING). Ignored for TCP |
| `--public-addr` | `GAMESERVER_PUBLIC_ADDR` | *(listen addr)* | Full `host:port` advertised to clients through the registry. Used **only when Agones is off** — with Agones on and the status read working, the port comes from Agones and the host from `GAMESERVER_ADVERTISE_HOST`; see the Agones section |
| `--advertise-host` | `GAMESERVER_ADVERTISE_HOST` | *(unset → Agones `status.address`)* | **Host only, no port.** Replaces the host of the address read from the Agones GameServer status; the port always stays the Agones-assigned one. Ignored (with a warning) when Agones is off or the status read fails. Needed because `status.address` is the *node* address, which a client outside the cluster network cannot dial |
| `--register-on-allocated` | `GAMESERVER_REGISTER_ON_ALLOCATED=true` | off | Hold the registry entry back until Agones reports this GameServer **Allocated**, instead of publishing it right after Ready. Agones-only; ignored (with a warning) when Agones is off — see below |
| `--redis` | `REDIS_ADDR` | *(unset)* | Registry Redis; unset disables self-registration, the `events:game` publisher, and the duplicate-login kick consumer on `events:kick` (ADR-20) |
| `--redis-password` | `REDIS_PASSWORD` | *(unset)* | Registry Redis password |

### Enemy combat: the survivability arithmetic

**327 enemies attacking is not a fight, it is an instant delete**, and lowering the
per-enemy damage does not fix it: `CombatLogic.CalculateDamage` floors at
`GameConstants.MinDamage` (1), so three hundred enemies deal at least three hundred
damage per round however weak each one is. A per-enemy cooldown does not fix it either —
three hundred enemies each respecting the same 500ms cooldown still deliver three hundred
hits every 500ms. The bound has to be expressed on the **target**, which is what
`GAMESERVER_ENEMY_ATTACKERS_PER_TARGET` is.

Worst case a player can take:

```
damage per hit  = max(1, GAMESERVER_ENEMY_ATTACK - player defense)
damage per sec  = GAMESERVER_ENEMY_ATTACKERS_PER_TARGET x damage per hit
                  / GAMESERVER_ENEMY_ATTACK_INTERVAL
time to die     = player HP / damage per sec
```

At the shipped defaults and a default player (HP 100, defense 5, enemy attack 5):

| Setting | Damage/hit | Damage/sec | Time to die from full |
|---|---|---|---|
| **defaults** (`3` per `0.5s`) | 1 | 6 | **16.7 s** |
| `ATTACKERS_PER_TARGET=1`, `INTERVAL=1` | 1 | 1 | 100 s |
| `ENEMY_ATTACK=20` (defaults otherwise) | 15 | 90 | 1.1 s |
| `ATTACKERS_PER_TARGET=20` | 1 | 40 | 2.5 s |

The number is **independent of the enemy population**, which is the property the cap
exists to give: 16.7 seconds surrounded by twenty enemies, and 16.7 seconds surrounded by
three hundred. `EnemyAiSettings.WorstCaseDamagePerSecond` computes it in code, and
`EnemyAiSettingsTests.WorstCaseDamagePerSecond_IsWhatTheKnobsSay` is the table above as a
test.

Rounding, stated because it moves the third decimal: the interval is rounded up to whole
world ticks, so the default 0.5s is really 8/15 = 0.533s and the default worst case is
5.63 damage/second, i.e. 17.8 seconds. The table uses the configured interval.

`/status` publishes `enemy_attacks_decided` and `enemy_attacks_throttled`. The second is
the one that matters: a crowd on a player with `enemy_attacks_throttled` at zero means the
cap is **not** what is limiting the fight, and the limit is somewhere you have not looked.

### Death and respawn

Before enemy attacks existed, a player's HP could not reach 0 in normal play, and what
happens when it does was never designed. What the code does is: nothing reaps a dead
player (`EnemyReapSystem` only queries the enemy archetype), `InputHandler` refuses every
input from it for the life of the process, the enemy AI stops counting it as somebody to
fight, and `AsyncSaver` persists `hp = 0`. There is no `dead` column in `player_states`,
so `PlayerSpawn.Resolve` restores that 0 verbatim on the next join — **on any server** —
and the character is dead permanently.

`GAMESERVER_PLAYER_RESPAWN=on` (the default) gives that a defined end and nothing more:
the player is dead for at most one world tick — long enough for the `Death` game event and
the `Dead` action to be sampled by a snapshot, so the death is observable — and is then
back at the map's spawn point with its own `MaxHp`. There is no death screen, no timed
respawn, no corpse, no penalty and no schema change; a timed respawn needs a per-entity
tick to count down to, which is a component field and a wire consideration, and is
deliberately not done here.

It applies to **every** cause of death, not just enemies: the system asks the world who is
dead rather than being told by whatever killed them. It applies to synthetic players
(`GAMESERVER_BOTS`) too, which matters more than it sounds — a dead bot never acts again
for the life of the process, so without this a demo silently drains its own crowd while
`bots_alive` still reports the full count.

**Every `GAMESERVER_ENEMY_*` value is parsed strictly**, the same rule as
`GAMESERVER_AOI_RADIUS` and `GAMESERVER_FIELD_DELTA`: an unparseable, out-of-range or
unrecognised value **exits 2 with a named reason** rather than falling back to the
default. These knobs decide how many entities exist and where they go, so a typo that
silently ran a fleet at the default while its manifest said something else is a fight
nobody configured — and no counter, log line or wire field would report it. Decimals are
InvariantCulture, so `12.5` and never `12,5`. The values actually in force are published
as `enemy_ai` (and the derived `enemy_ai_max_now`) on `/status`, because an
already-allocated Agones GameServer keeps the environment it was created with and a fleet
update reaches only new pods.

### Adding a knob: strict parsing is only half of it

A `GAMESERVER_*` knob is **two** things — the constant the server parses, and a line in the
`environment:` block of **every** compose service that runs the game server. Docker forwards
nothing a service did not declare, so a knob with only the first half is documented,
strictly parsed, settable in `deploy/.env` and **silently ignored**: the server runs its
compiled default, `/status` reports that default truthfully, and the strict parser cannot
help because it never sees a value to refuse. There is no log line, no counter and no wire
field that differs.

That has happened three times here. `GameServer.Tests/Deploy/ComposeEnvPassthroughTests.cs`
is now the mechanical link: it reflects over every `GAMESERVER_*` constant the assembly
declares and fails `dotnet test` if any is missing from `gameserver-dotnet` in
`backend/deploy/docker-compose.yml` or from `gameserver-dotnet-map02` in
`backend/deploy/docker-compose.override.yml`. **Both**, because map_02 declares its own block
and inherits nothing — a one-service fix leaves the two maps reading the same `.env`
differently, which is harder to find than the original gap. Write each entry as
`NAME: ${NAME:-}` so an unset variable stays unset. A knob that genuinely should not be
passed through goes in that file's `Excluded` dictionary **with a reason**; it is empty
today.

**The gate's scope is narrower than it sounds.** It covers names declared as
`const string` (31 today). It does **not** cover names read from an inline literal (25
today, including `GAMESERVER_FIELD_DELTA` and `GAMESERVER_TICK_RATE` — several of those are
per-service values set literally in compose, not forwarded from `.env`), it does not cover
names built by concatenation **at runtime**, which exist nowhere as a whole string, and it
does not read `deploy/k8s/app/50-fleet-map.yaml`, which has the same shape of gap.

Declaring a new knob's name as a constant is what brings it under the gate, and that is
worth doing deliberately: `GAMESERVER_IMPORTANCE_W_{DISTANCE,CHANGE,TYPE,COMBAT}` were
assembled from a prefix and four suffixes, which is why the gate could not see the *first*
of the incidents above. Written as `const string` — `EnvVar + "_W_DISTANCE"` is a
compile-time constant, so each is a real literal in the assembly — they came under the gate
and it found a fifth instance at once: all four had been added to `gameserver-dotnet` when
that gap was first fixed and never to `gameserver-dotnet-map02`, so setting a weight changed
map_01's replication policy and silently left map_02 on the profile's own. The seven
*refused* `_W_` factors stay assembled from suffixes on purpose — a knob the server exits 2
on must not be demanded in compose.


**Bots count as players everywhere the simulation asks the world**, which is the point and
also the thing to know before turning them on. The enemy population cap is
`GAMESERVER_ENEMY_MAX + GAMESERVER_ENEMY_MAX_PER_PLAYER × players`, and a bot is one of
those players — so `GAMESERVER_BOTS=24` at the default allowance is a **1110-enemy world
before a single real client connects**. The server computes that number and logs it at
startup rather than leaving it to be discovered from a snapshot size; lower
`GAMESERVER_ENEMY_MAX_PER_PLAYER` alongside it. The one place a bot is deliberately *not*
a player is **persistence**: the save sweep reads `PersistablePlayerStates()`, which
excludes them by archetype tag (not by id), so a development server with bots on never
writes a player row for one.


#### Realtime transport (`--transport`, `TRANSPORT_KEY`)

> **The server tells you what it is actually doing, on every boot.** "Is this deployment
> encrypted" is not answerable from one variable, and — since `GAMESERVER_SEALED` began
> defaulting to `require` — **not answerable from the `transport_*` fields at all**. Those
> describe the transport only. On the default configuration the transport is TCP with no
> packet-crypt layer, so `transport_encrypted` is `false`, while every gameplay frame is
> encrypted and authenticated a layer above it by the sealed session. Read
> `sealed_required` before concluding anything from `transport_encrypted`. The posture is logged at startup (at **Warning** whenever
> traffic is in cleartext, Information when it is not) and published on `/status`:
>
> | field | meaning |
> |---|---|
> | `transport` | `tcp` or `kcp` |
> | `transport_key_configured` | `TRANSPORT_KEY` holds a value — **not** the same as encryption being on |
> | `transport_encrypted` | packets leave as ciphertext |
> | `transport_authenticated` | tampering is detectable — **`false` on every configuration this server supports today** |
> | `transport_cipher` | `aes-256-cfb`, or `none` |
> | `transport_posture` | one line stating what is happening and what is not |
> | `sealed_required` | a sealed session is required on the gameplay hop (`GAMESERVER_SEALED=require`, the default). **This, not `transport_encrypted`, is what says gameplay frames are encrypted** |
> | `sealed_cipher` | `chacha20-poly1305`, or `none` when sealing is off |
>
> Mirrored as `gameserver_transport_encrypted` and `gameserver_transport_authenticated`,
> which are **gauges** rather than counters precisely so they are present when they read
> `0` (a never-incremented counter is absent from `/metrics` entirely — see
> `docs/METRICS.md`).
>
> The four combinations, all reported:
>
> | transport | key | result |
> |---|---|---|
> | `tcp` (default) | unset | **plaintext** — the default, and it used to log nothing at all |
> | `tcp` | set | **plaintext**, and the key is *ignored* — the configuration most easily mistaken for working encryption |
> | `kcp` | unset | **plaintext** |
> | `kcp` | set | **encrypted**, `aes-256-cfb`, and **not authenticated** |
>
> **Encrypted is not authenticated.** The KCP path is AES-CFB with a CRC32, and a CRC32 is
> a linear checksum, not a MAC: an attacker who can modify datagrams can make controlled
> changes to the plaintext and repair the checksum. That is why the two are separate
> fields, and why `transport_authenticated` is published while false rather than omitted —
> so that its becoming true is a visible event. See ADR-21 and
> `docs/ROADMAP-SECURITY.md` §2.

The gameplay hop (client ↔ this server) speaks **TCP** by default and **KCP over
UDP** with `--transport kcp`. KCP is reliable and ordered like TCP, but its ARQ
is tuned for latency instead of throughput: a lost packet recovers in roughly one
RTT instead of a TCP RTO backoff, which is what a 10-15Hz authoritative tick loop
wants on a mobile network.

The listener is wire-compatible with the Go side (`backend/shared/transport`,
`github.com/xtaci/kcp-go/v5`) — a Go or Unity client dialling through that
package reaches this server, and the tuning profile (nodelay 1, interval 10ms,
resend 2, no congestion control, 128/128 windows, MTU 1350, FEC off, stream mode)
is identical on both halves. `interop/kcpprobe` is a Go client that proves it;
see `docs/DESIGN.md`.

```bash
export TRANSPORT_KEY="$(openssl rand -hex 32)"   # same value on every peer
dotnet run --project GameServer -- --transport kcp --addr :9000
```

**Encryption.** `TRANSPORT_KEY` turns on AES-256 on every datagram, below the
ARQ — the join token and all gameplay state included. The key is accepted in two
forms, exactly as on the Go side:

- **64 hex characters** — used verbatim as the 32-byte key. Recommended
  (`openssl rand -hex 32`): full entropy, no derivation guesswork.
- **anything else** — treated as a passphrase and stretched with HKDF-SHA256. A
  short passphrase stays brute-forceable; HKDF spreads entropy, it does not
  create it.

There is **no negotiation and no downgrade path**. A peer without the right key
produces datagrams that fail the checksum and are dropped, so the session never
forms — "encrypted server + plaintext client" fails closed, silently, as a read
timeout on the client. Leaving the key unset logs a start-up WARNING; that is
fine for local dev and not for a port reachable from the internet.

`TRANSPORT_KEY` is ignored with `--transport tcp` (and warned about): TCP has no
packet encryption here, so TLS termination or the cluster network is the answer.

**Advertisement.** The transport is published into the registry alongside the
address, and the gateway hands it to clients in `EnterWorldResponse.Transport`.
Running this server with `--transport kcp` therefore also tells clients to dial
KCP — but only if it self-registers (`REDIS_ADDR` set). Under Agones the gateway
announces allocated servers before their own registration lands and falls back to
its own listen transport, so set `ALLOCATOR_TRANSPORT=kcp` on the gateway when the
fleet speaks KCP and the gateway does not.

#### Agones (`--agones`, `AGONES_SDK_HTTP_PORT`)

Off by default. With the flag set, the server talks to the Agones sidecar over
**HTTP on `localhost:9358`** — four POSTs with an empty JSON body (`/ready`,
`/health`, `/allocate`, `/shutdown`) plus one read, `GET /gameserver`. HTTP and
not the official C# SDK on purpose (ADR-14 decision 1): that SDK is gRPC and would
pull `Grpc.Net.Client` into a module whose rules are NativeAOT-compatible and no
external dependencies. `System.Net.Http` is in-box, the request bodies are string
literals, and the one response parsed goes through a `System.Text.Json` source
generator.

Lifecycle, in order:

| When | What |
|------|------|
| listener bound | `POST /ready` |
| after Ready, before registering | `GET /gameserver` — learn the address Agones assigned, and advertise **that** (see below) |
| — | **then** the Redis registry entry is written, never the other way round (ADR-14 decision 3) |
| every 2s while running | `POST /health`. The fleet manifest's health block uses `periodSeconds: 5`, so two pings fit in one window and a single dropped request is not a strike |
| first player joins | `POST /allocate`, once per process, off the join's critical path |
| graceful shutdown | registry entry removed first, **then** `POST /shutdown` |

**The advertised port comes from Agones, never from configuration** (ADR-15
decision 2, option A; the host is a separate question, answered right below). The
fleets use `portPolicy: Dynamic`, so Agones picks the
host port when it schedules the pod and *no* static value can be right: the
manifest passes `--addr=:9000` and sets no `GAMESERVER_PUBLIC_ADDR`, so without
this read the server registers the hostless `:9000`, the gateway copies it into
`MsgEnterWorldResp.ServerAddr` verbatim, and the client dials nothing. So after
Ready — the address does not exist until the pod is scheduled — the server reads
`GET /gameserver` and composes `status.address` with the port whose **name** is
`game`:

```json
{"status":{"state":"Ready","address":"192.168.65.3",
           "ports":[{"name":"game","port":7691}]}}
```

The port is selected by name and never by index, matching `ports[].name` in
`deploy/agones/fleet-*.yaml` and `gamePortName` in the gateway's
`registry/agones_allocator.go`; picking `ports[0]` would silently advertise the
wrong port the day a fleet declares a second one.

##### The port comes from Agones, the host usually needs an override

`status.address` is the **node** address, and outside the cluster network a client
cannot dial it. Measured on k3d (k3d v5.8.3, k3s v1.31.5, Agones 1.59.0, gameserver
ports 7000-7100 published by the serverlb) against a live `portPolicy: Dynamic`
GameServer reporting `172.20.0.3:7008`:

| From | To | Result |
|---|---|---|
| WSL2 | `127.0.0.1:7008` | **PONG** |
| Windows (where the Unity client runs) | `127.0.0.1:7008` | **True** |
| Windows | `172.20.0.3:7008` | False |
| WSL2 | `172.20.0.3:7008` | connection refused |

So the read gets the **port** exactly right — `7008` is the Agones-assigned dynamic
port and nothing else can supply it — and the **host** wrong. Hence
`GAMESERVER_ADVERTISE_HOST` / `--advertise-host`:

| When Agones is **on** and the status read succeeded | |
|---|---|
| host | `GAMESERVER_ADVERTISE_HOST` if set, else `status.address` |
| port | **always** the Agones-assigned `game` port — never configurable |

On the k3d setup above, `GAMESERVER_ADVERTISE_HOST=127.0.0.1` produces
`127.0.0.1:7008`, which is dialable from both WSL2 and Windows.

> **Two address knobs, and they do not overlap. Read this before setting either.**
>
> | | `GAMESERVER_PUBLIC_ADDR` | `GAMESERVER_ADVERTISE_HOST` |
> |---|---|---|
> | Value | full `host:port` | **host only, no port** |
> | Applies when | Agones is **off** | Agones is **on** *and* the status read succeeded |
> | Supplies the port | yes | **never** |
>
> Exactly one applies to any given deployment. Setting `GAMESERVER_ADVERTISE_HOST`
> with Agones disabled logs a warning and changes nothing. Setting it to a full
> `host:port` by mistake logs a warning, honours the host and discards the port —
> the port always comes from Agones.

Every failure falls back to today's resolution — `--public-addr` /
`GAMESERVER_PUBLIC_ADDR` / the listen address — and logs a warning: a sidecar that
does not answer, a non-2xx, an unparsable body, a status with no address (the pod
is not scheduled yet), or no port named `game`. **`GAMESERVER_ADVERTISE_HOST` is
not applied on that path**: with no Agones port to pair it with, composing it with
a *configured* port would invent an address that was never assigned to anything —
a plausible-looking value pointing nowhere, harder to diagnose than an honestly
wrong one. Never fatal: a server nobody can reach still serves the players already
on it, and a crash loop serves nobody. With Agones disabled the read is not
attempted at all.

Start-up and the composition both log which half came from where, because when
this is wrong it is wrong silently — the server runs, the registry looks healthy,
and only the client knows:

```
Advertising 127.0.0.1:7008 (host from GAMESERVER_ADVERTISE_HOST, port 7008 from
Agones status); configured value ':9000' not used
```

##### Registering on `Allocated` instead of on Ready (`--register-on-allocated`)

**Default off, and the default is the behaviour that shipped.** With the flag unset
the server registers immediately after `ReadyAsync()`, exactly as before; a fleet
that has not been migrated sees no change from this option existing.

Why it exists: every pod of the map fleet carries the same fleet-wide
`GAMESERVER_MAP_ID`, so a second `Ready` replica is a second **live** server for
that map with no allocation involved. Measured on k3d, scaling the fleet 1 → 2 put
two members into `servers:map:map_01` within a second of the new pod reaching
Ready, and `registry.FindServer` then returns the *least loaded* of the two — the
unallocated spare, which Agones is free to delete on the next scale-down. That is
ADR-2's one-live-server-per-map invariant broken by the replica count alone, which
is why the fleet is pinned at `replicas: 1` and why ADR-18 refuses a buffer
`FleetAutoscaler`.

With `GAMESERVER_REGISTER_ON_ALLOCATED=true` and Agones enabled, the server polls
`GET /gameserver` every second and publishes its registry entry only once
`status.state` reads `Allocated`. A `Ready`-but-unallocated pod therefore holds no
registry entry and is genuinely spare.

What does **not** change:

- **Ready still comes first.** This narrows ADR-14 decision 3 rather than reversing
  it: Ready remains a precondition of being allocatable, it has simply stopped
  being sufficient for being *registered*. On the way down the order is unchanged —
  deregister, then Agones `Shutdown`.
- **The address read stays where it is**, between Ready and registration, so the
  Agones-assigned `host:port` is still the first value written (ADR-15 decision 2).
- **The health loop keeps running while the pod waits.** The wait happens on a
  background task, not on the start-up path — a pod that blocked here would stop
  pinging and Agones would kill it — and the listener is accepting throughout, so a
  client reaching a just-allocated pod is not refused while the state read is in
  flight.
- **Agones off changes nothing.** There is no GameServer object to reach
  `Allocated`, so honouring the flag would mean never registering. The server logs
  that it is ignoring the flag and registers at start-up. docker-compose, local
  runs and the test suite are unaffected.

An **unreadable** state — no sidecar, a timeout, a non-2xx, a body without
`status.state` — is treated as "keep waiting", never as "assume allocated". The
wait has no timeout and never fails the server: a pod that waits forever holds no
entry and serves nobody, which is the safe end of the failure; the unsafe end is a
spare pod taking live players.

**Deallocation and restart.** Once registered, the entry stays for the life of the
process and is removed only by the existing shutdown path (deregister, then Agones
`Shutdown`) or by its 15s TTL lapsing. The gate is not re-armed and the server does
not deregister if a later state read stops saying `Allocated`. Two reasons: Agones
has no un-allocate — an `Allocated` GameServer leaves that state by being shut
down, which already terminates the process — and a second writer that could yank a
live server out of the registry on one transient read failure is exactly the
two-writers-one-datum hazard ADR-1 forbids. A pod that **restarts** while its
GameServer is `Allocated` re-registers at once: the gate's first read already says
`Allocated`.

> **Tooling note.** A pod that is `Ready` and deliberately unregistered is a new
> legitimate state. `verify.sh` layer 3, the integration suite and
> `refusal.split_world` assume a pod registers shortly after becoming Ready, and
> must be taught this state before the flag is turned on for a fleet. Nothing
> changes while it is off.

**No call can throw.** A missing, slow or 500-ing sidecar is logged and ignored:
every call site is either start-up or a background loop, and an exception in
either turns a sidecar hiccup into a dead game server. Health failures are
counted — the first logs a warning, every fifth consecutive one logs an error
naming the count — because swallowing them silently would hide the cause of the
pod restart that Agones will eventually perform when pings stop arriving.

With Agones **disabled** nothing changes from the pre-SDK behaviour, including the
health loop, which does not start at all: pinging the no-op SDK logged
"health loop started" and reported nothing to anyone, which reads in a log exactly
like a working liveness contract (ADR-14 decision 4).

> ⚠️ **Still unproven end to end, with one exception.** No C# server in this
> project has ever reported Ready to Agones, and no client has ever dialled an
> address this server learned from a sidecar. The tests use a local `HttpListener`
> standing in for the sidecar, which pins the HTTP shape and the failure behaviour
> and nothing about Kubernetes.
>
> The exception is the response shape of `GET /gameserver`, which was captured from
> a **real Agones 1.59.0 sidecar** (`kubectl port-forward` to
> `map-servers-dev-kl485-gsmrh` in `rpg-realtime`) and is used verbatim as the
> success fixture in `GameServer.Tests/Agones/HttpAgonesSdkAddressTests.cs`. The
> endpoint and the field names are therefore observed, not assumed; what remains
> unobserved is this server making that call from inside a pod.
>
> ADR-14 stage 4 (deploy the dotnet fleet, watch for a restart loop) is where the
> rest gets evidence; until then the fleet manifest's health block stays
> `disabled: true`.

#### Join-token secret (`JOIN_TOKEN_SECRET`)

The join token the client presents to this server is signed by the gateway with
`JOIN_TOKEN_SECRET`, **not** with `JWT_SECRET`. The two must hold the same value
on both halves — the gateway signs, this server verifies:

```bash
export JOIN_TOKEN_SECRET="$(openssl rand -hex 32)"   # same value on gateway + every game server
```

`JWT_SECRET` (the Nakama→client auth secret) is never distributed to game-server
pods, which is the whole point: a compromised pod holds only the join secret and
therefore cannot mint auth tokens for arbitrary users.

**Fallback.** When `JOIN_TOKEN_SECRET` is unset the server verifies join tokens
with `JWT_SECRET` and logs a start-up warning. The gateway does exactly the same,
so an unconfigured deployment still works — but the split is not active. Setting
it on **one** side only breaks every join.

**Rotation.** Both secrets accept a comma-separated list, `"current,previous"`.
The gateway signs with the first entry; every entry verifies here, so old tokens
drain instead of being rejected at the deploy. Procedure:

1. Deploy `JOIN_TOKEN_SECRET="new,old"` to the gateway and every game server.
2. Wait out the join-token TTL (`constants.JoinTokenTTL`), so no token signed
   with `old` is still in a client's hands.
3. Deploy `JOIN_TOKEN_SECRET="new"`.

Whitespace around entries is trimmed and empty entries are dropped, so
`"new, old"` and a trailing comma are fine. A spec with no usable secret at all
(and no `JWT_SECRET` either) fails **closed** — every join is rejected and the
start-up log says so.

#### Player state persistence (`GAME_DB_URL`)

When set, player state is persisted to the game-state PostgreSQL database and
survives a server restart. When unset the server falls back to an in-memory
store and **all player state is lost on restart** — the startup log says which
store is active.

```bash
dotnet run --project GameServer -- \
  --addr :9000 \
  --game-db-url 'postgres://game:localdev@localhost:5433/gamestate?sslmode=disable'
```

Accepted formats are a libpq URL (above) or a native Npgsql keyword string
(`Host=...;Database=...;Username=...;Password=...`). The password is masked in
every log line.

The schema is created on boot if missing (idempotent), so pointing at an empty
database is enough. If the database is configured but unreachable the server
logs a critical error and **exits with status 1** rather than silently
degrading to the memory store and losing writes.

#### Schema migrations (`--migrate-only`)

Schema history lives in numbered migrations under
`GameServer/Persistence/Migrations/`, embedded into the binary. They are applied
transactionally, in order, exactly once, and the checksums of already-applied
migrations are verified on every run — an edited migration fails loudly instead
of letting environments drift apart.

The server applies pending migrations at boot. `--migrate-only` does just that
and exits, which is how CD migrates before restarting anything:

```bash
gameserver-dotnet --migrate-only --game-db-url "$GAME_DB_URL"
# exit 0 = applied or already current, 1 = failure, 2 = no DSN given
```

Adding a migration and the backward-compatibility rules are documented in
`backend/deploy/docs/DATABASE.md`.

### Run Tests

```bash
# Run all tests
dotnet test

# Run with detailed output
dotnet test --verbosity normal

# Run with test results file
dotnet test --logger "trx;LogFileName=test-results.trx"
```

### Docker Build

The Dockerfile expects `backend/` as the build context:

```bash
cd backend/
docker build -f deploy/docker/Dockerfile.gameserver-dotnet \
  -t rpg-mmo/gameserver-dotnet:dev .
```

### NativeAOT Publish (local)

```bash
# On Ubuntu/Debian, install prerequisites first:
sudo apt-get install -y clang zlib1g-dev

# On Alpine:
apk add clang build-base zlib-dev

# Publish
dotnet publish GameServer/GameServer.csproj -c Release -o ./publish
```

The resulting binary is a single self-contained executable (~30-45 MB), with no
dependency on the .NET runtime.

## Shared Logic Usage

### In .NET Server

The `GameServer` project references `Shared.GameLogic` directly via
`<ProjectReference>`. All movement, combat, and validation calls go through the
shared library.

### In Unity Client

Add `Shared.GameLogic` to the Unity project as a local package or source folder:

1. Copy or symlink `Shared.GameLogic/` into `Assets/Plugins/Shared.GameLogic/`
2. Create an Assembly Definition (`Shared.GameLogic.asmdef`) in that folder:
   ```json
   {
     "name": "Shared.GameLogic",
     "rootNamespace": "Shared.GameLogic",
     "references": [],
     "includePlatforms": [],
     "excludePlatforms": [],
     "allowUnsafeCode": true
   }
   ```
3. Reference the assembly from your DOTS systems:

```csharp
using Shared.GameLogic.Components;
using Shared.GameLogic.Systems;

public partial struct PlayerMovementSystem : ISystem
{
    public void OnUpdate(ref SystemState state)
    {
        // Same fixed timestep the server uses: dt = 1 / tickRate.
        float dt = MovementSystem.DeltaTimeForTickRate(GameConstants.DefaultTickRate);
        MapBounds bounds = MapBounds.Default;

        foreach (var (transform, input) in
            SystemAPI.Query<RefRW<LocalTransform>, RefRO<PlayerInput>>())
        {
            // Identical call the server makes -> prediction matches authority.
            var result = MovementSystem.ResolveDirection(
                input.ValueRO.MoveX, input.ValueRO.MoveY, out Vec2 direction);

            if (result is MoveResult.Accepted or MoveResult.Clamped)
            {
                var predicted = MovementSystem.Integrate(
                    ToVec2(transform.ValueRO.Position), direction,
                    input.ValueRO.Speed, dt, bounds);

                transform.ValueRW.Position = ToFloat3(predicted);
            }
        }
    }
}
```

**Important**: `Shared.GameLogic` must never reference Unity-specific assemblies.
If you need Unity math types, create thin adapter methods in the client project
that convert between `System.Numerics` and `Unity.Mathematics`.

### Detailed Unity Integration Guide

There are three ways to add `Shared.GameLogic` to a Unity 6 project:

#### Option A: Git Submodule (recommended for team workflows)

```bash
# From Unity project root
git submodule add https://github.com/Cuvara/rpg-mmo-indie.git \
  Packages/com.rpgmmo.shared-gamelogic
```

Add to `Packages/manifest.json`:
```json
{
  "dependencies": {
    "com.rpgmmo.shared-gamelogic": "file:com.rpgmmo.shared-gamelogic/backend/gameserver-dotnet/Shared.GameLogic"
  }
}
```

The `Shared.GameLogic/` folder needs a `package.json` for UPM:
```json
{
  "name": "com.rpgmmo.shared-gamelogic",
  "version": "0.1.0",
  "displayName": "RPG MMO Shared Game Logic",
  "description": "Pure C# game logic shared between server and client",
  "unity": "2022.3"
}
```

And an Assembly Definition (`Shared.GameLogic.asmdef`):
```json
{
  "name": "Shared.GameLogic",
  "rootNamespace": "Shared.GameLogic",
  "references": [],
  "includePlatforms": [],
  "excludePlatforms": [],
  "allowUnsafeCode": true
}
```

#### Option B: Local Folder / Symlink

```bash
# Symlink into Assets (works on Linux/macOS; on Windows use mklink /D)
ln -s /path/to/backend/gameserver-dotnet/Shared.GameLogic \
  Assets/Plugins/Shared.GameLogic
```

Then create the `.asmdef` file as shown above. Reference from your DOTS
assemblies via the `references` array.

#### Option C: Copy (simplest, no auto-sync)

Copy `Shared.GameLogic/*.cs` files into `Assets/Plugins/Shared.GameLogic/`.
Add the `.asmdef` file. Manually sync when the server version updates.

#### Unity Type Adapters

`Shared.GameLogic` uses `System.Numerics.Vector2` for positions. Create a thin
adapter in the client project:

```csharp
using Unity.Mathematics;
using SysVec2 = System.Numerics.Vector2;

public static class MathAdapter
{
    public static float2 ToFloat2(this SysVec2 v) => new(v.X, v.Y);
    public static SysVec2 ToSysVec2(this float2 v) => new(v.x, v.y);
}
```

## Wire Protocol

The game server communicates over TCP using a length-prefixed JSON protocol,
identical to the Go game server:

```
+-------------------+--------------------+
| Length (4 bytes BE)| JSON payload       |
+-------------------+--------------------+
```

- **Length**: 4-byte big-endian unsigned integer, size of the JSON payload in bytes.
- **Payload**: UTF-8 JSON object with a `type` field and a `data` field.

### Envelope Format

```json
{
  "type": <integer>,
  "data": { ... }
}
```

### Message Types

| Type | Name               | Direction       | Description                          |
|------|--------------------|-----------------|--------------------------------------|
| 1    | `join`             | Client -> Server | Player join request (JWT token)      |
| 2    | `join_ack`         | Server -> Client | Join accepted (player ID, world state)|
| 3    | `input`            | Client -> Server | Player input (movement, actions)     |
| 4    | `snapshot`          | Server -> Client | World state snapshot (AOI-filtered)  |
| 5    | `attack`           | Client -> Server | Attack / skill use request           |
| 6    | `damage`           | Server -> Client | Damage event notification            |
| 7    | `death`            | Server -> Client | Entity death notification            |
| 8    | `spawn`            | Server -> Client | Entity spawn notification            |
| 9    | `despawn`          | Server -> Client | Entity despawn notification          |
| 10   | `disconnect`       | Either           | Graceful disconnect                  |
| 11   | `ping`             | Client -> Server | Latency measurement                  |
| 12   | `pong`             | Server -> Client | Latency measurement response         |

### Example: Join

```json
{"type": 1, "data": {"token": "eyJhbGciOiJIUzI1NiIs..."}}
```

### Example: Input

```json
{"type": 3, "data": {"tick": 1042, "move_x": 1.0, "move_y": 0.0, "attack_target_id": null}}
```

**`move_x` / `move_y` are a movement DIRECTION, not a displacement.** The server
integrates `direction * speed * dt` once per tick (`dt = 1 / tickRate`), then clamps
the result to the map bounds:

| Client sends           | Server does                                              |
|------------------------|----------------------------------------------------------|
| `(1, 0)`               | move right at full speed                                  |
| `(0.5, 0)`             | move right at half speed (analog stick)                   |
| `(1, 1)`               | normalized to `(0.707, 0.707)` — diagonals are not faster |
| `(1.2, 0)`             | normalized to `(1, 0)`                                    |
| `(5, 0)` / `NaN` / `∞` | rejected, logged at Debug, entity does not move           |
| `(0, 0)`               | no movement                                               |

Only the newest input per player is integrated each tick, so sending inputs faster
than the tick rate does not move the player further. Distance travelled depends only
on wall-clock time and the entity's `speed` stat (world units per second, default
5.0). `tick` is echoed back as the entity's `LastInputTick` for client reconciliation.

Map bounds default to 1000x1000 world units centered on the origin and are
configurable per server via `--map-width` / `--map-height`
(`GAMESERVER_MAP_WIDTH` / `GAMESERVER_MAP_HEIGHT`).

### Example: Snapshot

```json
{
  "type": 4,
  "data": {
    "tick": 1043,
    "entities": [
      {"id": "p_abc", "x": 10.5, "y": 20.3, "hp": 100, "state": "idle"},
      {"id": "p_def", "x": 12.0, "y": 19.8, "hp": 85, "state": "moving"}
    ]
  }
}
```

### JSON Field Naming

All JSON fields use `snake_case` to match the Go gateway convention. The C#
serializer is configured with `JsonNamingPolicy.SnakeCaseLower`.
