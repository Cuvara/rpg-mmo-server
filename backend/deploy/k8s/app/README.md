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

Two hops, and they are reached in **two different ways** — which is the
uncomfortable part of running the gateway inside k3d.

**Hop 2 first, because it is the one that works properly.** The client dials
the game server directly (ADR-3). Agones assigns a dynamic host port from
`MIN_PORT..MAX_PORT` = **7000-7100**, and the k3d serverlb container publishes
exactly `0.0.0.0:7000-7100` to the host — verified with `docker ps`, not
assumed. So `127.0.0.1:<agones port>` is genuinely dialable from Windows and
WSL2, and that is why `advertise-host: 127.0.0.1` is correct here.

**Hop 1, the gateway, has no such published port.** The default NodePort range
is 30000-32767 and the serverlb publishes **nothing** in it — only 7000-7100
and 6550->6443. On this box, use a port-forward:

```bash
kubectl --context k3d-rpg-dev port-forward -n rpg-k8s-realtime svc/gateway 18000:8000
# client then dials 127.0.0.1:18000
```

The two rejected alternatives, and why:

* **`hostPort` in 7000-7100** would be reachable — that range *is* published.
  But it is Agones' range, and the Agones port allocator does not know about a
  hostPort it did not assign. A collision leaves a GameServer pod
  unschedulable and Pending — including a **dev** GameServer in
  `rpg-realtime`, on the same single node. Borrowing from Agones' range to
  expose a non-Agones service can wedge the live dev fleet.
* **`k3d cluster edit --port-add`** recreates the serverlb container, i.e.
  interrupts the published 7000-7100 range the dev fleet's clients use.

**On a real single-node k3s node** neither problem exists: the gateway's
NodePort (or a LoadBalancer/ingress in front of it) is dialable at the node's
public address with the port open in the firewall, and 7000-7100/tcp is opened
alongside it for the game servers.

### The advertise host

`GAMESERVER_ADVERTISE_HOST` is the **host half only**; the port is always the
Agones-assigned one, read back from the sidecar's GameServer status (ADR-16
decision 2).

| Where | Value | Why |
|---|---|---|
| k3d (here) | `127.0.0.1` | the serverlb publishes the port range to the host loopback; `status.address` is `172.20.0.3`, the node **container's** docker-network address, not dialable by a client |
| real single-node k3s | the node's **public** IP or DNS name | `status.address` would be the node's private/internal IP — the right shape, the wrong network |
| Docker Desktop k8s | *(empty — and it still does not work)* | Kubernetes `hostPort` is never published to the host there at all (ADR-16 decision 1) |
| multi-node | **no correct value exists** | the right host differs per pod; the answers are an ingress or `status.hostIP` via the downward API, and neither exists in this repo |

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

`smoketest` run from the host against this tier — Nakama and the gateway
through port-forwards, the **game server dialled directly** at the address the
gateway advertised. `--strict-addr` forbids the harness from rewriting a
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
127.0.0.1:18000 (gw port-forward) connect+write, held open, no EOF (timeout)
gateway /healthz -> ok    /readyz -> ready
```

## What is NOT proven

* **The gateway's own allocation path never fired.** Fleet pods self-register
  at boot, so `map_01` always has a live server and `FindServer` finds it
  without allocating (ADR-16 records this as an open question). The allocation
  was proven by posting one directly as the ServiceAccount.
* **The gateway is not reachable from the host without a port-forward** on
  k3d, for the reasons above. The client hop that a real deployment would use
  (NodePort / LoadBalancer on the node's public address) is untested here
  because no port outside 7000-7100 is published.
* **Nothing was tested under load, and nothing was tested with more than one
  player, one map or one gateway replica.** In particular the two-replica
  allocation race ADR-16 describes is untried.
* **Nothing about capacity.** ADR-7's per-server ceiling is still unknown and
  is not measurable on this box.
