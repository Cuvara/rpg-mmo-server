# k3s / Agones — dev cluster bootstrap

Everything needed to get the map/dungeon fleets running on a Kubernetes cluster,
plus the path from a laptop cluster to a real k3s VPS.

- Scripts: `backend/deploy/k3s/`
- Manifests: `backend/deploy/agones/`
- Agones version: **1.59.0**, pinned to match `agones.dev/agones v1.59.0` in
  the Agones SDK. Override with `AGONES_VERSION=…`.

---

## TL;DR

```bash
cd backend/deploy
./k3s/setup-dev.sh --with-dungeon      # install Agones, namespaces, fleets, wait for Ready
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
2. **Preflight** — bail immediately with instructions if no context/cluster.
3. **Install Agones** `$AGONES_VERSION` via `kubectl apply --server-side` on the
   release `install.yaml`. Server-side is required: the CRDs exceed the 262 kB
   `last-applied-configuration` annotation that client-side apply writes.
4. **Wait** for `agones-system` deployments to be Available, then poll until the
   `agones.dev/v1` API actually serves Fleets — the webhook lags the deployment
   and applying a Fleet too early fails with *no endpoints available for service
   agones-controller-service*.
5. **Namespaces** — `rpg-realtime`, `rpg-meta`, `rpg-data` (`k3s/namespaces.yaml`).
6. **Dev config** — Secret `rpg-realtime-secrets` (`jwt-secret`) and ConfigMap
   `gameserver-config` (`redis-addr`, `game-db-url`), created via
   `create --dry-run=client -o yaml | apply` so they are idempotent.
7. **Fleets** — `fleet-map-dev.yaml` (+ `--with-dungeon`, `--with-autoscaler`),
   or the ghcr.io ones with `--prod-fleets`.
8. **Wait for Ready** — polls `GameServer.status.state` until one is `Ready`.
   That is the real end-to-end proof: the pod started, the binary connected to
   the SDK sidecar, and `sdk.Ready()` returned.
9. **Print** `agones-system` pods, fleets, gameservers, and the follow-up
   commands (address\:port, allocation, logs, teardown).

Flags: `--with-dungeon`, `--with-autoscaler`, `--prod-fleets`, `--skip-agones`.
Env: `AGONES_VERSION`, `KUBECTL_BIN`, `JWT_SECRET`, `JOIN_TOKEN_SECRET`,
`REDIS_ADDR`, `GAME_DB_URL`.

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
| `agones/fleet-map-dotnet-dev.yaml` | **The current one.** C# server, `rpg-mmo/gameserver-dotnet:dev`, health `disabled: true` — see below |
| `agones/autoscaler.yaml` / `autoscaler-dev.yaml` | Buffer autoscaler (prod 2/1–10, dev 1/1–2) — both still target the superseded map fleets |
| `agones/allocation.yaml` / `allocation-dev.yaml` | `GameServerAllocation` — `kubectl create`, never `apply` |
| `k3s/namespaces.yaml` | `rpg-realtime` / `rpg-meta` / `rpg-data` |

### Which fleet is real

Nine manifests, one live server implementation. Read this before applying any of
them. Full rationale: [ADR-14](../../docs/ARCHITECTURE-DECISIONS.md) — this
section is the operational summary, not a restatement.

**What the fleets are for.** A `Fleet` is Agones' unit of game-server supply: it
keeps N pods of one spec Ready and hands them out one at a time when the gateway
(or `kubectl create -f allocation*.yaml`) allocates. Map fleets hold long-lived
per-`map_id` servers; dungeon fleets are `replicas: 0` and exist to be allocated
per party. Neither is on the deploy path today — dev, staging and production all
run `DEPLOY_MODE=containers` under docker compose, and CI never applies anything
in `agones/`.

**The four Go-image manifests are marked superseded, not deleted.** They run
`rpg-mmo/gameserver:dev` / `ghcr.io/…/rpg-mmo-gameserver:latest`, built from
`backend/gameserver/`, which was deleted in `670a803`; `docker/Dockerfile.gameserver`
went with it, so the dev image cannot be rebuilt. They survive because
`map-servers-dev` and `dungeon-servers-dev` are *still running* on this cluster
from those files, and a live fleet with no manifest is worse than a manifest
marked stale. `setup-dev.sh` still applies the Go dev fleets — that is
deliberately unchanged here so this change alters nothing that runs.

> **Follow-up, needs a human.** Retiring the running fleets is a `kubectl delete`
> against `docker-desktop` and is **not** part of this or any manifest change:
> `kubectl -n rpg-realtime delete fleet map-servers-dev dungeon-servers-dev`.
> ADR-14 stage 8 pairs that with deleting the four files and repointing
> `setup-dev.sh`. Nobody has run it; both fleets are Ready with `ALLOCATED 0`.

**Why the dotnet fleet's health is disabled.** `fleet-map-dotnet-dev.yaml` sets
`health.disabled: true`. The C# server's Agones SDK is `NoopAgonesSdk` — it never
pings the sidecar, and `--agones` only logs a warning. With health enabled the pod
fails `failureThreshold` checks and Agones kills and recreates it forever, so the
flag is the difference between "not wired up yet" and "crash-looping". The
`initialDelaySeconds`/`periodSeconds`/`failureThreshold` values stay in the file
(the Agones v1 `health` block accepts all four fields independently; the timings
are simply inert while `disabled` is set) so stage 4 does not have to re-derive
them.

**The order in which Agones becomes real.** Each step is a precondition of the
next; skipping ahead produces a restart loop or an allocation that returns a pod
nothing can join.

1. `HttpAgonesSdk` lands against the sidecar on `localhost:9358` (ADR-14 stage 1,
   no deployment). The *ordering* the ADR asks for is already implemented in
   `GameServer/Server/GameServer.cs` — bind, then `ReadyAsync()`, then
   `_registration.StartAsync()`; on the way down `DeregisterAsync()` before
   `ShutdownAsync()` — so what stages 1–3 add is the SDK behind it, not the
   sequence. `Program.cs` hardcoded `new NoopAgonesSdk()`; that is the line that
   changes.
2. Deploy `fleet-map-dotnet-dev.yaml` **with `health.disabled: true` still set**
   and confirm the pod reaches `Ready` and stays there. Deploying and un-disabling
   in one change conflates two failures — a server that cannot come up, and a
   ping loop that cannot keep up.
3. Only then remove `health.disabled: true`, and confirm the pod survives the ping
   period: `kubectl -n rpg-realtime get gs -w`, `RESTARTS` stays 0 and the
   GameServer stays `Ready` over a sustained run. This is its own verification
   step, not a side effect of the SDK merging. If it flaps, put the flag back
   rather than widening `failureThreshold` — a starved ping task is a real
   liveness failure, and ADR-13's overload path is what should keep a merely-slow
   server from looking dead.
4. Set `ALLOCATOR=agones` on the gateway (it defaults to `none`).
5. Verify allocation end to end: `MsgEnterWorld` for an unserved map →
   `kubectl -n rpg-realtime get fleet` shows `ALLOCATED` move off 0 → a client
   joins the returned address. This is the first step that proves anything;
   1–4 only reduce risk.

### Reality-pass applied to the fleets

Written for the **Go** fleets and still true of them; the two SDK bullets do not
hold for the C# fleet, whose SDK is a no-op (see "Which fleet is real" above).
Checked against the game server configuration
and `docker/Dockerfile.gameserver-dotnet`:

- **Port `9000`** matches `GAMESERVER_ADDR` default `:9000`, `--addr=:9000` in the
  fleet args, and `EXPOSE 9000` in the Dockerfile. `portPolicy: Dynamic` is now
  explicit: Agones assigns the *host* port and publishes it on
  `GameServer.status.ports[0].port`; the container always binds `:9000`.
- **`--agones` is mandatory.** Without it `main.go` picks `NewNoopSDK`, never
  calls `Ready()`, and Agones tears the pod down after the health timeout.
- **Health is SDK-driven** — `StartHealthLoop` pings `sdk.Health()`. There is no
  exec/HTTP probe because the runtime image is distroless (no shell, no curl).
  `initialDelaySeconds` raised 5 → 10 to cover Postgres migration on start.
- **Config plumbing added.** Prod fleets read `JWT_SECRET` from Secret
  `rpg-realtime-secrets` and `REDIS_ADDR` / `GAME_DB_URL` from ConfigMap
  `gameserver-config`, all `optional: true` so the fleet still starts before
  those objects exist. `--redis` added so gateway and gameservers share one
  registry and event stream.
- **Dev fleets have zero external dependencies** by design: no `--redis`, no
  `GAME_DB_URL`, so they reach `Ready` on a bare laptop cluster with the
  in-memory registry and player store. Both are one uncomment away.

### Images

`ghcr.io/cuvara/rpg-mmo-gameserver:latest` is **not published yet**, which is
why the dev fleets exist. Build the local image first:

```bash
cd backend/deploy
docker build -f docker/Dockerfile.gameserver-dotnet -t rpg-mmo/gameserver-dotnet:dev ..
```

(`docker/Dockerfile.gameserver` — the Go one that produced `rpg-mmo/gameserver:dev`
for the superseded fleets — no longer exists. That image can only be whatever is
already in the local store.)

Note the `..` — the build context must be `backend/`, and under WSL `docker.exe`
cannot resolve absolute `/mnt/*` paths, so run it cwd-relative from
`backend/deploy`.

Getting that image into the cluster:

| Cluster | Import step |
|---------|-------------|
| Docker Desktop k8s | none — shares the Docker image store |
| k3d | `k3d image import rpg-mmo/gameserver-dotnet:dev -c rpg-dev` |
| real k3s | `docker save rpg-mmo/gameserver-dotnet:dev \| sudo k3s ctr images import -` |

### Talking to the host

Dev fleets point `REDIS_ADDR` at `host.docker.internal:6379` — the local compose
stack (`docker-compose.yml`) as seen from inside a pod. On k3d the equivalent
name is `host.k3d.internal`. Postgres is the same idea:

```
GAME_DB_URL=postgres://game:localdev@host.docker.internal:5433/gamestate?sslmode=disable
```

(port 5433 — that is the host-side mapping of `rpg-postgres-game`).

---

## Offline validation

`kubectl apply --dry-run=client` cannot check a `Fleet` without a live API
server — it needs discovery to learn the CRD. `k3s/validate-manifests.py` closes
that gap: it downloads the pinned Agones `install.yaml`, lifts the
`openAPIV3Schema` out of each CRD and validates our resources against it with
`jsonschema` (caching the release under `~/.cache/rpg-mmo/`).

```bash
python3 k3s/validate-manifests.py                      # all agones/ + k3s/ manifests
python3 k3s/validate-manifests.py --agones-version 1.58.0 agones/fleet-map.yaml
```

Limits, so nobody over-trusts a green run:

- `GameServerAllocation` is served by an **aggregated API**, not a CRD, so it has
  no schema in `install.yaml` and is reported as *skipped*.
- CRD schemas type-check structure, not semantics. A bogus enum value
  (`portPolicy: Bogus`) passes here and is rejected by the Agones **webhook** at
  apply time. Only a real cluster catches those.
- Requires `pyyaml` + `jsonschema`.

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
