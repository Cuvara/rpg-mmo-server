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
| `--mode` | `GAMESERVER_MODE` | `map` | `map` or `dungeon` (dungeon uses a 60s reconnect hold) |
| `--addr` | `GAMESERVER_ADDR` | `:9000` | Game traffic listen address |
| `--map-id` | `GAMESERVER_MAP_ID` | `map_01` | Map identifier, also the `map_id` metric label |
| `--server-id` | `GAMESERVER_ID` / `POD_NAME` | random | Server identity checked against the join token |
| `--capacity` | `GAMESERVER_CAPACITY` | `100` | Maximum concurrent players |
| `--sim-critical-hz` | `SIM_CRITICAL_HZ` | `60` | Frequency of the **critical** group (input, movement, combat). This is also the **base tick rate** of the loop — every other group is derived from it |
| `--sim-world-hz` | `SIM_WORLD_HZ` | `15` | Frequency of the **world** group (AI, spawning, despawning) **and of the snapshot broadcast**. Must divide `SIM_CRITICAL_HZ` exactly and must not exceed it, or the server exits with code 2 |
| `--sim-background-hz` | `SIM_BACKGROUND_HZ` | `5` | Frequency of the **background** group (work that tolerates a whole interval of delay). Must divide `SIM_CRITICAL_HZ` exactly and must not exceed `SIM_WORLD_HZ` |
| `--tick-rate` | `GAMESERVER_TICK_RATE` | *(unset → `60/15/5`)* | **Legacy single-rate switch.** Sets *every* group to this one rate, i.e. base = world = background, snapshots every tick — the pre-multi-rate server exactly. Only applies when no `SIM_*_HZ` environment variable is set; any of them present wins and the tick rate is ignored |
| `--keyframe-interval` | `GAMESERVER_KEYFRAME_INTERVAL` | `30` | Delta snapshots between full keyframes; `0` disables delta encoding (see `docs/API.md`) |
| `--map-width` | `GAMESERVER_MAP_WIDTH` | `1000` | Map width in world units |
| `--map-height` | `GAMESERVER_MAP_HEIGHT` | `1000` | Map height in world units |
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
| `--redis` | `REDIS_ADDR` | *(unset)* | Registry Redis; unset disables self-registration |
| `--redis-password` | `REDIS_PASSWORD` | *(unset)* | Registry Redis password |

#### Realtime transport (`--transport`, `TRANSPORT_KEY`)

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
