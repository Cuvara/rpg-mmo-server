# k3s / Agones — dev cluster bootstrap

Everything needed to get the map/dungeon fleets running on a Kubernetes cluster,
plus the path from a laptop cluster to a real k3s VPS.

- Scripts: `backend/deploy/k3s/`
- Manifests: `backend/deploy/agones/`
- Agones version: **1.59.0**, pinned to match `agones.dev/agones v1.59.0` in
  the Agones SDK. Override with `AGONES_VERSION=…`.

> **This file covers the cluster/Agones bootstrap and the earlier hybrid stack**
> (fleet in `rpg-realtime`, everything else in compose, reached as
> `host.k3d.internal`). Dev has since moved **entirely** into the cluster —
> namespaces `rpg-k8s-data` and `rpg-k8s-realtime`, manifests in
> `backend/deploy/k8s/`, brought up by `k8s/dev-up.sh`; the `rpg-realtime` fleet is
> scaled to 0 and kept only as the rollback target. For that stack read
> [`../k8s/README.md`](../k8s/README.md), and in particular its **Availability
> posture** section: every workload there is a single replica, and the two
> `hostPort` workloads deploy with `strategy: Recreate`, so **every gateway or
> Nakama rollout drops the join path** (ADR-17). Do not carry an availability
> assumption from this file into that one.

---

## Dev runs on Agones (switched 2026-08-18)

The dev realtime tier now serves `map_01` from the Agones fleet, not from the
compose `gameserver-dotnet` service. The gateway runs with `ALLOCATOR=agones`,
attached to both the compose network and `k3d-rpg-dev`. Verified end to end:
the smoke test passes 10/10 in `--strict-addr` mode, including
`gamestate_player_row` and `gamestate_reload`.

To reproduce, or to undo:

```bash
cd backend/deploy
# switch ON
kubectl config view --minify --flatten --context k3d-rpg-dev > kubeconfig.local
sed -i 's|server: https://127.0.0.1:6550|server: https://k3d-rpg-dev-serverlb:6443|' kubeconfig.local
chmod 600 kubeconfig.local                      # gitignored; never commit it
# set ALLOCATOR=agones (+ ALLOCATOR_* , KUBECONFIG_HOST, K3D_NETWORK) in .env
kubectl --context k3d-rpg-dev scale fleet map-servers-dotnet-dev -n rpg-realtime --replicas=1
docker stop rpg-gameserver                      # hand map_01 to the fleet; wait out its 15s TTL
docker compose --profile realtime -f docker-compose.yml -f docker-compose.override.yml \
               -f docker-compose.agones.yml up -d gateway

# switch OFF
# set ALLOCATOR=none in .env, then:
kubectl --context k3d-rpg-dev scale fleet map-servers-dotnet-dev -n rpg-realtime --replicas=0
docker start rpg-gameserver
docker compose --profile realtime up -d gateway
```

### Three things that bit, in the order they bit

**1. `.env` drifts from what is deployed.** CD writes its own secrets into the
running containers; the `.env` in the working tree is not updated to match. The
gateway had been running with CD's `JWT_SECRET` and picked up the stale file
value on restart, so every login failed with `invalid token` until `.env` was
synced. Read the truth from the container, never from the file:
`docker inspect rpg-gateway --format '{{range .Config.Env}}{{println .}}{{end}}'`
— **`docker exec rpg-gateway printenv` does not work**, the image is distroless
and you will silently capture the exec error text as the value.

**2. The kubeconfig bind must be cwd-relative.** `docker` here is the Docker
Desktop shim and absolute `/mnt/*` paths do not translate; the mount silently
becomes a *directory* and `kubectl` reports `read /kc: is a directory`, which
reads like a kubeconfig problem and is not. Run compose from `backend/deploy`
and keep `KUBECONFIG_HOST=./kubeconfig.local`.

**3. A fleet at `replicas: 1` has no spare pod, so allocation can never fire.**
Once the single pod is Allocated to `map_01`, a request for an unserved map
returns `no game server available in fleet (state "UnAllocated")` — correct, but
it means the allocation path is unreachable until the fleet carries spare Ready
capacity. Raise `replicas` before expecting an allocation to succeed.

## TL;DR

```bash
cd backend/deploy
./k3s/setup-dev.sh                     # Agones, namespaces, Secret/ConfigMap, the fleet, wait for Ready
kubectl get gameservers -n rpg-realtime
./k3s/teardown-dev.sh                  # reverse (--all also removes Agones)
```

No cluster? `setup-dev.sh` fails in ~0.1s telling you the one thing to do
(see [Cluster options](#cluster-options)).

Validate manifests without any cluster at all:

```bash
python3 k3s/validate-manifests.py      # checks against the real Agones CRD schemas
```

---

## Cluster options

Probed on the dev machine (WSL2 Ubuntu + Docker Desktop), in the order they were
considered:

| Option | Verdict here | Why |
|--------|--------------|-----|
| **Docker Desktop Kubernetes** | ✅ chosen | Already installed (`kubectl.exe` ships with it), shares the Docker image store so `rpg-mmo/gameserver:dev` needs no import step, and `host.docker.internal` resolves from pods. Currently **disabled** — one toggle away. |
| k3d (k3s in Docker) | ❌ unusable | Needs a working Docker endpoint from Linux. WSL integration is off: `/var/run/docker.sock` exists but resets the connection, and the daemon is not exposed on tcp://localhost:2375. The `docker` in `PATH` is a one-line shim that execs `docker.exe`, which k3d cannot use as a socket. |
| Native k3s in WSL | ❌ unusable | `curl -sfL https://get.k3s.io \| sh` needs root; `sudo -n true` fails on this box (no passwordless sudo). |
| Real k3s on a VPS | 🎯 target | See [Graduating to a real k3s cluster](#graduating-to-a-real-k3s-cluster). |

### The single human step

Docker Desktop's Kubernetes is off. Turning it on is a GUI toggle — there is no
CLI for it (`docker desktop enable` only manages the model runner, and
`docker desktop kubernetes` only lists images):

> **Docker Desktop → Settings → Kubernetes → "Enable Kubernetes" → Apply & restart**

It is deliberately left to a human because *Apply & restart* bounces the Docker
engine, which stops every running container (the local `rpg-*` compose stack
included). Do it when nothing else is mid-flight, then:

```bash
kubectl config get-contexts        # expect: docker-desktop
cd backend/deploy && ./k3s/setup-dev.sh
```

---

## What `setup-dev.sh` does

Idempotent — every step is an `apply`, so re-running is a no-op plus waits.

1. **Resolve kubectl** — Linux `kubectl`, else `kubectl.exe`, else Docker
   Desktop's bundled path. Override with `KUBECTL_BIN=`.
2. **Preflight** — bail immediately with instructions if no context/cluster, and
   classify the cluster from the context name (`docker-desktop` / `k3d-*` /
   unknown). Two later steps depend on that answer and both fail *silently* when
   it is guessed: whether a local image tag resolves without an import, and what
   hostname a pod uses to reach the host's compose stack.
2b. **Image check + import** — verify `rpg-mmo/gameserver-dotnet:dev` carries the
   git revision of the current HEAD (skip with `--skip-image-check`), then import
   it into the cluster when the cluster does not share the host's image store.
   On k3d, skipping the import does not error: `IfNotPresent` falls through to a
   registry pull and the GameServer sits in `ImagePullBackOff`.
3. **Install Agones** `$AGONES_VERSION` via `kubectl apply --server-side` on the
   release `install.yaml`. Server-side is required: the CRDs exceed the 262 kB
   `last-applied-configuration` annotation that client-side apply writes.
4. **Wait** for `agones-system` deployments to be Available, then poll until the
   `agones.dev/v1` API actually serves Fleets — the webhook lags the deployment
   and applying a Fleet too early fails with *no endpoints available for service
   agones-controller-service*.
5. **Namespaces** — `rpg-realtime`, `rpg-meta`, `rpg-data` (`k3s/namespaces.yaml`).
6. **Dev config** — Secret `rpg-realtime-secrets` (`jwt-secret`,
   `join-token-secret`, `redis-password`, `transport-key`, `game-db-url`)
   **sourced from `../.env`**, and ConfigMap `gameserver-config`
   (`redis-addr`, `advertise-host`), created via
   `create --dry-run=client -o yaml | apply` so they are idempotent. Sourcing
   `.env` is what keeps the pod's secrets equal to the compose-run gateway's;
   re-run the script after any `.env` change.
7. **Fleet** — `fleet-map-dotnet-dev.yaml`. There is only one, and there is no
   autoscaler (see "Why there is no autoscaler").
8. **Wait for Ready** — polls `GameServer.status.state` until one is `Ready`:
   the pod started, the binary reached the SDK sidecar, and `ReadyAsync()`
   returned. It does **not** prove a client can reach the server — Ready is
   reported from inside the pod, the client dials the node from outside.
9. **Print** `agones-system` pods, fleets, gameservers, and the follow-up
   commands (address\:port, allocation, logs, teardown).

Flags: `--skip-agones`, `--skip-image-check`.
Env: `AGONES_VERSION`, `KUBECTL_BIN`, `GAMESERVER_IMAGE`, `POD_REDIS_ADDR`,
`POD_GAME_DB_URL`, and
everything it reads out of `../.env`.

`teardown-dev.sh` reverses it: autoscalers first (a live one recreates replicas
under a dying fleet), then fleets, stray GameServers, config objects, namespaces,
and with `--all` Agones itself. `--fleets-only` keeps the namespaces.

---

## Manifests

| File | Purpose |
|------|---------|
| `agones/fleet-map.yaml` | ⚠️ superseded (Go image) — prod map fleet, config from Secret/ConfigMap |
| `agones/fleet-dungeon.yaml` | ⚠️ superseded (Go image) — prod dungeon fleet, `replicas: 0` (allocate on demand) |
| `agones/fleet-map-dev.yaml` | ⚠️ superseded (Go image `rpg-mmo/gameserver:dev`), but this is what the cluster is running |
| `agones/fleet-dungeon-dev.yaml` | ⚠️ superseded, dungeon mode, `replicas: 0` |
| `agones/fleet-map-dotnet-dev.yaml` | **The current one.** C# server, `rpg-mmo/gameserver-dotnet:dev`, health **enabled**, `GAME_DB_URL` from the Secret — see below |
| `agones/autoscaler.yaml` / `autoscaler-dev.yaml` | Buffer autoscaler (prod 2/1–10, dev 1/1–2) — both still target the superseded map fleets |
| `agones/fleet-map.yaml` | Prod map fleet — `ghcr.io/cuvara/rpg-mmo-gameserver:latest`, config from Secret/ConfigMap |
| `agones/fleet-dungeon.yaml` | Prod dungeon fleet, `replicas: 0` (allocate on demand) |
| `agones/fleet-map-dev.yaml` | Local image `rpg-mmo/gameserver:dev`, `IfNotPresent`, literal env, `replicas: 1` |
| `agones/fleet-dungeon-dev.yaml` | Same, dungeon mode, `replicas: 0` |
| `agones/autoscaler.yaml` / `autoscaler-dev.yaml` | Buffer autoscaler (prod 2/1–10, dev 1/1–2) |
| `agones/allocation.yaml` / `allocation-dev.yaml` | `GameServerAllocation` — `kubectl create`, never `apply` |
| `agones/fleet-map-dotnet-dev.yaml` | **The only fleet.** C# server, `rpg-mmo/gameserver-dotnet:dev`, `replicas: 1`, health **enabled**, postgres player store — see below |
| `agones/secret-example.yaml` | Template for the `rpg-realtime-secrets` Secret. Dev placeholders only; not the real object |
| `agones/allocation-dev.yaml` | `GameServerAllocation` — `kubectl create`, never `apply` |
| `k3s/namespaces.yaml` | `rpg-realtime` / `rpg-meta` / `rpg-data` |

### Which fleet is real

One fleet, one server implementation. Full rationale:
[ADR-14](../../docs/ARCHITECTURE-DECISIONS.md) — this section is the operational
summary, not a restatement.

**What a fleet is for.** A `Fleet` is Agones' unit of game-server supply: it
keeps N pods of one spec Ready and hands them out when the gateway (or
`kubectl create -f allocation-dev.yaml`) allocates. It is not on the deploy path
— dev, staging and production all run `DEPLOY_MODE=containers` under docker
compose, and CI never applies anything in `agones/`.

**The five Go-image manifests are DELETED, not marked.** `fleet-map.yaml`,
`fleet-map-dev.yaml`, `fleet-dungeon.yaml`, `fleet-dungeon-dev.yaml` and
`allocation.yaml` ran `rpg-mmo/gameserver:dev` / `ghcr.io/…/rpg-mmo-gameserver:latest`,
built from `backend/gameserver/`, deleted in `670a803` along with
`docker/Dockerfile.gameserver`. That image cannot be rebuilt, so those files
described software that does not exist.

They were previously kept with a `⚠️ SUPERSEDED` banner because the cluster was
still *running* `map-servers-dev` and `dungeon-servers-dev` from them, and a live
fleet with no manifest is worse than a stale manifest. That reason is gone: both
fleets have been retired (`kubectl get fleets -n rpg-realtime` returns nothing),
which is the other half of ADR-14 stage 8. A banner also never stopped anyone —
`kubectl apply -f agones/` does not read comments. Deleting them does.

The prod fleets went with them and no `fleet-map-dotnet.yaml` replaces them yet,
deliberately: a production manifest for a fleet that has never run, pointing at
an image tag that has never been published, is the same failure one generation
later. Write it when there is a production cluster to write it against.

**The dotnet fleet's health is now ON, and what that costs.** Both reasons for
the old `health.disabled: true` are spent. The first — "the C# Agones SDK is a
no-op" — died with commit `62131f5`, which landed `HttpAgonesSdk`: it really does
POST `/ready`, `/health`, `/allocate` and `/shutdown` to the sidecar, with the
health loop at 2s against the manifest's 5s `periodSeconds`. The second — "none
of that has ever run against a real sidecar" — died with ADR-14 stage 4, run
deliberately *with* the flag so a pod that failed to reach Ready read as a pod
that failed to reach Ready and not as a restart loop hiding which of image /
secret / sidecar / SDK was at fault. It passed, and removing the flag was then
its own step with its own check (step 3 below), which is what the measurement in
[Health, measured](#health-measured) records. The three timing values were kept
in the file while inert — the Agones v1 `health` block accepts all four fields
independently — so that step did not have to re-derive the policy.

What turning it on **couples**: the health ping shares a process with the 60Hz
tick loop, so a tick loop stalled past `periodSeconds * failureThreshold` (15s)
starves the ping and Agones kills the pod. Availability is now tied to
simulation latency, which it was not before. ADR-13's overload path — drop the
backlog and resynchronise rather than chase it — is what should keep a merely
slow server from being killed as a dead one, and the container's CPU limit was
raised 500m → 1000m for the same reason: CFS throttling does not read as "slow",
it reads as missed ticks, and a throttled server is now a killed server.

**The order in which Agones becomes real.** Each step is a precondition of the
next; skipping ahead produces a restart loop or an allocation that returns a pod
nothing can join.

1. ✅ `HttpAgonesSdk` lands against the sidecar on `localhost:9358` (ADR-14
   stage 1, no deployment) — **done**, commit `62131f5`. The *ordering* the ADR
   asks for was already implemented in `GameServer/Server/GameServer.cs` — bind,
   then `ReadyAsync()`, then `_registration.StartAsync()`; on the way down
   `DeregisterAsync()` before `ShutdownAsync()` — so what stages 1–3 added was
   the SDK behind it, not the sequence.
2. Deploy `fleet-map-dotnet-dev.yaml` **with `health.disabled: true` still set**
   and confirm the pod reaches `Ready` and stays there. Deploying and un-disabling
   in one change conflates two failures — a server that cannot come up, and a
   ping loop that cannot keep up.
3. ✅ Only then remove `health.disabled: true`, and confirm the pod survives the
   ping period: `kubectl -n rpg-realtime get gs -w`, `RESTARTS` stays 0 and the
   GameServer stays `Ready`/`Allocated` over a sustained run. This is its own
   verification step, not a side effect of the SDK merging — **done**, see
   [Health, measured](#health-measured). If it flaps, put the flag back
   rather than widening `failureThreshold` — a starved ping task is a real
   liveness failure, and ADR-13's overload path is what should keep a merely-slow
   server from looking dead.
4. Set `ALLOCATOR=agones` **and `ALLOCATOR_FLEET_MAP`** on the gateway (see
   "Enabling the allocator" below — the compiled-in default names a fleet that
   no longer exists).
5. Verify allocation end to end: `MsgEnterWorld` for an unserved map →
   `kubectl -n rpg-realtime get fleet` shows `ALLOCATED` move off 0 → a client
   joins the returned address. This is the first step that proves anything;
   1–4 only reduce risk. It additionally requires the sidecar status read
   (ADR-15 decision 2 option A) and a cluster whose game-server ports the client
   can actually dial — see "The address the client is given".

<a id="health-measured"></a>
### Health, measured

**`Allocated` is a one-way door.** Before any of the below: a `Fleet` will not
scale down an `Allocated` GameServer, and there is no `Deallocate` anywhere in
this project — the state is left only by shutting the pod down. The C# server
reports `Allocate` to the sidecar the first time a player joins
(`NotifyAgonesAllocatedOnce`, `GameServer/Server/GameServer.cs`), so a single
smoke run pins a pod in `Allocated` for good. That is genuinely useful evidence
that the Allocate report works against a real sidecar, and it is also an
operational trap in two directions: `kubectl scale fleet … --replicas=0` leaves
the pod running, and — the one that actually cost time here — **an `Allocated`
pod satisfies the replica count, so `kubectl apply` of a changed template creates
the new GameServerSet but never rolls a pod onto it.** Delete the stale
GameServer explicitly to let a redeploy take effect:

```bash
kubectl --context k3d-rpg-dev -n rpg-realtime delete gs <stale-allocated-gs>
```


Cluster `k3d-rpg-dev` (k3d 5.8.3, k3s 1.31.5, Agones 1.59.0), namespace
`rpg-realtime`, health block as committed (`initialDelaySeconds: 10`,
`periodSeconds: 5`, `failureThreshold: 3`), on two images that are the same code:
`rpg-mmo/gameserver-dotnet:integration` (revision `4d928e7`, whose
`gameserver-dotnet/` and `shared/` trees are byte-identical to `develop`) and
then `rpg-mmo/gameserver-dotnet:develop` (revision
`307f1e81b6621031ac145174cfd84d99cb6bd9ba`, i.e. `develop` itself). Always check
the label rather than the tag:

```bash
docker image inspect rpg-mmo/gameserver-dotnet:develop \
  --format '{{index .Config.Labels "org.opencontainers.image.revision"}}'
```

`rpg-mmo/gameserver-dotnet:dev` — the tag written in the manifest — is **stale on
this box** and must be rebuilt and re-imported before any apply that is meant to
prove something; the manifest header carries the copy-paste form.

**The pod does not flap.** Four independent windows, sampled every 30s:

| Pod | Window | `RESTARTS` | Ended by |
|-----|--------|-----------:|----------|
| `…-q7bdn-qjgl8` | 11m18s | 0 | external `Fleet` scale 1→3→1 by another operator |
| `…-q7bdn-5bqwx` | 12m22s | 0 | same |
| `…-q7bdn-9vxgc` | 12m26s | 0 | nothing — still `Ready` when observation stopped |
| `…-7jx8z-tbtq8` (`:develop`) | 13m25s | 0 | nothing — still `Allocated` when observation stopped |

The fourth ran on `:develop` (revision `307f1e8`) after the merge: the pod came
up `Ready`, logged `using postgres player store`, passed the same
`--strict-addr --require-db` smoke run on `127.0.0.1:7069`, went `Allocated` on
that client's join and then held. Across all four windows `kubectl get events`
shows exactly **one** `Unhealthy` on a `gameserver/map-servers-dotnet-dev-*` —
the deliberate `SIGSTOP` below.

```
NAME                                 READY   STATUS    RESTARTS   AGE
map-servers-dotnet-dev-7jx8z-tbtq8   2/2     Running   0          15m
```

`kubectl -n rpg-realtime get events` recorded **no `Unhealthy` event on any
`gameserver/map-servers-dotnet-dev-*`** across either window. Both pods were
destroyed by someone else rescaling the fleet — a shared-cluster collision, not a
health kill. Note the distinction when reading events during a fleet rollout:
the `Warning Unhealthy … Liveness probe failed: …:8080/healthz: connection
refused` lines that appear on a *pod* being torn down are the sidecar's own
liveness probe losing its target during shutdown, and they say nothing about the
SDK health ping.

**The ping is live, and the failure mode it introduces is real.** A clean
`RESTARTS: 0` cannot distinguish a working health loop from one Agones is not
actually enforcing, so the loop was failed on purpose. `SIGSTOP` on the
`./GameServer` process inside the pod (via the k3d node container, since the
image is distroless and has no shell) simulates the tick loop stalling long
enough to starve the ping:

```bash
PID=$(docker exec k3d-rpg-dev-server-0 sh -c 'ps aux | grep "[.]/GameServer" | awk "{print \$1}"')
docker exec k3d-rpg-dev-server-0 sh -c "kill -STOP $PID"
kubectl --context k3d-rpg-dev -n rpg-realtime get gs -w
```

Result: `Ready` → **`Unhealthy`** → `Shutdown` within ~20s of the freeze — inside
the 25s that `initialDelaySeconds + periodSeconds * failureThreshold` predicts —
with `Warning Unhealthy gameserver/… Health check failure` and
`Deleted gameserver in state Unhealthy` in the events, and a replacement
`Ready` about 25s later. So the coupling documented above is not theoretical:
**a stalled process is now a deleted pod.**

**What is NOT proven: behaviour under real overload.** The intended stress —
hold the server near its tick budget with `backend/loadtest` and confirm ADR-13's
overload path keeps a merely-slow server alive — **could not be run**. Every
`loadtest` invocation returns `INVALID: N/N players failed` with
`failures_by_phase: {"join": N}` and `sample_errors: ["join: join: recv: EOF"]`,
*even though* the game server logs `Player … joined (total: N)` and snapshots
flow (`snap p50 ≈ 67ms`, ~1 KB/s/client). This reproduces identically against the
**compose** game server on `127.0.0.1:9200` and through the **gateway**, so it is
a pre-existing `loadtest`↔server mismatch on `develop`, not an Agones, k3d or
health problem — and it is out of scope for `backend/deploy/`. The strongest
statement the cluster supports today is the partial one: 90 real clients did join
one pod and hold connections for ~30s each, and the pod stayed `Ready` with
`RESTARTS: 0` throughout. Sustained-overload survival remains untested.

### The fleet's configuration, checked against the binary

Checked against `GameServer/Program.cs` argument/env parsing, not against
memory:

- **Configuration is by environment, not by args.** Both work (`GetArg` accepts
  `--flag value` and `--flag=value`), but one channel is easier to read than two,
  and the env block is the one that matches `docker-compose.yml`. `--agones`
  became `AGONES_ENABLED=true` for the same reason.
- **Port `9000`** matches `GAMESERVER_ADDR`, and `EXPOSE 9000` in the Dockerfile.
  `portPolicy: Dynamic`: Agones assigns the *host* port and publishes it on
  `GameServer.status.ports[]`; the container always binds `:9000`.
- **`ports[].name: game` is a contract.** The gateway's allocator picks the
  client-facing port by that exact name (`gamePortName` in
  `gateway/registry/agones_allocator.go`). Rename it and every allocation fails
  with `no "game" port in allocation status`.
- **Secrets come from the `rpg-realtime-secrets` Secret**, not from literals —
  `jwt-secret` and `join-token-secret`, neither `optional`. See "Secrets" below.
- **`REDIS_ADDR` comes from the `gameserver-config` ConfigMap**, and is *not*
  `optional: true` either: an unset `REDIS_ADDR` is not an error in the server,
  it just runs without registering, so the pod would be Ready, the logs clean,
  and the gateway unable to find it.
- **`LOG_LEVEL` was removed.** `grep -rn LOG_LEVEL backend/gameserver-dotnet/GameServer/`
  returns nothing — the server pins the console logger to Information and reads
  no level from the environment. The variable configured nothing while reading
  as if it did.
- **`replicas: 1` is load-bearing.** Every pod carries the same
  `GAMESERVER_MAP_ID` and self-registers under it, so a second replica means two
  servers claiming `map_01` and two disconnected copies of the world — ADR-2's
  invariant broken by the replica count alone, with no allocation involved.

### Why there is no autoscaler

`autoscaler.yaml` and `autoscaler-dev.yaml` were deleted along with the fleets
they targeted. They are not coming back for the *map* fleet, and the reason is
not the policy type — a buffer policy on server count is exactly what ADR-14
decision 5 prescribes, because ADR-7's per-server player ceiling is unknown and
nothing may be keyed on players-per-server.

The reason is that **a buffer autoscaler is incoherent for this fleet**, on two
counts:

1. *Nothing consumes the buffer.* A buffer policy keeps N `Ready` (unallocated)
   servers spare. Map servers are never allocated in normal operation — a pod
   self-registers into Redis on startup and the gateway finds it through the
   registry, not through an allocation. `ALLOCATED` stays 0 whether one player is
   online or a thousand, so the buffer is never drawn down and the autoscaler
   holds `minReplicas` forever. With the gateway's current policy (allocation
   replaces an *absent* server and never adds a second one for a full map) that
   is not a bug to fix; it is what the design says should happen.
2. *Consuming it would be worse.* If the autoscaler did add a replica, that
   replica would come up with the same `GAMESERVER_MAP_ID` and register itself
   under it — manufacturing the exact split-world hazard ADR-2 forbids, without
   any allocation being involved. The autoscaler would be the thing that broke
   the invariant.

Buffer autoscaling becomes coherent when there is a fleet whose pods really are
spare capacity until handed out — the **dungeon** fleet, ADR-14 stage 6. Write it
then, against that fleet, with a per-instance id. ADR-14 stage 7 should be read
as belonging to stage 6, not to the map fleet.

### Enabling the allocator

The gateway's compiled-in defaults still name the retired Go fleets:

```go
DefaultFleetMap     = "map-servers-dev"      // gateway/registry/agones_allocator.go
DefaultFleetDungeon = "dungeon-servers-dev"
```

Neither exists. `ALLOCATOR=agones` alone therefore POSTs allocations against a
fleet that is not there, and the failure surfaces as "no server available" at the
one moment allocation was supposed to help. Set the override too:

```bash
ALLOCATOR=agones
ALLOCATOR_FLEET_MAP=map-servers-dotnet-dev
# ALLOCATOR_NAMESPACE defaults to rpg-realtime, which is still correct.
```

`k3s/validate-manifests.py` prints a `[warn]` for this mismatch on every run.
Changing the Go constants instead is the cleaner fix and belongs to the gateway,
not here.

### Allocating from the compose-run gateway

The gateway **stays in docker compose**; only the game servers run under Agones.
It therefore allocates *out-of-cluster*, over the Kubernetes API, and needs a
kubeconfig — ADR-15 decision 3 item 6, "the one most likely to be discovered
late, because `agones_allocator.go` is already written, already tested, and
already works — on a developer's kubeconfig". Inside the gateway container none
of `resolveRESTConfig`'s four sources exist, and `cmd/gateway/main.go` treats a
failed allocator construction as **fatal** (exit 1): with `ALLOCATOR=agones` and
no kubeconfig, the gateway does not start degraded, it does not start.

`docker-compose.agones.yml` is the opt-in overlay that supplies it:

**The route is: join k3d's own Docker network. Not `host.docker.internal`.**
client-go verifies the API server certificate and there is no acceptable way to
turn that off in a service holding allocation credentials, so the kubeconfig must
name a host that is both routable from the container *and* a SAN on the k3d
certificate. Read off the live listener, k3d issues:

```
DNS:k3d-rpg-dev-server-0, DNS:k3d-rpg-dev-serverlb, DNS:kubernetes,
DNS:kubernetes.default, DNS:kubernetes.default.svc,
DNS:kubernetes.default.svc.cluster.local, DNS:localhost,
IP:10.43.0.1, IP:127.0.0.1, IP:172.20.0.3, IP:::1
```

`host.docker.internal` is **not** among them. Both routes reach the API — a
`/version` request answers over either — but only one keeps verification intact:

| | Route | Verdict |
|---|---|---|
| **A** | attach the gateway to network `k3d-<cluster>`, dial `https://k3d-<cluster>-serverlb:6443` | **recommended** — that name *is* a SAN, so TLS passes with no cert change and no cluster recreate |
| B | `https://host.docker.internal:<api-port>` | rejected — needs verification disabled, or the cluster recreated with `--k3s-arg '--tls-san=host.docker.internal@server:*'`. Recreating a cluster to avoid one `sed` is the wrong trade |

```bash
cd backend/deploy
K3D_CLUSTER=rpg-dev

# 1. Export a kubeconfig and point it at the serverlb on its IN-NETWORK port
#    6443 (not the published host port; k3d writes a host-side 0.0.0.0/127.0.0.1
#    URL, and inside a container 127.0.0.1 is the container).
k3d kubeconfig get "$K3D_CLUSTER" > kubeconfig.local
sed -i -E "s#server: https://[^[:space:]]+#server: https://k3d-${K3D_CLUSTER}-serverlb:6443#" \
  kubeconfig.local

# 2. VERIFY from a container on that network, before wiring the gateway to it.
#    RUN FROM backend/deploy WITH A CWD-RELATIVE MOUNT PATH: on this WSL2 box
#    `docker` is Docker Desktop's shim, absolute /mnt/* paths do not translate,
#    and the mount silently becomes a DIRECTORY — kubectl then reports
#    `read /kc: is a directory`, which reads like a kubeconfig bug and is not.
docker run --rm --network "k3d-${K3D_CLUSTER}" \
  -v "./kubeconfig.local:/kc:ro" -e KUBECONFIG=/kc \
  bitnami/kubectl:latest get nodes

# 3. Bring the gateway up with the overlay.
docker compose -f docker-compose.yml -f docker-compose.agones.yml \
  --profile realtime up -d gateway
docker compose logs gateway | grep -i allocator
```

Measured output of step 2 in this worktree — no `-k`, no
`insecure-skip-tls-verify`; the CA travels in the kubeconfig and the serverlb
name verifies as itself:

```
NAME                   STATUS   ROLES                  AGE   VERSION
k3d-rpg-dev-server-0   Ready    control-plane,master   63m   v1.31.5+k3s1
```

**The network name and the serverlb hostname are both derived from the cluster
name** — `k3d-<cluster>` and `k3d-<cluster>-serverlb`. They are parameters, not
constants; set `K3D_NETWORK` for any cluster not called `rpg-dev`.

Two further notes on the overlay: the gateway is attached to **both** `default`
and the k3d network, because naming `networks:` on a service *replaces* its
network list rather than adding to it — listing only the k3d one would cut the
gateway off from redis, giving a working allocator and a gateway that cannot
reach its own registry. And the k3d network is declared `external`, which is the
second reason this lives in an overlay: compose refuses to start when an external
network is missing, so naming it in the base file would make the whole stack
depend on a k3d cluster existing.

`kubeconfig.local` is git-ignored. A developer's `~/.kube/config` grants every
cluster they have; for anything past a laptop, mint a ServiceAccount with
`create` on `gameserverallocations.allocation.agones.dev` in `rpg-realtime` and
build a kubeconfig around that token.

The base `docker-compose.yml` carries the `ALLOCATOR_*` variables with
`ALLOCATOR` defaulting to `none`, so the ordinary stack keeps working with no
cluster present. The kubeconfig mount lives only in the overlay: compose creates
a *directory* in place of a missing bind source, so a base-file mount would plant
an empty `kubeconfig.local/` in every no-cluster stack.

### The address the client is given

`GAMESERVER_PUBLIC_ADDR` is **deliberately absent** from the fleet, and adding it
back will not work: under `portPolicy: Dynamic` the host port is chosen by the
scheduler, so no value written in a manifest can be right. The server instead
reads its own address from the Agones sidecar's GameServer status and registers
that (ADR-15 decision 2, option A) — in flight, not merged. Until it lands the
server advertises the hostless `:9000` it listens on, the gateway forwards that
verbatim (ADR-3: the gateway never dials it), and the client cannot connect.

**The status read alone is not sufficient, on either cluster.** `status.address`
is the *node* address, and on neither local cluster is the node address something
a client can dial:

| Cluster | `status.address` | Dialable by a client? |
|---------|------------------|----------------------|
| docker-desktop | `192.168.65.3` | **No.** Measured: a probe GameServer with `hostPort: 7306` answered on `192.168.65.3:7306` from inside the cluster and was unreachable from both Windows and WSL2, while a compose-published port on the same host answered fine. Docker Desktop publishes *Docker* ports to the host, not Kubernetes `hostPort` — so **no** host string helps |
| k3d | `172.20.0.3` (node container's Docker-network address) | Not as-is — but `127.0.0.1:<agones-assigned-port>` **is**, published by the k3d serverlb, measured reachable from both Windows (where the Unity client runs) and WSL2 |

So on k3d the client-facing address is composed from two sources: the **port**
from the Agones status read (assigned per pod at scheduling time — nothing else
can know it) and the **host** from configuration. That is why the override being
added to the game server is a *host*, not a full address:
`GAMESERVER_PUBLIC_ADDR` replaces the whole thing and cannot carry a port it does
not know.

That override is **`GAMESERVER_ADVERTISE_HOST`** (flag form `--advertise-host`;
the flag wins). It takes a **host only** — IPv4, IPv6 bare or bracketed, or a
hostname — and the composed result is
`<GAMESERVER_ADVERTISE_HOST or status.address>:<Agones-assigned game port>`.

- **The port is never configurable.** It is assigned per pod at scheduling time,
  and supplying it is the entire purpose of the sidecar read.
- **Do not set `GAMESERVER_PUBLIC_ADDR` as well.** That one is read only when
  Agones is off, and it replaces the whole address, so it cannot carry a port it
  does not know.
- Misuse is caught, not silently applied: set with Agones off → warned and
  ignored; set to a full `host:port` → warned, host honoured, port discarded;
  blank → treated as unset. On a **failed** status read the override is ignored
  *entirely*, so no half-composed address is ever registered — the variable has
  no effect at all without a successful read, by design.

`k3s/setup-dev.sh` writes the `advertise-host` key into the `gameserver-config`
ConfigMap per cluster kind — `127.0.0.1` on k3d, **empty on docker-desktop**,
where no value can be correct — and `fleet-map-dotnet-dev.yaml` reads it with
`optional: true`. Optional is right here and is not the `REDIS_ADDR` case:
absent-or-blank is a meaningful, handled state (advertise `status.address`
unmodified), not a silent misconfiguration.

> ### This is a single-node arrangement and does not scale past one node
>
> `GAMESERVER_ADVERTISE_HOST` works **because the cluster has one node with a
> published port range**, so one well-known host serves every pod. That is a
> property of the *deployment*, not of the code.
>
> On a multi-node cluster the correct host **differs per pod** — it depends on
> which node the scheduler placed it on — and a single env var on the fleet
> cannot express a per-pod value. The answers there are an ingress in front of
> the fleet, or a per-node value injected through the downward API
> (`spec.nodeName` / `status.hostIP`). **Neither exists in this repo.** Do not
> read the k3d setup as evidence that the address problem is solved in general;
> it is solved for one node.

**Consequence for the ADR-14 stages: stage 4 (no restart loop) is provable on
`docker-desktop`; stage 5 (end-to-end allocation) is not**, because no client can
reach an allocated pod there at all. Stage 5 needs k3d.

### Secrets

The fleet reads every secret from the `rpg-realtime-secrets` Secret in
`rpg-realtime`. `agones/secret-example.yaml` documents the keys and shows the
shape; it is a **template with published dev placeholders**, and its
`metadata.name` is deliberately `rpg-realtime-secrets-example` so that
`kubectl apply -f agones/` cannot overwrite a real Secret with them.

| Key | Env | Notes |
|-----|-----|-------|
| `jwt-secret` | `JWT_SECRET` | HS256, client auth tokens. Server only *warns* when unset, so a wrong value reads as "all tokens rejected" |
| `join-token-secret` | `JOIN_TOKEN_SECRET` | HS256, gateway→gameserver join tokens. **Mandatory** — the server exits 2 without it. Never the same value as `jwt-secret`: it is on every pod, so a compromised pod must not be able to forge auth tokens. Rotate as `new,old` |
| `redis-password` | `REDIS_PASSWORD` | Empty unless redis runs `--requirepass`. `optional` in the fleet |
| `transport-key` | `TRANSPORT_KEY` | Pre-shared AES-256 key for KCP. **Unused today** (the fleet is TCP, and the server warns that the key is ignored on TCP). Carried anyway so enabling KCP is a manifest change, not a new secret-distribution problem |
| `game-db-url` | `GAME_DB_URL` | DSN for the game-state postgres, *containing a password*, which is why it is here and not in the ConfigMap. Written by `setup-dev.sh` with the host translated for a pod (see [Persistence](#persistence-game-db-url)). Blank is legal and means the in-memory store; the fleet reads it `optional`. A set-but-unreachable DSN makes the server **exit 1** rather than fall back to memory |

**Keeping them in sync with the gateway is the whole game.** The gateway runs in
docker compose and reads `backend/deploy/.env`; the game server reads this
Secret. If the two `JOIN_TOKEN_SECRET`s differ, the gateway mints a token the
server cannot verify and **every join fails signature verification**, with
nothing logging the cause at the point the mistake was made. So never type the
values twice — generate the Secret *from* `.env`:

```bash
cd backend/deploy
set -a; . ./.env; set +a
kubectl create secret generic rpg-realtime-secrets \
  --namespace rpg-realtime \
  --from-literal=jwt-secret="$JWT_SECRET" \
  --from-literal=join-token-secret="$JOIN_TOKEN_SECRET" \
  --from-literal=redis-password="${REDIS_PASSWORD:-}" \
  --from-literal=transport-key="${TRANSPORT_KEY:-}" \
  --dry-run=client -o yaml | kubectl apply -f -
```

`k3s/setup-dev.sh` runs exactly that, so **re-running it is how you re-sync after
any `.env` change**. Updating a Secret does not restart running pods and Fleets
have no `kubectl rollout restart`; delete the GameServers and let Agones recreate
them:

```bash
kubectl delete gs -n rpg-realtime -l agones.dev/fleet=map-servers-dotnet-dev
```

`.env` and `agones/secret-*.local.yaml` are both git-ignored. Nothing with a real
value should ever be committed.

### Deploying the dotnet fleet (ADR-14 stage 4)

**Rebuild → verify → import → apply is one sequence, not four optional steps.**
`imagePullPolicy: IfNotPresent` will happily run whatever `:dev` last pointed at,
and a fleet deployed from a stale image comes up green and proves nothing. This
has already happened: the `:dev` in the store on 2026-08-17 was built 5.4 hours
*before* the commit that added the real Agones SDK, so a pod from it would have
run the no-op SDK while the manifest, the ADR and the operator all believed
otherwise.

```bash
# 1. REBUILD from the branch under test, stamping the revision into the image.
cd backend
docker build -f deploy/docker/Dockerfile.gameserver-dotnet \
  --build-arg GIT_REVISION="$(git rev-parse HEAD)" \
  -t rpg-mmo/gameserver-dotnet:dev .
# (context must be backend/; under WSL, docker.exe cannot resolve absolute
#  /mnt/* paths, so run it cwd-relative.)

# 2. VERIFY the tag is what you think it is. Fails loudly if it is not.
python3 deploy/k3s/validate-manifests.py \
  --check-image rpg-mmo/gameserver-dotnet:dev \
  --expect-revision "$(git rev-parse HEAD)"

# 3. IMPORT — per cluster kind. Skipping it on k3d does not error: IfNotPresent
#    falls through to a registry pull that fails, and the GameServer sits in
#    ImagePullBackOff.
#      docker-desktop : nothing — the node shares the host's docker image store
#      k3d            : k3d image import rpg-mmo/gameserver-dotnet:dev -c <cluster>
#      real k3s       : docker save rpg-mmo/gameserver-dotnet:dev | sudo k3s ctr images import -

# 4. APPLY (setup-dev.sh does 2, 3 and 4, plus the Secret and ConfigMap).
kubectl apply --dry-run=server -f deploy/agones/fleet-map-dotnet-dev.yaml   # rehearse
deploy/k3s/setup-dev.sh --skip-agones
```

Then check what stage 4 is actually asking:

```bash
kubectl get gs -n rpg-realtime -w      # STATE reaches Ready and stays
kubectl get pods -n rpg-realtime       # RESTARTS stays 0 over a sustained run
kubectl logs -n rpg-realtime -l agones.dev/fleet=map-servers-dotnet-dev -c gameserver
kubectl exec -n rpg-realtime <pod> -c gameserver -- env | grep AGONES   # sidecar port
```

> **Recommended, not implemented here:** have the server print its build revision
> in the startup banner and expose it on the existing `/status` endpoint
> (`GameServer/Observability/ServerStatus.cs`). That turns "is the running pod
> the code under test?" from a procedure people skip into an assertion a smoke
> test can make against a *running* server, which is strictly stronger than
> inspecting an image label. It is a game-server change, not a deploy one.

### Talking to the host

The data tier (Redis, both PostgreSQL instances, Nakama, lgtm) **stays in docker
compose on the host** — that is deliberate, not a migration in progress, and
ADR-15 decision 4 argues against splitting orchestrators any further than this
one fleet. The consequence is that a pod must reach back to the host, and **the
hostname for that is cluster-specific and not portable**:

| Cluster | Host alias | Status |
|---------|-----------|--------|
| Docker Desktop k8s | `host.docker.internal` | verified working |
| k3d | `host.k3d.internal` | **measured** — `redis-cli -h host.k3d.internal -p 6379 ping` → `PONG` from a pod |
| real k3s / anything else | none exists | needs a real routable address |

On k3d, `host.docker.internal` and the bridge address `172.17.0.1` also answer
from a pod, and neither is the default. `host.docker.internal` is a Docker
Desktop convention that happens to be inherited, so using it quietly implies a
Docker Desktop that may not be there; `172.17.0.1` is an unstable bridge IP.
`host.k3d.internal` is the one k3d injects itself.

`setup-dev.sh` picks the default from the kubectl context and writes it into the
`gameserver-config` ConfigMap. Override with `POD_REDIS_ADDR=<host>:6379` when
the guess is wrong — and if you have to, record the working value in this table
rather than guessing again. Note the failure is quiet: an unresolvable
`REDIS_ADDR` makes the server log `Registry: disabled` and run unregistered, so
the pod is Ready and the gateway still cannot find it.

<a id="persistence-game-db-url"></a>
### Persistence (`GAME_DB_URL`)

Postgres is the same idea with one extra twist: **the port changes too**.
Compose's own DSN is `postgres-game:5432`, a service name on the compose network
that resolves nowhere else, and 5433 is the *host-side published* mapping of
`rpg-postgres-game`. A pod is on neither network, so it reaches the database the
way any other host process does — host alias, published port:

```
# compose (in-network)   postgres://game:localdev@postgres-game:5432/gamestate?sslmode=disable
# pod on k3d             postgres://game:localdev@host.k3d.internal:5433/gamestate?sslmode=disable
# pod on docker-desktop   (no correct value — see below)
```

The DSN carries a password, so it goes in the Secret, never in the fleet
manifest or the ConfigMap. `setup-dev.sh` composes it from the `POSTGRES_GAME_*`
values in `.env` and the cluster kind, and `POD_GAME_DB_URL=<dsn>` overrides it
wholesale. **MEASURED** from a pod in `rpg-realtime`:

```bash
kubectl --context k3d-rpg-dev -n rpg-realtime run pgcheck --restart=Never \
  --image=postgres:16-alpine --command -- sh -c \
  'PGPASSWORD=localdev psql -h host.k3d.internal -p 5433 -U game -d gamestate -c "\dt"'
# -> public | player_states | table | game
# -> public | schema_migrations | table | game
```

On **docker-desktop the value is deliberately empty**: no host string makes the
compose-hosted postgres addressable from a pod there, and inventing one would
hide that. Empty is a handled state, not a failure — the server treats a blank
`GAME_DB_URL` as unset and logs
`using in-memory player store (GAME_DB_URL unset -- state is lost on restart)`.
That is why the fleet reads the key with `optional: true`, and it is the one
line in the startup banner worth grepping for after any deploy: under a Fleet,
"state is lost on restart" is not an edge case, because every rollout, eviction
or health kill is a restart.

A wrong-but-set DSN does **not** degrade quietly. The server logs
`postgres player store unavailable -- refusing to start` and exits 1, i.e. a
visibly crash-looping GameServer instead of a server holding the world in RAM.

Proven end to end on 2026-08-17 against a map served **only** by an Agones pod
(compose `rpg-gameserver` stopped for the run, so `map_01` had exactly one live
server — ADR-2), with `--strict-addr --require-db`:

```
PASS  gateway_auth                5ms  transport=tcp map=map_01 server=127.0.0.1:7052 (tcp)
PASS  gameserver_join          1.113s  snapshots=15 (keyframes=1 deltas=14) final_x=4.83 ack_tick=10
PASS  gamestate_migrations       18ms  version=1 (001_init) applied=2026-08-05T07:34:08Z
PASS  gamestate_player_row     13.03s  map=map_01 x=4.8333 y=0.0000 hp=100/100 (14 polls)
PASS  gamestate_reload        20.113s  respawned at x=4.8333 from persisted x=4.8333 (waited 20s for hold)
SMOKE=PASS
```

`gamestate_player_row` taking ~13-20s and `gamestate_reload` ~20s is the
AsyncSaver sweep (30s) and the reconnect hold, not a hang.

---

## Offline validation

`kubectl apply --dry-run=client` cannot check a `Fleet` without a live API
server — it needs discovery to learn the CRD. `k3s/validate-manifests.py` closes
that gap: it downloads the pinned Agones `install.yaml`, lifts the
`openAPIV3Schema` out of each CRD and validates our resources against it with
`jsonschema` (caching the release under `~/.cache/rpg-mmo/`).

Schema validity is the cheap half. A Fleet can be perfectly schema-valid and
still be wrong in ways that only surface as a client that cannot connect, so the
script also asserts the **project contracts** no schema knows about:

| Check | Why it exists |
|-------|---------------|
| a port named `game`, `portPolicy: Dynamic` | the allocator selects the client port by name; Static collides under `scheduling: Packed` |
| `POD_NAME` from `fieldRef: metadata.name`, and **no `GAMESERVER_ID`** | the join token's `sid` is the GameServer name; `GAMESERVER_ID` wins over `POD_NAME` and would make every join fail |
| no literal `value:` for `JWT_SECRET` / `JOIN_TOKEN_SECRET` / `TRANSPORT_KEY` / `REDIS_PASSWORD` / `GAME_DB_URL` | secrets belong in a Secret, not in git |
| no `GAMESERVER_PUBLIC_ADDR` | no static value can be right under Dynamic ports |
| no `rpg-mmo/gameserver:` image | that is the deleted Go server |
| `replicas > 1` with a fixed `GAMESERVER_MAP_ID` | every replica registers the same map id — ADR-2 |
| autoscaler policy is `Buffer`; `fleetName` / allocation selectors name a real fleet | ADR-14 decision 5, and a dangling `fleetName` survives every schema check |
| the gateway's `DefaultFleetMap` / `DefaultNamespace` / `gamePortName` name something real | `[warn]`, not a failure — the env overrides exist |

```bash
python3 k3s/validate-manifests.py                      # all agones/ + k3s/ manifests
python3 k3s/validate-manifests.py --agones-version 1.58.0 agones/fleet-map-dotnet-dev.yaml

# Is the local image actually built from the code under test?
python3 k3s/validate-manifests.py \
  --check-image rpg-mmo/gameserver-dotnet:dev \
  --expect-revision "$(git rev-parse HEAD)"
```

Limits, so nobody over-trusts a green run:

- `GameServerAllocation` is served by an **aggregated API**, not a CRD, so it has
  no schema in `install.yaml`; only the contract checks apply to it.
- CRD schemas type-check structure, not semantics. A bogus enum value
  (`portPolicy: Bogus`) passes here and is rejected by the Agones **webhook** at
  apply time. Only a real cluster catches those.
- `--check-image` compares the image's `org.opencontainers.image.revision`
  label, which only exists on images built **after** that label was added to
  `docker/Dockerfile.gameserver-dotnet`, and only when `--build-arg
  GIT_REVISION=` was passed. An unlabelled image fails the check, which is the
  correct answer: it cannot be shown to contain the code under test.
- Requires `pyyaml` + `jsonschema`.

### Stronger: server-side dry run

With a cluster reachable, rehearse the apply against the real CRDs **and the
Agones admission webhooks**, which is strictly stronger than any schema guess:

```bash
kubectl apply  --dry-run=server -f agones/fleet-map-dotnet-dev.yaml
kubectl create --dry-run=server -f agones/allocation-dev.yaml   # create-only resource
```

Verified output on `docker-desktop` (Agones 1.59.0, k8s v1.34.1):

```
fleet.agones.dev/map-servers-dotnet-dev created (server dry run)
gameserverallocation.allocation.agones.dev/<unknown> created (server dry run)
```

---

## Graduating to a real k3s cluster

Nothing above is Docker-Desktop-specific except the image import and
`host.docker.internal`. On a VPS:

```bash
# 1. install k3s (single node, dev/alpha tier)
curl -sfL https://get.k3s.io | sh -s - --write-kubeconfig-mode 644

# 2. same bootstrap, unchanged
export KUBECONFIG=/etc/rancher/k3s/k3s.yaml
cd backend/deploy && ./k3s/setup-dev.sh --prod-fleets
```

Agones needs the game ports reachable: open **UDP/TCP 7000–8000** (the default
`gameservers.minPort`–`maxPort` range) in the VPS firewall, plus whatever the
gateway listens on.

### CD wiring (sketch — not implemented)

1. Store the cluster's kubeconfig as repository secret `KUBE_CONFIG` (base64 of
   `/etc/rancher/k3s/k3s.yaml`, with `server:` rewritten from `127.0.0.1` to the
   VPS address).
2. Add a `deploy_fleet` job to `.github/workflows/cd.yml`, after the existing
   image push, gated on the `production` environment:
   - write `$KUBE_CONFIG` to a temp file, export `KUBECONFIG`
   - `kubectl -n rpg-realtime set image fleet/map-servers gameserver=ghcr.io/…:${{ github.sha }}`
   - `kubectl -n rpg-realtime rollout status fleet/map-servers` — Agones does a
     rolling replace honouring `Allocated` game servers, so live matches drain
     instead of being killed.
3. Rollback = `set image` back to the previous SHA. Keep image tags immutable
   (`:sha`) and treat `:latest` as a pointer only.

The self-hosted-runner and environment-secret mechanics already documented in
`CICD.md` apply verbatim.

---

## Troubleshooting (WSL2 quirks)

| Symptom | Cause / fix |
|---------|-------------|
| `no Kubernetes cluster reachable` in 0.1s | No kubeconfig context. Enable Docker Desktop Kubernetes (above). |
| `kubectl` hangs ~25s then errors on `localhost:8080` | Empty kubeconfig — kubectl falls back to the legacy default and retries discovery 5×. The scripts check `current-context` first to avoid this. |
| `kubectl.exe` says a manifest path does not exist | Windows binary, Linux path. `lib.sh` always pipes local files through stdin (`apply -f -`); use `kube_apply_file` rather than `kube apply -f <path>`. |
| `docker` works but `/var/run/docker.sock` resets the connection | WSL integration is off; `docker` is a shim for `docker.exe`. Anything needing a real Linux socket (k3d, testcontainers) will not work. |
| Fleet apply fails: *no endpoints available for agones-controller-service* | Webhook not up yet. `setup-dev.sh` waits and retries 6×; if it persists check `kubectl get pods -n agones-system`. |
| GameServer stuck in `Scheduled`/`RequestReady` | Image missing (`IfNotPresent` + never imported), or `--agones` dropped from args. `kubectl describe gs -n rpg-realtime <name>`. |
| GameServer flaps `Unhealthy` | The binary crashed or never reached `sdk.Ready()`. Logs: `kubectl logs -n rpg-realtime <pod> -c gameserver` (the `-c` matters — the SDK sidecar is a second container). |
| `metadata.annotations: Too long` on Agones install | Client-side apply. Use `--server-side --force-conflicts`, as the script does. |
