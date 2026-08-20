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

## Redis: `noeviction` is load-bearing

`redis.conf` carries `appendonly yes`, `appendfsync everysec`, `save 60 1000`
and `maxmemory-policy noeviction`, mirroring the compose flags.

The last one is not tidiness. This Redis is a **system of record** for the server
registry (`servers:*`) and the event stream (`events:*`), not a cache. Evicting a
registry hash removes a live game server from matchmaking with no error emitted
anywhere; trimming a stream drops unacked cross-server events. Sessions are the
only genuinely expendable keys and they carry their own TTL. `noeviction` is
already the Redis default — it is written down so that adding a `maxmemory`
limit later cannot silently convert the registry into an LRU cache (ADR-4).

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

`NAKAMA_SERVER_KEY` is still `defaultkey` and was left alone deliberately: it is a
client-facing contract, hardcoded in `verify/targets/*.env` and defaulted in the
Unity client's `BackendCommandLine`, so moving it is a coordinated change across
both repositories rather than a server-side rotation.

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
| Nakama | `httpGet /healthcheck :7350` | Identical to what `/nakama/nakama healthcheck` does internally, without forking a 200MB binary every 10s. A `startupProbe` (24 x 5s) covers migrate + plugin load so the liveness timer never runs during a cold start. |

Liveness is deliberately slacker than readiness everywhere: readiness takes a
pod out of a Service, liveness kills it, and killing a database mid-recovery
turns a slow start into a crash loop.

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
