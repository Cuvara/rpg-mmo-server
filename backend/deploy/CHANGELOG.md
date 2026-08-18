# Changelog — Deploy / Infrastructure

All notable changes to deployment and infrastructure will be documented in this file.
Format based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Fixed
- **A containers-mode deploy could start a second stack beside the live k8s one, and nothing stopped it.**
  The per-step `deploy_mode != 'k8s'` conditions protect k8s mode from the compose steps, but nothing
  protected the cluster from `DEPLOY_MODE` being set back to `containers`. Measured 2026-08-18: a deploy
  in that state ran against a cluster already serving dev and left two gateways, two Redis instances and
  two registries up for an hour, with clients split by which port they dialled. CD now refuses a
  containers-mode deploy while `rpg-k8s-realtime`'s gateway has a ready replica, and names
  `rollback-to-compose.sh` as the supported way down. Verified both ways against the live cluster: it
  exits 1 while k8s is serving, and stands down when the namespace is absent.
- **CD-built images carried no provenance, so `dev-up.sh`'s revision check was inert on everything CD
  produced.** The build step did not pass `--build-arg GIT_REVISION`, so the label defaulted to
  `unknown` and the guard that refuses to pin an image stamped with a different commit silently skipped.
  Both images are now stamped with the deploying commit.

### Changed
- **`GAMESERVER_CAPACITY` is now set explicitly in both map fleets — 100 is a decision
  instead of a default (#145).** `deploy/k8s/app/50-fleet-map.yaml` and
  `deploy/agones/fleet-map-dotnet-dev.yaml` both left it unset, so `Program.cs` fell back to
  a compiled-in 100. That constant is what stopped a 120-player load run at exactly 100
  joins with 20 clients refused — not CPU, not memory, not the simulation. And it was never
  pod-local: the value is published into the Redis registry and the gateway enforces it
  (`FindServer` skips a server whose `PlayerCount >= Capacity`), so the fleet's admission
  limit was an unset environment variable inheriting a constant nobody had chosen.

  **The value stays 100, and that is the point.** What justifies it: 100 concurrent players
  observed on this fleet with tick p99 at 0.49 ms against a 66.67 ms budget (0.7% of it), 0%
  of ticks over budget at every level tested, 240m of a 1000m CPU limit and 54Mi of a 256Mi
  memory limit. 100 is a level this fleet has actually run and been measured at.

  **Why it was not raised on that headroom.** Headroom at 100 is not evidence about 150. The
  load generator shares the box with the server under test and uses more CPU than it
  (ADR-7) — tick p99 on this host has read 67.4-70.8 ms quiet and 224.7-240.6 ms with a
  deploy sharing it, a 3.3x swing — and on k3d every gameplay packet crosses the `serverlb`
  TCP proxy (#143), the likeliest cause of the 1252.9 ms ack p99 at 80 players while tick p99
  stayed at 0.49 ms. ADR-7 therefore still says the ceiling is UNKNOWN. This repo has already
  had to retract one number invented from apparent headroom (the "150 players per server"
  figure); raising this one on the same evidence would be that again. The rationale, and the
  conditions under which it moves, are written next to the value in both manifests.

- **CPU request 200m -> 250m in both map fleets.** `scheduling: Packed` packs pods by
  *request*, and 200m described the pod at idle (17m-ish) far better than under load
  (measured peak 240m at 100 players), so the scheduler was reserving less than the pod uses
  in exactly the situation where packing matters. Limits are unchanged at 1000m — this
  changes what is reserved, not what may be used. The memory request stays 128Mi on purpose:
  measured peak is 54Mi at 100 players and 82Mi at 200 (the highest figure `BENCHMARK.md`
  has ever recorded), so it already sits above load rather than at idle. Both requests are
  now above measured peak, which is the property that was missing.

### Documentation
- **`K3S.md` and ADR-16 now record that the k3d serverlb sits in the gameplay data path and
  triples snapshot jitter (#143).** The Agones dynamic port range 7000-7100 is published by the
  `k3d-<cluster>-serverlb` container, an nginx TCP proxy, so on k3d every gameplay packet to an
  Agones pod crosses a hop that has no counterpart on a real node. Measured with the same binary
  under the same load at 50 players: snapshot interval p99 **211.9 ms** through the serverlb
  against **72.7 ms** direct to a compose server, while the compose path at 80 players still
  reports tick p99 3.06ms and 0% of ticks over budget — so the simulation is not the constraint
  in either case, the proxy is. This is the same mechanism ADR-16 decision 1 depends on (Docker
  Desktop never publishes Kubernetes `hostPort`, k3d does), so it is a property of the local rig
  rather than a regression, and it is not something to fix. Documented because it runs opposite
  to the distortion people expect — ADR-7's confound depresses numbers through CPU contention,
  this one through the network path — and without it the first person to sweep capacity on k3d
  reports a ceiling roughly 3x too low and blames the game server. **Take capacity numbers on
  the compose path or direct to a node; k3d is for proving allocation and addressing, not a
  measurement surface.** Refs #143, ADR-16, ADR-7.
- **`K3S.md` now warns that the host clock cannot be used for rates, including from inside a
  pod (#153).** `CLOCK_REALTIME` on this box runs fast against `CLOCK_MONOTONIC` by
  **+11.1%, +16.7% and +16.65%** across three sessions, by a drifting amount that cannot be
  corrected for. New section *Measuring on this cluster* carries the twenty-second
  reproduction and the rule: **never derive a rate from a wall clock on this box** — not
  `date`, `$SECONDS`, `time.time()`, `DateTime.UtcNow`, nor a Prometheus `rate()` resting on
  server-assigned scrape timestamps. Called out specifically for k3d because a k3d node is a
  container on this kernel and shares its clock, so `kubectl exec ... date` reproduces the
  artifact rather than providing a second opinion. Points readers at the server's own
  `gameserver_tick_duration_seconds` histogram, which is built from `Stopwatch.GetTimestamp()`,
  instead of timing the loop from a shell. This already cost a filed issue (#147, closed as
  not-a-defect) that reported 54 Hz against an advertised 60 and reached an ADR in an open PR.
  Refs #153, #147.

### Added
- **`verify.sh` check `cluster.autoscaler` — a `FleetAutoscaler` on a single-map fleet is now
  a FAIL, not a paragraph.** It fails when any `FleetAutoscaler` targets a fleet whose pod
  template carries a literal fleet-wide `GAMESERVER_MAP_ID`, and stands down for a fleet that
  does not — so it stops being an error at exactly the moment a per-pod map id lands, rather
  than becoming the next thing to argue past. The hazard it guards is invisible in every other
  signal: the manifest applies, the pod reaches `Ready`, and the fleet reads *healthier* than
  before, while the spare pod self-registers as a second live server for `map_01`. Measured on
  `k3d-rpg-dev` 2026-08-18 — scaling the fleet `1 -> 2` put the new pod into `Ready` after
  **5.38 s** and `servers:map:map_01` held **two** members within a second of that, with no
  allocation involved; `registry.FindServer` then returns the least-loaded of the two, i.e. the
  unallocated spare. Fleet restored to `replicas: 1` immediately. Proven both ways: PASS with
  no autoscaler present, FAIL with one created against this fleet (capped at `maxReplicas: 1`
  so the probe could not spawn the splitting pod), then deleted. Refs #148, #151, ADR-18.
- **The `EnterWorld` cold-start policy is written down** (`deploy/k8s/README.md`). It was
  already implemented and only readable in `gateway/server/server.go`: *allocated but not yet
  registered* is retryable (`server is starting, retry shortly`, after the gateway itself
  waited up to `--allocation-wait-timeout`, 15 s), *map served but full* and *map not served by
  any fleet* are terminal, the latter remembered for 60 s because every retry allocates a pod
  that is never reclaimed. One case was named as **unstated and deliberately unchanged there**
  — when the fleet has no `Ready` GameServer at all the allocation fails outright and the
  client gets the terminal answer even though a pod may be seconds away (#152); it was fixed
  in the gateway by #157 and the table now records the new answer. The distinction is
  load-bearing and is stated in ADR-18 too: `EnterWorld` *does* wait on the branch where the
  allocation succeeds and `awaitRegistration` blocks for the pod's own registry entry; what
  it never does is wait when the fleet has no `Ready` pod to allocate, which is the case
  #148 was about. Conflating the two is how "add a buffer so the first player stops waiting"
  became a plausible sentence. Refs #148.

### Changed
- **The `EnterWorld` refusal table records the fix to its own exception** (#157,
  `deploy/k8s/README.md`). The table had four conditions and three messages, and named row two
  — *fleet has no `Ready` pod* — as the one refusal whose disposition did not match its
  condition, tracked as #152 and left as found because every retry allocates and Agones has no
  un-allocate. #157 split that row: an `UnAllocated` answer from the allocation API
  (`registry.ErrNoCapacity`) now returns the retryable `all servers busy, retry shortly`, while
  every *other* allocation failure keeps the terminal `no server available for map`. The
  narrowing is what keeps the pod leak bounded — `UnAllocated` is a decoded 2xx body stating
  that no GameServer was handed out, whereas a lost response may have allocated one. Five
  conditions, four messages, and every disposition now matches its condition. **Nothing about
  the deployment changes**: no buffer, no autoscaler, no longer wait. `EnterWorld` still
  refuses in milliseconds on that branch and leaves the backoff to the client, so ADR-18 is
  untouched and the measured 5.38 s cold start stays off the player's path.
- **`verify.sh` check `refusal.unknown_map` follows the gateway's new string.** The check
  reads the refusal text, and an empty fleet now answers `all servers busy, retry shortly`
  where it used to answer `no server available for map`. Without this the empty-fleet case
  would have fallen through to the catch-all and reported a **FAIL** for a healthy gateway —
  the check's own inconclusive branch, misfiled as a defect. Both strings now SKIP as
  inconclusive with distinct reasons: the new one means the fleet had nothing to allocate,
  the old one now means the allocation failed for some *other* reason and the gateway logs
  are worth reading. The retryable-refusal FAIL branch is unchanged and still fires on
  `server is starting, retry shortly`, which is the leak it exists to catch.
- **`cluster.fleet` no longer WARNs about `ready=0` on a fleet that pins a fleet-wide map id.**
  On that fleet `ready=0` is the designed steady state — a spare `Ready` pod would be a second
  live server for the map — so the check now passes and says why. A warning that fires on the
  correct state reads as an instruction to break it, which is how the buffer autoscaler came to
  be proposed as the fix. The warning is kept, with its reasoning, for fleets where spare
  capacity is meaningful. The one thing the old WARN carried that is worth keeping — that
  `refusal.unknown_map` cannot reach its branch in this state — is already stated by that
  check's own SKIP.
- **`deploy/docs/K3S.md` "Why there is no autoscaler" is now measured rather than asserted**,
  and records the two premises of #148 that measurement contradicts: the ~9 s cold start is not
  on the player's path (with no `Ready` pod the allocation fails immediately and the client is
  refused in milliseconds, it does not wait), and an autoscaler does nothing for the "a second
  map cannot be served" symptom, which is a consequence of allocation targeting a *fleet*.

### Fixed
- **`verify.sh` layer 5 reported PASS on a client run that never reached the deployment.**
  The check's stated claim is that the real client "completed its live-backend PlayMode
  tests against THIS gateway", but it asserted only `failed=0` and a test count — so a run
  of `total=30 passed=29 failed=0` passed every assertion while its single `LiveBackend`
  case was *Ignored*, having connected to the default `127.0.0.1:8000` rather than the
  deployment. The client was unverified and the suite said otherwise. Layer 5 now requires
  at least one `LiveBackend` case to be present **and** to have Passed, and reports the
  Ignored case's own reason (which names the address it actually dialled) when it has not.
  Categories are inherited from ancestor `<test-suite>` elements, because NUnit records
  `[Category]` on the fixture rather than on each case.
- **The invocation layer 5 prints did not work from WSL.** It used a
  `CUVARA_GATEWAY_HOST=... Unity.exe` prefix, and environment variables do not cross into
  a Windows process that way — only `WSLENV` carries them. Following the printed command
  therefore ran the client against its defaults. It now exports the variables, lists them
  in `WSLENV`, and resolves the editor from `ProjectSettings/ProjectVersion.txt` instead of
  globbing the Hub directory (which picks 2021.3 and starts downgrading the asset database
  of a Unity 6 project).
- **`dev-up.sh` pinned images it had never made available, so roll-forward was broken.**
  The import step warned and continued when a tag was absent from the local docker store,
  and the script then pinned that exact tag onto the gateway Deployment and the Fleet.
  k3d does not share the host docker image store, so the pods went straight to
  `ImagePullBackOff` and `kubectl rollout status` burned its full 180s timeout before
  failing with `timed out waiting for the condition` — a message naming neither the image
  nor the reason. Rollback to compose worked, roll-forward did not, so the cutover was
  provable once and not repeatable. A missing image is now fatal at the point it is
  detected, and the error names the two `docker build` commands that fix it. Three
  independent ways in are all closed: absent from both stores, absent with
  `IMPORT_IMAGES=0` (which skipped the whole loop while the pin still happened — the node
  is now asked directly, whether or not we imported), and present but stamped with a
  different commit.
- **A gateway image could not be tied to a commit.** `Dockerfile.gateway` never declared
  `ARG GIT_REVISION`, so the `--build-arg GIT_REVISION=...` that every build path passes
  was silently discarded and the image carried no provenance label — a stale image was
  indistinguishable from a correct one by inspection, which is what makes a mis-pinned
  tag so hard to see. It now stamps `org.opencontainers.image.revision` like the game
  server does, and `dev-up.sh` refuses to pin an image whose stamp disagrees with the
  commit it is deploying.
- **A hostPort Deployment under RollingUpdate deadlocks on a single node.** Adding
  `hostPort` to the gateway made every deploy *after the first* wedge: the replacement pod
  cannot be scheduled until the outgoing one frees the port, and RollingUpdate will not
  terminate the outgoing one until the replacement is Ready. `kubectl rollout status` sits on
  "1 old replicas are pending termination" while the new pod is Pending with
  "node(s) didn't have free ports for the requested pod ports". Both hostPort workloads now
  declare `strategy: Recreate`. This passes the first time and fails forever after, which is
  why it is written into the manifest rather than left to be rediscovered.
- **`dev-up.sh` drained the fleet on every run, including a no-op one**, taking `map_01` down
  each deploy. It compared the game server image *after* `kubectl apply` had already reset the
  Fleet spec to the manifest's moving `:develop` tag, so the comparison was unequal every
  time. It now reads the running image *before* the apply, and re-pins the spec without a
  drain when nothing changed.
- **`dev-up.sh` now ESTABLISHES the Agones port floor rather than asserting it**, so a fresh
  cluster — and therefore CD — reaches the correct state without a human having run
  `kubectl set env` first.

### Added
- **A real host route for the k8s dev stack — `hostPort` 7000/7001 out of k3d's published
  range, replacing the port-forwards.** k3d's serverlb publishes `7000-7100` and nothing in
  the default NodePort range `30000-32767`, so the gateway's NodePort was allocated and
  unreachable: the gateway answered inside the cluster while no client on the host could dial
  it, and Nakama — which the client must reach *before* the gateway — had no host route at
  all. Every component reported healthy and the deployment was unusable, the same failure
  class as advertising a hostless address. The range is now SPLIT rather than borrowed from:
  the Agones controller runs with `MIN_PORT=7010`, reserving `7000-7009` for infrastructure,
  so the allocator can never hand a GameServer the gateway's port and leave it Pending.
  `dev-up.sh` refuses to run if that pin is missing, because changing one without the other
  reintroduces the collision. On a real cluster this is a LoadBalancer or an Ingress and the
  hostPort disappears.
- **`registry.stack_identity` and `flow.stack_identity` — the suite now proves WHICH stack
  answered.** On a box where the previous stack is still running, `127.0.0.1:8000` and `:7350`
  belong to it, so a suite pointed at the conventional addresses goes green having verified
  the deployment it was meant to replace. Dialing a port is not evidence of whose port it is.
  The first check attributes the registry (the live server is a GameServer of the fleet under
  test); the second attributes the gateway (the server it assigned belongs to that fleet).
  The k8s target also moves off 8000/7350 entirely, so the ambiguity cannot arise.

- **Dev runs entirely on k3s/Agones (`backend/deploy/k8s/dev-up.sh`).** The dev environment
  was a hybrid — game servers under Agones in `rpg-realtime`, but gateway, Nakama, Redis and
  both PostgreSQL instances in docker compose, reached from the pods by `host.k3d.internal`.
  ADR-15 decision 4 calls that the worst place to stop, because the host alias is what makes
  it work on one machine and nowhere else. `dev-up.sh` brings the whole stack up in-cluster
  (`rpg-k8s-data` + `rpg-k8s-realtime`), retires the compose stack and the legacy fleet, and
  is the same script CD runs, so the box and CI cannot drift. Verified end to end:
  `verify.sh --target k8s-dev` reports `VERIFY=PASS`, 18 PASS / 0 FAIL, with the full client
  flow (auth → gateway token → EnterWorld → direct game-server dial → snapshots → the
  `player_states` write and the reload after the hold) running against in-cluster DNS only.
- **`backend/deploy/k8s/rollback-to-compose.sh` — dev back on the pre-cutover stack in
  minutes.** Scales the in-cluster workloads to zero, drains the k8s fleet, restarts the
  compose containers and restores the `rpg-realtime` fleet. Destroys nothing: every PVC,
  every compose volume and every compose container survives, so the rollback is reversible by
  re-running `dev-up.sh`. Exercised, not just written — see the entry under Fixed for the two
  defects the first run found.
- **`DEPLOY_MODE=k8s` in `.github/workflows/cd.yml`.** A third deploy mode alongside `host`
  and `containers`: it runs `dev-up.sh` and then `verify.sh --target k8s-dev` as the
  post-deploy healthcheck, so a push to `develop` reproduces the k8s deployment and fails the
  deploy if the suite does. Staging and production are untouched — they select their mode
  through `vars.DEPLOY_MODE` and never see this path. The compose data-tier, migration and
  stack steps are gated off in this mode; `post-deploy-smoke` is skipped because the same
  smoketest runs, with `--strict-addr --require-db`, as layer 4 of the suite.
- **The availability posture of the k8s dev stack is documented, and recorded as ADR-17.**
  Every workload in `deploy/k8s/` runs a single replica — `gateway`, `nakama`, `redis`,
  `postgres-meta`, `postgres-game` and the `map-servers-dotnet-k8s` Fleet — so Kubernetes is
  providing scheduling and lifecycle here, **not redundancy**, and nothing said so. Worse,
  `strategy: Recreate` on the two `hostPort` workloads (required: RollingUpdate deadlocks on a
  single node, see the entry below) makes **every gateway or Nakama rollout a full outage of
  the join path** — no `MsgAuth`, no `MsgEnterWorld`, no `gateway_token` — which is fine and is
  not a defect, but was discoverable only by reading a manifest comment. `k8s/README.md` gains
  an **Availability posture** section with a per-workload ownership table, the rollout cost,
  and the questions that must be answered before any tier above dev (the gateway's hostPort
  exposure *together with* ADR-16's per-instance single-flight; Redis persistence/replication
  against ADR-4, which rules out treating it as an evictable cache). `docs/DISASTER-RECOVERY.md`
  gains the k8s addendum — same blast radii, plus one that happens on purpose every deploy;
  `docs/K3S.md` gains a banner distinguishing itself from the stack that now runs; and the
  `docs/README.md` inventory row reading "k8s base/overlays — Planned" is corrected, since that
  stack shipped. The rollout window is deliberately **not** given a number: nobody has measured
  it (ADR-7's rule), and how to measure it is written down instead. In-progress gameplay
  survives all of it, verified in code rather than assumed — the game server verifies the join
  token itself and never calls the gateway during a session (ADR-3). No manifest changed.

### Fixed
- **An `Allocated` GameServer survives `kubectl scale fleet --replicas=0`.** Agones treats
  allocated as in use, so scaling alone left the pod running *and* its registry entry live.
  The first cutover run therefore reported the legacy fleet retired while its server was
  still serving map_01, and the game server image pin silently did not take because the old
  pod was never replaced. Both scripts now call `drain_fleet`, which scales to zero and then
  deletes the GameServers explicitly; the delete is a graceful pod termination, so the
  server's SIGTERM path deregisters itself instead of leaving an entry to expire on the 15s
  heartbeat TTL — the window in which the gateway hands a client the address of a server that
  is gone (ADR-2).
- **`docker stop`/`docker start` under the WSL Docker Desktop shim intermittently return
  non-zero after succeeding**, which under `set -e` aborted `dev-up.sh` before it started the
  port-forwards and aborted `rollback-to-compose.sh` before it restored the legacy fleet. In
  both cases the script exited leaving dev unreachable. The status is now tolerated and the
  actual container state asserted afterwards.
- **A port-forward that fails to bind left a live-looking pidfile and a zero exit**, so dev
  came up with an unreachable Nakama and nothing said so. `pf()` now polls until the socket
  accepts a connection and fails with the forward's own log if it never does.
- **The fleet image comparison read the Fleet spec, which `kubectl apply` had just rewritten**
  back to the manifest's `:develop`, so every run compared unequal and drained the fleet —
  taking map_01 down on a no-op re-run. It now compares against the image a GameServer is
  actually running.
- **`redis-cli --no-raw SMEMBERS` on an empty set yielded a member literally named `array)`**
  (from `(empty array)`), which the deregistration loop then dutifully deleted. Both scripts
  use `--raw`, which prints one member per line and nothing at all for an empty set.
- **`VERIFY_SECRETS_ALLOW_EMPTY` silently dropped every exemption but the last** for a secret
  named in more than one entry, because the lookup assigned instead of appending. A key the
  operator had exempted still failed the check
  (`backend/deploy/k8s/verify/lib/checks_cluster.sh`).

### Changed
- **`backend/deploy/k8s/verify/targets/k8s-dev.env` now describes the deployment that
  exists.** It shipped as placeholders (`rpg-realtime`/`rpg-data`, fleet
  `map-servers-dotnet-dev`, secrets `rpg-*-secrets`) that matched no manifest, so every layer-1
  check failed on absent objects. It now names `rpg-k8s-realtime`/`rpg-k8s-data`, fleet
  `map-servers-dotnet-k8s` and the real secrets, and sets `VERIFY_ADDR_ALLOW_LOOPBACK=1` with
  the measurement behind it: on k3d the node address `172.20.0.3` is **not** dialable from
  WSL2 or Windows while `127.0.0.1:<agones port>` is, because the serverlb publishes
  7000-7100 onto the host. That must return to `0` on any cluster where the client is not on
  the node; `registry.addr_dialable` is what keeps it honest, because it opens the address.
- **Images are pinned to an immutable tag.** `dev-up.sh` resolves `rpg-mmo/*:${GIT_SHA}` and
  CD passes `github.sha`, then asserts the running gateway carries it before trusting the
  suite. The moving `:develop` tag is retagged by hand and had silently lagged: at cutover the
  cluster ran `develop-307f1e8` while `develop` was at `b633aff`, so the deployment under test
  was not the commit under test.

- **`backend/deploy/environments.tsv` — reserved stack identity per environment.** One row
  per GitHub Environment declaring the deploy directory, compose project, container prefix
  and every published port it is allowed to own. It is an assertion over the GitHub
  Environment variables, not a source of truth: the variables still drive the deploy, but
  they are invisible to code review and this file is not. Changing a port now means editing
  the variable *and* the row, in the same change.
- **`backend/deploy/preflight-isolation.sh` — pre-deploy collision guard**, wired into
  `cd.yml` as the `Isolation preflight` step of the `deploy` job, immediately after the
  checkout and before the bundle sync (the first step that would otherwise write into the
  deploy directory). It refuses the deploy when the resolved deploy dir, compose project,
  container prefix or any published port is reserved for a different environment, when the
  resolved values contradict this environment's own row, when the target `deploy/.env`
  carries another environment's `DEPLOY_ENVIRONMENT` stamp, when a `<prefix>-<service>`
  container exists under a different compose project, or when a port it is about to publish
  is held by another project or by a non-docker listener. The limits are enumerated at the
  bottom of the script — chiefly that it cannot see a collision that is not live and is not
  declared in the registry.
- **`DEPLOY_ENVIRONMENT` and `COMPOSE_PROJECT_NAME` in the generated `deploy/.env`.** The
  first is the ownership stamp the guard reads back; the second means a human running
  `docker compose ps|logs|down` in a deploy directory targets that stack's project rather
  than the compose file's default `name:` — i.e. somebody else's containers.

### Fixed
- **`staging` had no isolation from `dev` at all.** Its GitHub Environment variables set
  the same `RPG_DEPLOY_DIR` (`/mnt/e/rpg-mmo-deploy`), no `COMPOSE_PROJECT_NAME`, no
  `COMPOSE_NAME_PREFIX` and the same published ports as `dev`, while `production` was
  isolated correctly. A staging deploy would have regenerated dev's `deploy/.env` wholesale
  from staging's variables — dropping `ALLOCATOR=agones`, which staging does not set, back
  to `none` — and `up -d` would have adopted dev's containers into staging's deploy. The
  variable values that resolve this are documented in `docs/CICD.md` ("The three reserved
  port sets") and asserted by `environments.tsv`; the variables themselves are set in the
  GitHub Environment, outside this repo.
- **Container names in `cd.yml` resolved to the `dev` stack for every environment.** The
  `Start / update the stack` step addresses containers as `${COMPOSE_NAME_PREFIX:-rpg}-…`
  for the Agones stop-check and the Redis deregistration, but `COMPOSE_NAME_PREFIX` was
  never in that step's `env:`, so the fallback always won: a production deploy checked, and
  deregistered against, **dev's** containers. `GAMESERVER_MAP_ID` had the same problem in
  the same block, and the `postgres-game` readiness wait was hardcoded to
  `rpg-postgres-game`. All three now take the environment's prefix.
- **The containers-mode healthcheck listed and logged the wrong environment's containers.**
  `docker ps --filter name=rpg-` is a *substring* filter, so it matched `rpg-prod-*` and
  `rpg-stg-*` too, and the failure-path `docker logs` calls named `rpg-gateway` /
  `rpg-gameserver` literally. Both now anchor on the prefix from the sourced `.env`.
- **`scripts/deploy-local.sh` used globally-fixed systemd unit names.** `rpg-gateway.service`
  / `rpg-gameserver.service` are host-global, so in `DEPLOY_MODE=host` a staging deploy
  would have restarted dev's units even with separate deploy directories. Unit names now
  derive from `COMPOSE_NAME_PREFIX`, defaulting to `rpg` so existing single-environment
  hosts are unaffected.
- **CD silently reverted the Agones switch on every deploy.** The dev stack was moved to
  serve `map_01` from the Agones fleet; the next push to `develop` put it straight back on
  the compose game server with `ALLOCATOR=none`, and nothing reported it. Three causes,
  all now closed:
  - CD deploys from a bundle (`$RPG_DEPLOY_DIR/deploy`), not from the working tree, so an
    `.env` edited in the repo never reached the running stack;
  - the bundle never shipped `docker-compose.agones.yml`, so the overlay could not be
    applied even if asked for;
  - the generated `.env` is written wholesale from environment variables and had no
    allocator keys, so `ALLOCATOR` reset to `none` on every run.
  The bundle now carries the overlay, the generated `.env` carries `ALLOCATOR*`,
  `KUBECONFIG_HOST` and `K3D_NETWORK` from environment variables, and the deploy step adds
  `-f docker-compose.agones.yml` when `ALLOCATOR=agones`. It also stops the compose
  `gameserver-dotnet` in that case: the fleet serves `map_01` and two live servers under
  one `map_id` is ADR-2's split world. Compose merges `profiles` by union, so an overlay
  cannot take a service out of a profile — stopping it explicitly is the honest way.


### Documentation
- **ADR-15 — what running the realtime tier on Kubernetes would cost, and the
  dynamic-address problem that blocks it** (`backend/docs/ARCHITECTURE-DECISIONS.md`).
  Status is **proposed, not accepted**: ADR-14 left the move to k8s explicitly undecided and
  this records the price rather than paying it. What it establishes:
  - **The two deploy stories do not meet.** `dev`, `staging` and `production` are all
    `DEPLOY_MODE=containers` and `cd.yml` applies no Kubernetes manifest anywhere. There is
    no k3s — the context is `docker-desktop` v1.34.1 and `deploy/k3s/setup-dev.sh` only
    `kubectl apply`s into whatever context is current — and `deploy/k8s/` does not exist
    despite the root `CLAUDE.md` describing a base/overlays tree.
  - **One thing is decided, because it blocks either answer: the game server cannot learn
    its own address.** With `portPolicy: Dynamic` the real address lives only in GameServer
    status, the fleet supplies no `GAMESERVER_PUBLIC_ADDR`, and `IAgonesSdk` cannot read
    status — so the server advertises `:9000` into Redis and the gateway hands that to
    clients verbatim. **This, not the health loop, is why `ALLOCATED` is 0.** The recommended
    fix is a GameServer-status read on the sidecar; static ports and gateway-side
    registration are considered and rejected (packing loss, and an ADR-1 two-writer
    violation respectively). PR #139 is necessary but not sufficient — it adds the four
    POSTs and no status read.
  - **Six prerequisites outside `deploy/agones/`** are tabulated against what
    `docker-compose.yml` provides today: StatefulSets and PVCs for the three stateful
    services, ConfigMaps for the initdb and monitoring mounts, image-baked or
    initContainer-delivered `nakama.so`, Secrets for the seven values CD writes into a
    mode-0600 `.env`, a registry push per environment, and a ServiceAccount with `create`
    on `gameserverallocations.allocation.agones.dev` — without which `AgonesAllocator`,
    which today rides the developer's kubeconfig, gets a 403 in-cluster.
  - **Sizing:** this outweighs ADR-14's stages 1-8 and precedes them; ADR-14's S/M sizes for
    stages 4-5 are honest only on `docker-desktop`.
  - ADR-3 survives unchanged — allocation sits inside `MsgEnterWorld` and nowhere else, and
    the gateway stays out of the gameplay path. Autoscaling stays off CCU per ADR-7.

  Documentation only. No manifest, workflow or compose file changed, and nothing was applied
  to any cluster.
- **`docs/REALTIME-FLOW.md`** — a new hop-by-hop account of what happens when a player
  enters a map, written for someone who has not read this repo. Three flows: **A**, the
  compose path that actually runs (push → `cd.yml` → bundle → self-hosted runner →
  generated `deploy/.env` → `docker compose up` → smoke test, then the handshake ending
  with the client talking directly to the game server); **B**, the Agones path as it
  stands in the tree; and **C**, what a working Agones path would require, with every
  arrow that does not exist marked as missing. Plus a table of what compose supplies that
  Kubernetes would have to replace, and the four one-command checks for telling which
  flow a given box is in. Linked from `docs/README.md`.

  The document's centre is a break that is **not** where it looks like it is. The gateway's
  `AgonesAllocator` correctly reads `status.address` and the `game` port and builds a
  dialable address (`gateway/registry/agones_allocator.go:242-254`) — but it is never
  called, because the pod self-registers first. Self-registration is gated on `REDIS_ADDR`
  alone (`GameServer/Program.cs:87`, `:313`), the C# fleet sets it and passes no
  `GAMESERVER_PUBLIC_ADDR`, so `publicAddr` falls back to the listen address
  (`Program.cs:101`) and the registry holds a hostless `:9000` — wrong host and, under
  `portPolicy: Dynamic`, wrong port. `FindServer` then finds that entry with capacity and
  returns at `gateway/registry/registry.go:233-235`, so the allocator branch at `:237` is
  never evaluated. Two defects stacked; fixing either alone leaves the flow broken, and
  the address the pod would need cannot be known at manifest-authoring time because
  `IAgonesSdk` has no status-read call (`GameServer/Agones/AgonesSdk.cs:5-19`).

  ADR-15 argues the decision; this document only describes the mechanics. Nothing in it
  was applied to any cluster, and Flow C describes nothing that currently runs.

### Changed
- **Dev serves `map_01` from the Agones fleet, not from the compose game server.**
  The gateway runs with `ALLOCATOR=agones`, attached to both the compose network and
  `k3d-rpg-dev`, reaching the API through `https://k3d-<cluster>-serverlb:6443` with TLS
  verification intact. Verified end to end: smoke test 10/10 in `--strict-addr` mode,
  including `gamestate_player_row` and `gamestate_reload` against a pod whose address
  only Agones could supply. Reversible in three commands — see `docs/K3S.md`.
  This is a **dev** switch. Staging and production remain `DEPLOY_MODE=containers`,
  and ADR-15's six prerequisites for a real cluster are still untouched.

  Three findings from doing it, now in `docs/K3S.md`:
  - `.env` drifts from what CD deployed, so restarting a service from the file can
    change its secrets underneath it. Read them from the container, and not with
    `docker exec printenv` — the image is distroless and that silently yields the
    exec error text.
  - The kubeconfig bind must be cwd-relative; the Docker Desktop shim turns an
    absolute `/mnt/*` bind into a directory, surfacing as `read /kc: is a directory`.
  - A fleet at `replicas: 1` has no spare pod, so the allocation path cannot fire
    once that pod is Allocated.


### Removed
- **Deleted the five Go-image manifests** — `agones/fleet-map.yaml`,
  `fleet-map-dev.yaml`, `fleet-dungeon.yaml`, `fleet-dungeon-dev.yaml` and
  `allocation.yaml`. They ran `rpg-mmo/gameserver:dev` / `ghcr.io/…/rpg-mmo-gameserver:latest`,
  built from `backend/gameserver/`, deleted in `670a803` along with its Dockerfile — software
  that cannot be rebuilt. PR #137 marked them superseded with a banner instead, because the
  cluster was still *running* `map-servers-dev` and `dungeon-servers-dev` from them. Both
  fleets have since been retired, so that reason is gone — and a banner never stopped anything
  anyway: `kubectl apply -f agones/` does not read comments. This is the manifest half of
  ADR-14 stage 8. No `fleet-map-dotnet.yaml` replaces the prod fleets yet, deliberately: a
  production manifest for a fleet that has never run, pointing at a tag that has never been
  published, is the same mistake one generation later.
- **Deleted `agones/autoscaler.yaml` and `agones/autoscaler-dev.yaml`.** Not because the policy
  was wrong — buffer-on-server-count is exactly what ADR-14 decision 5 prescribes, since ADR-7's
  per-server ceiling is unknown — but because **a buffer autoscaler is incoherent for a map
  fleet**. Nothing consumes the buffer: map servers self-register into Redis and the gateway
  finds them through the registry, never through an allocation, so `ALLOCATED` stays 0 and the
  buffer is never drawn down. And if it *were* consumed, the extra replica would come up with
  the same `GAMESERVER_MAP_ID` and register under it, manufacturing the split-world hazard
  ADR-2 forbids without any allocation involved. Buffer autoscaling becomes coherent for the
  **dungeon** fleet (ADR-14 stage 6), whose pods really are spare capacity until allocated;
  stage 7 should be read as belonging to stage 6.
- **Removed `LOG_LEVEL` from the fleet.** `grep -rn LOG_LEVEL backend/gameserver-dotnet/GameServer/`
  returns nothing — the server pins its console logger to Information and reads no level from
  the environment. The variable configured nothing while reading as if it did.

### Added
- **The dotnet fleet persists player state** (ADR-14 stage 4, second half). `GAME_DB_URL` is no
  longer a commented-out block in `agones/fleet-map-dotnet-dev.yaml`: it is a real env entry
  reading `game-db-url` from `rpg-realtime-secrets`, and `k3s/setup-dev.sh` now writes that key.
  Until this, every pod ran the **in-memory** player store, so a world under a Fleet was lost on
  every rollout, eviction or health kill — and the proof harness had to run `--skip-db`.

  The value is not the compose DSN. Compose uses `postgres-game:5432`, a service name that
  resolves only on the compose network; a pod reaches the same database the way any host process
  does, through the **published** port: `host.k3d.internal:5433`. The script composes it from
  `POSTGRES_GAME_*` in `.env` plus the cluster kind, with `POD_GAME_DB_URL` as a wholesale
  override, and **never logs it** — it carries a password, which is also why it is in the Secret
  and not in `gameserver-config`. On `docker-desktop` the value is deliberately **empty**: no
  host string makes the compose postgres addressable from a pod there. The fleet therefore reads
  the key with `optional: true`, matching `advertise-host` and not `redis-addr` — blank is a
  handled state (`using in-memory player store (GAME_DB_URL unset …)`), while a *set but
  unreachable* DSN makes the server log `postgres player store unavailable -- refusing to start`
  and exit 1, i.e. a visible crash loop rather than a silent RAM-only world.

  Measured from a pod in `rpg-realtime`:
  `psql -h host.k3d.internal -p 5433 -U game -d gamestate -c '\dt'` → `player_states`,
  `schema_migrations`. Proven end to end on k3d-rpg-dev with `map_01` served **only** by an
  Agones pod (compose `rpg-gameserver` stopped for the run, so the ADR-2 one-server-per-map
  invariant held), `smoketest --strict-addr --require-db`:

  ```
  PASS  gateway_auth                5ms  transport=tcp map=map_01 server=127.0.0.1:7052 (tcp)
  PASS  gameserver_join          1.113s  snapshots=15 (keyframes=1 deltas=14) final_x=4.83 ack_tick=10
  PASS  gamestate_migrations       18ms  version=1 (001_init) applied=2026-08-05T07:34:08Z
  PASS  gamestate_player_row     13.03s  map=map_01 x=4.8333 y=0.0000 hp=100/100 (14 polls)
  PASS  gamestate_reload        20.113s  respawned at x=4.8333 from persisted x=4.8333
  SMOKE=PASS
  ```

- **`agones/secret-example.yaml`** — template for the `rpg-realtime-secrets` Secret, enumerating
  every secret the fleet needs (`jwt-secret`, `join-token-secret`, `redis-password`,
  `transport-key`, and `game-db-url` for when persistence is turned on) with dev placeholders
  identical to `.env.example`. Its `metadata.name` is deliberately `rpg-realtime-secrets-example`
  so `kubectl apply -f agones/` cannot overwrite a real Secret with published placeholders.
- **`docker-compose.agones.yml`** — opt-in overlay that mounts a read-only kubeconfig into the
  gateway container so the compose-run gateway can allocate **out-of-cluster**. ADR-15
  decision 3 item 6: `resolveRESTConfig` tries in-cluster SA → `ALLOCATOR_KUBECONFIG` →
  `$KUBECONFIG` → `~/.kube/config`, none of which exist in the container, and
  `cmd/gateway/main.go` treats a failed allocator construction as fatal — so `ALLOCATOR=agones`
  without it does not start degraded, it does not start. Kept as an overlay because compose
  creates a *directory* in place of a missing bind source, which would plant an empty
  `kubeconfig.local/` in every no-cluster stack.
- **`ALLOCATOR*` settings on the compose `gateway` service and in `.env.example`**, defaulting
  to `ALLOCATOR=none` so the ordinary stack still needs no cluster. `ALLOCATOR_FLEET_MAP`
  defaults to `map-servers-dotnet-dev` because the gateway's compiled-in `DefaultFleetMap` is
  still `map-servers-dev` — the retired Go fleet.
- **`org.opencontainers.image.revision` on the gameserver image**, from a `GIT_REVISION` build
  arg, plus `validate-manifests.py --check-image/--expect-revision` to assert it. A mutable
  `:dev` tag is a claim about content, not a fact: the `:dev` in the local store on 2026-08-17
  was built **5.4 hours before** the commit that added the real Agones SDK, so a fleet deployed
  from it would have come up green running the no-op SDK. `cd.yml` should pass the arg too; it
  lives outside `deploy/` and is unchanged here.
- **Contract checks in `k3s/validate-manifests.py`** for the invariants no CRD schema can
  express: a port named `game` under `portPolicy: Dynamic`; `POD_NAME` from
  `fieldRef: metadata.name` **and no `GAMESERVER_ID`** (it wins over `POD_NAME`, so it would
  give every pod the same server id and make every join fail the `sid` check); no literal
  values for secret-bearing env vars; no `GAMESERVER_PUBLIC_ADDR`; no `rpg-mmo/gameserver:`
  image; `replicas > 1` with a fixed map id; `Buffer`-only autoscaler policies; and
  autoscaler/allocation targets that name a real fleet. It also `[warn]`s when the gateway's
  `DefaultFleetMap` / `DefaultNamespace` / `gamePortName` do not match the manifests.

### Changed
- **Documented that `Allocated` is a one-way door, and what it blocks** (`docs/K3S.md` →
  "Health, measured"). A `Fleet` will not scale down an `Allocated` GameServer, and this project
  has no `Deallocate` anywhere — the state is left only by shutting the pod down. The C# server
  reports `Allocate` to the sidecar on the first player join (`NotifyAgonesAllocatedOnce`), so a
  single smoke run pins a pod there permanently. Useful as evidence that the Allocate report
  works against a real sidecar; costly as an operational trap, because an `Allocated` pod
  satisfies the replica count and therefore **`kubectl apply` of a changed template creates the
  new GameServerSet but never rolls a pod onto it**. Delete the stale GameServer explicitly.
- **Agones health is ENABLED on `map-servers-dotnet-dev`** — `health.disabled: true` is gone
  from `agones/fleet-map-dotnet-dev.yaml`; the three timings (`initialDelaySeconds: 10`,
  `periodSeconds: 5`, `failureThreshold: 3`) are unchanged, because the Agones v1 `health` block
  treats all four fields independently. This is ADR-14 decision 4's second half, deliberately a
  step of its own: stage 4 deployed *with* the flag so a pod that could not reach Ready read as
  exactly that instead of as a restart loop hiding which of image / secret / sidecar / SDK was
  wrong. That passed, so the flag comes off.

  **No flapping.** Four independent windows on k3d-rpg-dev (image
  `rpg-mmo/gameserver-dotnet:integration`, revision `4d928e7`, whose `gameserver-dotnet/` and
  `shared/` trees are identical to `develop`; both halves then re-proven on
  `rpg-mmo/gameserver-dotnet:develop`, revision `307f1e8`), sampled every 30s: pod `…-q7bdn-qjgl8` held
  **11m18s** at `RESTARTS: 0`, pod `…-q7bdn-5bqwx` held **12m22s** at `RESTARTS: 0`, and pod `…-q7bdn-9vxgc` held **12m26s** at
  `RESTARTS: 0`. A fourth window on `:develop` itself held **13m25s** at `RESTARTS: 0` and was
  still `Allocated` when observation stopped. None produced an `Unhealthy`
  event on the GameServer; the first two were ended by *another operator* scaling the fleet
  1→3→1, not by health.

  **And the ping is really enforced**, which `RESTARTS: 0` alone cannot show. `SIGSTOP` on the
  `./GameServer` process (through the k3d node container — the image is distroless, so
  `kubectl exec` has no shell) simulates a tick loop stalled long enough to starve the health
  loop: `Ready` → `Unhealthy` → `Shutdown` in **~20s**, inside the 25s that
  `initialDelaySeconds + periodSeconds * failureThreshold` predicts, with
  `Warning Unhealthy … Health check failure` and `Deleted gameserver in state Unhealthy` in the
  events, and a replacement Ready ~25s later.

  So enabling this **couples availability to simulation latency**: the health ping shares a
  process with the 60Hz tick loop, and a stall past 15s is now a deleted pod. ADR-13's overload
  path is what should keep a merely-slow server from being killed as a dead one, and the CPU
  limit was already raised 500m → 1000m for the same reason. **That path is not proven**: every
  `backend/loadtest` run returns `INVALID: N/N players failed` with
  `sample_errors: ["join: join: recv: EOF"]` while the server logs the joins and snapshots flow —
  and it reproduces identically against the compose server and through the gateway, so it is a
  pre-existing loadtest↔server mismatch on `develop`, not anything to do with Agones. Sustained
  overload survival is therefore **untested**; see `docs/K3S.md` → "Health, measured".
- **Three cluster facts replaced with measurements** — all three were previously shipped as
  parameterised guesses with a "verify, do not assume" note, and all three now have answers:
  - **k3d pod → compose data tier is `host.k3d.internal`** (`redis-cli -h host.k3d.internal
    -p 6379 ping` → `PONG` from a pod). `host.docker.internal` and `172.17.0.1` also answer
    there and are still not used: the first is a Docker Desktop convention that happens to be
    inherited, so it quietly implies a Docker Desktop that may not be present; the second is an
    unstable bridge IP.
  - **Gateway → k3d API server now joins k3d's Docker network** instead of going through
    `host.docker.internal`. client-go verifies the API certificate, and `host.docker.internal`
    is **not** a SAN on the k3d cert, so that route needs either TLS verification disabled — not
    acceptable in a service holding allocation credentials — or the cluster recreated with an
    extra `--tls-san`. `k3d-<cluster>-serverlb` **is** a SAN, so attaching the gateway to the
    external `k3d-<cluster>` network and dialling `https://k3d-<cluster>-serverlb:6443` verifies
    as itself with no cert change and no recreate. Confirmed in this worktree:
    `kubectl get nodes` through the rewritten kubeconfig returns
    `k3d-rpg-dev-server-0 Ready control-plane,master`, with no `-k` and no
    `insecure-skip-tls-verify`. The network name and serverlb hostname are cluster-name-derived
    (`k3d-<cluster>`), so `K3D_NETWORK` is a parameter, not a constant.
    Two consequences worth stating because both fail quietly: the gateway is attached to **both**
    `default` and the k3d network, since naming `networks:` on a service *replaces* its network
    list rather than adding to it (k3d-only would leave a working allocator on a gateway that
    cannot reach redis); and the k3d network is `external`, which is the second reason this stays
    in the overlay — compose refuses to start when an external network is missing.
  - **The documented verification command must be cwd-relative.** `-v "$PWD/kubeconfig.local:…"`
    fails on this WSL2 box: `docker` is Docker Desktop's shim, absolute `/mnt/*` paths do not
    translate, and the mount silently becomes a **directory** (`read /kc: is a directory`, which
    reads like a kubeconfig bug and is not). Same missing-bind-source trap that keeps the mount
    out of the base compose file.
- **`GAMESERVER_ADVERTISE_HOST` is now live in the fleet** (was staged commented-out pending the
  confirmed name). Read from the `gameserver-config` ConfigMap with `optional: true`, which is
  the right call here and is *not* the `REDIS_ADDR` case: absent-or-blank is a meaningful,
  handled state — advertise `status.address` unmodified — rather than a silent
  misconfiguration. It is **host only**; the composed result is
  `<GAMESERVER_ADVERTISE_HOST or status.address>:<Agones-assigned port>` and the port is never
  configurable, since supplying it is the entire purpose of the sidecar status read.
  `GAMESERVER_PUBLIC_ADDR` stays unset and must not be set alongside it — it is read only when
  Agones is off.
- **Wrote down that this does not scale past one node.** `GAMESERVER_ADVERTISE_HOST` works
  because the cluster is a *single node with a published port range*, so one well-known host
  serves every pod. That is a property of the deployment, not of the code. On a multi-node
  cluster the correct host differs per pod — it depends where the scheduler placed it — and one
  env var on the fleet cannot express a per-pod value; the answers are an ingress or a per-node
  value through the downward API (`spec.nodeName` / `status.hostIP`), and neither exists here.
  Stated in `docs/K3S.md` beside the address table, and repeated at both places that set the
  value, so the k3d result is not mistaken for the address problem being solved in general.
- **Prepared the client-address host override, commented out on purpose** *(superseded by the
  entry above — the name was confirmed as `GAMESERVER_ADVERTISE_HOST` and the block is live)*. The Agones status read
  is not sufficient on either local cluster: `status.address` is the node address, measured as
  `192.168.65.3` on docker-desktop (unreachable from Windows and WSL2 — Docker Desktop publishes
  Docker ports to the host but not Kubernetes `hostPort`, so **no** host string helps) and
  `172.20.0.3` on k3d (the node container's Docker-network address, where the working client
  address is `127.0.0.1:<agones-assigned-port>` via the serverlb). The client address is therefore
  composed from two sources — **port** from the status read, which is the only thing that knows
  the per-pod port, and **host** from configuration — which is why the game server's override is a
  *host* and not `GAMESERVER_PUBLIC_ADDR`. `setup-dev.sh` now writes an `advertise-host` key into
  the `gameserver-config` ConfigMap (`127.0.0.1` on k3d, empty on docker-desktop where no value
  can be right), and the fleet carries the matching env block **commented out**: the game-server
  side is in flight and the variable name is not confirmed, and an env var the binary does not
  read looks configured while doing nothing. Uncommenting is the whole change once the name lands.
  Consequence for the staged plan: **ADR-14 stage 4 is provable on docker-desktop, stage 5 is
  not** — no client can reach an allocated pod there at all. Stage 5 needs k3d.
- **Rewrote `agones/fleet-map-dotnet-dev.yaml`** into a deployable fleet for ADR-14 stage 4:
  secrets via `secretKeyRef` instead of the literals `dev-secret-change-me` /
  `dev-join-secret-change-me` that were committed in the manifest; configuration moved to the
  env block (`AGONES_ENABLED`, `GAMESERVER_MODE/MAP_ID/ADDR`) so there is one channel, not two;
  `REDIS_ADDR` from the `gameserver-config` ConfigMap and **not** `optional`, because an unset
  `REDIS_ADDR` is not an error in the server — it just runs unregistered, so the pod is Ready,
  the logs clean, and the gateway still cannot find it. `replicas: 1` documented as
  load-bearing. CPU limit raised **500m → 1000m**: the benchmark measured 47.3% of one core at
  200 players, i.e. inside a 500m limit only by rounding, and CFS throttling there shows up as
  missed ticks — which, once health is enabled, is a killed pod. Memory left at 128Mi/256Mi
  against a measured ~30 MiB idle / ~82 MiB peak.
- **Rewrote the `health.disabled: true` comment, because the old one is now false.** It stated
  the C# Agones SDK is a no-op; commit `62131f5` landed `HttpAgonesSdk`, which really does POST
  `/ready`, `/health`, `/allocate` and `/shutdown`. The flag stays on for the reason that is
  actually true: none of it has ever run against a real sidecar (the tests stand a local
  `HttpListener` in for one), and ADR-14 stage 4 wants a pod that fails to reach Ready to read
  as exactly that, rather than as a restart loop hiding which of image / secret / sidecar / SDK
  was wrong. Removing the flag remains its own step with its own check.
- **`k3s/setup-dev.sh`**: sources secrets from `../.env` — the same file the compose-run gateway
  reads, which is how the two `JOIN_TOKEN_SECRET`s are kept equal (they differ silently, and
  every join then fails signature verification with nothing logging the cause); detects the
  cluster kind from the kubectl context; verifies the image's git revision and imports it on
  k3d; picks the pod→host redis address per cluster kind with a `POD_REDIS_ADDR` override.
  Dropped `--with-dungeon`, `--with-autoscaler` and `--prod-fleets` with the manifests behind
  them; added `--skip-image-check`.
- **Documented what `docker-desktop` cannot do.** Measured: a probe GameServer's dynamic
  `hostPort` answered on the node IP from inside the cluster and was unreachable from both
  Windows and WSL2, while a compose-published port on the same host answered fine — Docker
  Desktop publishes Docker ports to the host but not Kubernetes `hostPort`. Since the client
  dials the game server directly (ADR-3), **stage 4 is provable there and stage 5 is not**.
  `host.docker.internal` and image-store sharing are likewise Docker-Desktop behaviours; both
  are now parameterised per cluster kind rather than assumed, with k3d's `host.k3d.internal`
  marked unverified.
- **`docs/K3S.md`** rewritten around the one fleet: the secret sync procedure, the
  rebuild→verify→import→apply sequence, why there is no autoscaler, why
  `GAMESERVER_PUBLIC_ADDR` is absent, the `ALLOCATOR_FLEET_MAP` override the gateway needs,
  the out-of-cluster kubeconfig wiring (including the k3d API-server URL rewrite and the TLS
  SAN it requires), and the server-side dry-run output. `docs/README.md` and `docs/CICD.md`
  updated for the deleted manifests; `docs/README.md`'s gameserver build command was also
  wrong twice — it tagged `rpg-mmo/gameserver:dev` (the deleted Go server) with context
  `../gameserver-dotnet` instead of `..`.
- **Corrected a false comment in `docker-compose.yml`** claiming the C# arg parser only matches
  space-separated flags. `GameServer/Program.cs GetArg` handles `--addr=:9000` too.

### Changed (earlier in this Unreleased cycle)
- **`agones/fleet-map-dotnet-dev.yaml` now sets `health.disabled: true`** (ADR-14 decision 4).
  The flag stays, but **the reason recorded here has been superseded** — it said the C# Agones
  SDK is `NoopAgonesSdk`, which stopped being true when `62131f5` landed `HttpAgonesSdk`. See
  the health entry above for the reason that is currently true. Left in place rather than
  edited away, because "the changelog said the SDK is a no-op" is what sends someone looking
  for a no-op that is not there. `initialDelaySeconds`/`periodSeconds`/`failureThreshold` are
  kept: the Agones v1 Fleet `health` block accepts all four fields independently (verified
  against CRD `fleets.agones.dev`), so the timings stay in the file and are inert while
  `disabled` is set.
  Removing the flag is ADR-14 stage 4 — after `HttpAgonesSdk` lands and the pod is shown to
  stay Ready over a sustained run, and not before.
- **The four Go-image fleet manifests are marked ⚠️ SUPERSEDED rather than deleted**:
  `fleet-map.yaml`, `fleet-map-dev.yaml`, `fleet-dungeon.yaml`, `fleet-dungeon-dev.yaml`. Their
  images come from `backend/gameserver/`, deleted in `670a803`, and `docker/Dockerfile.gameserver`
  went with it, so `rpg-mmo/gameserver:dev` cannot even be rebuilt. They are kept because the
  `docker-desktop` cluster is *still running* `map-servers-dev` and `dungeon-servers-dev` from
  those files; deleting the manifests would leave live fleets with no source describing them.
  Deletion belongs to ADR-14 stage 8, together with retiring the running fleets.
  `autoscaler.yaml`, `autoscaler-dev.yaml` and `allocation-dev.yaml` gained notes recording that
  they still select those superseded fleets, not the C# one.
- **`docs/K3S.md`** gained a "Which fleet is real" section: what the fleets are for, why the
  dotnet fleet's health is disabled, and the order in which Agones becomes real (SDK →
  deploy the dotnet fleet → prove no restart loop → `ALLOCATOR=agones` → `ALLOCATED` off 0).
  The manifest table now lists `fleet-map-dotnet-dev.yaml` and flags the superseded files.

  **Follow-up, needs a human and is not part of this change:**
  `kubectl -n rpg-realtime delete fleet map-servers-dev dungeon-servers-dev`. Both are Ready,
  13 days old, `ALLOCATED 0`, running deleted source. Nothing here was applied to any cluster.

### Fixed
- **`docs/K3S.md` "Images" told you to build with a Dockerfile that no longer exists.** The
  build command and the k3d/k3s import rows referenced `docker/Dockerfile.gameserver` and
  `rpg-mmo/gameserver:dev`; both now use `Dockerfile.gameserver-dotnet` /
  `rpg-mmo/gameserver-dotnet:dev`. The "Reality-pass applied to the fleets" bullets about
  SDK-driven health are labelled as describing the Go fleets, since they are false for the C#
  one.
- **The post-deploy smoke test dialed default ports, not the environment's published
  ones.** Production is the first environment to move off the default port numbers. Its
  deploy succeeded and the stack was healthy, yet `Post-deploy smoke test` failed on its
  very first step. Two addresses were wrong, both in the same way:
  - `NAKAMA_URL` was never written into the generated `deploy/.env`, so the smoke test fell
    back to its own `DefaultNakamaURL` of `http://localhost:7350` while production
    publishes Nakama on `NAKAMA_HTTP_PORT=7360`. Nothing listens on 7350 there.
  - `GATEWAY_ADDR` in `deploy/.env` is the address the gateway listens on *inside* its
    container (`:8000`). The published port is `GATEWAY_CONTAINER_PORT`, which production
    sets to `8010`, so the gateway hop would have failed next.

  Both are now exported in the `post-deploy-smoke` step *after* it sources `deploy/.env`,
  derived from `NAKAMA_HTTP_PORT` and `GATEWAY_CONTAINER_PORT`. Overriding in the step
  rather than in the generated file matters twice over: `GATEWAY_ADDR` also feeds the
  derivation of `GATEWAY_CONTAINER_PORT` and must keep naming the port the container's
  hardcoded `--addr=:8000` binds, and a host-mode game server inherits `deploy/.env`,
  where `NAKAMA_URL` being unset is precisely what disables its Nakama S2S integration.

  Why this survived two environments: dev and staging pass only because their ports happen
  to equal the smoke test's own defaults, so the whole class of bug is invisible until an
  environment moves off them. The deploy job's healthcheck could not catch it either — it
  probes the *metrics* ports, which were already forwarded per-environment, so it reports
  green while the client-facing path is unreachable. The game-server hop needed no fix: the
  smoke test follows `EnterWorldResponse.ServerAddr`, i.e. `GAMESERVER_PUBLIC_ADDR`, which
  the "Write environment file" step already validates.

### Fixed
- **A brand-new environment could never complete its first deploy.** Schema migrations ran
  in `db-migrate`, which is ahead of `deploy` in the job graph. On an environment that has
  never been deployed the database does not exist yet, so `--migrate-only` failed on
  connect, `deploy` was gated on that migration, and `deploy` is the only job that would
  have created the database it was waiting for. The first production deploy failed exactly
  that way — `Failed to connect to 127.0.0.1:5443` — after having already pushed its images
  to GHCR.

  The ordering that put migrations first was protecting something real: the schema must be
  migrated before any new binary serves traffic. That is kept. `deploy` now brings up the
  **data tier alone** (postgres, redis, nakama — no `realtime` profile), runs the
  migrations against it, and only then starts the gateway and game server.

  Two details in the data-tier step are deliberate. It omits `--remove-orphans` and the
  `realtime` profile, so it cannot disturb a running gateway or game server while the
  schema changes. And it waits on `pg_isready` rather than on compose reporting the
  container started — on a first deploy postgres still has an empty data directory to
  initialise, and "container running" is not "accepting connections".

  `db-migrate` is now backup-only and its display name says so. Its job id is unchanged
  because `deploy` depends on it, and the pre-deploy dump is still what gates the deploy.

- **Two environments on one runner could not coexist, and the failure was silent.**
  `docker-compose.yml` already parameterises every container name
  (`COMPOSE_NAME_PREFIX`) and every published port for exactly this case — its header
  documents running "a SECOND, isolated stack beside a live one" — but `cd.yml` never
  forwarded those variables into the `.env` it generates. The consequences, all of which
  applied to `dev`, `staging` and `production` simultaneously because the sole runner
  carries all three labels:
  - Container names were always `rpg-*`, so a deploy **replaced the other environment's
    containers** rather than standing beside them. Setting a different `RPG_DEPLOY_DIR`
    did not help: the directory changed, the container names did not.
  - `postgres`, `postgres-game`, `redis` and `nakama` published on fixed ports
    underneath, so even distinct container names would have collided on binding.
  - The compose **project** was fixed, so two environments shared one network and one set
    of named volumes — meaning shared postgres and redis *data*. Worse than the name
    collision, because nothing reports it until the data is wrong.
  - `backup.sh` / `redis-backup.sh` resolve containers from `META_CONTAINER`,
    `GAME_CONTAINER` and `REDIS_CONTAINER`, which CD also never set. A prefixed
    environment would have dumped **another environment's** databases and reported
    success — and the migration that dump exists to protect would then run with no
    usable checkpoint. Wrong target, green result.

  `cd.yml` now forwards `COMPOSE_NAME_PREFIX`, `COMPOSE_PROJECT_NAME`, the postgres,
  redis and nakama ports, and the three backup container names. **Every default
  reproduces the value in use today**, so an environment that sets none of them behaves
  exactly as before — importantly including `COMPOSE_PROJECT_NAME`, whose default is the
  compose file's own `name:`; renaming a live project would orphan its containers and the
  volumes holding their data.
- **GHCR registry path changed from `ghcr.io/dycuong03/` to `ghcr.io/cuvara/`** in
  `cd.yml` and all deploy docs. The personal account registry returned `permission_denied`
  on production deploys; the org registry matches the repo ownership.
- **Production environment variables provisioned.** Added `GAMESERVER_PUBLIC_ADDR`,
  `GAMESERVER_CONTAINER_PORT`, `GATEWAY_CONTAINER_PORT`, and `REDIS_PASSWORD` to both
  staging and production GitHub environments. `GAMESERVER_PUBLIC_ADDR` is a placeholder
  (`<VPS_PUBLIC_IP>:9200`) — replace when VPS is provisioned.

### Added
- **`workflow_dispatch` trigger on `publish-shared-gamelogic.yml`** — allows manual
  re-run of the Shared.GameLogic publish pipeline without a code change.

### Added
- **`stack.sh` — one command that brings the whole backend up locally.**
  Everything needed to bring up a stack a client can actually connect to
  existed, but it was six manual steps spread across two docs (copy `.env`,
  build the Nakama plugin, build two images, `up`, `up --profile realtime`, find
  the secrets), and nothing told you whether the game server had registered
  itself — the one condition that decides whether `MsgEnterWorld` can be
  answered at all.

  ```bash
  cd backend/deploy
  ./stack.sh up      # build every image + start everything + wait for the registry
  ./stack.sh check   # drive the full client flow through it (smoketest)
  ./stack.sh down    # stop (--wipe to drop the data volumes too)
  ```

  Also `health` (probes every health endpoint **and** reads
  `servers:map:<map_id>` out of Redis, so "the game server is invisible to the
  gateway" is a distinct, named failure rather than a mystery), `ps` and `logs`.
  `--no-build` skips the image builds.

  It is a shell script, not a Makefile target, because **`make` is not installed
  on this project's dev box**; the `flow-*` Make targets are thin wrappers so
  both spellings work, and nothing in the documented path requires `make`.

- **`stack.sh up --scratch`** — a second, fully isolated stack (own compose
  project, own container names `rpgs-*`, own volumes, every published port
  offset). Without it there is no way to test a compose change on a machine that
  already has a stack up, and the failure mode of trying is bad: compose
  **adopts and recreates** the running containers with your `.env`, printing a
  normal successful recreate while silently replacing someone else's
  environment. That happened once while building this, to the live dev stack;
  it was restored by re-running CD's compose file, and the isolation flag exists
  so it cannot happen again.

### Changed
- `docker-compose.yml`: `container_name` is now `${COMPOSE_NAME_PREFIX:-rpg}-*`
  and Nakama's four published ports are env-driven
  (`NAKAMA_{GRPC,HTTP,CONSOLE,METRICS}_PORT`). Defaults are unchanged, so every
  existing command, script and CD path behaves exactly as before; this only
  makes an isolated second stack expressible.

### Fixed
- **CD generated a hostless `GAMESERVER_PUBLIC_ADDR`, silently reintroducing the
  bug it was supposed to deploy the fix for.** `cd.yml`'s env-file generator
  defaulted the value to `:${GAMESERVER_CONTAINER_PORT}` whenever
  `vars.GAMESERVER_PUBLIC_ADDR` was unset, on the same false premise corrected
  elsewhere — that clients normalize a listen-style address to loopback. Only
  some do: the Go smoketest rewrites it, a C# `TcpClient` throws on it. The
  value reaches clients verbatim, so a hostless one fails two steps later, in
  the client, where nobody looks.
  This was not theoretical. A manual fix to the deployed artefact was overwritten
  by the next CD run, which put `:9200` back into the live registry; the only
  reason clients kept connecting was the defensive normalization on the client
  side, which was never meant to be load-bearing.
  The generator is now environment-aware and refuses to emit an undialable value:
  - **dev** — defaults to `127.0.0.1:<port>` with a `::notice::`. The dev box is
    the client's host, so loopback is correct there and nothing better can be
    inferred without knowing the operator's network. The notice names the case
    where it is wrong (a phone on the LAN) and how to override it.
  - **staging / production** — no default. An unset value is a hard `::error::`
    and the deploy fails, because loopback would tell every client to dial
    itself and would do so silently.
  - **any environment** — an explicitly set but hostless value is also rejected.
    The host list (`""`, `0.0.0.0`, `::`, `[::]`) matches `NormalizeDialAddr` in
    `backend/smoketest/smoke/helpers.go` so both ends agree on what counts as
    listen-style. Bracketed IPv6 (`[2001:db8::1]:9200`) passes through unchanged.
  Logic exercised across 11 input/environment combinations before landing.
  The compose default is deliberately left hostless: `127.0.0.1` would be wrong
  for a VPS, and the generator is the right place to make the decision because it
  is the only layer that knows which environment it is deploying to.
- **`GAMESERVER_PUBLIC_ADDR`'s comment documented the opposite of the actual
  contract.** It claimed a bare `":9200"` "is normalized by the client to
  127.0.0.1:9200, which is right for a local stack". That is not the contract:
  `GameServer/Program.cs` states the advertised address is handed to clients
  verbatim and so must be **dialable by the client**, and the repo's own
  integration test configures a host-qualified value and asserts it comes back
  unchanged (`backend/integration_test/selfreg_flow_test.go`). Only the Go
  smoketest rewrites listen-style addresses, and it does so defensively for
  arbitrary deploys — it is not a guarantee the protocol makes. Taking the old
  comment at face value cost real debugging time: a C# `TcpClient` throws on a
  hostless address where Go's `net.Dial` resolves it, so a Unity client failed
  the second hop against a stack that looked correct. The comment now states the
  real requirement (host-qualified whenever ports are published, bare `":port"`
  only for host mode) and flags that the default value below it is hostless.

### Fixed
- **Scratch/second-stack configuration passed through exported environment
  variables was silently ignored.** On this project's dev box `docker` is a
  shell shim to the Windows `docker.exe` (`docs/CICD.md` §4a), and WSL only
  forwards an environment variable to a Windows process when it is listed in
  `$WSLENV`. So `export COMPOSE_PROJECT_NAME=… ; docker compose up` reaches
  compose with the variable **unset** and operates on the default project —
  with no warning, and with output that looks like success. Compounding it, the
  compose file's top-level `name:` beats `COMPOSE_PROJECT_NAME` anyway.
  `stack.sh` therefore passes configuration with `--env-file` and `-p`, never
  through the environment. Documented in the runbook alongside the existing
  `$PWD` bind-mount trap, since it is the same class of WSL-interop bug.

- **`JOIN_TOKEN_SECRET` was never wired into any deployment path**, so both
  realtime containers crash-looped on startup: `rpg-gateway` and `rpg-gameserver`
  each logged `JOIN_TOKEN_SECRET is required but not set -- refusing to start`.
  The split secret landed in the binaries (#22) but no deploy config supplied it.
  Now plumbed through every path that already carried `JWT_SECRET`:
  - `docker-compose.yml` — added to the `gateway` and `gameserver-dotnet`
    services, both reading the same `${JOIN_TOKEN_SECRET}`.
  - `.env` / `.env.example` — new `JOIN_TOKEN_SECRET` entry with a dev default.
  - `k3s/setup-dev.sh` — new `join-token-secret` key in `rpg-realtime-secrets`.
  - `agones/fleet-{map,dungeon}.yaml` — `secretKeyRef` to that key, **not**
    `optional`, so a missing secret fails container creation instead of
    crash-looping. `fleet-map-dev.yaml`, `fleet-map-dotnet-dev.yaml` and
    `fleet-dungeon-dev.yaml` get the literal dev value.
  - `scripts/deploy-local.sh` — exported for host mode (the C# arg parser only
    matches space-separated flags, so `--jwt-secret=X` was always inert there).
  - `.github/workflows/cd.yml` and `scripts/setup-github-env.sh` — new required
    secret, rejected when it equals `JWT_SECRET`.
- **`ci-dotnet.yml` could hang for six hours and say nothing.** Since 2026-08-08
  three runs (two on `develop`, one on a PR) have had their `Test` step stop
  emitting output partway through and run until the 6-hour default job timeout
  cancelled them. The suite normally finishes in ~3 minutes.
  - Both jobs now carry `timeout-minutes` (20 test, 25 publish), so a hang costs
    minutes of runner time instead of hours.
  - `dotnet test` runs under `--blame-hang --blame-hang-timeout 8m`, which turns
    a hang into a **failure that names the hung test** and writes a
    `Sequence_*.xml` beside the results. Today a hang produces no `.trx` at all,
    so the artifact step reports "no files found" and the run tells you nothing
    about which test never returned.
  - The artifact upload now collects `Sequence_*.xml` too, and declares
    `if-no-files-found: warn` rather than relying on the default.
  - **It worked**: the next run failed in 9 minutes with a hang dump attached
    instead of going silent for six hours, and the dump named a live-locked
    `Connection.Dispose()` spinning on a `Connection.Close()` on its own stack.
    Root cause and fix are in the gameserver module's changelog. The test
    `--blame-hang` named was a bystander, as its own warning says it may be.

### Changed
- **G11 re-confirmed against `e3909d3`** — the one drill result that could not be
  trusted after #22 rewrote the auth path (rate limiting, split
  `JOIN_TOKEN_SECRET`, KCP encryption). Re-measured on the deployed stack:
  behaviour is unchanged. With Redis down the gateway still answers `MsgAuth`
  with **nothing at all** — the client burns its full 10.009s deadline, and the
  gateway emits **zero application-level log lines** for the entire outage.
  `DISASTER-RECOVERY.md` gains a dated re-confirmation section.
  - The gateway was verified to be on `--backend=redis` **before** the drill
    rather than after. G10 means an unset `REDIS_ADDR` silently selects the
    in-memory backend, which would have made the whole drill measure nothing
    while reporting cleanly.
  - New observation: the go-redis failure changes shape mid-outage, from
    `connect: connection refused` to `lookup redis: i/o timeout` — stopping the
    container removes its Docker DNS record. **G5 is worse than it reads**: the
    unbounded stall budget includes resolver timeouts, not just the 5s dial
    default.
  - The recovery timing from this run is recorded as an **upper bound of 18s, not
    a heal time** — Redis was back 18s before the first probe, so the entry was
    already present when first observed. The measured ~4s self-heal from the
    post-G1 re-run stands as the real figure; this run is not a better one.
- **Retracted the 150-player ceiling from the deploy module's docs.**
  `deploy/CLAUDE.md` and `docs/README.md` still stated the per-game-server
  ceiling "IS measured: 150 players, bottleneck = snapshot JSON serialization".
  Both halves are now false — the figure predates Protobuf, the entity-type enum
  and id interning, which removed 81% of the wire and with it the constraint that
  produced 150. The root `CLAUDE.md` and ADR-7 had already been corrected; this
  module had not, and `deploy/CLAUDE.md` is loaded into agent context, so it was
  actively re-seeding a retracted number.
  - Both files now lead with the figure worth planning on — **45.9 KB/s per
    client at 200 players**, inside ADR-7's mobile threshold, reproduced to 0.3%
    — and state plainly that **the player ceiling is unknown and not measurable
    on the current hardware**, with ADR-7 item 6 named as the ⛔ blocker.
  - The "Game servers @ 150" column is **removed, not updated**. Every value in
    it was tier CCU divided by the retracted figure: arithmetic on a number that
    no longer exists, wearing the confidence of a measurement.
  - `GAMESERVER_CAPACITY=100` is re-described as a **policy limit rather than
    headroom against a measured ceiling** — there is no measured ceiling for it
    to have headroom against.

### Added
- `.env.example` now sets `GAME_DB_URL` instead of only mentioning it in a comment.
  Host-side tools read it — `bin/smoketest` (whose new `gamestate_*` persistence
  checks SKIP without it) and `gameserver-dotnet --migrate-only`. The gameserver
  *container* is unaffected: `docker-compose.yml:238` builds its own DSN from the
  `POSTGRES_GAME_*` values and points at `postgres-game:5432`, and nothing in
  compose substitutes `${GAME_DB_URL}`, so the new value cannot leak into it.

### Removed
- **The `GAMESERVER_METRICS_ADDR=gameserver-dotnet:9101` workaround — deleted.**
  The C# metrics endpoint was pinned to the compose service name because
  `METRICS_ADDR=:9101` produced `http://+:9101/`, which OpenTelemetry's
  `PrometheusHttpListener` pushed through `UriBuilder` — and `UriBuilder` rejects
  the HttpListener wildcards `+`/`*`, so the endpoint never started on any
  platform. `MetricsEndpoint.cs` now rewrites the listener prefix in
  `ConfigureHttpListener` (runs before `Start`), so the wildcard binds for real
  and the deploy-side workaround is dead weight. Reverted to `:9101` in
  `docker-compose.yml`, `.env.example`, `cd.yml` and `scripts/setup-github-env.sh`.
  - Visible effect: the host-mode `gameserver` scrape target
    (`host.docker.internal:9101`) has been **DOWN with a 404 since it was added**
    — it is now **UP**. Both game server targets read UP together for the first
    time.
  - `curl http://127.0.0.1:9101/metrics` from the host now returns `gameserver_*`
    series with **no `Host:` header**. The header requirement was the symptom;
    its disappearance is the proof.
  - CD's post-deploy `probe gameserver` no longer passes a Host header. If that
    probe ever 404s again the wildcard bind has regressed — fix
    `MetricsEndpoint.cs`, do **not** re-pin `GAMESERVER_METRICS_ADDR` to a name.
  - Docs de-workarounded: `MONITORING.md` (section rewritten, bug kept as a
    historical note), `CICD.md` (stale limitation dropped), `VPS-SETUP.md`,
    `RUNBOOK-local-dev.md`, `monitoring/prometheus.yaml`.
- **`scripts/register-gameserver.sh` — deleted.** Its own header said "Delete this
  script the day the C# server registers itself"; that day is here. The C# game
  server now writes, refreshes and removes its own registry entry
  (`gameserver-dotnet/GameServer/Registry/`). The script wrote the entry **once** at
  deploy time with `REGISTRY_TTL=3600` and nothing refreshed it, which is why a Redis
  wipe left every map unjoinable until a human re-ran it (G1), and why a crashed
  server kept black-holing joins for up to an hour (G2). Both gaps are closed.
  - `scripts/deploy-local.sh` no longer calls it, and now exports `REDIS_ADDR`,
    `REDIS_PASSWORD` and `GAMESERVER_PUBLIC_ADDR` so the server it starts can
    self-register.
  - `.github/workflows/cd.yml` no longer bundles, installs or invokes it; the
    "Register the game server in Redis" step is gone from containers mode.

### Changed
- `docker-compose.yml`: `REDIS_ADDR` for the gameserver is no longer a
  set-for-the-future no-op — the server reads it and self-registers. Added
  **`GAMESERVER_PUBLIC_ADDR`**, defaulting to `:${GAMESERVER_CONTAINER_PORT:-9200}`:
  the container listens on `:9000` but is published on 9200, and the gateway hands
  this value to clients verbatim, so it must be the PUBLISHED address. On a VPS set
  it to `<public-host>:<port>`.
- Docs updated to match: `DISASTER-RECOVERY.md` (G1 and G2 marked FIXED, the
  "step people forget" after a Redis restore is gone, replica advice reframed),
  `CICD.md` §2b, `RUNBOOK-local-dev.md`, `DATABASE.md`, `VPS-SETUP.md`,
  `docs/README.md`, plus `backend/docs/ARCHITECTURE-DECISIONS.md` (registry no
  longer has a shell-script writer) and `backend/docs/CORE_FLOW.md`.

### Added
- **`docs/CICD.md` §4a — the `dev` runner's `docker` shim is now documented.**
  It was undocumented tribal knowledge that would baffle anyone debugging a
  failed dev deploy, because it is invisible from every workflow file. Docker
  Desktop's WSL integration is disabled for this distro, so `/usr/bin/docker`
  points at a dead `/var/run/docker.sock` (`curl --unix-socket` → `curl: (56)`),
  and `/usr/local/bin/docker` is a two-line `exec docker.exe "$@"` shim that
  wins because the runner's frozen `~/actions-runner/.path` lists
  `/usr/local/bin` before `/usr/bin`. A CD deploy failed on exactly this after
  a reboot, before the shim existed.
  Documented with it: the path-translation rule the shim forces, which turns out
  to be **narrower and more dangerous than "keep paths cwd-relative"**.
  `docker.exe` does not reject Linux absolute paths, it resolves them against
  the current drive — loudly for `-f`
  (`open E:\mnt\e\…: The system cannot find the path specified`) but **silently
  for bind mounts**: `-v /mnt/e/…:/x` exits 0 with `/x` mounted **empty**,
  because Docker Desktop creates the nonexistent `E:\mnt\e\…` and mounts that.
  **`$PWD` is absolute and therefore affected** — `-v "$PWD:/x"` silently mounts
  nothing while `-v ".:/x"` works. Audited: nothing in the repo trips this
  today (compose bind mounts are relative, the four `db/` scripts use named
  volumes and `docker exec` stdio, `build-all.sh` and the `db/` scripts carry a
  `detect_docker()` fallback). Verified live rather than by reading — the
  prometheus/dashboard/`nakama.so` mounts are all non-empty inside the running
  containers.
- **`docs/CICD.md` §4b — recommendation on enabling WSL integration: not now**,
  with the evidence and a rollback. The only real benefit is removing the silent
  empty-mount landmine, which is latent, not live. Speed is not an argument
  (`docker.exe` measured at ~85 ms/invocation vs ~25 ms native — seconds per CD
  run), and bind-mount throughput is unchanged because the repo and
  `$RPG_DEPLOY_DIR` both sit on `/mnt/e`. Most importantly the toggle **alone
  changes nothing**: `/usr/local/bin` precedes `/usr/bin` in the runner's frozen
  `.path`, so the shim keeps shadowing the native CLI — switching is a two-step
  change (flip the toggle *and* remove the shim), and doing only the first looks
  like the toggle "did not work". Documented switch procedure verifies the
  socket *before* the shim is retired, since removing it with the toggle off
  leaves the runner with no working `docker` at all. The shim stays as the
  rollback.
- `docs/RUNBOOK-local-dev.md`: cross-reference to §4a and the `.` vs `$PWD`
  bind-mount rule.

### Fixed
- **`docs/DISASTER-RECOVERY.md` provenance note was wrong.** The drill writeup
  claimed there is no `$RPG_DEPLOY_DIR/COMMIT` on this host. There is — at
  `/mnt/e/rpg-mmo-deploy/COMMIT`, because `vars.RPG_DEPLOY_DIR` is
  `/mnt/e/rpg-mmo-deploy` and only the `/opt/rpg-mmo` default was checked. Both
  sources agree on `4c4c58a` for the drill window (`COMMIT` was rewritten to
  `184a779` at 10:19 UTC, after the drill ended at 10:11), so **no measured
  value changes** — only the note. Corrected in place, with the correction
  called out rather than quietly rewritten.
- **PRs into `develop` ran no CI at all.** `ci.yml` listed only
  `[main, master]` under `pull_request`, but every feature branch PRs into
  `develop`, so `gh pr checks <n>` answered "no checks reported" — which reads
  like a passing PR. Go changes have been merging into `develop` with zero
  automated validation for the life of the project. Added `develop` and
  `staging` to the `push` and `pull_request` branch lists of both `ci.yml` and
  `ci-dotnet.yml`.
- Removed the `paths:` filter from the `pull_request` trigger of both CI
  workflows. A filtered-out workflow does not run, and GitHub then reports the
  PR as having no checks — the same silent-pass failure mode, in a
  harder-to-spot form, plus a permanent block if a required status check is
  ever added to branch protection. Every PR into a protected branch now runs
  the full suite; the `push` triggers keep their filters. Documented in
  `docs/CICD.md` §6b along with the honest limit that a green `ci-dotnet.yml`
  on a Go-side wire change proves only that C# still builds — real wire-compat
  coverage needs the `backend/integration_test` E2E suite, which runs today
  only in `cd.yml` on push, i.e. after merge.
- **`db/redis-restore.sh --mode live` restored nothing and exited 0** — found by
  running the Redis failure drill, which is the first time either Redis script
  had been executed against a live stack. Feeding it a freshly-taken, freshly-
  rehearsed 5-key RDB wiped `rpg-redis` and brought it back with `DBSIZE 0`,
  printing `restored dataset: 0 keys` followed by `done`.
  Root cause: deleting `appendonlydir` before injecting the RDB is necessary but
  **not sufficient**. With `--appendonly yes` and no AOF manifest on disk, Redis
  7 does not fall back to `dump.rdb` — it initialises an empty dataset and
  writes a fresh AOF base from it (`Server initialized` → `Creating AOF base
  file`, with no `Done loading RDB` line). Not a permissions or path problem;
  Redis simply never opens the RDB. Same reason the Redis manual says to enable
  AOF via a runtime `CONFIG SET`, not by restarting into it.
  The scratch-mode rehearsal could never have caught this: it starts its
  throwaway container with `--appendonly no`, so it exercised a different Redis
  startup path than production. A green rehearsal was evidence about the file,
  never about the restore.
  Fix: `--mode live` now runs a short-lived **seed** container over the live
  volume with `--appendonly no` (which does load the RDB), issues `CONFIG SET
  appendonly yes` so Redis rewrites `appendonlydir` from the loaded dataset,
  waits for `aof_rewrite_in_progress:0` + `aof_last_bgrewrite_status:ok`, shuts
  it down, and only then starts the real container. A hard verification gate
  compares the live key count against the seed's and fails the script on
  mismatch — the old failure was silent, and the silence is what made it
  dangerous. Verified by using the fixed script to recover the stack from a
  deliberately emptied registry: 5 keys back, `SMOKE=PASS`, 8.5s.

### Changed
- **`docs/DISASTER-RECOVERY.md` — the Redis failure drill was executed** (2026-08-06,
  10:03–10:11 UTC, deployed commit `4c4c58a`, recorded from the image tag shared
  by `rpg-gateway` and `rpg-gameserver` since this is a compose host with no
  `$RPG_DEPLOY_DIR/COMMIT`). "Measured results" replaces the placeholder with
  timings, pasted evidence, and an explicit split between what was observed from
  a **natural** event (a container stop; a registration TTL expiring on its own)
  and what required a **forced `DEL`**. The estimate table is left unedited so
  the estimate-vs-reality delta stays visible.
  Headline numbers, all measured: clean Redis restart → verified joinable in
  **2.3s**, RPO 0 (AOF replayed, TTLs preserved absolutely, consumer groups
  intact, no `NOGROUP` loop). In-progress gameplay is untouched — a client held
  **286 snapshots across a 58s Redis outage**. Registry deleted → world
  unjoinable, polled for 70s with no self-recovery: **G1 is now measured, not
  inferred**. Deliberately a pre-G1 baseline; the doc says so and says which row
  should change when self-registration lands.
  Two new gaps filed from the drill: **G11** — with Redis down the gateway sends
  *no* `MsgAuth` response at all (the estimate said it would reject with
  `MsgAuthResp{OK:false}`); clients hang to their own timeout and the gateway
  logs nothing but go-redis pool chatter. **G12** — `servers:map:*` index sets
  carry no TTL while `servers:id:*` hashes do, leaving orphan members; bounded,
  since the gateway `SREM`s them on lookup, but it leaks for maps nobody queries.
  Drill cadence updated: a monthly scratch rehearsal is explicitly *not*
  sufficient, because that is exactly how a completely broken `--mode live`
  survived review.

### Added
- **`db/redis-backup.sh` / `db/redis-restore.sh`** — Redis now has the same
  backup story PostgreSQL has. Redis is a system of record here (server
  registry + event stream, ADR-4), so losing it is not a cache miss.
  `redis-backup.sh` issues `BGSAVE`, waits for `LASTSAVE` to advance, asserts
  `rdb_last_bgsave_status=ok`, streams `/data/dump.rdb` out through
  `docker exec cat` (no `docker cp`: docker.exe rejects absolute `/mnt/*`
  paths), verifies the `REDIS` magic with the same sync+retry the PG backup
  needs on WSL drvfs, then prunes to `--keep`. `redis-restore.sh` defaults to
  a **scratch container** rehearsal on a disposable volume and only touches the
  live instance with `--mode live --yes`. Both modes delete
  `appendonlydir`/`appendonly.aof` before injecting the RDB — with
  `--appendonly yes` Redis prefers the AOF at startup, so the obvious
  "drop dump.rdb in place" restore silently restores nothing.
- **`docs/DISASTER-RECOVERY.md`** — per-dependency blast radius (Redis, meta PG,
  game PG, Nakama, gateway, game server, lgtm): what in-progress players
  experience vs what new logins experience, recovery commands, RTO/RPO, the
  Redis durability config with the commands to verify it is actually in effect,
  a repeatable Redis failure-drill procedure, ten filed code gaps with
  `file:line` evidence, and the replica → Sentinel upgrade path per tier.
  Headline finding: **nothing in the running code ever registers a game
  server** — `scripts/register-gameserver.sh` writes the entry once at deploy
  time with a 3600s TTL and nothing heartbeats it, so any Redis data loss makes
  every map permanently unjoinable until a human re-runs that script.
  The failure drill itself is **not yet measured** (Docker Desktop was paused
  for the whole window); the expectations table is marked estimated-from-code
  and the doc reserves a section for the measured numbers.
  *(Superseded within this same Unreleased block: the drill was executed on
  2026-08-06 — see "Changed" above. Two of the estimated rows turned out wrong.)*

### Changed
- `cd.yml`: the `db-migrate` job now also takes a Redis checkpoint
  (`redis-backup.sh --skip-missing`) alongside the two `pg_dump`s, and the
  bundle ships `deploy/db/redis-backup.sh` + `deploy/db/redis-restore.sh` so
  the scripts exist on the deploy target. The Redis step is **non-fatal**
  (`|| echo "::warning::…"`) while the PostgreSQL dumps stay fatal: the PG dump
  gates a schema migration and deploying past a failed one risks unrecoverable
  data, whereas Redis holds only transient or reconstructible state (ADR-4), so
  a missing Redis checkpoint must never block a deploy.
- `docs/DATABASE.md`, `docs/README.md`: cross-reference the new Redis
  backup/restore pair and the disaster-recovery runbook.

### Fixed
- backup.sh verification flaked on WSL drvfs (/mnt/*) — a dump read
  immediately after write could appear truncated. Verification now syncs and
  retries up to 3 times before declaring the archive unreadable.

### Added
- **`docs/VPS-SETUP.md`** — the canonical, zero-prior-context runbook for
  bringing a new machine online as a deploy target: prerequisites and where to
  get a runner registration token, the one bootstrap command with its full flag
  reference, the **complete** secret + variable catalogue per environment
  (verified to match `cd.yml` exactly — every `secrets.*` and `vars.*` the
  workflow reads is documented, and nothing documented is stale), first deploy
  job-by-job with the resulting `$RPG_DEPLOY_DIR` layout, a verification
  checklist, how to move an environment between machines, and troubleshooting
  for the traps actually hit (runner post-job cleanup vs `RUNNER_TRACKING_ID`,
  concurrency cancellation, GHCR packages private by default, ufw vs Docker's
  `DOCKER-USER` chain, the named-prefix metrics 404).
- **`scripts/setup-github-env.sh`** — creates a GitHub Environment and populates
  every secret and variable `cd.yml` reads. Secrets come from flags, the
  environment, an interactive hidden prompt, or `--generate`, and are passed to
  `gh` on stdin so they never appear in argv. `production` (or `--strict`)
  enforces >= 32 characters and rejects placeholder values (`dev-secret`,
  `localdev`, `password`, `changeme`, `defaultkey`, ...). `--dry-run` prints
  every `gh` command without executing it.

### Changed
- `docs/CICD.md` no longer duplicates setup instructions that now live in
  `VPS-SETUP.md`: §2c (bootstrap), §4 (runner setup) and §8 (moving to a VPS)
  became pointers, and §5 keeps only how the pipeline *treats* secrets
  (required-checks, `umask 077` handling, environment-vs-repo scoping) instead
  of a second copy of the catalogue that would drift. §4 retains the host-mode
  systemd units, which are pipeline behaviour rather than machine setup.
- Root `README.md` gained a **Deploy** section linking `VPS-SETUP.md` as the
  entry point, with the two-command summary.

### Fixed
- Redis now starts with an explicit `--maxmemory-policy noeviction`. This Redis is
  a system of record for the server registry and the event stream, not a cache:
  evicting a registry hash silently drops a live game server out of matchmaking,
  and trimming a stream drops unacked cross-server events. `noeviction` was already
  the Redis default, so behaviour is unchanged — the point is that adding a
  `--maxmemory` limit later can no longer silently turn this into an LRU cache.
  See `backend/docs/ARCHITECTURE-DECISIONS.md`, ADR-4.

### Changed
- Tier cost/CCU tables in `CLAUDE.md` and `docs/README.md` marked as unbenchmarked
  estimates (ADR-7)

### Added
- **Numbered database migrations** for the game-state DB.
  `db/migrations/gamestate/001_init.sql` holds the current schema; every future
  change is a new numbered file. The gameserver applies them transactionally,
  in order, exactly once, with checksum verification of anything already applied
  (see `backend/gameserver-dotnet` CHANGELOG for the runner). The Nakama meta DB
  is untouched — it migrates itself.
- **`db/backup.sh`** — `pg_dump -Fc` of both instances through `docker exec`,
  timestamped into `$BACKUP_DIR` (default `/var/backups/rpg-mmo`) with
  per-database retention (`--keep`, default 7). Every dump is verified with
  `pg_restore --list` and only then renamed off `.partial`, so a corrupt or
  interrupted run never leaves a file that looks like a usable backup.
  `--skip-missing` makes absent containers a warning instead of a failure.
- **`db/restore.sh`** — restores an archive into the live database or, with
  `--target`, into a scratch database so restores can be rehearsed without
  risking live data. Refuses to run without `--yes`, verifies the archive first,
  and prints per-table row counts afterwards.
- **`db-migrate` CD job** between `bundle` and `deploy`: backs up both databases,
  then runs `gameserver-dotnet --migrate-only` against `GAME_DB_URL`. `deploy`
  now depends on it, so a failed migration stops the rollout with the previous
  version still serving. New environment settings: `BACKUP_DIR`, `BACKUP_KEEP`.
- **`docs/DATABASE.md`** — migration workflow (how to add `002_*.sql`, the
  backward-compatibility rule that CD's migrate-before-deploy ordering implies),
  backup/restore usage, and a disaster-recovery runbook covering game-state loss,
  meta loss, a migration that fails mid-deploy, and checksum drift.

### Changed
- `db/init-gamestate.sql` is now documented as a **first-boot seed only** — schema
  changes go into numbered migrations. Its content is unchanged; the header was
  rewritten (and mirrored into the orphaned `shared/storage/pgstore/schema.sql`,
  which a Go test byte-compares against it).
- Bundle validation now also requires `db/migrations/gamestate/001_init.sql`,
  `db/backup.sh` and `db/restore.sh`.

- **Full-docker deploy mode (`vars.DEPLOY_MODE=containers`)** — the `deploy` job
  can now run the realtime services as containers instead of host binaries.
  Same secrets, same ports, same smoke test; the switch is one environment
  variable and is reversible.
  - New steps, all gated on the mode: stop host-mode services (so they release
    the ports), build `rpg-mmo/{gateway,gameserver-dotnet}:<sha>` **on the
    runner** from `docker/Dockerfile.{gateway,gameserver-dotnet}`, bring up the
    compose `realtime` profile alongside `monitoring`, register the game server,
    then probe `/healthz` on both metrics ports plus TCP on both game ports.
    Host mode keeps `deploy-local.sh restart` + `health` untouched.
  - Images are built on the target, not pulled: dev/staging have no registry
    credentials. `build-images` (GHCR) remains the production/k8s path.
  - `deploy` now checks out the repo (needed for the image build) and outputs
    `deploy_mode`; the pipeline summary reports it.
  - New environment variables: `DEPLOY_MODE`, `GATEWAY_CONTAINER_PORT`,
    `GAMESERVER_CONTAINER_PORT`, `GATEWAY_METRICS_PORT`,
    `GAMESERVER_METRICS_PORT`, `GAMESERVER_METRICS_ADDR`,
    `GAMESERVER_PUBLIC_ADDR`. The container ports default to the ports
    `GATEWAY_ADDR` / `GAMESERVER_ADDR` already name, so `:8000` / `:9200` hold
    in either mode.
- **`scripts/bootstrap-vps.sh`** — idempotent one-command VPS preparation for
  Ubuntu 22.04/24.04: Docker CE + compose plugin from the official apt repo, a
  deploy user and directory, a GitHub Actions runner registered and installed as
  a systemd service, and a ufw policy (SSH + game ports tcp/udp open, Grafana
  denied with an `--admin-ip` allowlist option and a matching `DOCKER-USER`
  rule, because Docker's iptables rules bypass ufw). `--dry-run` prints every
  action without executing it.
- **`scripts/register-gameserver.sh`** — writes the game server's Redis registry
  entry (`servers:id:*` / `servers:map:*`), extracted from `deploy-local.sh` so
  both deploy modes share one implementation. `GAMESERVER_PUBLIC_ADDR` is the
  address handed to clients — the single value that must change on a VPS.
- `docs/CICD.md`: §2b (register-gameserver.sh), §2c (bootstrap-vps.sh), §3b
  (deploy modes) and §8 "Moving to a VPS — what actually changes", a single
  table whose punchline is that no code changes.

### Changed
- `docker-compose.yml`: the `realtime` profile now holds the gateway **and** the
  C# game server (`container_name: rpg-gameserver`, published on
  `GAMESERVER_CONTAINER_PORT`, default 9200, plus its metrics port). Both are
  parameterized so CD can hand them the canonical ports.
- `Dockerfile.gameserver-dotnet` documents the metrics port with `EXPOSE 9101`.
- `deploy-local.sh` delegates registry writes to `register-gameserver.sh`.
- `bundle` ships `register-gameserver.sh` and asserts both scripts are present.

### Fixed
- The C# game server's env var names in compose were wrong (`MAP_ID` /
  `SERVER_ID`); the code reads `GAMESERVER_MAP_ID` / `GAMESERVER_ID`, so those
  settings were silently ignored. The `command:` array was also using
  `--flag=value`, which that server's arg parser does not match — configuration
  now goes through the environment, which works either way.
- Removed the dead `gameserver` compose service, which still referenced the
  deleted Go module's `rpg-mmo/gameserver:dev` image.

- CD deployed monitoring config changes without applying them: Prometheus and
  Grafana read their bind-mounted files only at container start, and
  `docker compose up -d` does not recreate a container just because a mounted
  file changed. The deploy job now hashes `deploy/monitoring/` before and after
  the sync and restarts `lgtm` when it differs.
- Game server metrics were reaching Prometheus under **dotted** OpenTelemetry
  names (`gameserver.players.online`), because Prometheus 3 negotiates UTF-8
  metric names and the C# exporter then serves the raw instrument names. Every
  "RPG Gameplay" panel queried the underscore form and returned *No data* while
  the scrape target read UP. `monitoring/prometheus.yaml` now pins
  `metric_name_escaping_scheme: underscores` on both game server jobs.

### Known issues (not fixed here — owned by `agent-gameserver-dotnet`)
- The C# metrics endpoint **cannot bind a wildcard**: `METRICS_ADDR=:9101`
  produces `http://+:9101/`, which OpenTelemetry's `PrometheusHttpListener`
  rejects (`UriFormatException: Invalid URI: The hostname could not be
  parsed`), so `/metrics` and `/healthz` never start. This is why the host-mode
  `gameserver` scrape target has been DOWN. Containers mode works around it with
  a resolvable prefix (`GAMESERVER_METRICS_ADDR=gameserver-dotnet:9101`);
  documented in `docs/MONITORING.md`.

### Added
- **Monitoring now deploys through CD (VPS-ready)** — `cd.yml` brings the
  `monitoring` profile up on every environment; no hand-run `make monitoring-up`.
  - `bundle` stages `backend/deploy/monitoring/` **and** `backend/deploy/db/`
    into the artifact and asserts each mounted file exists. Previously only
    `docker-compose.yml` + `Makefile` + `.env.example` shipped, so a fresh host
    got empty *directories* where `prometheus.yaml` / `init-gamestate.sql` were
    expected — silently wrong config instead of a hard failure.
  - `deploy` installs those trees into `$RPG_DEPLOY_DIR/deploy/`, replacing the
    previous copies wholesale so deletions in git propagate.
  - Compose step runs `docker compose --profile monitoring up -d
    --remove-orphans`, gated on `vars.MONITORING_ENABLED != 'false'` (default
    ON). Disabling it removes the running `rpg-lgtm` rather than orphaning it.
  - Env-file step writes `MONITORING_ENABLED`, `OTEL_LGTM_VERSION`,
    `GRAFANA_USER/ADMIN_PASSWORD/PORT/BIND`, `PROMETHEUS_PORT/BIND`,
    `OTLP_GRPC_PORT/HTTP_PORT/BIND`. New **required** environment secret
    `GRAFANA_ADMIN_PASSWORD` (fails the deploy with `::error` when monitoring is
    enabled and it is unset).
- `scripts/deploy-local.sh health` curls Grafana `/api/health` on
  `GRAFANA_PORT`. Warn-only by design and skipped when `MONITORING_ENABLED=false`
  — observability is off the gameplay critical path, so a dead Grafana must not
  fail a deploy that put a healthy game stack on the box.
- `docs/MONITORING.md` §"Deploying to a VPS": per-environment secret/variable
  table, how staging/production get monitoring (set one secret), and firewall
  guidance — SSH tunnel, Caddy reverse proxy + TLS, ufw allowlist, plus the
  `DOCKER-USER` chain caveat (Docker's iptables rules bypass ufw `INPUT`).
  Guidance only; no proxy is implemented. Also documents the two Grafana gotchas
  found while verifying the deploy: the anonymous-admin default (above) and the
  fact that `GF_SECURITY_ADMIN_PASSWORD` is applied **only when Grafana creates
  the admin user** — rotating the secret against an existing `lgtm-data` volume
  silently keeps the old password, so the doc gives the drop-the-DB procedure
  (`grafana cli admin reset-admin-password` reports success but produces a hash
  that does not authenticate against this image).

### Fixed
- **Grafana was reachable as an anonymous org Admin.** `grafana/otel-lgtm`'s
  `run-grafana.sh` exports `GF_AUTH_ANONYMOUS_ENABLED=true` +
  `GF_AUTH_ANONYMOUS_ORG_ROLE=Admin` whenever the variable is *unset*, so the
  login page was decoration — verified live: `GET /api/org` returned 200 with no
  credentials. The `lgtm` service now always sets
  `GF_AUTH_ANONYMOUS_ENABLED: ${GRAFANA_ANONYMOUS:-false}`. Would have shipped
  an open admin console the moment Grafana was published on a VPS.

### Changed
- `lgtm` service port bindings are parameterised for VPS exposure control:
  Grafana `${GRAFANA_BIND:-0.0.0.0}`, while OTLP and the bundled Prometheus now
  default to `127.0.0.1` — both are completely unauthenticated and nothing
  off-box talks to them yet.
- Grafana admin password env renamed `GRAFANA_PASSWORD` → `GRAFANA_ADMIN_PASSWORD`
  (matches the CD secret name), default `admin` → `localdev` (matches the other
  dev defaults in `.env.example`). Updated in `docker-compose.yml`,
  `.env.example`, `Makefile` (`monitoring-up` banner) and `docs/MONITORING.md`.
  **Action:** update the key in any existing local `backend/deploy/.env`.
- `docs/CICD.md`: `GRAFANA_ADMIN_PASSWORD` added to the required-secrets table,
  new monitoring-variables table, deploy-dir layout lists `deploy/monitoring/`
  and `deploy/db/`.
- `monitoring` compose profile: one `grafana/otel-lgtm` container (Grafana +
  Prometheus + Loki + Tempo + Pyroscope + OTel Collector) on `${GRAFANA_PORT:-3000}`,
  `${PROMETHEUS_PORT:-9090}`, OTLP `${OTLP_GRPC_PORT:-4317}` / `${OTLP_HTTP_PORT:-4318}`,
  persisted in the `lgtm-data` volume. Replaces the hand-rolled Prometheus+Grafana
  pair — fewer moving parts and OTLP ingest is ready for traces/logs.
- `monitoring/prometheus.yaml` mounted over the image's own (its documented
  override point) with scrape jobs for nakama (`nakama:9100`), host-run gateway
  (`host.docker.internal:9102`) and C# gameserver (`host.docker.internal:9101`),
  plus the containerised `realtime` variants.
- Provisioned "RPG Gameplay" Grafana dashboard (`monitoring/dashboards/`): tick
  p99, players online, gateway connections, auth/enter-world failure ratio, save
  and allocation errors, scrape-target health.
- `make monitoring-up|monitoring-down|monitoring-logs|monitoring-targets`;
  `.env.example` gained `OTEL_LGTM_VERSION`, `GRAFANA_PORT`, `GRAFANA_USER`,
  `GRAFANA_PASSWORD`, `PROMETHEUS_PORT`, `OTLP_*_PORT`, `GATEWAY_METRICS_PORT`.
- `docs/MONITORING.md` — rationale, usage, dashboard guide, import-by-ID infra
  dashboards (1860 / 763 / 9628), Grafana Cloud (Alloy) and k3s
  (kube-prometheus-stack) graduation paths.

### Changed
- Containerised gateway (`--profile realtime`) exports metrics on `:9102`
  (`METRICS_ADDR`), published as `${GATEWAY_METRICS_PORT:-9102}`.

### Changed
- Removed `docker/Dockerfile.gameserver` (Go). Added
  `docker/Dockerfile.gameserver-dotnet` (C# .NET 10 NativeAOT multi-stage build:
  `dotnet/sdk:10.0` builder → `distroless/static-debian12:nonroot` runtime).
- `cd.yml` updated to build the C# gameserver image instead of the Go one.
- Added `ci-dotnet.yml` workflow for C# gameserver build + test.

### Changed
- Fleet manifests (`agones/fleet-map.yaml`, `fleet-dungeon.yaml`,
  `fleet-map-dev.yaml`, `fleet-dungeon-dev.yaml`) inject `POD_NAME` via the
  downward API (`fieldRef: metadata.name`). The gameserver uses it as its
  `--server-id`, so the id it registers equals the `gameServerName` the gateway
  receives from a `GameServerAllocation` and signs into the join token.

### Fixed
- k3s bootstrap hardening from the first live run on Docker Desktop Kubernetes:
  kubectl resolution now prefers the binary that actually has a kube context
  (WSL: Linux kubectl often has an empty kubeconfig while kubectl.exe holds
  docker-desktop); agones-system namespace is created before applying the
  pinned install.yaml; agones-sdk ServiceAccount + rolebinding are created in
  the GameServer namespace (Agones only pre-creates them in `default`).

### Added
- `k3s/setup-dev.sh` — idempotent dev-cluster bootstrap: resolves kubectl
  (Linux `kubectl` → `kubectl.exe` → Docker Desktop's bundled path), preflights
  the cluster, installs Agones **1.59.0** (pinned to `agones.dev/agones` in
  `gameserver/go.mod`) with `apply --server-side --force-conflicts` (the CRDs
  exceed the 262 kB client-side apply annotation), waits for `agones-system`
  Available *and* for the `agones.dev/v1` webhook to actually serve Fleets,
  applies namespaces + dev Secret/ConfigMap + fleets, then blocks until a
  `GameServer` reaches `Ready`. Flags: `--with-dungeon`, `--with-autoscaler`,
  `--prod-fleets`, `--skip-agones`.
- `k3s/teardown-dev.sh` — reverse order (autoscalers → fleets → stray
  GameServers → config → namespaces, `--all` also uninstalls Agones);
  `--fleets-only` keeps the namespaces.
- `k3s/lib.sh` — shared helpers covering the WSL2 quirks: kubectl resolution,
  `kube_apply_file`/`kube_delete_file` that always pipe local manifests through
  stdin (kubectl.exe cannot read Linux paths), fail-fast `require_cluster` that
  checks `current-context` before touching the network (an empty kubeconfig
  otherwise makes kubectl burn ~25 s retrying `localhost:8080`), `retry`/`wait_for`.
- `k3s/namespaces.yaml` — `rpg-realtime` / `rpg-meta` / `rpg-data`.
- `k3s/validate-manifests.py` — offline manifest validation. `kubectl apply
  --dry-run=client` cannot check a `Fleet` without a live API server, so this
  extracts each CRD's `openAPIV3Schema` from the pinned Agones `install.yaml`
  (cached under `~/.cache/rpg-mmo/`) and validates with `jsonschema`, translating
  OpenAPI-3.0-isms (`x-kubernetes-*`, `nullable`, boolean `exclusiveMinimum`).
- `agones/fleet-map-dev.yaml`, `agones/fleet-dungeon-dev.yaml` — dev variants
  using the local `rpg-mmo/gameserver:dev` image with
  `imagePullPolicy: IfNotPresent` (the ghcr.io image is not published yet),
  literal env, and **no external dependencies** (in-memory registry + player
  store) so they reach `Ready` on a bare laptop cluster.
- `agones/autoscaler-dev.yaml` (buffer 1, max 2) and `agones/allocation-dev.yaml`.
- `docs/K3S.md` — cluster-option comparison and why Docker Desktop Kubernetes
  was chosen over k3d/native k3s on this box, bootstrap/teardown usage, image
  import per cluster type, `host.docker.internal` wiring, offline validation and
  its limits, graduation path to a real k3s VPS (kubeconfig secret + CD job
  sketch), and a WSL2 troubleshooting table.
### Changed
- **CI/CD topology — two fat jobs split into single-purpose jobs.** `cd.yml`'s
  `build-test` (vet + test + build + plugin + images + bundle) and `deploy`
  (sync + env + compose + restart + smoke + summary) were monoliths where one
  slow or flaky step blocked everything and a failure told you nothing about
  *what* broke. New graph:
  `resolve` ∥ `test-shared` → {`test-gateway`, `test-gameserver`, `test-nakama`,
  `test-smoketest`} → `test-integration`; `build-{gateway,gameserver,smoketest}`
  and `build-plugin` each hang off their own module test; `build-images`
  (GHCR, production / `build_images=true` only) off the binary builds; `bundle`
  assembles the artifacts; `deploy` → `post-deploy-smoke` → `summary`
  (`if: always()`). Deploy step contents are unchanged; the smoke test and the
  summary are now their own jobs, so "deploy failed" and "the flow failed" are
  distinguishable. Job graph documented in `docs/CICD.md` §3.

### Added
- `.github/workflows/_go-module.yml` — reusable `workflow_call` workflow that
  runs checkout + `setup-go` (cache keyed on the module's own `go.sum` via
  `cache-dependency-path`) + `go vet` + `go test` + an optional build and
  artifact upload for **one** Go module. Inputs: `module_dir`, `go_version`,
  `cache_dependency_path`, `run_tests`, `test_flags`, `needs_docker`,
  `run_build`, `artifact_name`, `artifact_path`, `artifact_retention_days`.
  Both `ci.yml` and `cd.yml` call it, so the per-module recipe exists once and
  adding a module is one additive `uses:` block.
- `ci.yml` now covers `backend/nakama` and `backend/smoketest` (previously
  untested in CI), gained `workflow_dispatch`, a `ci-<ref>` concurrency group,
  `.github/workflows/**` in its path filter, and per-binary build jobs.
- CD artifact flow is now per-binary: `bin-{gateway,gameserver,smoketest}-<sha>`
  and `nakama-plugin-<sha>` are merged by the `bundle` job into
  `deploy-bundle-<sha>` (still `include-hidden-files: true` for `.env.example`).
- CD `deploy` now passes `GAME_DB_URL` (from the environment variable
  `vars.GAME_DB_URL`, default empty) into the generated `deploy/.env`, wiring
  the PostgreSQL game-state persistence into deployed gameservers. Empty keeps
  the in-memory player store. Because the gameserver opens the DSN at boot and
  exits 1 when it cannot connect, the compose step now waits for the
  `rpg-postgres-game` container healthcheck before restarting the realtime
  services.
- `docker/Dockerfile.gateway` and `docker/Dockerfile.gameserver` — real
  container images for the realtime services (previously the Agones fleets
  referenced images that were never built). Multi-stage:
  `golang:1.26-alpine` builder (`CGO_ENABLED=0`, `-trimpath`,
  `-ldflags "-s -w"`, `go mod download` layer-cached) →
  `gcr.io/distroless/static-debian12:nonroot` runtime (no shell, non-root
  uid 65532). Build context must be `backend/` (`replace ... => ../shared`).
  Measured sizes: gateway 16.1 MB, gameserver 37.4 MB. `EXPOSE` 8000 / 9000,
  the latter matching `containerPort` in `agones/fleet-{map,dungeon}.yaml`.
- `scripts/build-all.sh --images` — builds both images via the existing
  docker/docker.exe auto-detection, cwd-relative from `backend/deploy/`.
  Tag overridable with `IMAGE_PREFIX` / `IMAGE_TAG` (default `rpg-mmo/*:dev`).
- `docker-compose.yml`: profile-gated `gateway` + `gameserver` services
  (`profiles: ["realtime"]`) wired to `redis:6379` and
  `postgres-game:5432`, published on host ports 8100 / 9300. Off by default —
  `docker compose up` behaviour is unchanged; they exist for container-parity
  testing while normal local dev keeps both processes on the host.
- `.github/workflows/cd.yml`: `build_images` boolean `workflow_dispatch` input
  plus GHCR build & push steps (`docker/login-action@v3`,
  `docker/build-push-action@v6`, gha layer cache, `packages: write` on the
  `build-test` job). Gated to run only when the resolved environment is
  `production` **or** `build_images=true`. Tags
  `ghcr.io/dycuong03/rpg-mmo-{gateway,gameserver}:<short-sha>` and `:latest`,
  matching the Agones fleet manifests.
- `postgres-game` service in `docker-compose.yml`: second PostgreSQL instance
  (`postgres:16.4-alpine`) for game state, separate from the Nakama meta DB —
  DB/user `gamestate`/`game`, host port `${POSTGRES_GAME_PORT:-5433}`, own
  `postgres-game-data` volume, `pg_isready` healthcheck, and
  `db/init-gamestate.sql` mounted into `/docker-entrypoint-initdb.d/`.
- `db/init-gamestate.sql` — `player_states` schema for first boot of an empty
  volume. Byte-identical to `backend/shared/storage/pgstore/schema.sql` (a Go
  test enforces this); the gameserver applies the same idempotent DDL at boot.
- `make psql-game` — psql shell on the game state DB.
- `.env.example`: `POSTGRES_GAME_DB`, `POSTGRES_GAME_USER`,
  `POSTGRES_GAME_PASSWORD`, `POSTGRES_GAME_PORT`.

### Changed
- `agones/fleet-map.yaml`, `agones/fleet-dungeon.yaml` — reality-pass against
  `gameserver/cmd/gameserver/main.go` and `gameserver/agones/sdk.go`:
  `portPolicy: Dynamic` made explicit (Agones assigns the host port; the
  container always binds `:9000`), `initialDelaySeconds` 5 → 10 to cover the
  Postgres migration on start, `--redis` added so gateway and gameservers share
  one registry/event stream, and `JWT_SECRET` / `REDIS_ADDR` / `GAME_DB_URL`
  wired to Secret `rpg-realtime-secrets` + ConfigMap `gameserver-config` with
  `optional: true` so the fleets still start before those objects exist. Added
  `app.kubernetes.io/part-of` / `rpg-mmo/role` labels.
- `agones/allocation.yaml` — documented that `GameServerAllocation` is a
  create-only aggregated-API resource (`kubectl create`, never `apply`).
- `docs/RUNBOOK-local-dev.md`: documents the two-PostgreSQL layout, port 5433,
  game-state verification/reset steps, and the host gameserver wiring
  `GAME_DB_URL=postgres://game:localdev@localhost:5433/gamestate?sslmode=disable`.

### Added
- CD post-deploy smoke phase: `bin/smoketest` (new `backend/smoketest` module)
  is staged into the deployment bundle, installed to `$RPG_DEPLOY_DIR/bin`, and
  run after the healthcheck with env sourced from `$RPG_DEPLOY_DIR/deploy/.env`.
  It exercises the full flow (Nakama health → device auth → `gateway_token` RPC
  → gateway auth/enter-world → game server join → input/snapshot loop → clean
  disconnect) and fails the deploy on any broken step (`SMOKE=FAIL`).

### Fixed
- Nakama refuses to start when `session.encryption_key` equals
  `session.refresh_encryption_key` — compose now derives the refresh key as
  `${JWT_SECRET}-refresh` (found by running the stack for real).
- `scripts/build-all.sh --plugin` failed under WSL with `docker.exe`: Windows
  docker CLI cannot resolve absolute `/mnt/*` context paths. Build now runs
  from `backend/deploy` with cwd-relative Dockerfile/context/output paths.

### Added
- Initial deploy module structure
- CLAUDE.md agent instructions for DevOps Engineer role
- `docker-compose.yml` — local dev meta stack: `postgres` (postgres:16.4-alpine,
  pg_isready healthcheck, named volume) + `nakama` (heroiclabs/nakama:3.40.0,
  waits for postgres healthy, runs `migrate up` then serves, mounts `./modules`
  for the Go plugin, exposes 7349/7350/7351/9100). All image tags pinned.
- `nakama-plugin.Dockerfile` — multi-stage build on
  heroiclabs/nakama-pluginbuilder:3.40.0 producing `nakama.so` from
  `backend/nakama` (+ `backend/shared` for the replace directive); `export`
  target writes the .so to the host, `runtime` target bakes it into a
  nakama image.
- `docker-compose.yml` — `redis` service (redis:7.4-alpine, `redis-cli ping`
  healthcheck, named volume `redis-data`, AOF `everysec` + RDB save rule,
  port 6379 via `REDIS_PORT`, `--requirepass` applied only when
  `REDIS_PASSWORD` is non-empty). Backs the upcoming shared
  RedisSessionStore / RedisServerRegistry / RedisEventStream (go-redis v9).
- `Makefile` — `plugin`, `image`, `up`, `down`, `reset`, `logs`, `logs-nakama`,
  `ps`, `psql`, `redis-cli`, `health`, `console` targets. `health` checks both
  the Nakama HTTP healthcheck and a Redis PING.
- `.env.example` — pinned NAKAMA_VERSION, postgres credentials, `REDIS_PORT` /
  `REDIS_PASSWORD` (empty default = no auth, matches shared/config), `JWT_SECRET`
  (shared HS256 secret with gateway/gameserver), console credentials, server key.
- `.gitignore` (ignores `.env`, `modules/*.so`) and `modules/.gitkeep`.
- `docs/RUNBOOK-local-dev.md` — build/start/stop/verify/debug/reset procedures,
  port table, plugin ABI version-pinning rule, failure-mode table, Redis
  verification (PING, XINFO STREAM/GROUPS, FLUSHALL) and `REDIS_ADDR` /
  `REDIS_PASSWORD` wiring for host-run gateway + gameserver.

- `scripts/build-all.sh` (repo root) — single build entrypoint used by devs and
  CI: `go vet` + `go test` + `go build` across shared / gateway / gameserver /
  nakama / integration_test, binaries to `bin/`. Flags `--skip-tests`, `--race`
  (off by default — WSL boxes usually lack gcc), `--plugin` (builds
  `backend/deploy/modules/nakama.so` via docker). Detects `go` from PATH →
  `$HOME/go/bin` → `/usr/local/go/bin`, and docker from `docker` → `docker.exe`
  (Docker Desktop under WSL), validating each with `docker info`. Fail-fast with
  per-step output.
- `scripts/deploy-local.sh` (repo root) — `start|stop|restart|status|health` for
  gateway + gameserver on the target machine. Uses systemd units
  (`rpg-gateway` / `rpg-gameserver`) when present, otherwise nohup + pidfile with
  SIGTERM→SIGKILL stop. Loads env from `/etc/rpg-mmo/env` or
  `$RPG_DEPLOY_DIR/.env` without echoing values; post-start healthcheck via
  `nc -z` (bash `/dev/tcp` fallback) plus best-effort Nakama `/healthcheck` curl.
- `.github/workflows/cd.yml` — CD pipeline. Triggers: push to `develop`,
  `staging`, `release-*`, plus `workflow_dispatch` (environment choice +
  `skip_tests`). Jobs: `resolve` (ref → environment + runner labels),
  `build-test` (ubuntu-latest, runs `build-all.sh --plugin --race`, uploads
  `deploy-bundle-<sha>`), `deploy` (self-hosted runner labeled `dev` / `staging`
  / `production`; installs binaries keeping `.prev` copies, writes `.env` from
  Environment secrets at mode 0600, `docker compose up -d`, then
  `deploy-local.sh restart` + healthcheck). Per-environment concurrency group
  with `cancel-in-progress`. `ci.yml` untouched.
- `docs/CICD.md` — build script reference, CD job matrix, self-hosted runner
  registration + labels, systemd unit + sudoers samples, required Environment
  secrets (`JWT_SECRET`, `POSTGRES_PASSWORD`, `NAKAMA_CONSOLE_PASSWORD`) and
  optional vars, branch strategy, rollback procedures, known limits.

### Changed
- `docs/README.md` — documents current state (Agones manifests + local dev meta
  stack), local stack usage, plugin build commands, and secrets handling; adds a
  build/deploy automation section and the `CICD.md` index entry.

### Fixed
- `docker-compose.yml` — Nakama refuses to start when `session.encryption_key`
  and `session.refresh_encryption_key` are identical (runtime-fatal). The
  refresh key is now `$${JWT_SECRET}-refresh` while the session key keeps the
  raw `JWT_SECRET` that gateway/gameserver verify against. Documented in
  `docs/README.md` and `docs/CICD.md`.
- `docs/RUNBOOK-local-dev.md` — replaced the "nothing has been executed yet"
  caveat with the verified end-to-end path (plugin build → `restart nakama` →
  `gateway_token` RPC → gateway `MsgAuth`). Records the actual module-load log
  lines, the real `gateway_token` smoke test (needs a *user* session, not
  `http_key`; body is a JSON-encoded string), the profile-hook storage check,
  measured latencies (device auth ~22 ms, RPC ~1-4 ms), and the `docker.exe`
  WSL fallback (run compose from `backend/deploy/`; `-f <abs WSL path>` breaks on
  path translation). Confirmed `nakama-pluginbuilder:3.40.0` ships `go1.26.5`,
  matching `backend/nakama/go.mod`, so no toolchain override is needed —
  `GOTOOLCHAIN=auto` is explicitly called out as the wrong fix (it reintroduces
  the plugin ABI mismatch).
