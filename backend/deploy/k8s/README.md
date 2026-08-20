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
| `rpg-k8s-realtime` | `gateway` Deployment (1 replica, `hostPort` 7000, `strategy: Recreate`) + two ClusterIP Services, `map-servers-dotnet-k8s` Fleet |
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
the published host ports.

The Secret is **not** in the repo. `app/30-secret-template.yaml` is a template;
fill a copy outside the tree and apply it before the first run.

### Ports

k3d's serverlb publishes `7000-7100` (per port) and `6550->6443`, and nothing
in the default NodePort range `30000-32767`. So the client-facing ports are
**hostPorts inside the published range** — the same mechanism Agones already
uses to make a GameServer dialable at `127.0.0.1:<port>`, which means one
exposure mechanism in this deployment rather than two.

The range is **split**, not borrowed from:

| Host port | Reaches | How |
|---|---|---|
| 7000 | gateway | `hostPort` on the gateway pod |
| 7001 | Nakama HTTP | `hostPort` on the Nakama pod |
| 7010-7100 | Agones GameServers | Agones dynamic allocation |
| 15433 | postgres-game | port-forward, **test runner only** |

`dev-up.sh` pins the Agones controller to `MIN_PORT=7010`, reserving
`7000-7009` for infrastructure so the allocator can never hand a GameServer the
gateway's port. It establishes that floor rather than assuming it, and refuses
to continue without it — changing one without the other brings the collision
back as an intermittent unschedulable GameServer.

Only Nakama's **HTTP** port is published. `NetworkBootstrapConfig.cs` in the
netcode package reads `CUVARA_NAKAMA_HOST`, `CUVARA_NAKAMA_PORT` and
`CUVARA_NAKAMA_SCHEME` and dials HTTP; gRPC (7349) and the console (7351) stay
cluster-internal, and the console especially should not be on the host.

**Nothing supervises the client path.** There is no forward to keep alive and
no shell to keep open — that is the point. The one remaining `kubectl
port-forward`, for `postgres-game` on 15433, exists so the verification suite's
persistence assertions can run *from this host*; no client uses it and the
deployment does not depend on it.

**What a hostPort costs.** It pins the pod to a node and allows one replica per
node. On this single-node k3d that costs nothing. On a multi-node cluster it is
the same "works only here" trap as a loopback advertise-host — the gateway
would be reachable only on whichever node scheduled it. The real-cluster answer
is a LoadBalancer Service or an Ingress, at which point the hostPorts go away
and the `gateway` Service becomes what clients actually reach.

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

Running it by hand needs `go` on PATH, because `probe/` and `backend/smoketest`
are built from source when `VERIFY_PROBE_BIN` / `VERIFY_SMOKETEST_BIN` are unset.
CD does not: it exports both at binaries built on `ubuntu-latest` and shipped in
the deployment bundle, because the deploy runner has no Go toolchain and should
not need one. See `verify/README.md`.

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
therefore a second live server for `map_01` — ADR-2's split world.

Measured on k3d, 2026-08-18: scaling this fleet `1 -> 2` put the new pod into
`Ready` after **5.38 s**, and `servers:map:map_01` held **two** members within a
second of that, with **no allocation involved**. `registry.FindServer` then
returns the *least loaded* of the two — the unallocated spare — so live players
are handed a pod Agones may delete on the next scale-down. The single replica is
load-bearing for ADR-2, not a capacity dial.

The consequence is that a fleet whose one pod is Allocated reports `ready=0`.
That is the **correct** steady state here, and `cluster.fleet` says so rather
than warning about it; `cluster.autoscaler` **fails** if a `FleetAutoscaler`
ever appears on this fleet. Scaling the fleet needs per-replica map assignment
first, which does not exist yet — see **ADR-18**.

### Why `EnterWorld` waits on one branch and refuses on the others

The five conditions, the message each one sends the client and the `file:line`
for every path are tabulated in **"What `EnterWorld` does when no server is
ready, and what the client is told"** in this file. Two things that table
records but does not argue, kept here because the autoscaler decision (ADR-18)
turns on both:

**Waiting, not refusing, is the right answer when a pod has been allocated and
has not registered yet.** Allocation is single-flight per `map_id`
(`allocateOnce`), so a hundred clients arriving at one unserved map produce one
pod and one wait, not a hundred allocations — which matters because Agones has
no un-allocate and this gateway has no `Deallocate`, so every surplus allocation
is a pod leaked for good. The 15 s bound is what makes the wait safe rather than
merely kind: it sits below `server.MaxHandlerBlockingWait`, past which the wait
starves the connection's own heartbeat and the gateway drops the very client it
is waiting for. That ceiling is **derived, not a constant** —
`pongTimeout - pingInterval` (`connection.go:148`), 20 s at today's 30 s/10 s —
so changing the ping cadence moves it, and `cmd/gateway` refuses to start above
it rather than letting the two drift apart silently.

**The one refusal that was wrong has been fixed, and not here.** When the fleet
has no `Ready` GameServer at all the allocation used to fail into the *terminal*
`no server available for map`, even though a replacement pod is typically
seconds from `Ready` — the only branch whose disposition did not match its
condition. #157 gives that case its own retryable message,
`all servers busy, retry shortly`. It was a gateway change, not a deploy-time
one, and the deploy side is unchanged by it: nothing here needs a buffer, a
longer wait or an autoscaler as a result.

The reason the terminal answer existed at all was that every retry allocates and
Agones has no un-allocate, so a retry loop leaks a pod per attempt. That reason
does not apply to this one branch, which is why it could be changed alone: the
allocation API's `UnAllocated` answer is a decoded 2xx body stating that **no**
GameServer was handed out. The retry costs one allocation POST and no pod, and
the retry that finally succeeds usually costs neither, because a `Ready` pod
self-registers into Redis before any allocation (ADR-18) — so the next
`EnterWorld` resolves it on the registry path with no allocator call. The fix is
deliberately keyed on that sentinel alone (`registry.ErrNoCapacity`); every other
allocation failure may have allocated a pod whose response was lost, and those
keep the terminal answer. The other refusals earn theirs unchanged: *all servers
full* under ADR-2, since allocating there would register a second live server for
one `map_id` and split the world, and *no fleet serves this map* since no number
of retries makes a fleet host a different map.

**Note what none of this is.** No branch of `EnterWorld` waits on *pod startup* —
including the one #157 made retryable, which still refuses in milliseconds and
leaves the backoff to the client. The only wait `EnterWorld` performs begins
after an allocation has already succeeded. That is why a buffer of Ready pods
removes no wait from the join path, and why ADR-18 refuses one.

## Availability posture: one replica of everything, and what a rollout costs

Kubernetes is providing **scheduling and lifecycle** here, not redundancy. Every
workload in this deployment runs `replicas: 1`, and two of them cannot be given a
second replica without answering a question first. Read this before writing
"runs on k8s" anywhere that implies availability. The decision is recorded as
**ADR-17** in `backend/docs/ARCHITECTURE-DECISIONS.md`.

| Workload | Kind | Replicas | Owns | What a restart costs |
|---|---|---|---|---|
| `gateway` (`rpg-k8s-realtime`) | Deployment, `strategy: Recreate` | 1 | Nothing durable — sessions and the registry are in Redis | **No client can authenticate or `EnterWorld` until it is back.** In-progress sessions are unaffected (ADR-3) |
| `nakama` (`rpg-k8s-data`) | Deployment, `strategy: Recreate` | 1 | Accounts, economy, leaderboards, and the `gateway_token` RPC | **No new `gateway_token`, so no new joins.** JWTs already issued stay valid until expiry |
| `redis` (`rpg-k8s-data`) | StatefulSet, 1 PVC | 1 | Sessions (TTL), server registry `servers:*`, event stream `events:*` — **not a cache**, `noeviction` (ADR-4) | Joins fail while it is down; gameplay is untouched, and each game server repairs its own registry entry on the next heartbeat |
| `postgres-game` (`rpg-k8s-data`) | StatefulSet, 1 PVC | 1 | `player_states` — authoritative player position/HP, written only by the game server (ADR-1) | Gameplay continues and the 30s save sweep **fails silently into a log line and `gameserver.player.saves{status="error"}`**; new players cannot load saved state |
| `postgres-meta` (`rpg-k8s-data`) | StatefulSet, 1 PVC | 1 | Nakama's own database — accounts, storage, leaderboards. Migrated by Nakama, never by us | Nakama cannot authenticate |
| `map-servers-dotnet-k8s` | Agones Fleet | 1 | The live server for `map_01` | Everyone on the map drops. The map is unjoinable, refused in milliseconds rather than queued. See [Why the fleet is `replicas: 1`](#why-the-fleet-is-replicas-1), and #151 for what would actually unlock more than one |

Blast radii per dependency, with measured RTO/RPO numbers, are in
[`../docs/DISASTER-RECOVERY.md`](../docs/DISASTER-RECOVERY.md). This section is
about the one failure mode that document does not cover, because it is not a
failure: it happens **on purpose, on every deploy**.

### Every gateway rollout is a full realtime-join outage

`strategy: Recreate` on the gateway is **correct and must not be changed on its
own.** The reason is written into `app/40-gateway.yaml` and is worth restating
because reversing it looks like a modernisation:

> The gateway binds `hostPort: 7000`, and a hostPort is a node-level resource. On
> this single-node cluster the replacement pod cannot be scheduled until the
> outgoing one releases the port, while RollingUpdate will not terminate the
> outgoing one until the replacement is Ready. `kubectl rollout status` sits on
> `1 old replicas are pending termination` while the new pod is `Pending` with
> `node(s) didn't have free ports for the requested pod ports`. It survives the
> **first** deploy — the old pod has no hostPort yet — and wedges every deploy
> after it.

`nakama` binds `hostPort: 7001` and carries the same `Recreate` for the same
reason, plus its own: it shares one database with its migration initContainer and
two versions running at once during a rollout is not something this stack has
been shown to survive.

So `Recreate` is not an oversight and not a rolling-update tuning gap. It is the
only strategy that terminates on a single node, and the price of it is stated
here rather than discovered:

- **During a `gateway` rollout, nothing can join.** `MsgAuth` and `MsgEnterWorld`
  have no listener on `127.0.0.1:7000`. A player whose TCP connection drops in
  that window also cannot reconnect, because reconnecting needs a fresh
  `gateway_token` and a fresh join token.
- **During a `nakama` rollout, nothing can obtain a `gateway_token`**, which is
  the first hop of the flow, so the effect on joins is the same.
- **In-progress gameplay survives both.** Under ADR-3 the gateway hands the
  client `{ServerAddr, JoinToken}` and leaves; the client dials the game server
  directly. The game server verifies the join token locally against
  `JOIN_TOKEN_SECRET` (`GameServer/Server/GameServer.cs`, HMAC + an in-process
  JTI replay tracker) and never calls the gateway, so a connected player does not
  notice. The blast radius is **joins, not gameplay.**

**The window is not measured.** It is bounded below by the old pod's termination
and the new pod's readiness (`readinessProbe` `initialDelaySeconds: 2`,
`periodSeconds: 5`) and above by the default 30s termination grace period, plus
an image pull if the tag is not already on the node. Nobody has timed it, and
`dev-up.sh` does not report it. To measure it, poll `127.0.0.1:7000` from the
host across a `kubectl rollout restart deployment/gateway -n rpg-k8s-realtime`;
do not infer it from `kubectl rollout status`, which reports the pod, not the
port.

### What has to be decided before any tier beyond dev

Do not inherit this shape by promoting the manifests. Each row is a real decision:

1. **A multi-replica gateway needs the hostPort question answered first.** The
   hostPort exists because k3d's serverlb publishes `7000-7100` and nothing in the
   default NodePort range; it also pins the pod to a node and caps the Deployment
   at one replica per node. The real-cluster answer is a LoadBalancer Service or an
   Ingress, at which point the hostPort and `Recreate` both go away. Note that
   removing the hostPort is **necessary but not sufficient**: ADR-16 records that
   single-flight per `map_id` is per gateway instance, so two replicas racing on a
   cold map can allocate one GameServer each and Agones has no un-allocate.
2. **Redis needs a persistence/replication decision.** ADR-4 rules out treating it
   as an evictable cache — it is the system of record for the server registry and
   the event stream — so "just add a replica" is not the answer either; a replica
   changes the durability and failover story, not just the replica count. The
   Sentinel upgrade path sketched in `../docs/DISASTER-RECOVERY.md` is the
   starting point, not the conclusion.
3. **The two PostgreSQL instances have one PVC each and no standby.** Recovery is
   restore-from-backup, at backup RPO. That is a stated position, not an
   omission — see `../docs/DATABASE.md`.
4. **The fleet cannot be scaled until map assignment is per-replica** (ADR-2), and
   there is no FleetAutoscaler — but a cold map does **not** make the first player
   *wait*: with no `Ready` pod the allocation fails in milliseconds and the client is
   refused, never held. Since #157 that refusal is the *retryable*
   `all servers busy, retry shortly` rather than a terminal one, which corrects what the
   player is told without putting any wait on the join path (#148, #152). Scaling it today
   does not add capacity, it splits the world: a second
   pod self-registers into `servers:map:map_01` on becoming Ready, without any
   allocation. The unlock is **#151** — register on `Allocated` rather than `Ready` — and it
   buys `replicas > 1` for **one map only**; a second map needs a per-pod `GAMESERVER_MAP_ID`,
   because allocation targets a fleet and every pod in one carries the same map. ADR-18 covers
   the autoscaler itself.

<!-- HEADING IS LOAD-BEARING. The section "Why EnterWorld waits on one branch and refuses on
     the others" (added by the #148 branch) quotes this heading verbatim to point readers at
     the table below it. Renaming this heading breaks that pointer SILENTLY: a wrong link text
     is not a merge conflict and no test sees it. If it must change, change the reference in
     the same commit. -->

### What `EnterWorld` does when no server is ready, and what the client is told

"No live server for this map" is **five** conditions with four different client messages,
and conflating them is how "add a buffer so the first player stops waiting" became a
plausible sentence. All five are decided in `FindServer`
(`gateway/registry/registry.go`) and mapped to client text by `clientSafeAssignError`
(`gateway/server/server.go:906`):

| Condition | Path | Client is told | Retryable? | Duration |
|---|---|---|---|---|
| Allocation **succeeds**, pod not yet registered | `awaitRegistration` blocks up to `--allocation-wait-timeout` (`DefaultAllocationWaitTimeout` = 15s, `registry.go:46`) -> `ErrServerStarting` (`registry.go:188`) | `server is starting, retry shortly` | **Yes** — the only retryable one | Up to 15s |
| Fleet has **no `Ready` pod** to allocate | The allocation API answers `UnAllocated` -> `ErrNoCapacity` (`agones_allocator.go:63`), wrapped alongside `ErrNoServerAvailable` by a two-verb `%w` (`registry.go:559`) | `all servers busy, retry shortly` | **Yes** — since #157 | Milliseconds to refuse. The condition itself clears in ~5.4s as the Fleet controller brings a replacement to `Ready` |
| Allocation fails for **any other reason** (transport error, non-2xx, undecodable body) | `AllocateServer` fails -> `ErrNoServerAvailable` only (`registry.go:559`) | `no server available for map` | No — terminal | Milliseconds |
| Map **has** live servers and every one is **full** | Refusal without allocating, because a second server for one `map_id` would split the world (ADR-2) -> `ErrNoServerAvailable` (`registry.go:408`) | `no server available for map` | No — terminal | Milliseconds |
| **No fleet serves the map** at all | `ErrFleetMapMismatch` (`registry.go:211`), and `rememberMismatch` suppresses re-allocation for `DefaultMapMismatchTTL` = 60s (`registry.go:60`, `:480`) | `map is not available` | No — terminal, and remembered 60s | Milliseconds |

Message strings are at `server.go:868`, `:876`, `:883` and `:901`. Two things the table is
meant to settle:

- **The deployment does wait on the first row, deliberately, and does not wait on any of the
  others.** #148 was about the second row — the fleet having no `Ready` pod — where there is
  nothing to wait *for*, so a buffer autoscaler removes a latency that was never on the
  player's path. Row two being *retryable* since #157 does not change that: the gateway still
  refuses in milliseconds and does not hold the client while the replacement pod boots.
- **Every row's disposition now matches its condition.** Row two used to be the exception —
  a self-correcting condition given a terminal answer — and #157 fixed it. What made that
  safe is that the row is keyed on `ErrNoCapacity` specifically: an `UnAllocated` answer is
  a decoded 2xx body stating that **no** GameServer was handed out, so the retry it invites
  leaks no pod, and the retry that succeeds usually costs no allocation at all because a
  `Ready` pod self-registers before any allocation (ADR-18). Row three is the *other* half of
  the old row two and stays terminal for exactly that reason: a lost response may have
  allocated a pod. Rows four and five earn their terminal answers under ADR-2 and from the
  fact that no retry changes which map a fleet serves.

Related issues: **#143** (k3d serverlb sits in the gameplay data path and
triples snapshot jitter), **#147** (a reported 54 Hz tick against an advertised 60 Hz base rate,
which the #147 investigation reports as a measurement artifact — the loop paces on
`CLOCK_MONOTONIC` while the observer timed it against a `CLOCK_REALTIME` running ~10%
fast on the WSL2 host — rather than a code defect; **closed** on that basis, with the host
clock itself filed as **#153**),
**#148** (no FleetAutoscaler and `replicas: 1` — premises corrected on the issue,
autoscaler refused in ADR-18).

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

The verification step exports `VERIFY_SMOKETEST_BIN` and `VERIFY_PROBE_BIN` from
`dist/bin/` (the deployment bundle) and fails the step if either is missing. The
deploy job installs no Go: `build-smoketest` and `build-verify-probe` build them
on `ubuntu-latest` from the deployed commit. Without that, `data.nakama_plugin`
and `flow.smoke` fail with `go: command not found` and `flow.stack_identity`
skips behind them.

`cluster.restarts` fails on a container that restarted in the last
`VERIFY_RESTART_WINDOW` seconds (1800 in `targets/k8s-dev.env`) or that is not
running. If the WSL2 host or Docker restarted shortly before a deploy, every
container in the cluster carries a fresh restart and this check fails naming
them and identifying it as a node event; it clears once the event is older than
the window.

Staging and production are untouched: they select `containers`/`host` through
their own `vars.DEPLOY_MODE` and never enter this path.
