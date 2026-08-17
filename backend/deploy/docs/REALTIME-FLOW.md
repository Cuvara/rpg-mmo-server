# Realtime Flow — what happens when a player enters a map

One question, answered hop by hop: **a player opens the game and walks into
`map_01` — what actually moves, and in what order?** Twice over, because there
are two answers. Flow A is what runs today: Docker Compose on a self-hosted
runner, and it works end to end. Flow B is the Agones path as it exists in this
repo: fleets Ready in a cluster, allocating nothing, and unable to hand a client
a dialable address if they did. Flow C is what "working" would look like, with
every missing arrow marked as missing.

ADR-15 argues whether to go there. This document only shows the mechanics, so
that the argument is about something concrete.

Everything here is tied to a file and a line, because the interesting parts of
this system are the places where two components disagree about what an address
means, and that is only visible in the source.

## 0. The contract both flows have to satisfy

The handshake is normative in `backend/gameserver-dotnet/docs/API.md:65-83`; the
type numbers come from `backend/shared/messages/messages.go:33-52`. ADR-3
constrains it to exactly this shape.

```
   CLIENT                    GATEWAY                     GAME SERVER
     │                          │                             │
     │──(1) auth {token} ──────▶│  JWT verified locally,      │
     │◀─(2) auth_resp ──────────│  no Nakama roundtrip        │
     │                          │                             │
     │──(3) enter_world{map_id}▶│  registry lookup            │
     │◀─(4) enter_world_resp ───│  {server_addr, join_token}  │
     │      ▲                   │                             │
     │      └─ THE ADDRESS. Handed back verbatim.             │
     │                                                        │
     │──(5) join_token {token} ──────────────────────────────▶│
     │◀─(6) join_token_resp {ok, tick_rate} ──────────────────│
     │──(7) input {tick, move_x, move_y} ────────────────────▶│  per tick
     │◀─(8) snapshot {tick, ack_tick, full, entities[]} ──────│  per world tick
```

The gateway drops out after step 4. It never carries `input` or `snapshot` —
it is a redirector, not a proxy (ADR-3), and the join token it mints in
`backend/gateway/transfer/map_assign.go:37-50` names a server the client dials
itself.

That makes **step 4 the load-bearing hop of this entire document**. The gateway
copies `ServerInfo.Addr` out of the registry into `enter_world_resp.server_addr`
and does nothing else to it — no rewriting, no normalisation, no validation. Two
consequences follow, and both flows below are stories about them:

- whatever value the game server wrote into the registry is what the client
  will try to open a TCP connection to; and
- nothing in the gateway can notice that the value is wrong. A hostless or
  wrong-port address fails two hops later, inside the client, where no server
  log is watching.

### Where that value comes from

The C# server publishes itself. `GameServer/Program.cs:101`:

```csharp
string publicAddr = GetArg(args, "--public-addr") ?? Env("GAMESERVER_PUBLIC_ADDR") ?? addr;
```

The fallback is `addr` — the **listen** address. That is correct exactly when the
listen address is also the address a client can dial, i.e. host-mode deploys.
The moment anything maps ports (a container, a Kubernetes node port) the listen
address and the client-facing address diverge, and `GAMESERVER_PUBLIC_ADDR` has
to be set explicitly or the fallback publishes a lie. `RegistrationOptions`
states the contract on the field itself
(`GameServer/Registry/RegistrationService.cs:30-37`), and the server warns at
startup when the value has no host part (`Program.cs:154-169`) — a warning, not
a failure, because there is a legitimate host-mode case for it.

### The registry entry, and who keeps it alive

`RegistrationService` owns the entry for the process lifetime:

| Property | Value | Source |
|---|---|---|
| TTL | 15s | `RegistrationService.cs:14` (`RegistryDefaults.HeartbeatTtl`, mirrored from Go `shared/constants`) |
| Heartbeat interval | TTL/3, floor 1s | `RegistrationService.cs:20-21` |
| Missing entry | re-registered on the next heartbeat | `RegistrationService.cs:130-140` |
| Player count | pushed when it changes, off the hot path | `RegistrationService.cs:182-214` |
| Shutdown | explicit deregister, don't wait out the TTL | `RegistrationService.cs:220-232` |

Every heartbeat is also a repair, which is what makes a Redis wipe self-heal
within one interval. It is also why a wrong address is *sticky*: the entry keeps
being rewritten with the same wrong value for as long as the pod lives.

Registration is gated on `REDIS_ADDR` alone — `Program.cs:87` reads it,
`Program.cs:313` branches on it. There is no separate "register me" switch. A
server that can see Redis registers itself; a server that cannot runs
unregistered and the gateway answers `enter_world` with "no available server".

### How the gateway chooses

`backend/gateway/registry/registry.go:202-252`, in order:

1. `findByMapIDWithRetry` returns **every** server registered for the map.
2. More than one is a logged warning, not an error (`:208-216`) — the MVP
   invariant of one live server per `map_id` is documented (ADR-2) and enforced
   by nothing.
3. Least-loaded server with `PlayerCount < Capacity` wins, ties broken on
   `ServerID` so the choice is deterministic (`:218-235`).
4. **Only if none has room** does it consult the allocator (`:237-249`), register
   what came back, and return it.
5. With no allocator: `ErrNoServerAvailable` (`:251`) and the client cannot enter.

Step 4 is the door to Agones, and step 3 is the reason it stays shut. Hold on to
that ordering; it is the centre of Flow B.

`--allocator` is `""` by default, resolved through the `ALLOCATOR` env var to
`none` (`backend/gateway/cmd/gateway/main.go:47`, `:356-368`). Nothing in
`cd.yml` sets it, so every deployed gateway today runs with no allocator at all.

---

## 1. Flow A — today: compose on a self-hosted runner

This is the flow that works. Two halves: a deploy that puts processes on a box,
and a runtime handshake between them.

### A.1 Deploy

```
 push to develop / staging / release-*
   │
   ▼
 .github/workflows/cd.yml
   │
   ├─ resolve ──────────── ref → environment → runner labels        (cd.yml:74-103)
   │                       develop→dev, staging→staging, release-*→production
   ├─ bundle (ubuntu) ──── go build ×3 + dotnet publish + nakama.so
   │                       → artifact deploy-bundle-<sha>            (cd.yml:278-353)
   ├─ db-migrate ───────── pg_dump both DBs + redis checkpoint       (cd.yml:373-431)
   │                       (backup only; migrations moved into deploy)
   ▼
 deploy — ON THE SELF-HOSTED RUNNER (labels: self-hosted, <env>)
   │
   ├─ sync bundle into $RPG_DEPLOY_DIR/{bin,deploy,scripts}          (cd.yml:488-529)
   ├─ write $RPG_DEPLOY_DIR/deploy/.env   ← 7 secrets + ~40 vars     (cd.yml:532-729)
   │     └─ REFUSES a hostless GAMESERVER_PUBLIC_ADDR                (cd.yml:645-670)
   ├─ docker compose up -d          (data tier only: pg, pg-game, redis, nakama)
   │                                                                 (cd.yml:772-800)
   ├─ gameserver-dotnet --migrate-only --game-db-url …               (cd.yml:802-820)
   ├─ docker compose --profile monitoring [--profile realtime] up -d --remove-orphans
   │                                                                 (cd.yml:828-893)
   └─ healthcheck: /healthz on both metrics ports + TCP on both game ports
   │
   ▼
 post-deploy-smoke — bin/smoketest, same runner                      (cd.yml:995-1031)
   nakama health → device auth → gateway_token RPC → auth →
   enter_world → join_token → input/snapshot → clean disconnect
```

Two details in that chain are worth more than their line count.

**`cd.yml` applies no Kubernetes manifest anywhere.** There is no `kubectl` in
the workflow. Whatever the `agones/` directory says, nothing in CI or CD has ever
sent it to a cluster.

**The `.env` file is the deploy.** `docker-compose.yml` parameterises every
container name and every published port precisely so two environments can share
one runner (`docker-compose.yml:18-20`), and `cd.yml` forwards the whole set from
GitHub Environment variables. The stack's identity — which ports, which container
names, which database, which Redis — is a generated file, not a manifest under
version control.

### A.2 What compose actually runs

`backend/deploy/docker-compose.yml`, project name `rpg-mmo-meta`, container names
prefixed by `COMPOSE_NAME_PREFIX` (default `rpg`):

| Service | Profile | Publishes (default) | Notes |
|---|---|---|---|
| `postgres` | — | `${POSTGRES_PORT:-5432}:5432` | Nakama meta DB, volume `postgres-data` (`:28-47`) |
| `postgres-game` | — | `${POSTGRES_GAME_PORT:-5433}:5432` | game state, volume `postgres-game-data`, `init-gamestate.sql` mounted (`:54-77`) |
| `redis` | — | `${REDIS_PORT:-6379}:6379` | registry + sessions + streams, `maxmemory-policy noeviction` per ADR-4 (`:126-163`) |
| `nakama` | — | 7349/7350/7351/9100 | mounts `./modules` for `nakama.so` (`:79-124`) |
| `gateway` | `realtime` | `${GATEWAY_CONTAINER_PORT:-8100}:8000` | `--addr=:8000 --backend=redis`, no allocator flag (`:184-209`) |
| `gameserver-dotnet` | `realtime` | `${GAMESERVER_CONTAINER_PORT:-9200}:9000` | container name is `rpg-gameserver` (`:216-280`) |
| `lgtm` | `monitoring` | 3000 / 4317 / 4318 / 9090 | one `grafana/otel-lgtm` container, three config mounts (`:293-339`) |

`GAMESERVER_ADDR` is hardcoded `":9000"` in the service
(`docker-compose.yml:234`) while the host port is a variable — which is exactly
the listen-vs-public split from §0, made concrete. The service's own comment
spells the consequence out at `:257-274`, and the default
`GAMESERVER_PUBLIC_ADDR=:${GAMESERVER_CONTAINER_PORT:-9200}` is flagged there as
correct **only** for host mode, because it is hostless.

That is why `cd.yml:645-670` refuses a hostless value outright rather than
letting it through: it defaults to `127.0.0.1:<port>` on `dev` (where the runner,
the stack and the client are one machine) and **fails the deploy** on any other
environment that has not set it explicitly. The check matches `NormalizeDialAddr`
in `backend/smoketest/smoke/helpers.go`, so both ends agree on what "hostless"
means.

### A.3 Ports per environment

| | dev | staging | production |
|---|---|---|---|
| gateway, client-facing | 8000 | 8000 | **8010** |
| game server, client-facing | 9200 | 9200 | set per environment |
| Nakama HTTP | 7350 | 7350 | **7360** |
| gateway metrics | 9102 | 9102 | 9102 unless overridden |
| game server metrics | 9101 | 9101 | 9101 unless overridden |

Dev and staging leave everything at the compose defaults. Production is the first
environment to move off them, and the two values above are the ones this repo can
cite: `GATEWAY_CONTAINER_PORT=8010` and `NAKAMA_HTTP_PORT=7360`, both recorded in
`../CHANGELOG.md:52` and `CICD.md:234` because a smoke test that assumed the
defaults failed on them. **Every other production port lives in the `production`
GitHub Environment's variables and is not in this repository** — do not guess
them from a document; read them with `gh variable list --env production` or off
the box.

The class of bug is worth naming, since it will recur under Kubernetes for the
same reason: the deploy healthcheck probes the *metrics* ports, which are
forwarded per environment, so it goes green while the client-facing path is
unreachable. Only the smoke test, which dials what a client dials, catches it.

### A.4 Runtime, on a live dev box

Concretely, with the compose stack up (`docker.exe ps` on the current machine
shows `rpg-gateway` publishing `0.0.0.0:8000->8000` and `rpg-gameserver`
publishing `0.0.0.0:9200->9000`):

```
 boot:  gameserver binds :9000 inside the container      (GameServer.cs:333)
        publishes the bound address to waiters           (GameServer.cs:349)
        agonesSdk.ReadyAsync()  ← no-op here             (GameServer.cs:356)
        registration.StartAsync() → Redis hash           (GameServer.cs:364)
             servers:<id> = {addr: "127.0.0.1:9200", map_id, capacity, count}
                                   ▲
                                   └─ GAMESERVER_PUBLIC_ADDR, host-qualified

 play:  client → gateway :8000    auth → auth_resp
        client → gateway :8000    enter_world{map_01}
                                    └─ FindServer → the entry above
        gateway → client          enter_world_resp{server_addr:"127.0.0.1:9200",
                                                    join_token:"…"}
        client → gameserver 127.0.0.1:9200   join_token → join_token_resp
        client ⇄ gameserver                  input / snapshot, 15Hz world rate
        gateway is out of the picture from here on

 down:  registration.DeregisterAsync()                   (GameServer.cs:443)
        final save, then agonesSdk.ShutdownAsync()       (GameServer.cs:450)
```

The ordering is deliberate in both directions and already correct: **register
after the listener is up** so the registry never advertises a socket that is not
accepting, and **deregister before the final save** so the gateway stops sending
players to a server that is leaving instead of black-holing them for up to 15s of
TTL. Neither of those needs anything from Agones.

---

## 2. Flow B — the Agones path as it stands

The repository contains nine manifests in `backend/deploy/agones/` and a
bootstrap script that installs Agones 1.59.0 (`k3s/setup-dev.sh:27`) and applies
fleets by hand. A cluster is up. Nothing is allocating, and if something did, the
address it produced would not reach the client.

### B.1 What is actually running

Live, on the machine this was written on:

```
$ kubectl config current-context
docker-desktop                       ← not k3s; no k3s binary is installed

$ kubectl get fleets -A
NAMESPACE      NAME                  DESIRED  CURRENT  ALLOCATED  READY  AGE
rpg-realtime   dungeon-servers-dev   1        1        0          1      13d
rpg-realtime   map-servers-dev       1        1        0          1      13d

$ kubectl get gameservers -A
NAMESPACE      NAME                             STATE  ADDRESS       PORT  AGE
rpg-realtime   dungeon-servers-dev-2kdvr-zzmxb  Ready  192.168.65.3  7101  6h
rpg-realtime   map-servers-dev-kl485-gsmrh      Ready  192.168.65.3  7691  6h
```

Three facts in that output.

**`ALLOCATED 0`, for thirteen days.** Nothing has ever asked Agones for a server,
because nothing can: no deployed gateway runs with `--allocator=agones`.

**Those are the Go fleets.** `map-servers-dev` and `dungeon-servers-dev` come
from `agones/fleet-map-dev.yaml` and `fleet-dungeon-dev.yaml`, both on image
`rpg-mmo/gameserver:dev` (`fleet-map-dev.yaml:73`, `fleet-dungeon-dev.yaml:46`) —
built from `backend/gameserver/`, which was deleted in `670a803` along with
`docker/Dockerfile.gameserver`. The image still sits in the Docker Desktop store
from 2026-08-04, which is the only reason those pods are up; it **cannot be
rebuilt**, because its Dockerfile no longer exists. `setup-dev.sh:129` still
prints a build command naming that deleted file. The C# fleet,
`fleet-map-dotnet-dev.yaml`, is applied by nothing — `setup-dev.sh:118-133`
selects between the dev and prod *Go* fleet files and never mentions it.

**The port is dynamic and it is not 9000.** `portPolicy: Dynamic`
(`fleet-map-dotnet-dev.yaml:35`, and the same in every other fleet) means Agones
picks a host port per pod — 7691 and 7101 above. The only place that number
exists is the GameServer's status: `status.address` and `status.ports[].port`.

### B.2 The dead end, drawn

```
  ┌─ CLUSTER (docker-desktop) ────────────────────────────────────────┐
  │                                                                    │
  │   Fleet map-servers-dotnet-dev          ← applied by nothing       │
  │     replicas 1, portPolicy Dynamic                                 │
  │     health.disabled: true   ← on purpose: the C# SDK is a no-op,   │
  │                                so an enabled probe crash-loops     │
  │                                the pod (ADR-14 decision 4)         │
  │                                                                    │
  │   ┌ pod ──────────────────────────────────────────────┐            │
  │   │ container listens :9000                           │            │
  │   │ Agones maps it to host 192.168.65.3:<random>      │            │
  │   │                                                   │            │
  │   │ env passed by the fleet (:82-112):                │            │
  │   │   POD_NAME  JWT_SECRET  JOIN_TOKEN_SECRET         │            │
  │   │   SIM_*_HZ  REDIS_ADDR  LOG_LEVEL                 │            │
  │   │   ✗ no GAMESERVER_PUBLIC_ADDR                     │            │
  │   │                                                   │            │
  │   │ REDIS_ADDR is set → self-registration is ON       │            │
  │   │   (Program.cs:87, :313 — gated on REDIS_ADDR      │            │
  │   │    alone; the `--redis` arg at :81 is commented   │            │
  │   │    out and would be redundant anyway)             │            │
  │   │                                                   │            │
  │   │ publicAddr falls back to the LISTEN addr          │            │
  │   │   (Program.cs:101) ───────────────┐               │            │
  │   └───────────────────────────────────┼───────────────┘            │
  │                                       │                            │
  │   status.address = 192.168.65.3       │ writes                     │
  │   status.ports[0].port = 7691         │ servers:<pod> = {          │
  │        │                              │   addr: ":9000"  ✗         │
  │        │ the REAL address …           │ }                          │
  │        ▼                              ▼                            │
  │   ┌──────────────┐            ┌───────────────┐                    │
  │   │ GameServer   │            │     Redis     │                    │
  │   │   status     │            │   registry    │                    │
  │   └──────┬───────┘            └───────┬───────┘                    │
  └──────────┼────────────────────────────┼───────────────────────────┘
             ╎                            │
             ╎ IAgonesSdk has NO           │ FindServer reads this,
             ╎ status-read call            │ finds capacity, returns
             ╎ (Agones/AgonesSdk.cs:5-19:  │ at registry.go:233-235 —
             ╎  Ready/Shutdown/Allocate/   │ the allocator branch at
             ╎  Health, nothing else)      │ :237 is NEVER REACHED
             ╎                             ▼
             ╎                     ┌───────────────┐
             ╎                     │    GATEWAY    │
             ╎                     └───────┬───────┘
             ╎                             │ enter_world_resp
             ╎                             │   server_addr = ":9000"
             ╎                             ▼
             ╎                     ┌───────────────┐
             └╌╌╌╌ the arrow that  │    CLIENT     │  TcpClient throws.
                   does not exist  └───────────────┘  Player never joins.
```

Read that in the order it fails, because the order is the surprising part.

**The break is not in the allocator.** `AgonesAllocator` is written correctly: it
POSTs a `GameServerAllocation` to the aggregated API
(`registry/agones_allocator.go:193-219`), reads `status.address` and the port
named `game` (`:242-254`, `gamePort()` at `:306-317`), and builds a proper
`host:port`. If it ran, it would produce `192.168.65.3:7691`.

**It does not run, because self-registration got there first.** The pod registers
itself at boot with the hostless `:9000`. By the time a client sends
`enter_world`, `FindServer` sees a registered server for `map_01` with capacity,
returns it at `registry.go:233-235`, and the allocator branch at `:237` is never
evaluated. The gateway hands out `:9000`.

So there are two defects stacked, and fixing either one alone leaves the flow
broken:

| # | Defect | Where | Consequence alone |
|---|---|---|---|
| 1 | Fleet passes no `GAMESERVER_PUBLIC_ADDR`; the C# fallback is the listen address | `fleet-map-dotnet-dev.yaml:82-112`, `Program.cs:101` | registry holds `:9000` — hostless, and the wrong port besides, since the host port is dynamic |
| 2 | Self-registration pre-empts allocation | `Program.cs:313`, `registry.go:233-235` | even a perfect allocator is never consulted while any pod is registered with capacity |

And the reason defect 1 cannot be fixed by simply setting the env var: the value
is not knowable at manifest-authoring time. `portPolicy: Dynamic` assigns it per
pod, at schedule time, and the only source is the GameServer status —
which `IAgonesSdk` cannot read. Its four methods are `ReadyAsync`,
`ShutdownAsync`, `AllocateAsync`, `HealthAsync` (`Agones/AgonesSdk.cs:5-19`), and
the only implementation is `NoopAgonesSdk`, which returns `Task.CompletedTask`
from all four (`:21-28`). There is no `GameServer()` / `WatchGameServer()` call
to ask "what port did I get?"

That is also why `health.disabled: true` sits in the C# fleet
(`fleet-map-dotnet-dev.yaml:36-64`) with a large comment telling you not to
remove it: with a no-op SDK the pod sends no health pings, trips
`failureThreshold`, and is killed and recreated forever. Agones is right; there
genuinely is no liveness signal.

### B.3 The manifests, and which of them describe anything real

| File | Targets | State |
|---|---|---|
| `fleet-map-dev.yaml` | `rpg-mmo/gameserver:dev` (Go) | ⚠️ superseded — **but running in the cluster**, 13d |
| `fleet-dungeon-dev.yaml` | same | ⚠️ superseded — running, 13d |
| `fleet-map.yaml` | `ghcr.io/dycuong03/rpg-mmo-gameserver:latest` | ⚠️ superseded; image name matches neither `cd.yml:261` nor `README.md` |
| `fleet-dungeon.yaml` | GHCR Go image | ⚠️ superseded |
| `fleet-map-dotnet-dev.yaml` | `rpg-mmo/gameserver-dotnet:dev` | the current server — applied by nothing |
| `autoscaler-dev.yaml` / `autoscaler.yaml` | `fleetName: map-servers-dev` / `map-servers` | point at superseded fleets (`autoscaler-dev.yaml:4-6`) |
| `allocation-dev.yaml` / `allocation.yaml` | `dungeon-servers-dev` | manual `kubectl create` stand-in for the gateway allocator (`allocation-dev.yaml:9-14`) |
| `namespaces.yaml` (in `k3s/`) | `rpg-realtime`, `rpg-meta`, `rpg-data` | applied by `setup-dev.sh:83-84` |

The superseded files are deliberately kept rather than deleted: the cluster is
still running fleets from them, and deleting a manifest under a live fleet leaves
the fleet with no source describing it. Retiring both together is ADR-14 stage 8
(`../CHANGELOG.md`, Unreleased § Changed).

---

## 3. Flow C — what it would look like working

**Nothing in this section runs today.** Solid arrows exist; `╌╌╌` arrows are
missing and each one is named below.

```
   FleetAutoscaler ──── keeps N Ready ────▶ Fleet (C# image)
        │                                        │
        │  Buffer policy, bufferSize N           │ pods: Ready, health pings
        │  (autoscaler-dev.yaml:14-19,           │ ╌(a)╌ needs HttpAgonesSdk
        │   today points at the Go fleet)        │        for Ready + Health
        │                                        ▼
        │                              ┌───────────────────┐
        │                              │  GameServer pod   │
        │                              │  listens :9000    │
        │                              │  host <ip>:<dyn>  │
        │                              └─────────┬─────────┘
        │                                        │
        │                            ╌(b)╌ read own status.address /
        │                                  status.ports[].port and use it
        │                                  as GAMESERVER_PUBLIC_ADDR
        │                                        │
   CLIENT                                        │
     │                                           │
     │─ enter_world{map_01} ─▶ GATEWAY           │
     │                           │               │
     │                           │ FindServer: no live server with capacity
     │                           │  ╌(c)╌ AND self-registration must not have
     │                           │        pre-claimed the map
     │                           │
     │                           ├─ POST GameServerAllocation ──▶ Agones API
     │                           │   (agones_allocator.go:193-225)   │
     │                           │   ╌(d)╌ needs --allocator=agones  │
     │                           │   ╌(e)╌ needs RBAC on             │
     │                           │         gameserverallocations     │
     │                           │                                   ▼
     │                           │◀── status: Allocated,      pod flips
     │                           │    address + game port      Ready→Allocated
     │                           │
     │                           ├─ reg.Register(allocated)   (registry.go:243)
     │                           │
     │◀─ enter_world_resp{server_addr:"<node-ip>:<dyn-port>", join_token} ─┘
     │
     │── join_token ──▶ pod, then input/snapshot directly
```

What each missing arrow needs:

| | Missing | What it needs | Tracked as |
|---|---|---|---|
| (a) | Ready + Health ever reach the sidecar | `HttpAgonesSdk` against `localhost:9358`, replacing `NoopAgonesSdk`. The **ordering** around it already exists and is correct — `ReadyAsync()` at `GameServer.cs:356`, registration after it at `:364` | ADR-14 stage 1 |
| (b) | The pod learns its own host address | A status read the SDK interface does not have (`Agones/AgonesSdk.cs:5-19`). Either extend `IAgonesSdk` with `GameServer()`/`WatchGameServer()`, or accept the address from the allocation path instead of self-publishing | — |
| (c) | Allocation is reachable at all | Suppress self-registration when the server is Agones-managed, or the pre-registered hostless entry keeps short-circuiting `FindServer` at `registry.go:233-235` | — |
| (d) | The gateway asks | `--allocator=agones` (or `ALLOCATOR=agones`) plus `--allocator-fleet-map` pointing at the C# fleet — defaults are `map-servers-dev`, the Go one (`agones_allocator.go:31-33`). Nothing in `cd.yml` sets any of it | — |
| (e) | The API call is permitted | A ServiceAccount with `create` on `allocation.agones.dev/gameserverallocations` in `rpg-realtime`, bound to the gateway pod. `setup-dev.sh:95-100` creates only the `agones-sdk` binding, which is for the *game server* sidecar, not the gateway | — |
| — | Health re-enabled | Delete `health.disabled: true` — but only after (a) lands **and** a deployed pod is shown to stay Ready across a sustained run | ADR-14 stage 4 |

Note the shape of (b) and (c) together: they are alternatives more than they are
a list. Either the pod discovers its real address and self-registration keeps
working as it does in compose, or the pod stops self-registering under Agones and
the allocator — which already computes the right address — becomes the only
writer of the registry entry. Doing both halves of one and neither of the other
leaves the flow broken in exactly the way §2 describes.

---

## 4. What compose supplies that Kubernetes would have to replace

Compose is not only "the containers". Everything below is currently provided by a
file on the runner and would need a Kubernetes object, and none of these objects
exist in this repository — there is no `k8s/` directory.

| What | Today | Kubernetes equivalent needed |
|---|---|---|
| Data volumes | four named volumes: `postgres-data`, `postgres-game-data`, `redis-data`, `lgtm-data` (`docker-compose.yml:341-345`) | PVCs + StorageClass, and PostgreSQL as a StatefulSet |
| Nakama plugin | host bind mount `./modules:/nakama/data/modules` (`:118`), populated by `cd.yml`'s sync step | bake `nakama.so` into an image, or an init container — a ConfigMap cannot carry a shared object |
| Game DB bootstrap | `./db/init-gamestate.sql` mounted into the entrypoint dir (`:71`) | ConfigMap + init container, or drop it and rely on the numbered migrations |
| Monitoring config | three mounts: `prometheus.yaml`, `grafana-dashboards.yaml`, `dashboards/` (`:327-329`) | three ConfigMaps — and note compose needs an explicit `restart lgtm` when they change (`cd.yml:875-881`); a k8s rollout would need the same trigger |
| Secrets | seven values written into `deploy/.env` mode 0600: `JWT_SECRET`, `JOIN_TOKEN_SECRET`, `POSTGRES_PASSWORD`, `NAKAMA_CONSOLE_PASSWORD`, `REDIS_PASSWORD`, `NAKAMA_SERVER_KEY`, `GRAFANA_ADMIN_PASSWORD` (`cd.yml:532-560`) | Secrets. `setup-dev.sh:102-107` already creates `rpg-realtime-secrets`, but with **two** of the seven and hardcoded dev literals |
| Non-secret config | ~40 vars in the same `.env` | ConfigMaps, per environment |
| Images | local tags `rpg-mmo/{gateway,gameserver-dotnet}:<sha>`, built on the runner and never pushed (`cd.yml:751-762`) | a registry every node can pull from, plus `imagePullSecrets`. GHCR push exists but only fires for `production` (`cd.yml:218`) |
| Service discovery | compose DNS: `redis:6379`, `postgres-game:5432`, `nakama:7350` | Services — mostly a rename, and the one easy row here |
| Client-facing ports | fixed published ports per environment | dynamic per pod under `portPolicy: Dynamic`; see §2.2 for why that is the hard part, not a detail |
| Gateway → Agones | nothing (no allocator runs) | ServiceAccount + Role/RoleBinding granting `create` on `gameserverallocations` in `rpg-realtime`, plus in-cluster config, which `NewAgonesAllocator` already prefers (`agones_allocator.go:163-166`) |
| Restart policy | `restart: unless-stopped` | Deployment for the gateway; the game server is Agones' job, not a Deployment's |

---

## 5. How to tell which flow you are looking at

Four commands. Run them before believing anything else, including this document.

```bash
# 1. Which cluster, if any? "docker-desktop" is a laptop, not k3s.
kubectl config current-context

# 2. Is Agones doing anything? ALLOCATED 0 means nothing has ever asked.
kubectl get fleets -A
kubectl get gameservers -A          # ADDRESS + PORT here are the REAL ones

# 3. Which compose stack is on this box? The prefix is the environment.
docker ps --filter name=rpg- --format 'table {{.Names}}\t{{.Ports}}'
#   rpg-*        → the default-prefix environment (dev, on the current runner)
#   <prefix>-*   → another environment sharing the runner; the prefix comes
#                  from vars.COMPOSE_NAME_PREFIX, see CICD.md § "Two
#                  environments on one runner"

# 4. Is the gateway even able to allocate? Absent flag = allocator "none".
docker inspect rpg-gateway --format '{{join .Config.Cmd " "}}'
docker exec rpg-gateway env 2>/dev/null | grep '^ALLOCATOR='   # distroless: expect nothing
```

Reading the results:

| Observation | You are in |
|---|---|
| `rpg-*` containers up, `--allocator` absent | **Flow A.** The whole player path is compose. Anything in the cluster is scenery. |
| Fleets Ready, `ALLOCATED 0` | **Flow B.** Pods exist; no client has ever been sent to one. |
| Fleets Ready and `--allocator=agones` on the gateway | Flow B with the trap armed — see §2.2 before assuming it works |
| `ALLOCATED > 0` and a player actually moving in a pod | **Flow C**, which as of this commit has never happened |

On WSL, `kubectl` and `docker` may be Windows binaries: use `docker.exe ps` if
the Linux `docker` shim behaves oddly, and note that it cannot resolve absolute
`/mnt/*` paths — the same constraint `cd.yml:751-762` works around by keeping
build paths cwd-relative.

## See also

- `K3S.md` — dev cluster bootstrap, "Which fleet is real", the order in which
  Agones becomes real
- `CICD.md` — `cd.yml` job graph, deploy modes, the two-environments-on-one-runner
  variable set
- `RUNBOOK-local-dev.md` — starting and debugging the compose stack by hand
- `backend/gameserver-dotnet/docs/API.md` — normative wire protocol
- `backend/docs/ARCHITECTURE-DECISIONS.md` — ADR-2 (one server per map), ADR-3
  (redirector, not proxy), ADR-4 (Redis is a system of record), ADR-14 (Agones
  owns the pod, Redis owns the lookup)
