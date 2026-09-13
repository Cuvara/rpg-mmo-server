# Data tier on Kubernetes

The stateful half of the backend, as cluster manifests: **PostgreSQL meta**
(Nakama's own DB), **PostgreSQL gamestate** (`player_states`), **Redis**
(sessions + server registry + event stream), and **Nakama** with our Go runtime
plugin baked in.

`../../docker-compose.yml` is the specification these reproduce. Where the two
differ, the difference is deliberate and stated below.

This is the first half of moving the system to k3s/Agones. The realtime half —
gateway and the Agones game-server fleet — lives in `../app/` and
`../../agones/`, and is not touched here.

---

## Layout

| File | What it is |
|---|---|
| `namespace.yaml` | `rpg-k8s-data`, the bring-up namespace |
| `postgres-meta.yaml` | StatefulSet + headless Service + PVC — Nakama's database |
| `postgres-game.yaml` | StatefulSet + headless Service + PVC — `player_states`, separate credentials, separate volume |
| `redis.yaml` | StatefulSet + headless Service + PVC — sessions / registry / streams |
| `redis.conf` | The Redis config, mirrored from the compose flags; `noeviction` lives here |
| `nakama.yaml` | Deployment + ClusterIP Service, `migrate up` as an initContainer, plugin baked into the image |
| `secrets.example.yaml` | Secret **templates**. Placeholder values only — see §Secrets |
| `kustomization.yaml` | Ties it together; `namespace:` is the one line to retarget |
| `apply.sh` | Bring-up. Use this, not bare `kubectl apply -k` — see §The initdb ConfigMap |

## Bring-up

```bash
./apply.sh                                   # k3d-rpg-dev / rpg-k8s-data
KUBE_CONTEXT=k3d-rpg-dev NS=rpg-data ./apply.sh
```

The nakama image must exist in the cluster's image store first; k3d does **not**
share Docker's:

```bash
docker build -f backend/deploy/nakama-plugin.Dockerfile -t rpg-mmo/nakama:3.40.0 backend/
docker save rpg-mmo/nakama:3.40.0 | docker exec -i k3d-rpg-dev-server-0 ctr -n k8s.io images import -
```

`postgres:16.4-alpine` and `redis:7.4-alpine` are stock and containerd pulls
them itself. (`docker save` of those two fails on this box — the local store
holds multi-arch manifest lists whose non-native content was never fetched.
`crictl pull` inside the node is the working path if you ever need it offline.)

---

## Storage

**Verified, not assumed:**

```
$ kubectl --context k3d-rpg-dev get sc
NAME                   PROVISIONER             RECLAIMPOLICY   VOLUMEBINDINGMODE      ...
local-path (default)   rancher.io/local-path   Delete          WaitForFirstConsumer   ...
```

So: no `storageClassName` is set on any `volumeClaimTemplate` and all three PVCs
bind to **`local-path`**. Three consequences worth knowing before this leaves a
laptop:

- **`WaitForFirstConsumer`** — the PVC stays `Pending` until its pod is
  scheduled. A `Pending` PVC on a fresh apply is normal, not a fault.
- **`Delete` reclaim policy** — deleting a PVC destroys the data with it. There
  is no snapshot and no backup. `kubectl delete ns` on this namespace deletes
  the PVCs.
- **local-path is node-local `hostPath` underneath.** The volume is a directory
  on one node; the pod can only ever schedule back to that node, and the data
  does not survive that node. Fine for one k3d node, not a production answer —
  a real cluster needs a real StorageClass, and `../../docs/DISASTER-RECOVERY.md`
  is the document that has to be satisfied before either PostgreSQL holds
  anything anyone minds losing.

Bound in practice:

```
persistentvolumeclaim/data-postgres-game-0   Bound   2Gi   RWO   local-path
persistentvolumeclaim/data-postgres-meta-0   Bound   2Gi   RWO   local-path
persistentvolumeclaim/data-redis-0           Bound   1Gi   RWO   local-path
```

---

## The initdb ConfigMap, and what a reused PVC means

`postgres-game` mounts `db/init-gamestate.sql` into
`/docker-entrypoint-initdb.d/`. The postgres entrypoint runs that directory
**exactly once: at container start, only when it has just run `initdb`, i.e.
only when `$PGDATA` was empty.**

For a PVC that is reused — a pod restart, a rescheduling, a rollout, an image
bump, an upgrade — **it does not run at all**, silently. Proven:

```
$ kubectl -n rpg-k8s-data delete pod postgres-game-0 && kubectl wait --for=condition=Ready pod/postgres-game-0
$ kubectl -n rpg-k8s-data logs postgres-game-0 | grep -i initialization
PostgreSQL Database directory appears to contain a database; Skipping initialization
```

So this file is a **first-boot seed and nothing more**, exactly as in compose.
The schema authority is the numbered migrations in
`../../db/migrations/gamestate/`, which the game server applies at boot and CD
applies explicitly beforehand (`../../docs/DATABASE.md`). Do not add tables to
the init script; add a migration.

Two safeguards follow from that:

- the volume is mounted **`optional: true`**, so the tier still comes up with no
  ConfigMap present — you get an empty database and the gameserver's migrator
  builds the schema, which is the same end state by the supported path;
- `apply.sh` creates the ConfigMap **before** applying the StatefulSet. Creating
  it after is a race you usually win, and when you lose it you get an empty
  database with no error anywhere. (The first bring-up here did exactly that and
  won; the ordering was fixed rather than left to luck.)

The ConfigMap is generated by `apply.sh` from `../../db/init-gamestate.sql`
rather than committed here, because kustomize refuses file sources above its
root:

```
security; file '.../deploy/db/init-gamestate.sql' is not in or below '.../k8s/data'
```

and copying the SQL in would make a **third** copy of a schema that two existing
tests already pin to two copies (`MigratorTests.InitGamestateSql_MatchesFirstMigration`
and Go's `TestSchemaMatchesDeployInitScript`). Generating from the original is
the only version that cannot drift.

---

## Redis: `noeviction` is load-bearing, and `maxmemory` is what makes it safe

`redis.conf` carries `appendonly yes`, `appendfsync everysec`, `save 60 1000`,
`maxmemory-policy noeviction` and `maxmemory 128mb`, mirroring the compose flags.

The last one is not tidiness. This Redis is a **system of record** for the server
registry (`servers:*`) and the event stream (`events:*`), not a cache. Evicting a
registry hash removes a live game server from matchmaking with no error emitted
anywhere; trimming a stream drops unacked cross-server events. Sessions are the
only genuinely expendable keys and they carry their own TTL. `noeviction` is
already the Redis default — it is written down so that the `maxmemory` limit
beside it cannot silently convert the registry into an LRU cache (ADR-4).

`maxmemory 128mb` is the other half, and the policy is unsafe without it (#202).
With no ceiling, growth is bounded only by the pod's `limits.memory: 256Mi`,
which the kernel enforces by killing Redis whole — losing sessions, the registry
and the stream in one go, the exact outcome `noeviction` exists to prevent. With
the ceiling, Redis stays up, keeps answering reads, and refuses writes with
`OOM command not allowed when used memory > 'maxmemory'`. **That error is a
capacity signal, not a bug**: read `INFO memory` and `XLEN events:game`, then
raise the pod limit and `maxmemory` together at the same 50% ratio. 128mb is half
of 256Mi because Redis forks for AOF rewrite and RDB, and the gap has to cover
allocator fragmentation plus copy-on-write — the arithmetic is in `redis.conf`.

There is **no `requirepass`**, matching `backend/shared/config`'s default
`REDIS_PASSWORD=""`. Adding auth means adding the directive to `redis.conf`
*and* adding `-a $REDIS_PASSWORD --no-auth-warning` to both probes in
`redis.yaml` — otherwise the probes fail closed and the pod never goes Ready.

---

## Why the plugin is baked into an image

Nakama loads our Go plugin (`nakama.so`) from `/nakama/data/modules`. Compose
host-mounts `deploy/modules/` there. **A host mount does not exist in a
cluster** (ADR-15 decision 3, item 3). The two candidates were an image and an
initContainer; this uses **an image** — `nakama-plugin.Dockerfile`'s existing
`runtime` target, which needed no change.

The reason is ABI locking, not convenience. A Go plugin is bound to the exact Go
toolchain and `nakama-common` version of the server binary it loads into; a
mismatch fails at load with *"plugin was built with a different version of
package …"*. The plugin and the server are therefore **one artifact that happens
to be two files**, and only an image can express that:

- **Image** — `rpg-mmo/nakama:3.40.0` names a server *and* the plugin built
  against it. The pairing is in the tag, `nakama-plugin.Dockerfile` derives the
  builder tag from the same `NAKAMA_VERSION` as the server tag, and an
  ABI mismatch becomes impossible to express rather than something to detect.
- **initContainer** — would fetch or build the `.so` into an `emptyDir` at pod
  start. That reintroduces the exact failure the ABI lock creates: two
  independently-versioned things meeting at runtime, on a pod that is already
  scheduled. It also needs somewhere to fetch from (a registry or an artifact
  store this project does not have) or a Go toolchain in the pod, which is a
  1.27GB builder image on every pod start.

The one real cost is that bumping `NAKAMA_VERSION` is now an image rebuild, not
an edit. That is correct: it *is* a rebuild — the plugin has to be recompiled
either way, and compose already had to rebuild it too.

---

## Addresses

### From inside the cluster (what the game server and gateway pods use)

Same namespace, short name is enough; the FQDN is what to use across namespaces.

| Service | In-cluster address | Notes |
|---|---|---|
| PostgreSQL meta | `postgres-meta:5432` | `postgres-meta.rpg-k8s-data.svc.cluster.local` |
| PostgreSQL gamestate | `postgres-game:5432` | `GAME_DB_URL=postgres://game:localdev@postgres-game:5432/gamestate?sslmode=disable` |
| Redis | `redis:6379` | `REDIS_ADDR=redis:6379` — same value as compose, so app env ports over unchanged |
| Nakama HTTP | `nakama:7350` | `NAKAMA_URL=http://nakama:7350` — also unchanged from compose |
| Nakama gRPC / console / metrics | `nakama:7349` / `:7351` / `:9100` | metrics for a future Prometheus scrape |

The three stores use **headless** Services (`clusterIP: None`): a single-replica
StatefulSet needs no load balancing, and headless also yields the stable per-pod
name `postgres-game-0.postgres-game`, which is what a future replica or a
restore procedure will want. Nakama gets an ordinary ClusterIP because it is a
Deployment that may one day have more than one replica.

Verified:

```
postgres-meta    10.42.0.51
postgres-game    10.42.0.49
redis            10.42.0.50
nakama           10.43.228.243
```

### From the host (local tooling, `psql`, `redis-cli`, the smoke test)

**Via `kubectl port-forward`, on ports that do not collide with the compose dev
stack** (which owns 5432 / 5433 / 6379 / 7349-7351 / 9100 on this box):

```bash
kubectl --context k3d-rpg-dev -n rpg-k8s-data port-forward svc/postgres-meta 15432:5432 &
kubectl --context k3d-rpg-dev -n rpg-k8s-data port-forward svc/postgres-game 15433:5432 &
kubectl --context k3d-rpg-dev -n rpg-k8s-data port-forward svc/redis         16379:6379 &
kubectl --context k3d-rpg-dev -n rpg-k8s-data port-forward svc/nakama        17350:7350 &

PGPASSWORD=localdev psql -h 127.0.0.1 -p 15433 -U game -d gamestate
redis-cli -p 16379 ping
curl -s http://127.0.0.1:17350/healthcheck
```

**Not NodePort, deliberately.** The k3d cluster publishes exactly
`7000-7100` (plus `6550` for the API server):

```
k3d-rpg-dev-serverlb   0.0.0.0:7000-7100->7000-7100/tcp, 127.0.0.1:6550->6443/tcp
```

The default NodePort range (30000-32767) is therefore not reachable from the
host at all, and `7000-7100` is Agones' `MIN_PORT`/`MAX_PORT` range — taking one
for a database would collide with a game server allocation. Port-forward is the
correct tool for host-side access to a data tier regardless; a database wants to
be reachable from the cluster, not from the internet.

This also means the **smoke test and any host-run gateway keep talking to the
compose stack** unless you point them at the forwarded ports explicitly. Nothing
here changes any default.

---

## Namespaces

`../../k3s/namespaces.yaml` reserves `rpg-data` (stores) and `rpg-meta`
(Nakama) for the eventual layout. This stage uses one namespace,
`rpg-k8s-data`, so bring-up cannot collide with `rpg-realtime` — which holds the
live Agones fleet — and so a teardown is a single namespace delete.

Splitting later is `kustomization.yaml`'s `namespace:` plus one detail: once
Nakama is in a different namespace from `postgres-meta`, its `--database.address`
must use the FQDN `postgres-meta.rpg-data.svc.cluster.local`, and so must the
initContainer's. Nothing else is namespace-aware.

## Secrets

`secrets.example.yaml` is a **template, and is no longer applied**. It carries the
same published dev placeholders `docker-compose.yml` defaults to (`localdev`,
`dev-secret-change-me`, `defaultkey`, `defaulthttpkey`), and it is committed as a
shape to copy — never as values to run.

It used to be listed in `kustomization.yaml` as a resource, so that a laptop
bring-up needed no secret-management story. The cost of that convenience was not
obvious: kustomize then **owned** those Secret objects, so every `apply -k data/`
reset whatever the namespace already held. Dev ran on `dev-secret-change-me` from
the day the cluster was built and nothing said so, because the app tier's
`rpg-app-secrets` had been filled with the same placeholder and the two halves
agreed. Staging, given a generated value, came up healthy and rejected every
login with `local jwt verify: invalid signature`.

So the data tier now follows the contract the app tier always had: **create the
Secrets before the first deploy**. `dev-up.sh` applies `namespace.yaml` first so
there is somewhere to put them, then fails with a named list if any of
`postgres-meta`, `postgres-game`, `nakama` is absent — and fails again if
`nakama`'s `JWT_SECRET` does not equal `rpg-app-secrets`' `jwt-secret`.

A laptop bring-up is therefore one extra command, e.g.:

```bash
kubectl --context k3d-rpg-dev apply -f data/secrets.example.yaml   # laptop only
```

with the same caveat as before: those values are published, and belong on a
laptop and nowhere else. For anything shared, generate them —

```bash
JWT=$(openssl rand -hex 32); JOIN=$(openssl rand -hex 32)   # JOIN must differ
```

— and set `nakama`'s `JWT_SECRET` and `rpg-app-secrets`' `jwt-secret` to the SAME
value. **Dev's were rotated off the placeholder on 2026-08-20**; consumers must be
restarted together (`nakama`, `gateway`, and a fleet drain, since a GameServer
reads the secret once at start). The same convention as
`../../agones/secret-example.yaml`.

### `NAKAMA_CONSOLE_PASSWORD`

Rotated on both clusters on 2026-08-20, from the published placeholder `password`
to 48 random hex characters — the length `../../docs/VPS-SETUP.md` already
specifies for a real environment. Different value per cluster, so console access
to one grants nothing on the other.

Only `nakama.yaml` consumes it (`--console.password`); no verify target, workflow
or client reads it, so rotating it needs nothing but a Nakama restart.

**The cluster is the only copy.** To read it:

```bash
kubectl --context k3d-rpg-dev get secret nakama -n rpg-k8s-data \
  -o jsonpath='{.data.NAKAMA_CONSOLE_PASSWORD}' | base64 -d; echo
```

### `NAKAMA_SERVER_KEY`

Rotated on both clusters on 2026-08-20, from `defaultkey` to 32 random hex
characters, a different value per cluster.

Unlike the others this one is a **client-facing contract** — every client presents
it to Nakama — so rotating it needed the consumers lined up first. They already
were, with one exception: everything reads it from an env var or flag with
`defaultkey` merely as a *default* (`verify.sh`, `checks_client.sh`,
`checks_flow.sh`, `probe/main.go`, `smoketest`, `loadtest`, the client's
`BackendCommandLine`, and `run-clients.sh --nakama-key`). The exception was
`verify/targets/k8s-*.env`, which asserted the literal; it now reads the Secret,
and that change was landed **before** the rotation so the two never disagreed.

Read it the same way:

```bash
kubectl --context k3d-rpg-dev get secret nakama -n rpg-k8s-data \
  -o jsonpath='{.data.NAKAMA_SERVER_KEY}' | base64 -d; echo
```

Running a client by hand now needs `--nakama-key`; omitting it does not fail at
launch, it authenticates 401 and looks like a client that never joins. The
client repo's `CLAUDE.md` says so at the invocation.

For a real environment, copy to `secret-*.local.yaml` and add that pattern to
`../../.gitignore` (it currently ignores `agones/secret-*.local.yaml`; extend it
to `k8s/data/secret-*.local.yaml` when the first one is written).

`JWT_SECRET` in the `nakama` Secret is the cross-component one: Nakama signs
client session tokens with `session.encryption_key = $JWT_SECRET`, which is what
lets the gateway verify a client token locally with no Nakama roundtrip (ADR-3).
It **must** equal the gateway's and game server's `JWT_SECRET`. It is *not* the
same as `JOIN_TOKEN_SECRET`, deliberately.

## Health probes

Carried across from the compose healthchecks, with the intent preserved:

| Service | Probe | Why this and not a TCP check |
|---|---|---|
| both PostgreSQL | `pg_isready -U $POSTGRES_USER -d $POSTGRES_DB -h 127.0.0.1` | A listening socket during recovery is not a database that accepts queries. `-h 127.0.0.1` forces the TCP path, which is the one clients use — the unix socket answers earlier. |
| Redis | `redis-cli ping` | Exits non-zero on anything but `PONG`, so the exit code carries what compose's `grep -q PONG` carried. |
| Nakama | `httpGet / :9100` | **The metrics port, not the client port, and that is the whole point.** :7350 changes protocol when meta-hop TLS is on (ADR-24); :9100 never does, so one spec works in both modes. A `startupProbe` (24 x 5s) covers migrate + plugin load so the liveness timer never runs during a cold start. See below. |

Liveness is deliberately slacker than readiness everywhere: readiness takes a
pod out of a Service, liveness kills it, and killing a database mid-recovery
turns a slow start into a crash loop.

### Why Nakama's probe left `/healthcheck`

It used to be `httpGet /healthcheck` on `:7350`, which is the port
`--socket.ssl_certificate` converts to TLS. Measured on k3d-rpg-dev 2026-09-12:
with the meta-hop flag on, all three probes failed with
`client sent an HTTP request to an HTTPS server` and the pod never became Ready
while the container itself was perfectly healthy.

Every other candidate was tried and rejected on a measurement, not a preference:

| Candidate | Why not |
|---|---|
| `scheme: HTTPS` on `:7350` | Right with TLS on, wrong with it off — and off is the default everywhere. Trades a broken opt-in for a broken default, and there is no per-environment overlay to vary it in. |
| exec `/nakama/nakama healthcheck` | Nakama v3.40.0's subcommand is `http.Get("http://localhost:" + port)` — hardcoded plaintext, no TLS branch — so it breaks under TLS in the same way. It also forks a 200MB binary every 10s. |
| exec `curl` / `wget` | The image has neither. `heroiclabs/nakama:3.40.0` is Debian 12 with no `curl`, no `wget`, no `nc`, no `python3` and no `openssl` (measured in the running pod, 2026-09-13). |
| `tcpSocket :7350` | Mode-independent, and answers the wrong question: "is something accepting connections", not "is Nakama serving". A probe that cannot tell a wedged Nakama from a healthy one is worse than the bug it replaces. |

**What the new probe gives up, stated plainly.** `:9100` is a different
`http.Server` in the same process than the client API, so it proves the process
is alive and serving HTTP — not that the client-API mux still answers. The gap
is narrow because the old target was narrow too: Nakama's `/healthcheck` is a
static 200 (`{}` on the wire) that checks no dependency. A certificate the
operator got wrong is *not* in that gap — Nakama cannot read it and exits, and
the pod crash-loops visibly.

**Open item: the wedged API mux, and the automatic restart this cost.** The
paragraph above was first written as "not a regression, because the old target
checked nothing either". True of *dependency* checking, and false of one thing
that matters:

> The old liveness probe would have **restarted the pod** when the `:7350`
> server itself stopped answering — a dead accept loop, a wedged listener —
> because that is exactly what it hit. **The new one will not.** A narrow class
> of failure has lost its automatic recovery.

That is the real cost of this fix. It is the right trade against the four
alternatives in the table, and it is written here so nobody has to re-derive it.

**Nothing else closes it in steady state**, checked rather than assumed:

| Where you might expect to notice | What is actually there |
|---|---|
| the gateway | **not a consumer** — it makes no Nakama call at all, so its health says nothing |
| the C# game server | the only in-cluster consumer of `:7350`; its Nakama failures are `LogWarning` with **no counter and nothing on `/metrics`** |
| monitoring | **no alert rules anywhere** in `deploy/monitoring/` — one scrape config, one dashboard. Nakama's own API counters on `:9100` would flatline visibly, but only to someone already looking |
| `dev-up.sh`, `verify/lib/checks_flow.sh`, smoketest | exercise the hop for real, **only at deploy time** |

So the first notice is **a human, when players cannot authenticate**.

**The cheapest close, named and not built:** a counter on the game server's
Nakama call outcomes — it already distinguishes `Granted` / `Partial` /
`NotGranted` / transport failure — plus an alert on the failure rate. That covers
the wedge *and* a certificate misconfiguration, from the consumer's side, which
is the side that cares whether the hop works.

Compose has the same trap and it was not recorded when the flag landed: its
healthcheck was bare `/nakama/nakama healthcheck`, which defaults to 7350. It is
now `/nakama/nakama healthcheck 9100`, for the same reason and with the same
trade.

## Turning the meta hop's TLS on

ADR-24's flag, off at every deploy path. **Four things move together**; three of
four is a deploy that looks fine and breaks the reward path, or a client that
cannot log in. Everything below is `k3d-rpg-dev`; swap the context for staging.

The certificate is self-signed, and that is fine *because both clients of this
hop pin it* — pinning is stricter than the public trust store, not looser. There
is no accept-anything setting anywhere in this system (ADR-24 decision 4).

**Step 0 — generate the pair.** SANs matter: the game server dials the Service
DNS name, the Unity player dials the host address.

```bash
mkdir -p /tmp/nakama-tls && cd /tmp/nakama-tls
openssl req -x509 -newkey rsa:2048 -nodes -days 365 \
  -keyout tls.key -out tls.crt \
  -subj "/CN=nakama.rpg-k8s-data.svc.cluster.local" \
  -addext "subjectAltName=DNS:nakama,DNS:nakama.rpg-k8s-data.svc.cluster.local,DNS:localhost,IP:127.0.0.1"
openssl x509 -in tls.crt -noout -subject -ext subjectAltName -enddate
```

**Step 1 — the Secret Nakama reads (private key included).**

```bash
kubectl --context k3d-rpg-dev -n rpg-k8s-data create secret tls nakama-tls \
  --cert=tls.crt --key=tls.key
```

**Step 2 — the Secret the game-server pods read (PUBLIC HALF ONLY).** Different
namespace, so it is a second Secret — a Secret cannot cross one, the same reason
`NAKAMA_HTTP_KEY` exists twice. **Do not copy the key into it.**

```bash
kubectl --context k3d-rpg-dev -n rpg-k8s-realtime create secret generic nakama-tls-pin \
  --from-file=tls.crt=tls.crt
```

**Step 3 — set two ConfigMaps. No manifest edit.**

This used to be four file edits and it no longer is. The manifests are applied
**unchanged to dev and staging**, so an edit here turned the flag on in both
clusters or neither — and a required Secret volume wedged every pod in the cluster
that had not opted in. The mounts now ship uncommented with `optional: true`, and
the paths come from ConfigMap keys that are also optional, so **an absent key is
the flag off**. That is the same shape ADR-23's gateway TLS uses, for the same
reason.

```bash
# Nakama's end: where it reads its certificate and key from.
kubectl --context k3d-rpg-dev -n rpg-k8s-data create configmap nakama-config \
  --from-literal=tls-cert-path=/nakama/tls/tls.crt \
  --from-literal=tls-key-path=/nakama/tls/tls.key \
  --dry-run=client -o yaml | kubectl --context k3d-rpg-dev apply -f -

# The game server's end: the URL it dials and the pin it checks the leaf against.
kubectl --context k3d-rpg-dev -n rpg-k8s-realtime patch configmap gameserver-config \
  --type=merge -p '{"data":{
    "nakama-url":"https://nakama.rpg-k8s-data.svc.cluster.local:7350",
    "nakama-tls-pin":"/etc/nakama-tls/tls.crt"}}'
```

The probes need no edit — that is what moving them to `:9100` bought.

> **A fleet update does NOT recreate an Allocated GameServer.** Measured
> 2026-09-12/13: after applying the fleets and patching the ConfigMap, the one
> `Allocated` map server kept running with the old plaintext `NAKAMA_URL` and no
> pin mounted, and **every reward RPC it made failed** with
> `BadRequest code=-1 Client sent an HTTP request to an HTTPS server` while the
> game itself carried on working perfectly. Environment variables are fixed at pod
> creation, so a ConfigMap patch never reaches a running pod either. Delete the
> allocated GameServer (`kubectl delete gs <name>`) and let the fleet replace it,
> or accept that the rollout is not complete until that player leaves.
>
> Nothing alerts on this. It is the steady-state gap ADR-24 §8.1 records, seen
> live.

**Step 4 — apply, and watch the thing that used to fail.**

```bash
KUBE_CONTEXT=k3d-rpg-dev ./apply.sh          # from k8s/data/ -- NOT plain `apply -k`, see the top of apply.sh
kubectl --context k3d-rpg-dev -n rpg-k8s-data rollout status deploy/nakama --timeout=180s
kubectl --context k3d-rpg-dev -n rpg-k8s-data logs deploy/nakama | grep -i "ssl mode"
kubectl --context k3d-rpg-dev apply -f k8s/app/20-configmaps.yaml \
  -f k8s/app/50-fleet-map.yaml -f k8s/app/60-fleet-dungeon.yaml
```

Expect `INFO SSL mode enabled` plus Nakama's own
`WARNING: enabling direct SSL termination is not recommended` — that warning is
expected and is argued with in ADR-24 §4, not ignored.

**Verify each of the three consumers separately**, because none of them implies
another:

```bash
# 1. Nakama itself: https answers, http is refused.
kubectl --context k3d-rpg-dev -n rpg-k8s-data run tlscheck --rm -i --restart=Never \
  --image=curlimages/curl -- \
  sh -c 'curl -sk -o /dev/null -w "https:%{http_code}\n" https://nakama:7350/healthcheck; \
         curl -s  -o /dev/null -w "http:%{http_code}\n"  http://nakama:7350/healthcheck'
# expect https:200 and http:400

# 2. The game server: no certificate error on the reward path.
kubectl --context k3d-rpg-dev -n rpg-k8s-realtime logs -l agones.dev/fleet=fleet-map \
  --tail=200 | grep -E "NakamaTLS|reward|certificate"
# expect "NakamaTLS: pinned to /etc/nakama-tls/tls.crt (sha256:...)"

# 3. The Unity player: hand it the same PEM.
#   Tools/verify-multiclient.sh --exe … --nakama-port 7001 … \
#     -- -cuvara-nakama-scheme https -cuvara-nakama-tls-cert /tmp/nakama-tls/tls.crt
```

**Handing the pin to a client** is the whole client-side story: copy `tls.crt`
to the machine running the player and pass `-cuvara-nakama-tls-cert <path>`
alongside `-cuvara-nakama-scheme https` (or `CUVARA_NAKAMA_TLS_CERT` /
`CUVARA_NAKAMA_SCHEME`, or the `backend.env` file on Android). The client
compares the certificate Nakama presents against that file byte for byte and
refuses anything else. Without the flag the player fails every request with
`Curl error 60: Cert verify failed … UnityTls error code: 7`, which is the
correct refusal, not a bug.

### The same thing under docker compose

```bash
cd backend/deploy
mkdir -p tls && cd tls        # a Windows-visible path, NOT /tmp -- see the note in .env.example
openssl req -x509 -newkey rsa:2048 -nodes -days 365 -keyout tls.key -out tls.crt \
  -subj "/CN=nakama" -addext "subjectAltName=DNS:nakama,DNS:localhost,IP:127.0.0.1"
cd ..
```

Mount the pair into the `nakama` service (a `./tls:/nakama/tls:ro` volume) and
into the gameserver service, then:

```bash
NAKAMA_TLS_CERT=/nakama/tls/tls.crt \
NAKAMA_TLS_KEY=/nakama/tls/tls.key \
NAKAMA_URL=https://nakama:7350 \
NAKAMA_TLS_PIN=/nakama/tls/tls.crt \
  ./stack.sh up
```

All four names are in `STACK_OVERRIDABLE`, so a `.env` on disk does not clobber
them. The compose healthcheck already probes `:9100` and needs no change.

**What this does NOT cover**, and must not be described as covered: the Nakama
console `:7351` and metrics `:9100` stay plaintext (ADR-24 §4), the
Nakama→Postgres DSN still specifies no `sslmode`, and the session token still
travels in the `/ws` query string where TLS protects the wire but not Nakama's
own access log.

**Turning it back off** is the same four files in reverse plus a rollout; the
two Secrets can stay, they are inert while the paths are empty.

## Verification

The bring-up proofs — pods Ready, both databases reachable with the right
credentials, `player_states` present, `PING` + `CONFIG GET maxmemory-policy`,
Nakama `/healthcheck`, and the `gateway_token` RPC returning a token signed with
`JWT_SECRET` (proving the plugin is loaded, not merely that the process started)
— are reproducible with the commands in this file. The plugin check specifically:

```bash
TOKEN=$(curl -sS -u 'defaultkey:' -H 'Content-Type: application/json' \
  -d '{"id":"proof-0001"}' \
  'http://127.0.0.1:17350/v2/account/authenticate/device?create=true' \
  | python3 -c 'import sys,json;print(json.load(sys.stdin)["token"])')

# NOTE the payload is a JSON *string*, not an object: --data-raw '"{}"'.
# Sending '{}' returns 400 "cannot unmarshal object into Go value of type string".
curl -sS -H "Authorization: Bearer $TOKEN" --data-raw '"{}"' \
  'http://127.0.0.1:17350/v2/rpc/gateway_token'
```

A 200 with a `payload` proves the plugin registered the RPC; an unregistered RPC
name returns 404, which is the control worth running alongside it.
