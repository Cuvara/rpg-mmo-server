# App tier on Kubernetes — gateway + map fleet in `rpg-k8s-realtime`

The Go gateway as a Deployment with the RBAC it needs to allocate, and an
Agones Fleet of C# game servers, both inside the cluster. It is the second
half of moving the whole system to k3s/Agones; the data tier (Redis, both
PostgreSQL instances, Nakama) belongs in `rpg-k8s-data` and is owned
elsewhere.

**This does not replace anything.** `backend/deploy/agones/` holds the live
dev fleet in namespace `rpg-realtime`, allocated from by the compose-run
gateway; it is untouched. The deploy path for dev/staging/prod is still
`DEPLOY_MODE=containers` (ADR-16: "Agones is proven, not adopted").

Read first: ADR-16 (what shipped and how it was proven), ADR-15 decision 3
(the six prerequisites), ADR-14, ADR-3, ADR-2, ADR-1.

## Files

| File | What it is |
|---|---|
| `00-namespace.yaml` | `rpg-k8s-realtime`. Gateway **and** fleet live here — the allocation Role is namespaced, so co-locating them is what keeps it narrow |
| `05-agones-sdk-rbac.yaml` | `agones-sdk` SA + RoleBinding. **Required in every GameServer namespace**; Agones' Helm chart only created it in `rpg-realtime` |
| `10-rbac.yaml` | `rpg-gateway` SA + Role + RoleBinding — `create` on `gameserverallocations`, and nothing else |
| `20-configmaps.yaml` | Addresses only: data-tier DNS, and the game server's advertise host |
| `30-secret-template.yaml` | Template. `jwt-secret`, `join-token-secret` (**different values**), `redis-password`, `game-db-url`, `transport-key` |
| `40-gateway.yaml` | Gateway Deployment + NodePort Service (client) + ClusterIP Service (metrics) |
| `50-fleet-map.yaml` | `map-servers-dotnet-k8s` Fleet, `replicas: 1`, dynamic port, health on, no autoscaler |
| `proof/*` | Scaffold Redis and a ConfigMap override, for bringing this tier up before `rpg-k8s-data` exists. **Not the data tier** — with that namespace present, skip both files |

## Apply

```bash
K="kubectl --context k3d-rpg-dev"

# Images: k3d does NOT share the host docker image store.
docker save rpg-mmo/gateway:develop            | docker exec -i k3d-rpg-dev-server-0 ctr -n k8s.io images import -
docker save rpg-mmo/gameserver-dotnet:develop  | docker exec -i k3d-rpg-dev-server-0 ctr -n k8s.io images import -
# (`k3d image import` fails on this box — Docker Desktop uses a named pipe.)

$K apply -f 00-namespace.yaml -f 05-agones-sdk-rbac.yaml -f 10-rbac.yaml -f 20-configmaps.yaml
# Secret: fill a copy OUTSIDE the repo, or use `kubectl create secret generic`.
$K apply -f /tmp/rpg-app.secret.yaml
# ONLY while rpg-k8s-data does not exist (skip both once it does):
$K apply -f proof/redis-scaffold.yaml -f proof/configmaps-scaffold.yaml
$K apply -f 40-gateway.yaml -f 50-fleet-map.yaml
```

## How a client reaches this tier

Two hops, and now **one mechanism** for both — which is the point of the
current shape.

**Hop 2, the game server.** The client dials it directly (ADR-3). Agones
assigns a dynamic host port and the k3d serverlb publishes `0.0.0.0:7000-7100`
to the host — verified with `docker ps`, not assumed — so
`127.0.0.1:<agones port>` is genuinely dialable from Windows and WSL2, which is
why `advertise-host: 127.0.0.1` is correct here.

**Hop 1, the gateway.** Reached the same way: a `hostPort` inside that same
published range. The default NodePort range is 30000-32767 and the serverlb
publishes nothing in it, so a NodePort Service is allocated and unreachable —
it prints in `kubectl get svc` as `8000:32276/TCP` and looks exactly like the
client's route while being a dead end. The Service is therefore ClusterIP now,
and the client path is the pod's hostPort.

| Host port | Reaches |
|---|---|
| 7000 | gateway (`hostPort`, containerPort 8000) |
| 7001 | Nakama HTTP (`hostPort`, containerPort 7350) |
| 7010-7100 | Agones GameServers |

**The collision is handled by splitting the range, not by hoping.** 7000-7100
is Agones' allocation range, and the allocator does not know about a hostPort it
did not assign — a collision leaves a GameServer Pending, intermittently and
maddeningly. So the Agones controller runs with `MIN_PORT=7010`, reserving
7000-7009 for infrastructure. `dev-up.sh` establishes that floor and refuses to
continue without it: changing the hostPorts without the floor, or the floor
without the hostPorts, brings the collision straight back.

`k3d cluster edit --port-add` remains rejected — it recreates the serverlb
container and so interrupts the published 7000-7100 range the fleet's clients
are using.

**What this costs.** A `hostPort` pins the pod to a node and allows one replica
per node. On this single-node k3d that is free. On a multi-node cluster it is
the same "works only here" trap as a loopback advertise-host, and the answer
there is a LoadBalancer Service or an Ingress — at which point the hostPorts
disappear and the `gateway` Service becomes what clients reach.

A `kubectl port-forward` survives for **postgres-game only**, so the
verification suite can assert persistence from the host. It is a test-runner
convenience, clearly labelled, and no client and no part of the deployment
depends on it.

## Proof (2026-08-18, cluster `k3d-rpg-dev`, namespace `rpg-k8s-realtime`)

Images: `rpg-mmo/gateway:develop` (gateway images carry **no**
`org.opencontainers.image.revision` label — the usual check returns an empty
string), `rpg-mmo/gameserver-dotnet:develop` (revision `307f1e8`, three
deploy-script commits behind `develop`'s `2ec7ebb`).

Wired to the **real** data tier in `rpg-k8s-data` — Redis, PostgreSQL
(`postgres-game`) and Nakama — not to the scaffold. `jwt-secret` is the same
value as `rpg-k8s-data/nakama`'s `JWT_SECRET`, which is the cross-namespace
contract that makes a Nakama-issued client token verifiable at the gateway;
`join-token-secret` is a different value (ADR-8).

```
$ kubectl get pods,gs -n rpg-k8s-realtime
pod/gateway-5ccfdcd7bb-48gm2             1/1   Running
pod/map-servers-dotnet-k8s-wlvw4-9j4vl   2/2   Running
gameserver.agones.dev/map-servers-dotnet-k8s-wlvw4-9j4vl   Ready   172.20.0.3   7017
```

Gateway picked up the in-cluster ServiceAccount with no kubeconfig, and the
in-cluster Redis:

```
"msg":"using redis backend","addr":"redis.rpg-k8s-data.svc.cluster.local:6379"
"msg":"agones allocator enabled","namespace":"rpg-k8s-realtime",
"fleet_map":"map-servers-dotnet-k8s","fleet_dungeon":"(unconfigured)","transport":"tcp"
```

The game server composed the address rather than advertising its listen value,
and took the PostgreSQL player store rather than the in-memory one:

```
using postgres player store (postgres://game:****@postgres-game.rpg-k8s-data.svc.cluster.local:5432/gamestate)
Advertising 127.0.0.1:7017 (host from GAMESERVER_ADVERTISE_HOST, port 7017 from
  Agones status); configured value ':9000' not used
Registered map-servers-dotnet-k8s-wlvw4-9j4vl in Redis: map=map_01
  addr=127.0.0.1:7017 transport=tcp capacity=100 ttl=15s
```

Registry entry (`HGETALL servers:id:map-servers-dotnet-k8s-wlvw4-9j4vl`, read
from `rpg-k8s-data/redis-0`) — host-qualified, server id == GameServer name:

```
server_id  map-servers-dotnet-k8s-wlvw4-9j4vl
map_id     map_01
addr       127.0.0.1:7017
transport  tcp
capacity   100
player_count 0
```

RBAC, sufficient and bounded (`kubectl auth can-i --as=system:serviceaccount:rpg-k8s-realtime:rpg-gateway`):

```
create gameserverallocations.allocation.agones.dev -n rpg-k8s-realtime  yes
create gameserverallocations.allocation.agones.dev -n rpg-realtime      no   <- cannot touch dev
list|get|delete gameservers.agones.dev -n rpg-k8s-realtime              no
create fleets.agones.dev -n rpg-k8s-realtime                            no
get secrets|pods -n rpg-k8s-realtime                                    no
```

And the grant is not merely non-403 — a real allocation posted **as that SA**
returns an allocated server:

```
$ kubectl create -f alloc.yaml -n rpg-k8s-realtime \
    --as=system:serviceaccount:rpg-k8s-realtime:rpg-gateway
gameServerName: map-servers-dotnet-k8s-wlvw4-9gfjg
ports: [{name: game, port: 7056}]
address: 172.20.0.3        # <- the NODE address: this is what is NOT dialable
state: Allocated
```

### The full client flow, in strict-address mode

`smoketest` run from the host against this tier, with the **game server
dialled directly** at the address the gateway advertised.

> **Historical transcript.** This run predates the move to published
> hostPorts: it reached Nakama and the gateway through port-forwards on
> 17350/18000. The current route is 7001/7000 with no forward; see
> "How a client reaches this tier" above. The transcript is kept because what
> it proves — the strict-address flow end to end — is unchanged by how the
> first two hops were reached. `--strict-addr` forbids the harness from rewriting a
listen-style address to loopback, which is what makes this evidence rather
than decoration: without it the harness silently repairs the exact defect the
advertise-host composition exists to fix.

```
PASS  nakama_health               5ms  http://127.0.0.1:17350/healthcheck
PASS  device_auth                10ms  device_id=smoketest-6ac1cedf30e0959d
PASS  gateway_token_rpc           3ms  user_id=ae87841c-d367-4212-bd72-991b6a2b2ad2
PASS  gateway_auth                7ms  transport=tcp map=map_01 server=127.0.0.1:7017 (tcp)
PASS  gameserver_join          1.109s  snapshots=15 (keyframes=1 deltas=14) final_x=4.83 ack_tick=10
PASS  nakama_account              6ms  user=ae87841c... devices=1
PASS  nakama_profile              5ms  player/profile level=1
PASS  gamestate_migrations       13ms  version=1 (001_init) applied=2026-08-18T03:40:41Z
PASS  gamestate_player_row    24.085s  map=map_01 x=4.8333 hp=100/100 (25 polls)
PASS  gamestate_reload         9.029s  respawned at x=4.8333 from persisted x=4.8333
SMOKE=PASS
```

### Reachability from the host

A bare connect proves nothing on k3d — the serverlb accepts on **every** port
in 7000-7100 and only then fails upstream — so each dial writes a byte and
waits for a reply:

```
127.0.0.1:7017 (game server)      connect+write, held open, no EOF (timeout)  <- real listener
127.0.0.1:7099 (unmapped port)    connect, immediate EOF                      <- control
127.0.0.1:7000 (gateway hostPort) connect+write, held open, no EOF (timeout)
gateway /healthz -> ok    /readyz -> ready
```

## What is NOT proven

* **The gateway's own allocation path never fired.** Fleet pods self-register
  at boot, so `map_01` always has a live server and `FindServer` finds it
  without allocating (ADR-16 records this as an open question). The allocation
  was proven by posting one directly as the ServiceAccount.
* **The client hop a real deployment would use is still untested.** The
  gateway IS now reachable from the host — hostPort 7000, no forward — but
  that is a k3d-shaped answer that depends on the serverlb publishing
  7000-7100. A LoadBalancer or Ingress on a node's public address, which is
  what a real cluster would use, has never been exercised.
* **Nothing was tested under load, and nothing was tested with more than one
  player, one map or one gateway replica.** In particular the two-replica
  allocation race ADR-16 describes is untried.
* **Nothing about capacity.** ADR-7's per-server ceiling is still unknown and
  is not measurable on this box.
