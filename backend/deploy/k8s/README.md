# Dev on k3s/Agones

The dev environment runs **entirely** inside the k3d cluster `k3d-rpg-dev`: the
data tier, Nakama, the gateway and the Agones map fleet are all k8s workloads
that reach each other over cluster DNS. No docker compose container is in the
path and nothing is addressed by `host.k3d.internal`.

That last point is the whole reason this exists. Dev used to be a hybrid — game
servers under Agones, everything else in compose, reached from the pods by the
`host.k3d.internal` alias. ADR-15 decision 4 calls that the worst place to stop,
because the alias is exactly what makes the deployment work on one machine and
nowhere else.

| Namespace | What is in it |
|---|---|
| `rpg-k8s-data` | `postgres-meta`, `postgres-game`, `redis` (StatefulSets, one PVC each), `nakama` |
| `rpg-k8s-realtime` | `gateway` Deployment + NodePort/ClusterIP Services, `map-servers-dotnet-k8s` Fleet |
| `rpg-realtime` | The **pre-cutover** fleet. Scaled to 0. Kept as the rollback target |

Manifests: `data/` (kustomize) and `app/` (numbered, applied in order). Read
`app/README.md` for the app tier's own design notes.

## Bring dev up

```bash
cd backend/deploy/k8s
./dev-up.sh
```

Idempotent, and the same script CD runs in `DEPLOY_MODE=k8s`, so the box and CI
cannot drift apart. It imports images into the k3d node, applies both tiers,
pins the image tags, retires the legacy fleet and the compose stack, and starts
the port-forwards.

The Secret is **not** in the repo. `app/30-secret-template.yaml` is a template;
fill a copy outside the tree and apply it before the first run.

### Ports

k3d's serverlb publishes only `7000-7100` (the Agones host-port range) and
`6550`. The gateway's NodePort lands in 30000-32767 and Nakama has no node port
at all, so `dev-up.sh` port-forwards them, bound to `0.0.0.0` so the Unity
Editor on Windows reaches them as well as WSL2:

| Host port | Reaches |
|---|---|
| 8000 | `svc/gateway` — what the client dials first |
| 7350 / 7349 | `svc/nakama` HTTP / gRPC |
| 15433 | `svc/postgres-game` — for the suite's persistence assertions |
| 7000-7100 | Agones-assigned game-server ports, published by k3d directly |

The forwards are supervised only by their pidfiles under
`${RPG_K8S_RUN_DIR:-/tmp/claude-1000/rpg-k8s-dev}`. Re-run `dev-up.sh` to
restore any that died; it checks each socket actually accepts a connection and
fails loudly rather than leaving a live-looking pidfile behind an unbound port.

### Why the advertised address is `127.0.0.1`

The gateway hands `ServerAddr` to the client verbatim (ADR-3), and under
`portPolicy: Dynamic` the server composes `GAMESERVER_ADVERTISE_HOST` with the
Agones-assigned port. Here that host is `127.0.0.1`, and that is correct rather
than a shortcut: `status.address` is the node address `172.20.0.3`, which is on
the k3d docker network and is **not** dialable from WSL2 or from Windows —
measured, not assumed — while `127.0.0.1:<agones port>` is, because the serverlb
publishes the whole range onto the host.

On any cluster where the client is not on the node this must change, and
`VERIFY_ADDR_ALLOW_LOOPBACK` in the verify target must go back to `0`.

## Verify

```bash
cd backend/deploy/k8s/verify
JWT_SECRET="$(kubectl --context k3d-rpg-dev get secret -n rpg-k8s-realtime \
  rpg-app-secrets -o jsonpath='{.data.jwt-secret}' | base64 -d)" \
  ./verify.sh --target k8s-dev
```

`./verify.sh --list` explains what each check proves *and what it cannot*. Two
checks do not run on this deployment and both are deliberate:

* **`client.playmode`** needs a Unity PlayMode NUnit XML. The suite never
  launches Unity; pass `--unity-results <abs path>` after a run.
* **`refusal.unknown_map`** costs one GameServer that is never reclaimed, and on
  a `replicas: 1` fleet that consumes the fleet. See the ADR-2 note below for why
  the fleet cannot simply be made larger.

### Why the fleet is `replicas: 1`

The C# server self-registers at **startup**, not on allocation, and every
replica in this fleet carries the same `GAMESERVER_MAP_ID`. A second replica is
therefore a second live server for `map_01` — ADR-2's split world. The
consequence is that a fleet whose one pod is Allocated reports `ready=0`, which
`cluster.fleet` warns about; that warning is expected here. Scaling the fleet
needs per-replica map assignment first, which does not exist yet.

## Roll back

```bash
cd backend/deploy/k8s
./rollback-to-compose.sh
```

Puts dev back on the pre-cutover hybrid stack in a couple of minutes and
**destroys nothing** — every PVC, every compose volume and every compose
container survives, so `dev-up.sh` reverses it. It drains the k8s fleet, scales
the in-cluster tiers to zero, restarts the compose containers and restores the
`rpg-realtime` fleet, then waits until a server has actually registered an
address for `map_01` before reporting success.

Verify the rolled-back stack with the *other* target:

```bash
JWT_SECRET=... ./verify/verify.sh --target dev-agones
```

Give it a minute first. Registration and the Redis heartbeat both lag the pod
becoming Ready, and a suite run started immediately after the rollback sees a
registry entry with an empty `addr` and reports a failure that cures itself.

## Two things that will bite

**Scaling a Fleet to 0 does not remove an `Allocated` GameServer.** Agones
treats allocated as in use. Scaling alone leaves the pod running *and* its
registry entry live, so "the fleet is retired" can be false while the old server
still serves the map — and an image change silently does not take, because the
old pod is never replaced. Both scripts use `drain_fleet`, which scales to zero
and then deletes the GameServers explicitly. The delete is a graceful pod
termination, so the server's SIGTERM path **deregisters** itself instead of
leaving an entry to expire on the 15s heartbeat TTL — the window in which the
gateway hands a client the address of a server that is gone (ADR-2).

**`:develop` is a moving tag and lags the branch.** It is retagged by hand; at
cutover the cluster ran `develop-307f1e8` while `develop` was at `b633aff`, so
the deployment under test was not the commit under test. `dev-up.sh` resolves
`rpg-mmo/*:${GIT_SHA}` instead, and CD asserts the running gateway carries the
commit it is deploying before it trusts the suite.

## CD

A push to `develop` deploys this, not the compose stack, when the dev GitHub
Environment sets `vars.DEPLOY_MODE=k8s`. The job builds the images on the
runner, tags them `${GITHUB_SHA}`, runs `dev-up.sh`, then runs
`verify.sh --target k8s-dev` as the healthcheck — a FAIL fails the deploy.

Staging and production are untouched: they select `containers`/`host` through
their own `vars.DEPLOY_MODE` and never enter this path.
