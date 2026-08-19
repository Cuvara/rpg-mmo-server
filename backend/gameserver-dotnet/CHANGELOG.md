# Changelog

All notable changes to the GameServer .NET module will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Added
- **`sgl-notify-client.yml`: a new `sgl-v*` tag now tells the client repo it exists.** Nothing in
  either repo knew the other did: the coupling is a UPM git URL in the client's `manifest.json`
  plus a resolved commit in its `packages-lock.json`, both edited by a human who has to remember.
  A release would land and the client would stay pinned to the old tag with no signal — and the
  golden-vector tests, which replay fixtures from the *pinned* package, would keep passing against
  the stale ones. The job dispatches the client's `sgl-pin-check` workflow, which reports whether
  its manifest and lock agree and how far behind the pin now is. **It deliberately does not bump
  anything** — moving to a new simulation version is a judgement call, and the two files must move
  together or the bump is silently ignored.
  This is the one thing `GITHUB_TOKEN` cannot do: it is scoped to the repository that minted it and
  cannot reach another repo at all. It dispatches a *workflow* (`actions: write`) rather than a
  `repository_dispatch` event, which would need `contents: write` — a permission the app
  installation has not been granted. The token is minted for one repo and one permission rather
  than the installation's full set.
- **`docs/rejected/` — approaches that were written, evaluated and not taken.** First entry is
  `2026-08-19-slow-client-rescale-and-skip.patch`: the #154 fix that rescaled multi-interval
  snapshot samples (`StepLog.MovingIntervals`) instead of discarding them, and skipped the case via
  `[SkippableTheory]` when every retry stalled. Worth keeping because it was not hypothetical —
  3951c40 landed the rescaling on `develop` and dd316ca took it back out, so the next person to
  reach for the same idea can find out it was already tried. A sample spanning a lost frame is not
  a small measurement of one step, it is not a measurement of one step at all; dividing it down
  produces a plausible-looking number and averages away the repayment `ASilentClientsRepayment`
  exists to detect — the test keeps passing and loses the ability to fail. The patch is a record,
  not a backlog: it does not apply to the current tree and is not meant to be applied.
- **`--register-on-allocated` / `GAMESERVER_REGISTER_ON_ALLOCATED`: hold the registry entry
  back until Agones reports this GameServer `Allocated` (#151).** Off by default — with the
  flag unset the server registers immediately after `ReadyAsync()`, byte for byte the
  behaviour that shipped, so an unmigrated fleet sees no change.

  The problem it fixes was measured on k3d and recorded in ADR-18: every pod of the map
  fleet carries the same fleet-wide `GAMESERVER_MAP_ID`, so a second `Ready` replica is a
  second *live* server for that map with **no allocation involved** — scaling 1 → 2 put two
  members into `servers:map:map_01` within a second of the new pod reaching Ready, and
  `registry.FindServer` then hands live players the unallocated spare that Agones is free to
  delete on the next scale-down. With the flag on, a `Ready`-but-unallocated pod holds no
  registry entry and is genuinely spare, which is the unlock for `replicas > 1` and a buffer
  `FleetAutoscaler`.

  - `IAgonesSdk.GetStateAsync()` reads `status.state` from the same `GET /gameserver`
    document the address read already parses; `HttpAgonesSdk` now shares one fetch between
    the two so they cannot drift on what a non-2xx or an unparsable body means.
  - `AgonesAllocationGate.WaitForAllocatedAsync` polls it every second. An **unreadable**
    state is "keep waiting", never "assume allocated"; the wait has no timeout and never
    fails the server. It runs on a background task so the health loop keeps pinging (a pod
    that blocked on the wait would be killed for missing pings) and the listener keeps
    accepting.
  - **Ready still comes first.** This narrows ADR-14 decision 3 rather than reversing it —
    Ready remains a precondition of being allocatable and has simply stopped being sufficient
    for being registered. The Agones address read stays between Ready and registration
    (ADR-15 decision 2), and shutdown still deregisters before Agones `Shutdown`.
  - **Inert without Agones.** With `IsEnabled` false there is no GameServer object to reach
    `Allocated`, so honouring the flag would mean never registering; the server logs that it
    is ignoring it and registers at start-up. docker-compose, local runs and every test are
    unaffected.
  - **Deallocation and restart:** once registered, the entry stays for the life of the
    process and is removed only by the existing shutdown path or by its TTL. The gate is not
    re-armed and a state that stops reading `Allocated` does not deregister — Agones has no
    un-allocate (an `Allocated` GameServer leaves that state by being shut down, which ends
    the process anyway), and a second writer that could yank a live server out of the
    registry on one transient read failure is the two-writers-one-datum hazard ADR-1 forbids.
    A pod that restarts while `Allocated` re-registers at once, because the gate's first read
    already says so.
  - Shutdown settles an in-flight gate (bounded, 5s) before deregistering, so a registration
    cannot land *after* the deregistration and leave the gateway handing out a black hole
    until the 15s TTL reaped it.
  - Tests: `AgonesAllocationGateTests` (gate opens only on an exact `Allocated`, keeps
    waiting on every other state, on an unreadable one and on a throwing SDK, and returns
    false on cancellation), `HttpAgonesSdkAddressTests` state cases against the fake sidecar,
    and host-level cases pinning that a gated Ready pod writes nothing, that allocation
    releases it with the Agones-assigned address, that an already-`Allocated` pod registers
    immediately, that the flag is ignored without Agones, and that the default path never
    reads the state at all.
- **`/status` publishes `achieved_tick_hz` — a *measured* base-tick rate, from the monotonic
  clock (#144, #153).** The endpoint exposed a configured rate, a tick counter and an uptime,
  and no measured rate, so the obvious move for anyone checking whether the loop was healthy
  was `current_tick / uptime_seconds`. That mixes clocks, and on this host it reports a
  healthy 60 Hz loop as ~54 Hz: issue #147, filed against a server that was running at
  exactly 60, propagated into an ADR and blamed for a client prediction defect before being
  closed as not-a-defect. Fixing the field labels alone would have left that trap armed —
  the gauge is what disarms it, because an observer that has to supply its own clock will
  eventually supply a bad one and the result looks exactly like a server defect.

  `AchievedRateMeter` samples once per base tick from `Stopwatch.GetTimestamp()` — the same
  timestamps that pace the loop — over a 2 second sliding window. O(1) and allocation-free
  per tick, so it does not perturb the budget it measures. There is deliberately **no**
  overload accepting a `DateTime`: a wall-clock-derived achieved rate would reproduce #147
  *inside* the server, carrying the server's authority, which is worse than the bug it
  replaces. `0` means "no window completed yet", not "stalled"; `current_tick` distinguishes
  those, and it is documented rather than signalled with a null that would break typed
  readers.

  **Base timeline only.** The world and background groups are exact integer divisors of the
  base rate, so three measured rates would be one measurement plus two pieces of arithmetic —
  three things that can drift instead of one. Per group,
  `rate(gameserver_sim_group_runs_total[...])` is already the measurement.

  Also exported as the Prometheus gauge `gameserver_achieved_tick_hz`.

### Fixed
- **A join refused for capacity was completely silent (#145).** The capacity check called
  `SendError` and logged nothing, so `grep -c full` over a pod log during a 120-player run
  that was cut off at 100 joins returned **0**. An operator could not tell a server correctly
  turning players away from a server that was broken — the two produced identical logs.
  The refusal now logs at Warning with the user, the current count, the limit, and the fact
  that the limit is `GAMESERVER_CAPACITY` rather than a resource limit. Warning rather than
  Information because the number being hit is a chosen admission limit, so hitting it is the
  signal that the choice needs revisiting.

  Covered by `CapacityRejectionTests`, including the negative case — a join that fits must
  not log a capacity warning, or a line emitted on every join would satisfy the positive
  test. These are the first tests in the suite to assert on log output; everything else runs
  on `NullLoggerFactory`, which is precisely why a missing log line was invisible.

- **`/status` reported a rate nobody had configured (#144).** The endpoint filled
  `tick_rate` from the legacy `--tick-rate` / `GAMESERVER_TICK_RATE` scalar. No current
  deployment sets that variable, so the field reported the compiled-in default of **15**
  forever — on servers running the standard `SIM_CRITICAL_HZ=60` configuration, i.e. wrong
  by 4x, and silently disagreeing with both the startup banner and the join response. The
  Unity DOTS sample polls this endpoint and reads that field.

  The root problem was the shape, not the number: the server runs three simulation groups
  at three frequencies (ADR-13), so one unqualified rate cannot be right for every reader.
  `/status` now publishes rates derived from the resolved `SimulationRates`, each field
  named for its group:
  - `tick_rate` — kept, and **defined as the critical/movement rate, identical to the wire
    field `join_token_resp.tick_rate`** (normative definition in `docs/API.md`). It keeps
    its name because clients already read it under that name from both surfaces; what was
    wrong was its source, not its name. A contract test asserts the two surfaces cannot
    diverge — both read `SimulationRates.MovementHz`.
  - `sim_critical_hz`, `sim_world_hz`, `sim_background_hz` — the three configured group
    rates, explicit. `sim_world_hz` is the one that matters for a client jitter buffer and
    for bandwidth: snapshots broadcast on the world cadence, not the critical one.
  - `capacity` — the admission limit the server enforces, previously unobservable.

  The mapping now lives in a testable `ServerStatus.ApplyRates`; it was inline in
  `Program.cs`, a top-level-statement file with no seam, which is why it could read an
  unrelated variable for as long as it did without a test noticing.

- **`/status` reported uptime on the wrong clock.** `uptime_seconds` came from
  `DateTime.UtcNow` (`CLOCK_REALTIME`) while `current_tick` is advanced by a
  `Stopwatch`-paced loop (`CLOCK_MONOTONIC`). `current_tick / uptime_seconds` is the obvious
  way to derive an achieved tick rate, and on a host whose realtime clock runs 10-17% fast
  — this one does (#153) — that quotient reports a 60 Hz loop as ~54 Hz. That is precisely
  issue #147: a defect filed against a server that did not have one, because the observer
  supplied the clock. Uptime is now monotonic, so both terms of the quotient share a source.

  **This changes the meaning of a documented field, which this repo has been bitten by
  before, so it is stated rather than slipped in:** `uptime_seconds` is now elapsed process
  time, not a wall-clock difference. It therefore no longer follows a clock step — an NTP
  correction, a suspend/resume — and can disagree with `date`-derived arithmetic on a
  drifting host. That disagreement is the point: an elapsed interval should never have come
  from a wall clock, and the previous behaviour was not a feature anyone relied on, it was
  the bug. The alternative considered and rejected was adding a second field and leaving this
  one wrong; that keeps a field whose only use is to mislead.

- **`RedisServerRegistryTests` TTL assertion no longer breaks on hosts with clock steps
  (#161).** The test asserted an absolute remaining TTL (`Assert.InRange(…, 1, 15)`) and read
  16.87s against a 15s ceiling on a host whose `CLOCK_REALTIME` stepped backward during the
  window. A relative `TimeSpan` expiry (`PEXPIRE`) cannot exceed its configured TTL by
  construction — Redis computes the deadline on its own clock — so the reading was a backward
  clock step inflating `deadline − now`. Replaced with a decay assertion: read the TTL twice
  with a 500ms gap and assert the second is strictly less than the first. Monotonic regardless
  of clock steps in either direction.
- **`SlowClientMovementTests.AnExplicitStopIsNotTreatedAsLostTime` no longer flakes at
  `critical:15` (#154).** A scheduling hiccup at the 66.7ms tick interval produced a snapshot
  pair spanning two world ticks instead of one, with ~2x normal movement. The test read that
  as a repaid pause and failed. Fixed by adding `LargestSingleStepAfterResume(worldEvery)`
  which discards multi-interval pairs (`FrameGap > worldEvery`), keeping only single-tick
  samples. The repayment test (`ASilentClientsRepayment_IsBoundedByTheCap`) still uses the
  unfiltered `LargestAfterResume()` because multi-tick samples there are legitimate repayment
  steps, not scheduling artifacts.

### Documentation
- **Distinguished clock *rate skew* from clock *steps* in `BENCHMARK.md`, with the sign table
  that tells them apart (#153).** This host has both faults and they need different responses:
  rate skew understates any measured rate and is fixed by never deriving a rate from the wall
  clock; a step corrupts a single reading and is not fixed by that rule at all. Added because
  matching on magnitude alone already produced one wrong attribution — a Redis TTL assertion
  reading **16.87s against a 15s ceiling** looked like the 10-17% skew at +12.5%, but the
  registry sets a *relative* expiry (`PEXPIRE`), so Redis computes the deadline on its own
  clock and the remainder cannot exceed 15s by construction; and decisively, a fast clock makes
  a TTL decay faster, so it reads **lower**, never higher. The observation ran the wrong way for
  the mechanism it was blamed on. Recorded as a worked example with the rule it teaches —
  **magnitude matching is not diagnosis, confirm the sign** — which is the same discipline #147
  failed. The Redis flake itself is deliberately not fixed or filed here: it is out of scope,
  the backward-step hypothesis is not reproducible on demand, and hardening a test against an
  undemonstrated cause is how a flake acquires a wrong fix that hides it. Refs #153, #147.
- **`BENCHMARK.md` and `K3S.md` now point at the measured `achieved_tick_hz` instead of warning
  people off arithmetic (#153/#144).** The gauge landed with #144, so the docs give the answer
  rather than only the prohibition: read **`achieved_tick_hz`** on `/status` or
  **`gameserver_achieved_tick_hz`** on `/metrics`, and compare it against the configured
  `sim_critical_hz` — a healthy server has the two equal to within rounding. Both are fed once
  per base tick from `Stopwatch.GetTimestamp()`, over a 2s sliding window. Documented the one
  way to misread it: **`achieved_tick_hz == 0` means "not measured yet"** (process younger than
  ~2s, no completed window), not a stopped loop — `current_tick` distinguishes those, and a
  freshly scheduled k3d pod will show `0` briefly. Also recorded that the gauge covers the
  **base timeline only** — world and background are exact integer divisors, so three measured
  rates would be one measurement plus two pieces of arithmetic; per-group measured rates come
  from `rate(gameserver_sim_group_runs_total[...])`. The `current_tick / uptime_seconds`
  quotient is documented as wrong in one of two ways depending on build: clock-skewed before
  the #144 uptime fix (observed live at **54.10 Hz** on a loop genuinely running 60), and a
  since-boot average that hides recent degradation after it. Notes the consequent behaviour
  change — `uptime_seconds` is elapsed process time now, so it no longer follows a clock step
  and can legitimately disagree with `date`-derived arithmetic. Links
  `gameserver-dotnet/docs/METRICS.md` rather than restating it. Refs #153, #147, #144.
- **Warned that the achieved tick rate must not be computed from `/status` (#153).** The
  endpoint reports `tick_rate` (the *configured* rate), `current_tick` and `uptime_seconds`,
  which invites `current_tick / uptime_seconds` — but `uptime_seconds` is derived from
  `DateTime.UtcNow` (`GameServer/Program.cs`), i.e. `CLOCK_REALTIME`, which runs 10-17% fast
  on this host. The quotient therefore reads **~51 Hz on a healthy 60 Hz loop** and **~12.9 Hz
  on a healthy 15 Hz loop**, reproducing #147 from inside the server's own status endpoint
  rather than from `date`. Documented in `BENCHMARK.md` and `K3S.md` with the safe
  alternative (`gameserver_tick_duration_seconds` on `/metrics`, built from
  `Stopwatch.GetTimestamp()`). **No code change made here** — `ServerStatus` and the missing
  `Stopwatch`-derived achieved-rate field belong to #144 and were flagged to its owner rather
  than changed under it. Refs #153, #147, #144.
- **`BENCHMARK.md` records the k3d serverlb as a third local-measurement trap (#143).** On k3d
  the Agones port range is published by an nginx TCP proxy, so a capacity sweep through an
  Agones pod measures the proxy: snapshot interval p99 **211.9 ms** through the serverlb against
  **72.7 ms** direct to a compose server, same binary and load. Nothing already in the document
  was measured that way — Part I and Part II both ran against a directly-dialled server — but
  the local Agones rig is the obvious next place someone would sweep, so the trap is recorded
  next to the existing confounds with the rule to prefer the compose or direct-to-node path.
  Refs #143, ADR-16.
- **The host clock is a measurement hazard, and every BENCHMARK figure has now been audited
  against it (#153).** `CLOCK_REALTIME` on this WSL2 box runs *fast* relative to
  `CLOCK_MONOTONIC` — measured at **+11.1%, +16.7% and +16.65%** in three sessions on
  different days — and the amount drifts, so it cannot be corrected with a constant, only
  avoided. `backend/docs/BENCHMARK.md` gains the twenty-second reproduction, the rule
  (**never derive a rate from a wall clock on this box**), and a figure-by-figure audit
  traced to source. **Verdict: no figure in the document is affected.** Tick p50/p99/mean
  come from `Stopwatch.GetTimestamp()`/`Stopwatch.Frequency`; every client-side rate,
  latency and the achieved `ticks/s` come from Go `time.Time` subtraction, which uses the
  monotonic reading Go embeds in `time.Now()`; peak RSS is a byte count with no interval in
  it; and the Protobuf-vs-JSON savings and the 5:1 `still`-vs-`cluster` ratio are ratios
  within one run, which would cancel a shared skew anyway. Corrected one piece of wording
  that said otherwise — §2 described the snapshot interval as a "wall-clock gap", which
  would have led a reader to discard a sound figure. Also recorded why the **14.7Hz** drift
  is *not* this bug and the arithmetic that proves it: a 16.7% fast clock would have made
  15Hz read as ~12.9, not 14.7, so timer granularity remains the live explanation. The
  hazard is not hypothetical — it produced #147 ("the tick loop runs at 54 Hz while
  advertising 60"), which was filed, propagated into an ADR in an open PR, and blamed for a
  client prediction defect before being closed as not-a-defect; `TickLoop` paces on
  `Stopwatch` and the observer timed it with `date`. Refs #153, #147, ADR-7.

## [v1.5.2] — 2026-08-17

### Fixed
- **`SlowClientMovementTests` measured the transport and blamed the simulation.** Two of its
  cases flaked on CI during one afternoon, each costing an investigation, and both readings
  were wrong in the same direction: they reported a defect in a server that had behaved
  correctly.
  - `AnExplicitStopIsNotTreatedAsLostTime` judged **every** sampled step, including those
    from the movement phase *before* the pause it is named for. Under load the server's own
    tick loop runs late and the elapsed-time step then integrates two ticks into one — which
    is #100 working as specified. That lands on exactly `2.00x`, the value the threshold
    rejects. Observed at `0.6667@tick9` with the pause not ending until ~tick 21. It now
    judges only samples at or after the restart, with the boundary taken from the snapshot
    stream's own tick rather than from wall clock.
  - Samples spanning a **lost** snapshot are discarded. The outbound channel drops the
    oldest frame under load; when the dropped frame mentioned the player, the next mention
    is two steps away and the raw difference again reads as exactly `2.00x`. Lost frames are
    detected by tracking the tick of *every* snapshot, not only those carrying the player —
    which is what distinguishes a dropped frame from an entity that legitimately did not
    move and so was absent from a delta.
  - `MeasureAsync` took "the newest position seen", which deltas and drops can leave several
    ticks stale, reading as a short distance — again the failure shape. It now requests a
    keyframe (`MsgResync`) and reads the position out of that, which carries every entity in
    the AOI unconditionally.
  - Failures now print the sample set: count, how many were before the restart, lost frames,
    discarded pairs, and the three largest steps with their tick and gap. The first
    occurrence after adding it identified the cause immediately, having previously been
    misattributed twice.

  **Rejected approach, recorded because it looked right.** Dividing each step by its tick gap
  removes the drop artefact — and also removes the defect: banked repayment puts a whole
  silent interval's travel into one tick and the previous mention is a whole interval
  earlier, so the quotient comes back at exactly one normal step. Both banking tests then
  measured the defect as *absent* while passing. Caught by running them; it would have
  shipped two permanently green, permanently blind tests.

## [v1.5.0] — 2026-08-17

### Added
- **`GAMESERVER_ADVERTISE_HOST` / `--advertise-host`** — host-only override for the address
  composed from the Agones GameServer status. **Host** = this value if set, else
  `status.address`; **port** = always the Agones-assigned `game` port, never configurable.
  - **Measured, not theorised.** ADR-15 warned that `status.address` is the *node* address;
    the consequence was not drawn until a live `portPolicy: Dynamic` GameServer on k3d
    (k3d v5.8.3, k3s v1.31.5, Agones 1.59.0, ports 7000-7100 published by the serverlb)
    reported `172.20.0.3:7008` and was probed: `127.0.0.1:7008` answers from WSL2 (`PONG`)
    and from Windows, where the Unity client runs (`Test-NetConnection` True), while
    `172.20.0.3:7008` is refused from WSL2 and unreachable from Windows. **The read gets the
    port exactly right and the host wrong** — the port is the half only Agones can supply,
    the host is a deployment fact the cluster cannot know.
  - **The two address knobs do not overlap, on purpose.** `GAMESERVER_PUBLIC_ADDR` is a full
    `host:port` used when Agones is **off**; `GAMESERVER_ADVERTISE_HOST` is host-only and used
    when Agones is **on** and the status read succeeded. Setting the latter with Agones off
    logs a warning and changes nothing; setting it to a full `host:port` by mistake logs a
    warning, honours the host and discards the port.
  - **Not applied when the status read fails.** With no Agones port to pair it with,
    composing the override host with a *configured* port would invent an address that was
    never assigned to anything — a plausible-looking value pointing nowhere, harder to
    diagnose than the honestly-wrong configured one. That path falls back unchanged.
  - IPv6 hosts are bracketed when composed (`[2001:db8::1]:7008`), since the gateway hands
    the string to clients verbatim and a bare `::1:7008` does not parse as an endpoint.
  - The composition logs which half came from where — `Advertising 127.0.0.1:7008 (host from
    GAMESERVER_ADVERTISE_HOST, port 7008 from Agones status)` — because when this is wrong it
    is wrong silently: the server runs, the registry looks healthy, and only the client knows.
  - 24 further tests: override applied, override unset (pre-override behaviour pinned),
    Agones disabled, failed read, hostname hosts, blank-as-unset, a value carrying a port, and
    host normalisation including bare and bracketed IPv6.
- **The server learns its own dialable address from Agones** (`GameServer/Agones/AgonesSdk.cs`,
  `GameServer/Registry/RegistrationService.cs`, `GameServer/Server/GameServer.cs`) — ADR-15
  decision 2, option (A). `IAgonesSdk` gains `GetAddressAsync()`; `HttpAgonesSdk` implements it
  as `GET /gameserver` against the sidecar, taking `status.address` and the port whose **name**
  is `game`, and the host advertises that pair into Redis instead of its configured address.
  - **This is what has kept the Agones path from ever carrying a player**, and it is not the
    health loop. `deploy/agones/fleet-map-dotnet-dev.yaml` uses `portPolicy: Dynamic`, so
    Agones assigns the host port at scheduling time and no static value can be correct: the
    manifest passes `--addr=:9000` and sets no `GAMESERVER_PUBLIC_ADDR`, so the server
    registered the hostless `:9000`, the gateway copied it into `MsgEnterWorldResp.ServerAddr`
    verbatim (`transfer/map_assign.go` → `server/server.go`), and the client dialled nothing.
  - The port is chosen **by name, never by index** — matching `ports[].name` in the fleet
    manifests and `gamePortName` in the gateway's `registry/agones_allocator.go`. `ports[0]`
    works right up until a fleet declares a second container port, and then silently
    advertises the wrong one.
  - The read sits **between `ReadyAsync` and the registry write**, and both halves are
    load-bearing: the address does not exist until the pod is scheduled, and the *first* value
    written to Redis must already be correct — a value repaired one heartbeat later is a
    15-second window in which the gateway hands clients a dead address.
  - **Falls back to today's exact resolution on everything**: Agones disabled (the read is not
    even attempted), sidecar unreachable, non-2xx, unparsable body, a status with no address,
    or no port named `game`. Each logs a warning and none is fatal, for the same reason as the
    rest of this class — a server nobody can reach still serves the players already on it.
    Running outside a cluster is byte-for-byte unchanged.
  - `RegistrationService` gains `PublicAddr` and `OverridePublicAddr(string)`. The override
    throws after `StartAsync` rather than half-applying, since by then the wrong value is
    already in Redis.
  - The AOT rules are intact: one source-generated `JsonSerializerContext` for the three fields
    read, no reflection-based serialization, no new package (`System.Net.Http` is in-box —
    the official Agones C# SDK is gRPC, which is why ADR-14 decision 1 chose HTTP).
  - Start-up logging: under Agones a hostless `--public-addr` is now reported as *expected*
    rather than as "clients will fail to connect", because it is no longer the value that
    gets registered.
  - 28 tests (`GameServer.Tests/Agones/`, 46 in the directory total): the success shape, a
    500, an absent sidecar, malformed JSON, a missing/empty address, an out-of-range port, a
    missing `game` port, and port selection where `game` is **not** index 0; plus the wiring —
    the assigned address is the first thing registered, the read lands after Ready and before
    registration, a failed read registers the configured address, and with Agones disabled the
    SDK is never asked and the configured address is registered unchanged.
  - **The response shape is observed, not assumed.** The success fixture is a verbatim capture
    from a live Agones **1.59.0** sidecar (`kubectl port-forward` to
    `map-servers-dev-kl485-gsmrh` in `rpg-realtime`), which returned HTTP 200 and
    `{"status":{"address":"192.168.65.3","ports":[{"name":"game","port":7691}], ...}}`. What
    remains unproven is this server making that call from inside a pod.
- **Real Agones SDK over the HTTP sidecar** (`GameServer/Agones/AgonesSdk.cs`) — ADR-14
  stages 1-3. `HttpAgonesSdk` POSTs an empty JSON body to `/ready`, `/health`, `/allocate`
  and `/shutdown` on `localhost:9358` (`AGONES_SDK_HTTP_PORT` overrides the port; an
  unparsable value warns and falls back rather than refusing to boot, because a server that
  will not start is a restart loop). HTTP and not the official C# SDK on purpose: that SDK is
  gRPC and would pull `Grpc.Net.Client` into a module whose rules are NativeAOT-compatible and
  no external dependencies — `System.Net.Http` is in-box and the body is a string literal, so
  no serializer is involved at all.
  - **No method throws.** A missing, slow or 500-ing sidecar is logged and swallowed: every
    call site is start-up or a background loop, and an exception in either turns a sidecar
    hiccup into a dead game server. Health failures are *counted* rather than silently
    dropped — first failure warns, every fifth consecutive one logs an error naming the count,
    a recovery logs the gap — because Agones restarts the pod when pings stop, so a swallowed
    error would otherwise hide the cause of a real restart.
  - `--agones` / `AGONES_ENABLED=true` now selects it; the start-up warning saying the flag
    "has NO effect" is gone because it became false. Unset still means `NoopAgonesSdk`.
  - `IAgonesSdk` gains `IsEnabled`. The health loop keys off it and no longer runs against the
    no-op (ADR-14 decision 4): it used to log "health loop started" and then report nothing to
    anybody, which reads in a log exactly like a working liveness contract. **This is the one
    behaviour difference with Agones disabled** — no health-loop log lines.
  - Health ping interval 2s against the fleet manifest's `periodSeconds: 5`, so two pings fit
    one window and one dropped request is not a strike.
  - `AllocateAsync` fires once, on the first player to join, off the join's critical path.
    Nothing balances it on the way down: Agones has no un-allocate, and an Allocated
    GameServer leaves that state by being shut down.
  - Ordering per ADR-14 decision 3 — Ready before the Redis registry write, deregister before
    Agones Shutdown — was already what `GameServerHost.RunAsync` did; it is now commented as a
    contract and pinned by tests, because it is invisible in a log and silently reversible by
    anyone reordering two awaits.
  - 18 tests (`GameServer.Tests/Agones/`): the four paths against a real local `HttpListener`,
    a 500 and an absent sidecar not throwing, port resolution including four bad values, the
    Ready-before-register and deregister-before-Shutdown orderings, Allocate-once, and the
    disabled build reporting nothing while still registering at the same point.

> ⚠️ **Not proved against Agones.** No C# server in this project has ever reported Ready to a
> real sidecar. The tests stand a local `HttpListener` in for it, which pins the HTTP shape and
> the failure behaviour and nothing about Kubernetes. ADR-14 stage 4 — deploy the dotnet fleet
> and watch for a restart loop — is where this first gets evidence; until then the fleet
> manifest's health block stays `disabled: true`.

### Documentation
- **ADR-14's decision 3 asked for work that was already done.** It states that the server
  must register into Redis only after reporting Ready, written as though that were pending.
  `GameServer.RunAsync` already does it: the bind completes at
  `GameServer/Server/GameServer.cs:349`, `ReadyAsync()` runs at 356, `_registration.StartAsync()`
  at 364, and the descent deregisters at 443 before `ShutdownAsync()` at 450. What was actually
  missing was decision 1 alone — `Program.cs:365` hardcoded `new NoopAgonesSdk()`, so a
  correctly ordered sequence of calls all landed on the no-op.

  The decision stands as the rule; it needs a test pinning the order against a future refactor,
  not a restructure. Corrected in place with a dated note rather than edited away, because an
  ADR that asks for finished work sends the next reader hunting for it — the same failure the
  ADR-10 and nakama status corrections fixed earlier the same day.

### Documentation
- `docs/README.md`: new "Agones (`--agones`, `AGONES_SDK_HTTP_PORT`)" section — the four
  endpoints, the lifecycle order, the health cadence, the never-throws rule and what is still
  unproven. The flag table row no longer describes a stub, and `AGONES_SDK_HTTP_PORT` is listed.
- `docs/README.md`: that section now also documents `GET /gameserver`, why the advertised
  port comes from the GameServer status under `portPolicy: Dynamic`, the by-name port
  selection, and the fallback list. The `status.address`-is-the-node-address limit is no
  longer a caveat but a sub-section with the k3d reachability matrix that measured it, the
  `GAMESERVER_ADVERTISE_HOST` resolution table, and a side-by-side of the two address knobs
  so the wrong one is harder to reach for. The `--public-addr` and `--agones` flag rows say
  Agones overrides the configured address, and `--advertise-host` is listed.
- **ADR-14 — Agones owns the pod, Redis owns the lookup; the C# server's SDK is a stub and must
  be written over the HTTP sidecar** (`backend/docs/ARCHITECTURE-DECISIONS.md`). Accepted
  2026-08-17, **not yet implemented** — nothing in it has shipped, and it must not be cited as
  evidence that Agones works for this server.
  - Records what the startup log already admits: `GameServer/Agones/AgonesSdk.cs` is 58 lines
    of interface plus `NoopAgonesSdk`, the only implementation in the solution, so `--agones` /
    `AGONES_ENABLED` parses, logs a warning, and changes nothing. The gateway half is real and
    tested (`gateway/registry/agones_allocator.go`), which is why the gap is one-sided.
  - Four consequences named: an unserved map cannot be entered, a full map is refused because
    ADR-2's allocation branch cannot produce a live server, a crashed server is dropped from
    Redis by TTL but never replaced, and dungeon-per-party instancing cannot exist —
    `--mode=dungeon` today only widens the disconnect hold window from 30s to 60s.
  - Decides the SDK is implemented over the **Agones HTTP sidecar on `localhost:9358`**, not
    the official C# SDK, which is gRPC and would pull `Grpc.Net.Client` against this module's
    NativeAOT and minimal-dependency rules. The deleted Go server's `agones/sdk.go` (101 lines,
    at `670a803^`) is the shape reference.
  - Decides the ownership split ADR-1 requires: Agones owns pod lifecycle, Redis owns the
    `map_id -> server` lookup, and the server registers into Redis **only after** reporting
    Ready — deregistering before `ShutdownAsync` on the way out.
  - Notes that `deploy/agones/fleet-map-dotnet-dev.yaml` sets a 5s health period and no
    `disabled: true`, so deploying it today would restart-loop the pod; and that the cluster
    still runs `map-servers-dev` / `dungeon-servers-dev` on `rpg-mmo/gameserver:dev`, the Go
    server deleted in `670a803`. Retiring those is stage 8 of eight.
  - Leaves explicitly open whether the realtime tier moves to Kubernetes at all — dev, staging
    and production all run `DEPLOY_MODE=containers`, so Agones is a parallel path today.

### Fixed
- **`SlowClientMovementTests` measured the transport and blamed the simulation.** Two of its
  cases flaked on CI during one afternoon, each costing an investigation, and both readings
  were wrong in the same direction: they reported a defect in a server that had behaved
  correctly.
  - `AnExplicitStopIsNotTreatedAsLostTime` judged **every** sampled step, including those
    from the movement phase *before* the pause it is named for. Under load the server's own
    tick loop runs late and the elapsed-time step then integrates two ticks into one — which
    is #100 working as specified. That lands on exactly `2.00x`, the value the threshold
    rejects. Observed at `0.6667@tick9` with the pause not ending until ~tick 21. It now
    judges only samples at or after the restart, with the boundary taken from the snapshot
    stream's own tick rather than from wall clock.
  - Samples spanning a **lost** snapshot are discarded. The outbound channel drops the
    oldest frame under load; when the dropped frame mentioned the player, the next mention
    is two steps away and the raw difference again reads as exactly `2.00x`. Lost frames are
    detected by tracking the tick of *every* snapshot, not only those carrying the player —
    which is what distinguishes a dropped frame from an entity that legitimately did not
    move and so was absent from a delta.
  - `MeasureAsync` took "the newest position seen", which deltas and drops can leave several
    ticks stale, reading as a short distance — again the failure shape. It now requests a
    keyframe (`MsgResync`) and reads the position out of that, which carries every entity in
    the AOI unconditionally.
  - Failures now print the sample set: count, how many were before the restart, lost frames,
    discarded pairs, and the three largest steps with their tick and gap. The first
    occurrence after adding it identified the cause immediately, having previously been
    misattributed twice.

  **Rejected approach, recorded because it looked right.** Dividing each step by its tick gap
  removes the drop artefact — and also removes the defect: banked repayment puts a whole
  silent interval's travel into one tick and the previous mention is a whole interval
  earlier, so the quotient comes back at exactly one normal step. Both banking tests then
  measured the defect as *absent* while passing. Caught by running them; it would have
  shipped two permanently green, permanently blind tests.

### Added
- **`EcsWorld.UpdateComponentsParallel(workerCount, body)`** — runs a body on N threads
  inside one write scope. **Nothing in the tick loop calls it.** It exists so the two
  parallel-simulation preconditions ADR-12 records can be demonstrated rather than
  asserted; a fix to a concurrency hazard that is never run concurrently is a claim, not a
  result. It does not check that the bodies are safe to run together — that is
  `ComponentAccess.IsDisjointFrom`'s job and belongs to whoever builds the schedule.
- **`ParallelRegionDeterminismTests`** — the determinism harness. Asserts the world is
  byte-identical across 25 runs and across worker counts, that structural ops replay in
  slot order rather than in the order workers finished, that a spawn inside a region stays
  invisible until the region ends, and that a failing worker surfaces only after every
  worker has joined. Verified to fire by reintroducing the shared queue: exactly the two
  order-sensitive tests fail, the other eight still pass.

### Fixed
- **The deferred-structural queue was a single unsynchronised `List<StructuralOp>`**, safe
  only because exactly one thread mutated it under the write lock. It is now one list per
  worker slot, drained in slot order. Locking a shared list would have fixed the data race
  and left the hazard that actually matters: ops are replayed through `Arch.Create`, so
  queue order sets creation order, which sets chunk layout, which sets iteration order,
  which sets the order floats accumulate. Under a shared queue that becomes arrival order,
  so the golden vectors and the byte-identical snapshot digests would have broken
  *intermittently* — the failure mode that is hardest to attribute. Replay order is now a
  function of (slot index, position within slot) and of nothing else.
- **The immediate-versus-deferred decision was `[ThreadStatic]`**, so under workers it
  answered "is *this thread* iterating" when the question is "is anything iterating this
  world" — one worker could take the immediate path and mutate archetypes while another was
  mid-iteration over them. Deferral is now also driven by a world-level `_parallelRegion`
  flag covering the span of a parallel region. The thread-static depth is kept for the
  same-thread re-entrancy it always caught; it is no longer the whole rule.
- Both were fixed **ahead of any worker**, which is what ADR-12 asked for: *"Neither may be
  left to be discovered by the change that first spawns a worker."*

### Changed
- `EcsWorld` gained an optional `maxWorkerSlots` constructor parameter, defaulting to 1.
  The default world allocates exactly one queue, as before, and the serial path is
  unchanged — pinned by a test so a future widening of the deferral rule shows up as a
  failure rather than as a quiet behaviour change.
- **`SystemSchedule` still runs serially, but for a different reason**, and its doc comment
  now says which. It is no longer blocked on world safety; it is blocked on there being
  anything to overlap. Two of the three systems in the schedule declare `Structural` and
  are excluded from concurrency outright, and the third has nothing to pair with, so every
  pair conflicts. Revisit when two non-structural systems have disjoint component sets.
- **ADR-10's status was still `not yet implemented`** long after both halves of it
  shipped. The server's Arch migration completed in five stages under ADR-12, and the
  Unity client now consumes `Shared.GameLogic` as a UPM package pinned to `sgl-v0.1.9`
  — so the ADR that exists to govern the shared-simulation boundary read as pending
  work to anyone deciding whether that boundary applies to them yet. The `Context`
  section is left as written, since it records the state on the date the decision was
  taken; a dated note above it lists the two statements that the code has since
  overtaken.

## [v1.4.1] — 2026-08-15

### Fixed
- **A coalesced stop input failed to clear held movement in single-rate mode.** When the
  server fell behind and drained a batch containing both a stop (deadzone) and a later
  resume input, per-tick coalescing gave the resume `applyMovement = true` and the stop
  `applyMovement = false`. The stop's `MoveResult.None` branch — which clears
  `HeldFromTick` — lives inside the `applyMovement` guard, so it never ran. In multi-rate
  mode `ApplyHeldMovement` masked this by keeping `LastMoveTick` current between packets;
  in single-rate mode (`WorldEvery = 1`) that pass is a no-op, so `StepDeltaTime` saw the
  full pause as elapsed time and repaid it in one step — capped at `MaxBankedTicks` (4x at
  15 Hz). The fix moves the deadzone detection outside the `applyMovement` guard: a
  zero-vector input clears `HeldFromTick` and resets `LastMoveTick` regardless of whether
  it is the newest input in the batch.

### Added
- **`Shared.GameLogic` released as `sgl-v0.1.8`**, carrying `GameConstants.MaxBankedMovementTicks`
  — the cap on how much elapsed simulated time a step may bank, added by the elapsed-time
  movement fix (#100). It lives in the shared library rather than on the server because a
  client that banks unbounded time reconciles against a server that does not, on exactly the
  frames where the network was worst. `package.json` is bumped in the tagged commit itself,
  per `backend/TEAM.md`: a tag whose package reports an older version installs cleanly and
  UPM never warns.

### Fixed
- **A deliberate stop was being repaid as movement, and it lurched.** The elapsed-time step
  above restores the correct distance, but it said nothing about the *rate*: a pause was
  repaid in a single step, so a player who stopped and started got up to 250ms of travel in
  one frame. Measured against a live server that was a **1.36-unit jump where a normal step
  is 0.083** — 16x — and it was reported by a player as jerkiness rather than as sluggishness,
  which is the signature of a banked interval discharged at once.

  The cause was that the server could not tell "I stopped" from "my packets stopped".
  A deadzone input clears the hold, so an entity with no held direction is *stopped*, and
  time no longer accrues to it. Stopping and starting is the most common thing a player
  does, which is why this lurched constantly rather than occasionally.

  **The residual is deliberate and bounded**: a client that goes quiet *without* sending a
  deadzone is the genuine lost-input case and is still repaid in one step, capped at 250ms.
  Removing that too costs something a player would also feel — either a longer coast after
  release, or a repayment that alternates between full and quarter speed — so it is tracked
  as #104 rather than guessed at. `SlowClientMovementTests` pins both halves: no lurch after
  an explicit stop, and a cap-bounded one after silence.

- **Bursty input arrival lost most of a player's movement (#100).** Per-tick coalescing
  turns several inputs arriving in one tick into a single movement step — correctly, since
  that is what stops a client travelling further by spamming packets — but the simulated
  time the discarded inputs stood for went with them. A client whose packets clumped, which
  is what TCP batching, a client GC pause or a mobile radio waking up all produce, covered a
  fraction of the ground its send rate should have earned:

  | Configuration | Even 15 Hz | Bursts of 4, same average rate |
  |---|---|---|
  | `60/15/5` | 6.00 ✅ | **2.75 — 46% of intended** |
  | `15/15/5` (single-rate) | 6.00 ✅ | **1.67 — 28% of intended** |

  **This predated multi-rate scheduling and was worse without it** — the held-input model
  happened to cover part of the gap — so it was not a regression from the scheduler work; it
  had been live in every configuration for as long as coalescing had existed.

  A movement step now covers the time since that entity last moved,
  `dt = min(now − last_move_tick, cap) / tick_rate`, instead of a fixed single tick. This
  does not weaken what coalescing defends: a client sending every tick always has
  `last_move_tick == now − 1` and so earns exactly one tick per tick, and a client that was
  silent is bounded by the cap. It is the same rule expressed against time rather than
  against packet count.

  `GameConstants.MaxBankedMovementMs` is **250 ms**, and the number is load-bearing rather
  than round: it has to cover one send interval of the slowest client we support, or that
  client is never made whole. Measured — at 200 ms a bursting 15 Hz client against a 15 Hz
  server recovers to 72% rather than 100%. It sits just above the ~200 ms dead-reckoning
  limit in the netcode model because banking is the safer of the two operations: the client
  demonstrably *did* send input covering the period and coalescing discarded it, where dead
  reckoning extrapolates input that was never sent.

  **A predicting client must apply the same cap** — it is part of the movement model, not a
  server-side valve. `docs/API.md` now states all three movement rules together.

  Byte identity is preserved: a client that sends every tick sees `elapsed == 1` and
  therefore the same `dt` as before, so the snapshot digests and golden vectors are
  untouched and were not regenerated.

### Added
- **`SlowClientMovementTests` measures movement over a real socket.** Joins over TCP, sends
  input on a wall clock, and reads the position back **out of the snapshot stream** — the
  number a client actually sees. It covers a 15 Hz client against a 60 Hz server, the
  single-rate configuration, a 60 Hz client, both burst patterns, and the cap holding
  against a 1.5 s silence.

  It exists because the unit tests could not answer whether they exercised the live path:
  they build the world by hand and push inputs straight into the queue, skipping the join
  handshake, the network thread, the entity's real creation path, per-tick coalescing and
  the encoder. Two readings of `TickLoop` disagreed about whether the server holds a
  client's last input, and nothing in a green suite could settle it. `backend/TEAM.md` now
  requires a live-path test for movement-adjacent behaviour, with the reasoning.


### Documentation
- **`JoinTokenResponse.tick_rate` is now specified, not merely implemented.** The field
  shipped with #93 described by a code comment, and a client team building against it was
  left inferring a protocol contract from that comment — which is the same move that
  produced the defect the field exists to close. `docs/API.md` now states it normatively:

  - **What it means.** The rate at which the authoritative tick advances *and* at which
    player movement is integrated. A client MUST build its prediction timestep as
    `1 / tick_rate`, and MAY use it to convert `tick`/`ack_tick` into seconds. It is
    **not** the snapshot cadence — snapshots follow the world rate, and a client sizing an
    interpolation buffer from this field would be four times short at the default.
  - **Why it is not defined as "the critical group's rate".** It carries `CriticalHz`
    today because movement is critical-group work and the critical group is the base
    timeline, but the field is specified in terms of *movement*, not of the group. If
    movement were ever scheduled elsewhere, the server would owe the client the movement
    rate here — or a new field — rather than letting this value follow a group name while
    prediction followed something else.
  - **What to do when it is absent or `0`.** A client MUST NOT assume 15. It SHOULD
    measure instead: `(tick₂ − tick₁)` over the wall-clock gap between two snapshots *is*
    the rate, and that also cross-checks the advertised value. A fallback to a configured
    rate is permitted only if it is observable, because a silent fallback is behaviourally
    the pre-#93 code. Refusing to predict is the safest option for a player-facing build.

  The absent/zero rule deliberately differs from `speed` (#91), and the document says why:
  `speed` is per-entity and its error is bounded by that entity's real speed, whereas
  `tick_rate` scales *every* predicted displacement by a whole ratio — 4x at 15-against-60,
  which lands under a typical snap threshold and so is corrected by smoothing on every
  reconcile instead of announcing itself.

### Changed
- **`SimulationRates.MovementHz` gives the wire contract one definition in code.** The rate
  published as `tick_rate` and the rate handed to the movement integrator were two
  independent reads of `CriticalHz`; both now read `MovementHz`. Two reads are two things
  that can drift, and this drift is silent on both sides.

### Added
- **`SlowClientMovementTests` measures movement over a real socket.** A reading of
  `TickLoop` that stopped at the `applyMovement` line concluded the server integrates once
  per packet, and a measurement against a running server appeared to confirm it. The unit
  tests that assert otherwise build the world by hand and push inputs straight into the
  queue, so they could not answer whether they exercised the live path. These join over
  TCP, send input on a wall clock, and read the position back out of the snapshot stream —
  the same number a client sees. A 15Hz client against a 60Hz server travels its full
  speed; a 15Hz client against a 15Hz server is unchanged; a 60Hz client is unchanged.

- **`JoinTickRateContractTests` enforces the contract behaviourally.** It measures one
  tick of movement and asserts the displacement is `speed / advertised_rate` — the
  client's own arithmetic — across four rate configurations, plus that the advertised rate
  is the base tick rate, that it is not the snapshot cadence, and that a client predicting
  at the advertised rate agrees with the server over a full second including when it sends
  at a quarter of the server's rate. If the coupling breaks, this fails instead of a player
  feeling something soft.


### Added
- **The join response now tells the client which rate to predict at**
  (`join_token_resp.tick_rate`). Closes
  [#93](https://github.com/Cuvara/rpg-mmo-server/issues/93). This is the companion to
  the multi-rate change below and the reason it is safe to ship: making
  `SIM_CRITICAL_HZ` configurable armed a latent silent desync. The client's prediction
  rate was a hardcoded 15 in a different repository, matching the server only by
  coincidence, and an operator tuning the rate for performance would have gotten a
  server that starts cleanly, a client that logs nothing, and a player reporting
  rubber-banding — no error anywhere in the chain.

  The value sent is `SimulationRates.CriticalHz`, not the world rate: the critical
  group is input, movement integration and combat, which is precisely the work a
  predicting client replays. The world rate drives snapshot cadence, which the client
  can observe directly. When `ServerOptions.SimulationRates` is null the rate is the
  uniform configuration derived from `TickRate`, so a legacy single-rate server
  reports its own tick rate and not a constant.

  Sent on the **success path only**. A rejected join has no session to predict in and
  the caller has not proved it is entitled to anything, so answering an
  unauthenticated peer with the server's tuning would be giving that away for free.
  Absent decodes as `0`, which the schema defines as "refuse to predict" — the
  correct answer to a failed join.

  Both encodings carry it: protobuf via the regenerated bindings, and the legacy JSON
  codec (`GameServer/Net/WireJson.cs`) as `tick_rate`, omitted when zero so the two
  agree on "absent means not supplied". A field present in one encoding and missing
  from the other would leave clients on the legacy path silently back to guessing,
  which is why `JoinTickRateTests` drives every case through both.

- **Multi-rate simulation: three configurable ECS groups on one integer base-tick
  timeline** (ADR-13). The server ran one fixed 15Hz loop, so nothing could run more often
  than every 66ms and everything that would have been fine at 200ms paid 66ms anyway.
  Raising the global tick to 60Hz was rejected rather than tried: it quadruples the cost of
  every system whether or not it benefits, and quadruples snapshot bandwidth, which the
  measured 45.9 KB/s per client at 200 players cannot absorb inside ADR-7's 50 KB/s mobile
  budget.

  Systems now declare a group — `Critical`, `World`, `Background` — and nothing else about
  timing. The frequencies are configuration (`SIM_CRITICAL_HZ` / `SIM_WORLD_HZ` /
  `SIM_BACKGROUND_HZ`, defaults 60/15/5, also `--sim-critical-hz` etc), which is why the
  groups are named for responsibility: a group called `Hz60` would be a lie the first time
  an operator tuned it. `GAMESERVER_TICK_RATE` still works and still means "every group at
  that one rate".

  - **The base rate is derived, not configured**: it is the critical rate, and every other
    group must divide it exactly. `60/25/5` has no integer timeline at 60Hz — its true
    common base is 300Hz — so it is **rejected at startup** with an error naming the
    variable and listing the usable divisors, rather than silently running the server five
    times faster than anyone asked for.
  - **Each group integrates with its own dt.** A world system receives `1/15`, not `1/60`.
    Handing it the base timestep while running it every fourth tick is the defining bug of
    a multi-rate scheduler, and it is silent — every speed and duration in the group would
    be wrong by the rate ratio with nothing to observe but "the game feels off".
  - **Durations counted in ticks follow the rate that advances them.** The attack cooldown
    is derived from the critical rate because `CooldownUntilTick` is compared against the
    base tick, so 500ms stays 500ms at 15, 30 and 60Hz.
  - **Group order is fixed Critical -> World -> Background** and encodes write ownership:
    the faster group's writes land before the slower group reads them, so a slow group can
    never overwrite newer state with a value computed from an older read.
  - **Scheduling is centralised.** No gameplay system counts ticks, tests `tick % 4`, or
    reads a configured frequency; all of it lives in `SimulationRates` and
    `SimulationSchedule`.

- **Per-group telemetry**: `gameserver_sim_group_duration_seconds` and
  `gameserver_sim_group_runs_total` (both labelled by `group`),
  `gameserver_tick_overruns_total`, and `gameserver_tick_backlog_dropped_total`. The last
  two are the ones that answer "is the configured critical rate sustainable on this host",
  which was previously only visible as a log line at a 2x overrun.

### Changed
- **Replication is gated to the world rate, not the base rate.** Simulation rate and
  replication rate stay separate concepts: snapshots still ship ~15 times a second at the
  default. Sending every base tick would quadruple downstream bandwidth to deliver state the
  client interpolates across anyway, and would silently redefine the keyframe interval,
  which counts *snapshots* — 30 snapshots is 2 seconds at 15Hz and half a second at 60Hz.

- **Movement is continuous rather than packet-driven.** The server integrates the newest
  input once per critical tick and holds it for one world interval, instead of once per
  received packet. Without this a client sending at 15Hz against a 60Hz server would move at
  quarter speed — travel distance would become a function of the client's send rate, which
  `MovementSystem`'s own documentation forbids. The hold is bounded: a client that goes quiet
  coasts for at most one world interval (66ms at the default), and an explicit deadzone input
  clears the hold immediately rather than refreshing it, so releasing the stick still stops
  the player at once.

  **On a single-rate configuration this is a no-op.** The hold window collapses to one tick,
  so movement is exactly one step per packet as before. That is what let the snapshot
  byte-identity digests and the enemy characterization suite pass unchanged — no golden data
  was regenerated and no digest was rebaselined.

- **The tick loop schedules against a deadline instead of sleeping a rounded interval.**
  `1000 / 60` truncates to 16ms, a 4% fast clock — 2.4 extra ticks a second, and every
  duration expressed in ticks short by the same factor. The old integer arithmetic was exact
  at 15Hz and is not at 60.

- **Overload behaviour is bounded and observable.** Past 8 base ticks of lag the loop drops
  the backlog, resynchronises, logs it and counts the discarded ticks. It never runs catch-up
  ticks: each one costs more than the budget it reclaims, so a server that fell behind would
  fall further behind. Simulation time runs behind wall time under sustained overload, which
  is a bounded failure rather than a spiral.

### Notes
- **The background group ships with no systems, deliberately.** Nothing in the current
  simulation tolerates a 200ms scheduling delay without a visible behaviour change — enemy
  reaping reads like cleanup but is what stops a dead or centre-arrived enemy from being
  observable in the snapshot built later in the same tick. Inventing a tenant so the group
  looked used would be shipping a regression to satisfy a diagram. The infrastructure is
  built, tested, and documented with the rule for what may enter it.
- **No performance claim is made.** The point of multi-rate is high frequency where it is
  needed and low frequency where it is not, not throughput, and the per-server tick ceiling
  remains unmeasurable on the current hardware (ADR-7).


### Fixed
- **The server's tick-rate default was a hardcoded `15` instead of
  `GameConstants.DefaultTickRate`**, while its three immediate neighbours in
  `Program.cs` — keyframe interval, map width, map height — all fall back to the shared
  constant. That inconsistency made the obvious way to change the tick rate silently
  wrong in the *opposite* direction to the one anyone would guard against: bumping
  `GameConstants.DefaultTickRate` and tagging a new `sgl` moves the **client**, which
  derives its integration step from that constant, and leaves the **server** on the
  literal. The client then integrates at a different `dt` than the server, is corrected
  by every snapshot, and the player sees rubber-banding. Nothing logs an error on either
  side, because neither side is wrong about anything it can observe.

  This does not close the wider gap: `--tick-rate` and `GAMESERVER_TICK_RATE` can still
  move the server alone, because the tick rate is not on the wire and the client cannot
  observe it. That is #93. This makes the *default* path coherent, which is the path a
  tick-rate change would actually be attempted through.

### Added
- **`Shared.GameLogic` released as `sgl-v0.1.7`**, carrying the `Speed` field added to
  `SnapshotData` by the per-entity speed work. `package.json` is bumped in the tagged
  commit itself, per `backend/TEAM.md` — a tag whose package reports an older version
  installs cleanly and UPM never warns, so the two must move together. The change is
  additive: the previous constructor is kept and forwards with `speed: 0`, so a client
  still pinned to `sgl-v0.1.6` is unaffected until it moves its manifest.

### Added
- **The snapshot encoder now sends per-entity `speed`** (`wire.proto` field 9), read
  straight off `EntityState.Speed` — the value `MovementSystem.TryMove` is actually
  integrating with. Closes
  [#91](https://github.com/Cuvara/rpg-mmo-server/issues/91). No ECS plumbing was
  needed: `Compose`/`ComposeFromChunk` already populate `Speed` from `Locomotion`.

  Three coupled places, and getting any one wrong fails silently in a different way:

  | Place | Omitting it |
  |---|---|
  | `SnapshotDeltaState.ToMsg` | never reaches the wire at all |
  | `SnapshotDeltaState.SentView` | **a speed-only change is never resent** — see below |
  | `SnapshotDeltaState.Rent` | a pooled `EntitySnapshot` carries the previous entity's speed |

  `SentView` is the sole arbiter of whether a delta resends an entity and it compares
  the whole struct, so an entity buffed *while standing still* changes nothing else,
  compares equal, and gets skipped — the client keeps predicting at the old speed for
  up to a full keyframe interval. That failure is invisible to every keyframe test,
  because a keyframe resends everything unconditionally.
  `Delta_ResendsAnEntityWhoseOnlyChangeIsSpeed` fails if `Speed` is removed from
  `SentView.Equals`; that was checked by removing it, not assumed.

- **`EntitySnapshotData.Speed` in `Shared.GameLogic`, added via a new constructor
  overload.** The 6-argument constructor is kept and forwards with `speed: 0`. This
  library is compiled **as source** by the Unity client against a pinned tag (ADR-10),
  so changing the existing signature would break that build for every call site at
  once the moment the tag moved. Adding an overload costs nothing; changing a
  signature costs a coordinated release. `AoiLogic.EncodeSnapshot` passes the speed
  through.

- **Four snapshot tests**: speed-only delta resend, unchanged-speed still suppressed,
  speed present on handle-only mentions (interned path), and speed on keyframes.

### Changed
- **`SnapshotByteIdentityTests` digests rebaselined for both encodings.** These
  deliberately hard-to-change constants moved because the protocol gained a field, not
  because a walk was restructured. Both previous digests are recorded in the source
  beside the new ones so the change stays attributable, and the comment tells the next
  reader to establish whether *their* change was supposed to alter the wire before
  touching the constant.
- **`WireProtocolTests.SnapshotMessage_JsonMatchesGoFormat`** now pins a non-zero
  speed. A zero would have pinned only that the key exists.

### Documentation
- `docs/API.md` — the normative wire reference. `entities[]` field list, the JSON
  example, and the `speed <= 0` rule. The two Protobuf hexdumps are **left as
  captured**, with a note that they predate field 9 and what it adds: tag `0x4d`
  (field 9, wire type 5), 5 bytes per entity, and a freshly captured 25-byte delta
  showing the entity block header moving `22 0d` → `22 12`. Re-capturing would have
  changed the uuid and every offset in a pair of dumps that exist to illustrate
  interning.
- `docs/DESIGN.md` — the delta comparison list now reads `type/x/y/hp/max_hp/speed`,
  matching `SentView`.


### Notes
- **A uniform spatial index for AOI was built, proved correct, measured, and
  rejected. No production code changed.** It is 2.8x slower than the brute-force
  scan at realistic density. Full numbers and reasoning: `docs/BENCHMARK.md`
  Part V; the implementation and its differential test are on
  `feat/aoi-spatial-index` at `2e3e5db`, reverted by the commit after it.
  - **The premise was wrong in the same way as before.** The case for the index
    was "40 000 distance tests per tick at 200 players, O(n²)". The count is
    right; the cost is not. Those tests are sequential reads over contiguous
    chunk arrays and are worth microseconds. The scan's real cost is composing an
    `EntityState` per **match**, which is proportional to how many players are
    near you — a property of the game, not of the algorithm — and no index
    reduces it.
  - The index made composition worse: the scan composes from the chunk it is
    already iterating, while the index holds only an entity handle and composes
    through seven random-access lookups per match.
  - It *is* 1.4–2.9x faster when AOI sets are nearly empty (2.8 matches per
    query) — exactly where the absolute cost is negligible, 77 µs to 38 µs
    against a 66 ms budget. A density-switched hybrid was considered and
    rejected: two code paths and a heuristic to win 0.06% of a tick in the case
    that was never the problem.
  - **Preserving byte-identical wire output costs a per-query sort**, which the
    Big-O argument never accounted for. The delta encoder interns entity ids in
    AOI arrival order, so a grid's cell-major enumeration changes the bytes for
    an identical set. The first implementation got the set right and the order
    wrong; the differential test caught it on the first run. Disabling the sort
    as a diagnostic did not rescue the measurement.
  - Measurement method, since a timing claim on this host needs one: paired
    in-process A/B running both implementations back to back in one process, five
    runs, comparing the ratio *within* each run. Absolute timings swing ±50%
    here; the realistic-density ratio came out 0.32–0.45 across all five, far
    tighter than the noise.
  - `docs/BENCHMARK.md` §9 item 6 is struck through, and the extension-seams
    table in the repo-root `CLAUDE.md` no longer promises a spatial grid as the
    production answer — it records that one was measured and lost, with the
    conditions that would make it worth revisiting.

### Changed
- **The snapshot path stopped throwing away its objects: entities are pooled and the
  serialization buffers are reused.** These were the two allocation sources stage 4's
  breakdown left standing — `Encode` building a fresh `EntitySnapshot` per entity per
  viewer (134 699 B/tick at 200 players, the largest remaining term by an order of
  magnitude) and `ToByteArray` allocating a new array per snapshot (44 280 B/tick).
  Neither was algorithmic and neither touches the wire.
  - **`SnapshotDeltaState` owns one `SnapshotMessage` and a pool of `EntitySnapshot`
    objects.** Rented objects are returned by resetting a high-water mark at the top of
    each encode, never by an explicit release — so there is no path that returns one
    twice, and none where a snapshot staged but never claimed leaks one. An encode that
    does not happen rents nothing, which matters because encoding has been lazy since
    stage 4. `Rent()` clears *every* field rather than the ones about to be written: a
    stale `Id` or `Handle` surviving a tick would be wrong state on the wire, the exact
    failure this subsystem exists to prevent, and clearing to the proto3 defaults is
    also what keeps the bytes identical.
  - **The ownership contract is now explicit**: the message and its entities belong to
    the state object and are valid only until the next `Encode` on that instance. The
    write task's claim → encode → serialize → write loop already satisfies it.
  - **New `SnapshotFrameWriter` serializes into buffers it keeps**, replacing four
    allocations per snapshot (payload array, `ByteString` copy, envelope body array,
    framed array) with two grown-once buffers and an `UnsafeByteOperations.UnsafeWrap`
    view over the payload. Both messages are still written by the **generated**
    protobuf writers — nothing here hand-rolls a tag or a varint, which is the version
    of this change that would have put the wire at risk.
  - **Per-connection, not shared.** Encoding moved off the tick thread onto one write
    task per connection in stage 4, so a shared pool would be touched by many threads
    and need a lock on the hottest path in the server. Both the pool and the buffers sit
    beside `SnapshotDeltaState`, which is already per-connection and single-threaded by
    design; no synchronisation is added anywhere.
  - **JSON keeps the allocating path.** It is not the production encoding and
    `JsonWriter` would need its own reuse story; the branch is one `if` in the write
    loop and is commented as such.

  **Measured — paired A/B in one process, both arms in the same binary over identical
  inputs, 60 ticks after warm-up, `GC.GetAllocatedBytesForCurrentThread`:**

  | viewers × 40 visible | legacy shape | pooled entities only | + reused buffers |
  |--:|--:|--:|--:|
  | 50 | 372 933 B/tick | 181 733 B/tick | **1 600 B/tick** |
  | 200 | 1 491 733 B/tick | 726 933 B/tick | **6 400 B/tick** |

  Identical to the byte across three repeat runs. The residual is exactly **32 bytes per
  viewer per tick** — one `ByteString` wrapper object per snapshot, the one allocation
  `UnsafeWrap` still makes. Pooling and buffer reuse are worth roughly half each; either
  alone would have left the other half in place, which is why both are here.

  **What this is not.** It is not the live server at 200 players: the harness holds AOI
  at a fixed 40 visible entities per viewer and every viewer sends every tick, where the
  real server's AOI varies and coalescing engages under load. The transferable result is
  the ratio and the per-viewer residual, not the absolute B/tick — quoting 1.49 MB/tick
  as a production figure would be wrong. **No wall-clock claim is made**: this host's
  spread on an unchanged binary is wide enough to swallow the effect, and the claim here
  is about garbage, not latency.

  **Guards.** `SnapshotByteIdentityTests`' pre-change digests pass unchanged (they are
  literals; regenerating them would have destroyed their value). A new
  `WriteFrame_IsByteIdenticalToTheAllocatingPath` compares the reused-buffer frame with
  the allocating one byte-for-byte over 40 ticks, because the digest test frames through
  `WireProtocol.Encode` itself and never reaches the new writer.
  `PooledEntities_CarryNoStaleFieldsBetweenTicks` asserts the failure a pool introduces
  and a fresh object cannot. 589 passed, 0 failed, 12 skipped (docker-gated).
- **The simulation is query-driven, its ordering is declared, and its state lives in
  the world (ECS migration, stage 5).** Commissioned as architecture, not
  performance — see *Measured*, which is a table of zeroes and says so.
  - **The core stops naming the gameplay.** `EcsWorld.EnemyCount`,
    `QueryEnemiesLocked` and `WorldWriter.QueryEnemies` are now `CountWith<TTag>()`
    and `QueryWith<TTag>(Span<EntityHandle>)`. The tag comes from whoever owns the
    content. Query descriptions are memoised per closed generic in a static generic
    field — allocation-free and AOT-safe, since every instantiation is reached from
    concrete code rather than constructed at runtime.
  - **`EnemyMoveSystem` iterates chunks.** It walks `Span<Position>` and
    `Span<Health>` through a new `SimChunk` view instead of resolving each entity
    through a handle. The per-chunk body is a `struct` visitor passed by `ref`, so
    the call devirtualises and nothing is allocated per chunk or per tick.
  - **Spawn and reap deliberately keep random access, and say why.** Spawn reads one
    piece of state and creates entities — there is no array to walk. Reap decides per
    entity and then performs a structural change, which needs an entity identity that
    a component span does not carry; exposing handles through the chunk view purely to
    satisfy a shape rule would leak the one Arch type the view exists not to leak.
  - **`SystemSchedule`**: an ordered set of systems, each declaring `Order` and a
    `ComponentAccess` of reads/writes. Ordering is declared, not implied by call
    order — pass the three enemy systems in any order and they still run
    spawn → move → reap — and a duplicate `Order` is rejected at construction, because
    an ambiguous pair would silently run in array order, which is the implicit
    ordering the type exists to remove.
  - **`ISimulationPhase.TrackedEntityCount` is gone.** The core defined a
    content-agnostic contract whose single consumer immediately renamed the property
    after the content, and it forced every future phase to summarise itself as one
    unlabelled int, which stops composing the moment there are two. The status number
    now comes from `ServerOptions.StatusEntityCount`, supplied by `Program.cs` — the
    composition root is the one place allowed to know what the game is. **The status
    JSON field name is unchanged**: the Unity DOTS sample polls `/status` and reads
    `EnemiesAlive`.

### Fixed
- **The spawner's simulation state was living in class fields, behind the seam that
  was built to stop exactly that.** `_nextEnemyNumber` and `_spawnAccumulator` were
  private instance fields on `EnemySpawnSystem` from the day the seam shipped. They
  are now an `EnemySpawnState` component on a singleton entity. The consequences were
  not stylistic: state in a field is invisible to the world, so it could never be
  snapshotted, persisted, or reset with everything else, and two instances of the
  system would each have kept their own idea of when the next wave was due.
  - The singleton carries **only** that component, so it matches none of the queries
    that require the seven standard ones and can never appear in an AOI scan, a
    snapshot, the player sweep or the entity count. Pinned by
    `SpawnStateEntity_IsInvisibleToEveryGameplayQuery`, and confirmed in the published
    NativeAOT binary, which reports `gameserver_entities{} 6` with the singleton
    present.

### Added
- **`SimulationStateArchitectureTests` — the rule becomes a constraint.** It reflects
  over every `ISimulationPhase` and `IEcsSystem` implementation, and their nested
  types, and fails on any mutable instance field or settable property. **Verified to
  fire**: reintroducing `private float _spawnAccumulator;` on the real
  `EnemySpawnSystem` builds clean and fails the test naming
  `EnemySpawnSystem._spawnAccumulator`. That is the third guard in this module
  demonstrated rather than assumed, after the AOT hints and the golden vectors.
  - The one sanctioned exception is `[SimulationScratch]`, for reusable buffers, with
    a strict criterion: a field qualifies only if resetting it at any tick boundary
    would change nothing but allocation. `EnemyReapSystem`'s handle buffer carries it.
  - Nested types are included because moving state into a private nested helper is the
    obvious way around the rule.
- **`SystemScheduleTests`** covering declared-vs-argument ordering, duplicate-order
  rejection, and the disjointness predicate in both directions. Also asserts that
  **no pair in the real enemy schedule is concurrently runnable** — spawn and reap are
  structural, and move writes the positions reap reads — so the serial order is what
  the declarations say rather than a temporary convenience.

### Measured
- Enemy AI phase, paired in-process A/B against `develop`, Release: **108 B/tick on
  both sides, identical.** Timing differences at these magnitudes (2.5 vs 3.8 µs at 0
  players) are noise.
- **Stage 5 measures nothing, which is what was expected and what was asked for.**
  Steady-state population is 4–6 enemies, so a chunk loop over five entities cannot
  show anything, and this stage was commissioned as an architectural requirement after
  its performance premise had already been disproved in stage 4. The case is that the
  shape is right for gameplay that does not exist yet — worth taking before writing
  gameplay against the seam rather than after.

### Notes
- **What a parallel step still needs, written down before anything is parallel.**
  `ComponentAccess.IsDisjointFrom` answers the component half of "can these two run
  together" and is tested. Two things it deliberately does not answer, both verified in
  the current code and both blocking:
  1. `EcsWorld._structural` is a plain `List<StructuralOp>` with no lock of its own,
     safe today only because exactly one thread mutates it under the write lock. Two
     systems doing structural work concurrently would race on the queue itself rather
     than on any component — which is why `ComponentAccess.Structural` excludes a
     system from concurrency outright instead of reasoning about its components.
  2. `EcsWorld`'s iteration-depth guard is `[ThreadStatic]`, so "is anything iterating"
     would become a per-worker fact rather than a property of the world — and that flag
     is what decides whether a spawn or despawn applies immediately or is deferred.
  Neither is fixed here, because nothing runs on another thread here.
- **The cost of the `Arch.System` generator ban, stated concretely.** A general
  N-component chunk query needs either a source generator to emit an overload per
  arity — banned, because the AOT hint guard cannot enumerate generated query shapes —
  or a hand-written combinatorial API nobody keeps complete. `SimChunk` therefore
  exposes the one component set the simulation walks linearly, and adding a system with
  a different set means adding an explicit shape or justifying handle access.
- **Content-owned components do not have to live in the core.** `EnemySpawnState` is
  declared in `Scaffolding` and is still covered by the hint guard, which discovers
  component types by namespace **or** by `[EcsComponent]` anywhere in the assembly.
  `EnemyAi` stays in `World/Components.cs` only because moving it is not this change's
  job.

### Changed
- **The server core no longer names any gameplay: what to simulate arrives through
  `ISimulationPhase`, and the content that implements it moved to `GameServer/Scaffolding`.**
  The enemy AI in this module exists to give the core something to simulate and the tests
  something to assert — it is not the game. Nothing said so, so it read as production
  gameplay wired into the host: `GameServerHost` constructed `EnemySpawner` directly behind
  an `EnableEnemySpawner` flag, and `TickLoop` held a field of that concrete type.
  - `GameServer/AI/` → `GameServer/Scaffolding/`, `GameServer.Tests/AI/` →
    `GameServer.Tests/Scaffolding/`, namespaces with them. Moves and namespace lines only,
    except `EnemySpawner` which also gained `: ISimulationPhase` and an explicit
    `TrackedEntityCount`. No logic changed in any of them.
  - New `ISimulationPhase` (`Tick(ulong)`, `TrackedEntityCount`) in `GameServer/Server/`.
    `TickLoop` holds one of those instead of an `EnemySpawner`, and calls it in the same
    place in the same write scope, so anything it changes still lands in the same tick's
    snapshot rather than the next one.
  - `GameServerOptions.EnableEnemySpawner` became
    `SimulationPhaseFactory(EcsWorld, ILoggerFactory)`. A factory because the phase needs
    the world, which the host creates; a factory rather than a flag because a flag makes
    the core name the gameplay it is supposed to know nothing about. `Program.cs` — the
    composition root, which is allowed to know what the game is — supplies it, still gated
    on `GAMESERVER_ENEMIES`.
  - The test of the seam is deletability, and the accurate statement of it is: **the core**
    does not name `Scaffolding`. Deleting the directory needs the composition root edited
    with it — `Program.cs` both `using`s the namespace and constructs the phase, by design,
    because deciding what the game is what a composition root is for. Verified: deleting
    the directory fails with `CS0234` at `Program.cs:7`, and after removing those two
    references the core builds clean with no other change. An earlier wording of this entry
    claimed the server builds with the directory simply removed, which is false as written.
  - **`Health` and `Combat` deliberately stayed in `World/Components.cs`.** They read like
    gameplay, but `hp` and `max_hp` are first-class fields in `wire.proto` and in the
    `EntityState` the Unity client compiles as source at `sgl-v0.1.6`. They are protocol.
    Moving them would change what the client compiles against — disqualifying under ADR-10.
    The comment there now says so, because the next reader will try to tidy them away.
  - Directories, not assemblies: `ArchAotHintTests` reflects over the assembly to prove
    every component type has a `T[]` hint, and splitting the scaffolding out would put
    components beyond that guard's reach — the same guard that caught a missing `EnemyAi`
    hint in stage 2. The seam is worth having; it is not worth blinding the AOT check for.
  - Scope check: the scaffolding is ~200 of ~13 000 lines in `GameServer/`, and only three
    files referenced it. The boundary already existed in practice; this names it and stops
    the core reaching across it.
  - **Gameplay written against this seam must be ECS** — systems and queries over
    components, per ADR-12. `ISimulationPhase` is the host's call into that work, not a
    licence to put simulation state in a class.
  - No behaviour change, and the evidence for each part of that separately, because the
    digests do not cover as much as they sound like they do: `SnapshotByteIdentityTests`
    builds its loop with `simulationPhase: null`, so its unchanged pre-refactor digests
    prove the **phase-less** path is byte-identical and say nothing about snapshots
    containing enemies. Enemy behaviour is covered instead by the stage-2 characterization
    tests, which were written against the pre-split shape and are unmodified here. That the
    phase call site kept its position relative to input application and snapshot building
    is established by reading the diff, not by any test. Golden vectors untouched;
    575 passed / 0 failed / 1 skipped.
  - **Known leak, not fixed here.** `ISimulationPhase.TrackedEntityCount` exists only to
    feed `GameServerHost.EnemiesAlive` on the status endpoint, which renames it straight
    back to a content word — so the core carries a gameplay-shaped concept end to end while
    claiming to attach no meaning to it. It is one int and the status JSON is unchanged, but
    it will not compose: a second phase means one number for two owners. The shape that
    would compose is a per-phase diagnostics contribution (name → count).
  - **The ECS rule behind this seam is honour-system, and should not stay that way.**
    `Tick(ulong)` passes neither world nor writer, so an implementer must capture `EcsWorld`
    itself — which is exactly the shape that invites private simulation state beside it.
    `EnemySpawner` behaves, but nothing in the signature requires it. Handing the phase a
    writer would make drift hard rather than merely discouraged; that conflicts with the
    phase opening its own scope so its structural drain lands once, before snapshots, and
    the tension is unresolved on purpose rather than papered over.

- **Snapshot encoding and serialization moved off the tick thread (ECS migration,
  stage 4).** The tick used to build every viewer's `SnapshotMessage` and
  protobuf-serialize it before handing the envelope to the connection's write
  task. Now the tick only *gathers* — it stages each viewer's AOI view on its own
  connection and signals — and the connection's existing write task encodes and
  serializes immediately before the write.
  - **Tick-thread allocation at 200 players: 192 935 → 32 B/tick.** Paired
    in-process A/B against `develop`, three runs each side, deterministic to a
    couple of bytes.
  - Ordering is **structural, not disciplinary**: each connection has one write
    task reading one channel, so tick N+1's frame cannot overtake tick N's. The
    send queue carries a `SendItem` that is either a built envelope or a "snapshot
    staged" marker, so both kinds share that one ordered path.
  - The AOI buffer is **double buffered**. Two is provably enough: a buffer is only
    handed to the encoder when a job is claimed, and an unclaimed job is overwritten
    in place, so at most one buffer is being encoded and one being filled.
  - **Ticks and snapshots are no longer 1:1 when the writer lags — a real, intended
    behaviour change.** Previously the tick encoded every snapshot and queued the
    finished envelope, so a lagging client eventually received *all* of them: a
    backlog of stale positions, late. Now it receives fewer, fresher ones. For a
    realtime game that is the better trade, but it is a change in observable
    behaviour and not merely an internal one.
    Two tests asserted the 1:1 ratio and now assert what actually holds.
    `ClientApplyingDeltas_ReconstructsServerStateExactly` asserts that the client's
    reconstructed state equals the server's exactly — the stronger statement, and
    the one the delta protocol exists to guarantee.
    `DeltaEncoding_UsesLessBandwidthThanFullSnapshots` compared raw byte totals
    across two runs of assumed-equal length; it now compares **bytes per
    snapshot**, because with coalescing the two runs can deliver different counts
    and a total-versus-total comparison would measure the coalescing rather than
    the encoding. That one only failed in CI, where the runner is slower than this
    machine and the writer lagged once in a hundred ticks.
  - **Back-pressure: coalesce to newest, and it loses nothing.** If the writer has
    not claimed the staged snapshot, the next gather overwrites it. Nothing is lost
    because encoding is lazy — a snapshot that was never encoded never advanced the
    delta encoder's `_lastSent`, so the next one encoded carries every change since
    the last snapshot *actually sent*.

### Fixed
- **Snapshots dropped under load no longer lose state until the next keyframe.**
  This is a pre-existing bug the restructuring removes rather than a new feature.
  The old order was: encode on the tick (advancing `_lastSent`), then hand the
  envelope to a bounded channel that drops the oldest when full. A dropped frame's
  updates were therefore recorded as sent and never retransmitted — the affected
  entities stayed stale on that client for up to a full keyframe interval. With
  encoding moved to the moment of writing, a frame that is never sent is also never
  encoded, so its changes roll into the next one.
  `SnapshotPipelineTests.StalledClient_CoalescesToNewest_AndLosesNoState` stalls a
  client for 80 ticks, releases it, and reconstructs the client's merged view to
  assert it matches the world exactly.

### Added
- **`SnapshotPipelineTests` — end-to-end guards on the threaded path.** Unlike
  `SnapshotByteIdentityTests`, which reproduces bytes from public inputs, these
  capture what the write task *actually wrote to the stream*, through a recording
  transport that can also be stalled on demand. They cover the four things that
  are not provable by inspection once encoding is concurrent: bytes identical to
  the reference encoder (Protobuf and JSON), strictly increasing tick order per
  connection, lossless coalescing under a stalled client, and a closed or disposed
  connection mid-broadcast neither throwing into the tick nor stalling it.

### Measured
- Paired in-process A/B against `develop`, Release, three runs per side. The probe
  runs the connections' write tasks, so the encoding really happens somewhere —
  without that the work would be *skipped* rather than moved and the number would
  be a fiction. It reports bytes reaching the transport as proof.

  | | `develop` | this branch |
  |---|---|---|
  | tick-thread alloc, 200 players | 192 935 B/tick | **32 B/tick** |
  | tick-thread alloc, 50 players | 21 628 B/tick | **160 B/tick** |

- **Wall-clock is not claimed.** Tick time was lower in all three paired runs
  (~1.9–3.1 ms → ~1.2–1.8 ms at 200 players) but this host's spread on an unchanged
  binary is ±50%, so the honest statement is: the work demonstrably left the tick
  thread, and how much that is worth in wall time is not measurable here.
- **Total CPU is unchanged.** This moves work, it does not remove it. It is a win
  where there are spare cores and roughly a wash on a single-vCPU pod. Nothing here
  makes encoding cheaper — that would be a different change.

### Known issues (observed, not fixed here)
- **Two bugs were introduced during this stage and caught before merge**, both worth
  recording because neither would have failed loudly. The first: gating the "snapshot
  staged" marker on *no job already pending* looks like an obvious optimisation and is
  a **permanent starvation bug** — the send queue is bounded and drops the oldest item
  when full, so a dropped marker would have left the job pending forever and that
  connection would have stopped sending snapshots for the rest of its life, silently.
  Markers are now unconditional and surplus ones are free. The second: the gather read
  its buffer index outside the lock while the write task flipped it, so a slow gather
  could write into the buffer being encoded. Buffer selection now belongs to the tick
  thread alone and only advances when the previous job was claimed.
- **The premise the stage was commissioned on does not reproduce.** The analysis
  cited a ~5:1 serialization:AOI ratio and called serialization "the real 80%".
  Measured at 200 players, splitting the old phase B by hand: AOI gather ~874–1177
  µs/tick, `SnapshotDeltaState.Encode` ~998–1272 µs/tick, and protobuf
  `ToByteArray` **~79–144 µs/tick**. Serialization proper is **4–6% of the tick**,
  not 80%. The two dominant terms are the brute-force AOI scan (O(viewers ×
  entities); a spatial index is the standing "production" item) and the delta/message
  building inside `Encode` — which allocates 134 699 B/tick at 200 players, almost
  all of it `EntitySnapshot` objects, and is a **pooling** problem rather than a
  threading one. Stage 4 still pays for itself because it moves *both* Encode and
  serialize off the tick, but the next real win is one of those two terms, not more
  threading.
- At 200 players in the probe the write tasks could not keep up with 15 Hz and
  coalescing engaged (wire bytes 35 030 → 30 638 B/tick). That is the designed
  policy working, and it is lossless, but it does mean 200 players on this host is
  already past the encoder's throughput.

### Changed
- **The snapshot broadcast is two phases: read the world for every viewer under one
  lock, then encode and send under none (ECS migration, stage 3 of 3).** Each
  viewer used to take the world read lock twice — once for its AOI anchor, once
  for its AOI scan — so a 200-player tick acquired it 400 times, and serialization
  ran interleaved with world reads. `TickLoop.TickOnce` now calls
  `EcsWorld.ReadAll` once, gathers every connection's anchor and AOI into buffers
  the connection owns, leaves the scope, and only then encodes.
  - New `WorldReader`, the read-side counterpart of `WorldWriter`: a scope object
    with the lock already held, so `Connection.GatherSnapshotView` can read without
    re-acquiring per call.
  - `ConnectionManager.CopyTo(Span<Connection>)` gives the broadcast a stable list
    it can walk twice without holding an enumerator or a delegate across the two
    phases. Same count-don't-saturate contract as everything else here.
  - The per-tick `ForEach(conn => ...)` delegate allocation is gone; the gather
    callback is built once.
  - The viewer scratch array is cleared after each broadcast, so a dropped
    connection is not kept alive by a stale slot until some later tick overwrites
    it.
  - **Trade, stated rather than buried:** a join or leave arriving mid-broadcast
    now waits for the whole gather instead of slipping between two viewers. The
    gather is position tests over chunk spans with no serialization in it, which is
    exactly why serialization was moved out of the locked phase rather than left
    inside it.

### Added
- **A wire byte-identity test, generated before the change and unchanged by it.**
  `SnapshotByteIdentityTests` drives a fully deterministic scenario — enemy spawner
  disabled, since it draws from `Random.Shared` — through the real tick loop for
  120 ticks and SHA-256s the exact bytes of every snapshot envelope, for Protobuf
  and for legacy JSON separately. The expected digests are literals in the test,
  not a file, because the one thing that would destroy this test's value is
  regenerating it to make it pass. It covers what a spot check cannot: an ordering
  change, a keyframe landing a tick early, a despawn arriving late. Protobuf is
  pinned separately because entity-id interning allocates handles in AOI arrival
  order, which is the most order-sensitive thing downstream of this stage.

### Measured
- Paired in-process A/B against `develop`, Release, whole `TickLoop.TickOnce`:
  **21 692 → 21 628 B/tick at 50 players (−0.3%)**, and **192 956 → 192 949 B/tick
  at 200 players** — the latter is noise. Byte-identical across paired runs.
- **This stage buys no measurable throughput, and that was the expectation going
  in.** The AOI inner loop was already chunk-iterating and compose-free, and its
  per-client allocation was removed in stage 1; there was nothing left there to
  win. The −64 B/tick is the one delegate. What did change is not measurable in
  bytes: read-lock acquisitions per tick went from **2 per viewer to 1 in total**
  (400 → 1 at 200 players), and the broadcast now has a boundary it did not have.
- ADR-12 decision 7 makes that a recorded result rather than a failure. Whether the
  extra type is worth the boundary is a judgement call, and it is recorded as one.

### Notes
- **This makes moving serialization off the tick easier, not harder** — the
  question that decides whether a stage 4 is worth having. Serialization still runs
  inside the tick (`WireProtocol.NewEnvelope` in `TickOnce`; `conn.Send` only
  enqueues), and `BENCHMARK.md` section 9 still lists moving it off as outstanding.
  It could not be lifted out before, because encoding was interleaved with locked
  world reads per viewer — there was no point in the tick where a viewer's snapshot
  input existed independently of the world. After phase A there is: every
  connection holds a self-contained view with no world reference and no lock
  dependency, so phase B can be handed to another thread without touching
  `EcsWorld` at all. That is the whole reason to keep this stage.

### Changed
- **The enemy AI is three systems over an archetype query, not one method over a
  list of ids (ECS migration, stage 2 of 3).** `EnemySpawner.Tick(get, set, tick)`
  walked a `List<string>` of enemy ids, resolved each through the world's string
  index, composed a whole `EntityState`, mutated the copy and wrote all seven
  components back — every enemy, every tick. The id list was a second source of
  truth for "which entities are enemies", kept in step with the world by hand.
  - Split into `EnemySpawnSystem`, `EnemyMoveSystem` and `EnemyReapSystem`, run in
    `EnemyAiPhase` order inside one world write scope. **Order is load-bearing**:
    spawn first so a new enemy takes its first step on the tick it appears (the
    original got that by spawning into the list it was about to walk, and it is
    visible in the snapshot); reap last, because "arrived at the centre" is a fact
    the move system produces earlier in the same tick.
  - **`[UpdateInGroup]` was not used, because there is no server-side group tree.**
    The one in the codebase belongs to the Unity *client* package and is DOTS'
    scheduler. The attribute-driven option server-side would be `Arch.System`'s
    source generator, which ADR-12 decision 4 bans: `ArchAotHintTests` reflects over
    component structs and cannot enumerate generated query shapes, so adopting it
    would create AOT surface no test can see. Ordering is therefore explicit and
    total, and `EnemyAiPhase` documents why each position is the only correct one.
  - New `EnemyAi` archetype tag, queried instead of scanning for
    `EntityKind.Value == "mob"`. **Enemy-ness is ownership, not type**: the suite
    creates static mobs constantly and one placed by anything other than the spawner
    must stay where it was put, so the tag is applied only at spawn and is
    *preserved, never re-derived*, when an existing entity is written back. Deriving
    it from the type string would have put every test mob on a march to the origin,
    and re-deriving it on update would have stripped it from any enemy written back
    through `AddEntity` — which the combat path does on every hit.
  - `AliveCount` is now a count over that archetype rather than `_enemyIds.Count`,
    so it is answered by the world and cannot disagree with it.
  - **Structural operation kinds are still exactly *add* and *remove*.** Spawning
    and reaping are what ADR-12 predicted would wake this, and neither needed a
    third kind: `EnemyAi` is applied at creation, so it rides on the add as a tag
    payload rather than as an add-component op. Arch's `CommandBuffer` remains
    unused and unusable (ADR-11).
  - Despawns now go through the deferred structural phase inside the write scope.
    The old shape could not: it collected ids into `PendingRemovals` which the tick
    loop drained *after* releasing the lock, because `RemoveEntity` inside the lock
    would have deadlocked on it. Both orderings apply removals before the snapshot
    broadcast, which is what stops a client from ever seeing an enemy inside the
    despawn radius — now checked on every tick of a 400-tick run rather than
    assumed.
  - `ArchAotHints.cs` gained its `new EnemyAi[1],` line in the same commit. The
    guard was verified to fire: deleting that one line builds clean, and
    `ArchAotHintTests` fails naming `EnemyAi`.

### Added
- **Characterization tests for the enemy AI, which had none.** Written against the
  original `Tick(get, set, tick)` and left **unmodified** across the system split —
  the fact that all 14 pass on both shapes is the parity evidence, and editing them
  during the refactor would have destroyed it. They pin the constants and the
  arithmetic as they are, not as they ought to be, including the deliberate
  non-use of `MovementSystem`.

### Measured
- Enemy AI phase, paired in-process A/B against `develop`, Release, 600 ticks after
  a 400-tick warm-up: **367 B/tick → 172 B/tick (−53%)**, identical to the byte
  across three paired runs on each side.
- **In context this is small, and that is the honest result.** At 50 connected
  players the same probe moves the whole tick from 49 949 to 49 767 B/tick — a
  **0.36%** cut, because snapshot encoding dominates by two orders of magnitude and
  stage 2 does not touch it. ADR-12 makes "measures little at its own level" a
  stop-and-keep-what-landed rather than a failure; the structural result (one source
  of truth for enemy population, real deferred structural ops, no `PendingRemovals`
  dance) is the part worth keeping.
- Wall-clock is not claimed, for the reason given in stage 1: this host's spread on
  an unchanged binary is ±50%.
- The enemy population never approaches its cap of 30. Steady state is **4–6**:
  waves of 2 arrive every 22.5 ticks and take ~63 ticks to walk in, so the cap has
  never been the binding constraint at the shipped tuning.

### Known issues (observed, not fixed here)
- **There is no "center-zone damage".** The old `EnemySpawner` class comment and the
  tick loop's `// Enemy AI: spawn, move, center-zone damage, remove dead` both
  described a phase that no code has ever implemented — enemies reaching the centre
  are despawned and nothing is damaged. Nothing was removed in this change; the
  comments were wrong, and the split is spawn/move/reap because that is what exists.
  Whether enemies reaching the centre *should* cost the players something is a
  gameplay decision, not a refactor.
- **Enemies do not use `MovementSystem`.** The AI carries its own step: unclamped by
  map bounds, normalised with a reciprocal square root rather than through
  `ResolveDirection`. Unifying the two would move every enemy onto different floats,
  so the expression was preserved character-for-character and pinned bit-exactly by
  `OneTickOfMovement_IsBitExactAgainstTheAisOwnArithmetic`. Unification is a real
  question with a wire consequence, and it is a decision for the user rather than
  something to fold into a restructuring.
- `TickLoop.TickOnce` still allocates a delegate per tick for its
  `_connections.ForEach(conn => ...)` broadcast, which is most of the 172 B/tick the
  AI phase now measures. That is the snapshot path — stage 3, not this one.

### Changed
- **The AOI walk fills a caller-owned buffer instead of allocating a list per
  client per tick.** `EcsWorld.GetEntitiesInRange` opened with
  `new List<EntityState>()`, and `TickLoop` called it once per connected client
  every tick via `SnapshotEncoder.GetNearbyEntities` — at 15 Hz that is one
  throwaway list per player every 67 ms, plus its growth reallocations, for no
  reason other than that the caller had nowhere to put the results.
  - New `GetEntitiesInRange(Vec2, float, Span<EntityState>)` returning the match
    count. Its overflow contract is **deliberately identical** to the one
    `Shared.GameLogic/Systems/AoiLogic.cs` already publishes — *count, do not
    saturate*: on a short buffer the first `destination.Length` matches are
    written and the scan continues, so the return value is the size the buffer
    needed to be. A saturating variant would make "full" indistinguishable from
    "exactly full", which is silent AOI truncation — entities missing from a
    keyframe with no error anywhere. Two AOI functions in one server with two
    different overflow contracts would be worse than either contract alone, so
    `AoiSpanAndInputBindingTests.SpanScan_OverflowContract_MatchesAoiLogic`
    cross-checks the two directly rather than restating the rule.
  - Each `Connection` owns the buffer, because its right size is a property of
    that client's neighbourhood and it has to survive between ticks to be worth
    anything. `Connection.ScanAoi` implements the retry half of the contract:
    grow to exactly the needed count and rescan once. A spawn landing between the
    two scans falls back to the list path for that one tick rather than
    truncating.
  - Both the span and list forms now run one scan implementation, so the AOI
    predicate and iteration order cannot diverge between them — order matters,
    because the delta encoder's bookkeeping is order-sensitive and a reordering
    would be a wire change.
  - `SnapshotDeltaState.Encode` gained a `ReadOnlySpan<EntityState>` overload; the
    list overload forwards to it via `CollectionsMarshal.AsSpan`, so there is one
    implementation and existing callers are untouched.
- **Input is bound to its entity at ingest, so the tick loop no longer touches the
  world's string index.** `EcsWorld.PushInput` resolves the user id to an
  `EntityHandle` on the **network** thread, under a read lock that contends with
  nothing. The simulation thread then never hashes a user id: movement coalescing
  is keyed by handle (an integer hash) rather than by a `Dictionary<string, int>`,
  and the handler addresses the entity directly.
  - `_index` remains, and is still the authority for join, reconnect and
    persistence. It is simply no longer on the per-input path.
  - A handle can go stale between ingest and drain — a disconnect inside the hold
    window destroys the entity and a reconnect creates a different one.
    `EcsWorld.RebindStale` re-resolves exactly those entries at the top of the
    input phase, costing a lookup only for entries that are actually stale
    (normally none). This preserves the old behaviour, which resolved at process
    time and so always addressed the *current* entity; `PendingInput` keeps the
    user id for that reason and for logging.
  - Arch's `Entity` is a stable identity rather than a slot pointer, so a handle
    survives archetype moves and chunk compaction. Destruction is the only thing
    that invalidates it, which is why revalidation is a liveness check and not a
    re-resolve.
  - `DrainInputs` gained an overload that drains into a caller-owned list, so the
    tick reuses one list instead of building a new one every tick.
- **The per-tick input path now addresses components, not whole entities (ECS
  migration, stage 1 of N).** Arch has been the storage engine since ADR-10, but
  nothing above it was written as an ECS: `EcsWorld.Update(get, set)` handed
  callers a getter that composed a whole `EntityState` out of seven components and
  a setter that wrote all seven back. Moving a player therefore cost fourteen
  component lookups plus two managed-reference stores — `EntityIdRef` and
  `EntityKind` were rewritten on every input even though neither can change after
  spawn — to update a position and an input cursor. That is Arch used as a
  dictionary with extra steps, and it is the shape this change starts unwinding.
  - New `EcsWorld.UpdateComponents(Action<WorldWriter>)`: the same write lock and
    the same deferred structural phase as `Update`, but the callback receives a
    `WorldWriter` giving `ref` access to individual components. `EntityHandle`
    wraps Arch's `Entity`, so no `Arch.Core` type became public and
    `Shared.GameLogic` still never sees the ECS.
  - The string id is now resolved **once per entity per scope** rather than on
    every read and every write. This is the first half of ADR-10's integer
    simulation handle; the string id survives unchanged on the wire, in
    persistence and in `EntityState.Id`, which are separate migrations.
  - `InputHandler.ProcessInput` reads `Health`, `InputCursor`, `Position` and
    `Locomotion` and writes `Position`, `InputCursor` and `Combat` in place.
  - `TickLoop`'s per-connection snapshot prologue was `View(get => ...)`, which
    composed an entire `EntityState` to read two fields and allocated a closure
    per connection per tick to carry them out of the lambda. It is now
    `EcsWorld.TryGetSnapshotAnchor`, reading `Position` and `InputCursor` directly
    and allocating nothing.
  - **Measured end to end, whole `TickLoop.TickOnce`**, same probe run on this
    branch and on `develop` (Release, real `Connection` objects over a null
    transport, Protobuf encoding, clustered so AOI actually matches):

    | players | develop | this branch | |
    |---|---|---|---|
    | 50 | 436 276 B/tick | 21 692 B/tick | **20x** |
    | 200 | 6 762 858 B/tick | 192 984 B/tick | **35x** |

    Allocation is deterministic — three paired runs at 200 players spread under
    0.05% on both sides. **Wall-clock is not reported as an improvement**: paired
    medians moved ~3.7 ms -> ~2.0 ms per tick, but this host's run-to-run spread on
    the same binary is +-50% (2.9-4.5 ms on `develop` alone), which is the
    contamination ADR-7 documents. The allocation number is the claim; the timing
    is directional at best.
  - **No arithmetic was re-derived.** The movement step still calls
    `MovementSystem.TryMove`; it is handed the three fields `TryMove` reads instead
    of a fully composed entity. That field selection is the one new assumption, and
    it is the kind that fails silently, so
    `ComponentInputPathTests.Movement_ComponentPath_IsBitExactAgainstWholeEntityTryMove`
    replays every position/speed/move/bounds combination in the ADR-10 movement
    fixture through the real handler and compares the stored position bit-for-bit
    against `TryMove` called with a fully populated `EntityState`. The golden
    vectors themselves are untouched and still pass.
  - **No new components and no new archetypes**, so `World/ArchAotHints.cs` is
    unchanged and `ArchAotHintTests` still covers everything the process can
    create. `CommandBuffer` is still unused (ADR-11). NativeAOT publish verified.
  - The attack branch still composes whole `EntityState` values, because
    `CombatLogic.ValidateAttack` / `CalculateDamage` / `HandleDeath` and the death
    callback are `Shared.GameLogic` entry points shaped that way, and
    `Shared.GameLogic` is deliberately not being changed. Its write-back is already
    component-level. Removing that round trip needs a decision about
    `Shared.GameLogic`'s API surface and is not stage 1's to make.
  - `EnemySpawner` and `AsyncSaver` still use the `EntityState` API — stage 2 and
    a later stage respectively. Left alone on purpose: this change is meant to be
    revertible on its own.
  - **Structural operation kinds are unchanged**: the deferred queue drained by
    `ApplyStructuralChanges` still carries exactly *add* and *remove*. Stage 1's
    systems need no add/remove-component op, and the machinery for one is not
    being built before something uses it (ADR-12 decision 2).
  - **A per-entity pending-input component was considered and rejected.** It would
    hold at most one input per entity per tick, but the server processes *every*
    queued input for attacks and only the newest for movement — collapsing them
    into one component would drop attacks under multi-input ticks, which is a wire
    change. Binding the handle to the queue entry achieves the same "resolve once,
    at ingest" property with no behavioural cost.
  - **No new component types**, so `ArchAotHints.cs` is unchanged and the hint list
    is still complete (ADR-12 decision 3). No `Arch.System` source generator, so no
    query shape the reflection guard cannot enumerate (ADR-12 decision 4).

### Known issues (observed, not fixed here)
- **A self-targeted attack applies its cooldown and fires the death callback, but
  its damage is discarded.** This is not new and it is not a rule anyone wrote: the
  old `get`/`set` input path ended with `set(userId, attackerCopy)`, which
  overwrote the target write whenever attacker and target were the same entity, so
  the HP change was silently rolled back after `HandleDeath` had already run and
  already notified. Component writes have no such last-writer-wins accident, so the
  new path would have *changed* the observable outcome. Since stage 1 promises no
  wire-visible change, `InputHandler.ProcessInput` now discards that write
  explicitly, and `ComponentInputPathTests.SelfTargetedAttack_KeepsCooldownButDiscardsDamage_MatchingPreviousBehaviour`
  pins it — so a later change that decides to fix the oddity has to delete a test
  that states what it is deleting, rather than change behaviour by accident.

### Added
- **One shared way for tests to get a bound port: `GameServer.Tests/Infrastructure/TestPorts.cs`.**
  This is the deliverable; the classes it fixes are incidental. Seven copies of the
  same eight-line `FreeTcpPort()` helper existed across the test project — bind
  port 0, read the number, **close the listener**, hand the number to whoever
  binds next — and every copy carried the same TOCTOU window, in which any
  sibling test could take the port and produce
  `SocketException : Address already in use`. Fixing one copy (`TransferMapTests`,
  previous entry) taught the other six nothing, which is precisely the argument
  for a single implementation. All seven are deleted, plus an eighth inline copy
  in `MetricsEndpointTests`.
  - **`TestPorts.StartServerAsync(server, ct)`** — for anything that runs a
    `GameServerHost`: starts it on `":0"` and reads the port back out of the
    listener via `ListeningAddressAsync`. Nothing is predicted and nothing is
    released, so there is no window at all. It also replaces the connect-and-retry
    probes that were standing in for "is it up yet" — the bind completing *is* the
    answer. Used by `EntityLifecycleTests`, `GameServerHostShutdownTests`,
    `JoinTokenSecretTests`, `MapIdReloadIntegrationTests`,
    `PostgresPersistenceIntegrationTests`.
  - **`TestPorts.Lease`** — for a binder that cannot report what it bound and must
    be told a number up front. The socket is *held* until the instant before the
    handoff. This is documented as a narrowing, not a fix: the port is still free
    between `Dispose()` and the real bind.
  - **Docker fixtures use a third answer.** `EphemeralPostgres` publishes
    `-p 127.0.0.1:0:5432` and asks docker what it assigned
    (`TestDocker.PublishedPort`, i.e. `docker port <name> 5432/tcp`). Docker's own
    bind is the allocation, so the port is occupied from the moment it exists —
    the strongest of the three. `TestDocker.FreeTcpPort` is gone.
    **`EphemeralRedis` deliberately does not do this**, and the reason is worth
    recording: that fixture exposes `Stop()`/`Start()` to simulate an outage, and a
    container published on `":0"` is given a *different* host port each time it
    starts. Converting it made
    `RedisOutage_DoesNotKillTheService_AndItReRegistersOnReconnect` fail on every
    single run — `RegistrationService` reconnects to the address it was given at
    construction, and that address had moved. Caught by the ten-run requirement,
    which is the second time that rule has paid for itself. It uses a `Lease`.

  The one case the `":0"` pattern genuinely cannot serve is `MetricsEndpointTests`:
  it exercises `MetricsEndpoint`, which binds an `HttpListener`, and `HttpListener`
  prefixes require a literal port, have no ephemeral-bind mode, and report nothing
  back. That class uses a `Lease`. Its observed failure mode is consistent with the
  release-immediately helper handing the *same* port to more than one of the four
  parallel `[Theory]` cases — the kernel will re-issue a port it has just taken
  back — and leases held concurrently cannot collide, so that part is closed even
  though the final handoff is not atomic.

  **Measured, 10 consecutive full-suite runs each side, both on the same base**
  (559 tests, same host, unmodified tree in a scratch worktree vs this change):

  | | baseline | with the harness |
  |---|---|---|
  | fully green runs | 6/10 | **9/10** |
  | `MetricsEndpointTests` | 4 runs | **0** |
  | `JoinTokenSecretTests` | 1 | **0** |
  | `GameServerHostShutdownTests` | 1 | **0** |
  | `RegistrationServiceTests` | 0 | **0** |

  The one failing run afterwards failed on `EcsWorldTests.ConcurrentAccess_NoDeadlock`
  and `RedisServerRegistryTests.Heartbeat_ReArmsTtl` — neither a port bind, both
  passing 6/6 in isolation, and neither touched here.

  **What these numbers can and cannot show.** They are a probability, measured on
  one host on one day. A race's rate moves with scheduling — the ECS stage-1 work
  that cut per-tick allocation shifted it enough that three consecutive suites on
  the same base showed none of these classes failing at all. So read the table as
  corroboration, not proof. What is actually proven is structural and does not
  depend on any run: the eight TOCTOU copies are gone from the source, and every
  `GameServerHost` consumer now learns its port from the listener instead of
  predicting it, which removes the window rather than shrinking it. The one place
  a window remains is `MetricsEndpointTests`, and it is documented as remaining.

### Added
- **`docs/API.md`: map transfer (13/14) and the KCP transport are now documented
  as client contracts.** Both were reachable only by reading server source: the
  message-type table stopped at 15 with a note that 13/14 were "reserved", and
  KCP had no entry at all. Written for a client implementer, with the maturity of
  each stated up front rather than left to be discovered.
  - **Map transfer** — shapes, the five-step client-driven sequence, and the
    three consequences a client must be built around: the *server* closes the
    game-server connection, a new join token can only come from the gateway
    (there is no server-to-server handoff), and completing the hop needs an
    authenticated gateway connection — so keeping that connection open across
    the session avoids a re-auth round trip on every transfer.
  - Documented that transfer is the one path where the 30 s hold does **not**
    apply: the entity is reaped immediately because there is nothing to
    reconnect to, so a transferring player leaves no ghost. That is the inverse
    of the disconnect path and is diagnostic in both directions.
  - **The unvalidated destination**, which is the sharp edge: the current server
    checks only that `map_id` is non-empty and different from its own. It does
    not check the target exists or has capacity, yet it destroys the entity and
    drops the connection regardless. A client whose destination turns out to be
    unreachable is left nowhere — off the old server, not on the new one, entity
    already reaped. Guidance is to read `ok = true` as "you have left", not "you
    have arrived", keep the original `map_id` to fall back to, and not tear down
    the local world until the join on the new server succeeds.
  - **KCP** — that framing and the whole message layer are unchanged (same 4-byte
    prefix, no KCP-specific handshake), the exact ARQ parameters the server uses
    with the reasoning behind each, and the crypto in reimplementable detail:
    HKDF-SHA256 with no salt and the exact info string, the 16 B nonce + 4 B
    CRC32 header, whole-buffer AES-CFB with kcp-go's fixed IV given byte by byte.
  - Recorded that **encryption exists only on the KCP path** — `TcpTransportListener`
    takes no key — so TCP is not "unencrypted for now", it has no encryption path
    at all, and encryption arrives with KCP or not at all.
  - Recorded that a client does **not** need to hand-roll KCP or adopt a
    third-party library: `GameServer/Net/Transport/` already holds a complete,
    dependency-free C# port of kcp-go, with the two caveats that it is not in
    `Shared.GameLogic` and implements the listener side.

### Fixed
- **`TransferMapTests` is deterministic again — it had *three* independent races,
  and only one of them was the one previously written down here.** The known-issue
  entry described the port race alone; the failure that took CI red on `develop`
  was a second one, and repeat-running the suite locally (10x) exposed a third
  that no single run had shown. All three are defects in the test, not the server.
  - **Race 1 — the port (TOCTOU, `SocketException : Address already in use`).**
    The old `FreeTcpPort()` bound port 0, read the assigned number, **closed its
    listener**, and handed the number to the server, which bound it some time
    later; any concurrent test could take the port in that gap. Fixed by removing
    the guess entirely rather than by retrying around it: the harness now starts
    the server on `":0"` and asks it which port it got. `GameServerHost` gained
    `ListeningAddressAsync` (`GameServer/Server/GameServer.cs`), a task completed
    with the bound address the moment `TransportFactory.Listen` returns — the
    transport already resolved ephemeral binds, the address just was not
    reachable from outside. There is no window left to lose: the socket the
    server reports is the socket the server is listening on. It is faulted if the
    bind throws and cancelled on shutdown, so a waiter cannot hang on a server
    that never came up. This also deletes the harness's connect-retry loop —
    "the listener is bound" is now a fact, not something to poll for.
  - **Race 2 — the online gauge (`Assert.Equal() Failure: Expected: 0 /
    Actual: 1` at `TransferMapTests.cs:57`).** The test waited for
    `EntityCount == 0` and then asserted on `metrics.PlayersOnline`, a different
    counter updated on a later line: `HandleTransferMap` removes the entity
    (`_world.RemoveEntity`) and only *then* decrements the gauge
    (`_metrics.PlayerLeft()`). The waited-for condition is therefore reached
    strictly before the asserted one, and any preemption in that window — likely
    on a loaded CI runner, rare on an idle laptop — failed the test. Fixed by
    waiting for the condition actually being asserted
    (`EntityCount == 0 && PlayersOnline == 0`). **The server is not at fault and
    was not changed for this**: nothing promises the two counters move
    atomically, and reordering them would only move the same window elsewhere.
    The timeout was not touched — it was already 15s, and the failure was an
    ordering bug, not a slow machine. `WaitForAsync`'s failure message now prints
    the gauge alongside the entity count, since `entities=0` on its own reads
    like a passing state.
  - **Race 3 — the reply is not the next frame (`Expected: 14 / Actual: 8`).**
    Each test wrote a request and then decoded exactly one envelope, assuming it
    was the reply. It is not: the tick loop broadcasts snapshots at `TickRate`
    (20 Hz in these tests), so a snapshot emitted between the request and the
    reply arrives first and the assert reads message type 8 (`Snapshot`) where 14
    (`TransferMapResp`) was expected. This one never reproduced in a single run —
    it took repeat runs of the full suite to surface, which is why the earlier
    known-issue entry missed it. Fixed with a `ReadUntilAsync(stream, want)`
    helper that decodes until the awaited type arrives; the join handshake reads
    through it too. It skips only the types the server legitimately pushes
    unsolicited (`Snapshot`, `Pong`, `Resync`) and fails hard on anything else,
    so it cannot mask a wrong reply.
  - **Evidence.** 10 consecutive full-suite `dotnet test` runs (531 tests each)
    plus 5 runs of the class in isolation: **0 `TransferMapTests` failures**,
    where the same 10-run loop before the fix failed it 3 times. A single green
    run proves nothing here — the CI failure this started from went green on a
    plain re-run with no source change.
  - **Not fixed, and not caused by this change:** the same repeat-run loop shows
    `GameServerHostShutdownTests`, `RegistrationServiceTests.Heartbeat_*`,
    `MetricsEndpointTests.TryStart_*` and `JoinTokenSecretTests` failing
    intermittently under load, the last of them with the same "Address already in
    use" signature — those classes still carry their own copy of the racy
    `FreeTcpPort()`. Verified against an unmodified tree (4 full-suite runs), so
    they predate this work. Porting the `":0"` + `ListeningAddressAsync` pattern
    to them is the obvious follow-up.
- **The KCP cross-language interop tests were silently skipping; they run again,
  and they pass.** All nine `KcpInteropTests` cases reported as *skipped* rather
  than failed because the Go probe they drive, `interop/kcpprobe`, no longer
  built: its `go.sum` carried no entry for `google.golang.org/protobuf v1.36.6`,
  which `shared` requires — only `/go.mod` hashes for 2020-era versions. It had
  not built since `shared` took its Protobuf dependency. `go mod tidy` on that
  module fixes it (+6 lines across go.mod/go.sum, no version changes).
  Result: `dotnet test --filter Kcp` goes from **44 passed / 9 skipped** to
  **53 passed / 0 skipped**. Everything that had been dark is now verified
  against the real `kcp-go`: HKDF key-derivation agreement
  (`GoDeriveKey_MatchesCSharpDeriveKey`), echo through the C# listener for
  plaintext, a hex key and an HKDF-stretched passphrase, a full join over both
  plaintext and encrypted KCP, and three mismatched-key cases proving the
  session fails closed. **The AES-CFB crypto interop passes** — that was the
  specific thing at risk, since a decrypt mismatch produces noise rather than an
  error message.
- **A build failure in that harness now FAILS instead of skipping.** The skip was
  what made the regression invisible: `Skip.If` covered "no Go toolchain" and
  "probe failed to build" identically, so a broken harness read as an
  unsupported environment and CI stayed green. `GoProbe` now distinguishes them
  — a missing toolchain still skips, since a C#-only build machine is
  legitimate, but a toolchain that IS present and cannot build the probe fails
  with the build's stdout/stderr attached. Verified by temporarily restoring the
  broken `go.sum`: the case failed with "The kcpprobe Go harness FAILED TO
  BUILD…" instead of skipping.

### Known issues (observed, not fixed here)
- ~~**The KCP cross-language interop tests have been silently skipping.**~~
  *Fixed in this release — see the Fixed entry above. Original description
  retained for context:* All nine
  `KcpInteropTests` cases — Go/C# key-derivation agreement, echo through the C#
  listener for plaintext / hex key / passphrase, wrong-key-fails-closed, and a
  full join over plaintext and encrypted KCP — report as *skipped*, not failed.
  Cause: the Go probe they drive, `gameserver-dotnet/interop/kcpprobe`, no longer
  builds. Its `go.sum` carries no entry for `google.golang.org/protobuf v1.36.6`,
  which `shared` requires — only `/go.mod` hashes for 2020-era versions — so the
  module has not built since `shared` took its Protobuf dependency. The harness
  catches the build failure and calls `Skip.If`, which is why nothing is red.
  Consequence: the C# KCP implementation is verified C#-to-C# (44 tests pass) but
  its agreement with the real `kcp-go` is currently **unverified**, including the
  crypto interop that is most likely to fail silently. CI does not catch it —
  `dotnet test --no-build -c Release` treats skips as success.
  Not fixed here: this task was to document the client contract, and repairing
  the probe's module hygiene is a separate change. Worth doing before anyone
  implements a KCP client, so there is a working cross-language oracle.

### Added
- **`docs/API.md`: a held entity is indistinguishable from a live one standing
  still, and now says so.** When a client disconnects its entity is not removed
  — it is held for the reconnect grace period and stays in every nearby client's
  snapshots at its last position with full HP. There is no "held" flag on the
  wire and no `disconnected` field on `EntitySnapshot`, so for up to 30 s a
  dropped player renders exactly like one standing still, then vanishes with no
  warning. Nothing in the protocol distinguishes them. Documented beside the
  removal semantics because it is the same class of trap as "`removed` does not
  mean gone forever", along with the two weak signals a client does have (the
  entity stops changing — a hint, not proof, since players do stand still; and
  the eventual `removed`, which is authoritative but does not separate a despawn
  from an AOI exit).
  Extended with the consequence that a client **cannot observe when a peer
  leaves**, only when the server eventually says so — so any duration a client
  computes that ends at "my peer left" is an upper bound inflated by up to the
  full hold, silently, because the held entity keeps arriving in snapshots.
  Recorded with the measured instance: two clients timing the same co-presence
  window, both clocking from peer-visible, reported 62.8 s and 74.9 s against a
  true 63.0 s — the second overstating by 11.9 s, which equals the time it
  outlived its peer (server-side timestamps put the two exits 11.96 s apart).
  Neither client was faulty. The general rule is stated: a co-presence duration
  cannot be computed by one client; it is the minimum across both, equivalently
  second-join-to-first-exit, and needs both clients' data by nature.
  Worth having because it is the more dangerous face of the held-entity trap —
  the rendering symptom produces a ghost someone may notice, this silently
  corrupts a number that looks entirely reasonable. Found and disproved by the
  Unity client team after an earlier per-client metric shape had been reviewed
  and wrongly endorsed, including by this module's own maintainer.
  Recorded with its corroboration: a client observed the peer frozen at
  `x = 0.85` for 16 s, and that peer's server-side `player_states` row reads
  `x = 0.854`. Client view and persisted value being the same number is what
  establishes the entity is genuinely frozen rather than the client having
  stopped receiving updates for it. Surfaced by the Unity client team during the
  two-process visibility test.
- **AOI radius and the entity hold are now recorded as MEASURED constants, not
  just specified ones.** Both were quoted throughout the docs as bare
  specifications. Each has now been measured against the running local stack
  from two independent directions, and the figures agree:
  - **AOI 50 units** — server side, two players persisted 61.00 units apart in
    `player_states` were outside each other's snapshots; client side, a remote
    player was last visible at 50.5 units apart and absent by 62.2, from a Unity
    client's own snapshot tracking.
  - **Map-server entity hold ~30 s** — server side, `gameserver_entities` lagged
    `players_online` by 29 s and 32 s across two disconnects before converging;
    client side, a removal reached the surviving client 30.1 s after a deliberate
    disconnect, carried by id.
  The agreement is the point: the two paths share no code and neither
  measurement was taken with sight of the other, so landing within ~1 unit and
  ~1 second is evidence in a way either figure alone is not. Recorded with the
  date and what they were measured against, because this repo already has one
  number that outlived its evidence (the 150-players ceiling, ADR-7) and the way
  not to repeat that is to say what a figure is *of*.
  Landed in `docs/DESIGN.md` as a "Measured constants" table, cross-referenced
  from `docs/API.md` where it matters to a client — a multiplayer test that
  spawns or drives two clients more than 50 units apart fails against a
  *correct* server, so distance is the first thing to check, not the netcode.
  Also annotated the stale `backend/docs/CORE_FLOW.md` AOI row inline, following
  that document's own convention for superseded entries, since it still cites
  the radius against deleted Go paths.
  Updated with tighter figures from a later two-process run: the hold is now
  measured by **three independent methods** — Prometheus gauge lag (29 s / 32 s),
  server log disconnect-to-hold-expiry end to end (30 s / 31 s), and client-side
  timing of the `removed` entry (30.1 s). All three paths are kept listed rather
  than collapsed to one tight number, because the agreement across methods using
  different data is the evidence; a single figure would read as precision it has
  not earned.
  Added a persistence detail a reader would otherwise guess wrong: **the save
  lands with the hold EXPIRY, not the disconnect** — a client that disconnected
  at 08:00:52 has its row stamped 08:01:21.56. Anyone assuming save-on-disconnect
  misjudges the crash-loss window by the full hold: if the process dies during a
  hold, up to 30 s of movement was never written.
  Noted alongside them that a client run shorter than 30 s cannot test the
  heartbeat at all — it ends before the timeout could fire — so a clean short
  run is not evidence the heartbeat is handled. The heartbeat is now confirmed
  exercised rather than assumed: a two-process run held connections in-world for
  75 s and 74 s, ~7 pings each, with no `Heartbeat timeout` for either. Stated as
  a rule to keep the claims separate — short runs prove visibility, long runs
  prove liveness, and reading the first as the second is the mistake.
- **`docs/METRICS.md`: `gameserver_entities` disagreeing with
  `gameserver_players_online` is CORRECT, and now says so.** The two gauges count
  different things — entities in the world versus live connections — so during a
  reconnect hold the first legitimately exceeds the second. This reads as a leak
  to anyone meeting the metrics cold; it did to me. Documented with the measured
  timeline showing the gauges agreeing *exactly* while both clients were
  connected and diverging only after disconnect, so the benign case is
  distinguishable from a real one: a disagreement while connection count is
  stable, or `entities` staying high well past the hold, is a defect; a few tens
  of seconds of `entities > 0` at `players_online = 0` is the hold working. Also
  records that the registry's `player_count` tracks `players_online` (agreeing
  in all 450 samples), not `entities`.

### Fixed
- **`docs/API.md`: the normative merge algorithm resolved handles on a keyframe,
  which fails silently on a malformed one.** Raised by the client team during an
  implementation audit, and they were right. The section resolved a bare handle
  against the *existing* table regardless of `full`, and instructed implementers
  not to reorder. For valid input that is harmless — a keyframe re-introduces
  every binding, so the lookup never fires. For a keyframe that wrongly carries
  `handle != 0` with an empty `id`, it resolves against the **previous
  interval's** bindings and produces exactly the wrong-entity corruption the same
  section warns about twice, with no error raised.
  Verified against both implementations before changing anything:
  `SnapshotDeltaState.EncodeFull` clears `_handles` and resets `_nextHandle` to 1
  *before* encoding, so every keyframe entity takes the "first mention" branch and
  carries both `id` and `handle` — a keyframe referencing a prior-interval handle
  is therefore structurally impossible, and rejecting one can never refuse valid
  input. `wire.proto` states the same contract.
  The fix is not the reordering that was proposed (clear-then-resolve): that
  fails closed but mutates before validating, so a malformed keyframe empties the
  world and leaves it empty until a resync completes. Step 1 now treats a bare
  handle on a keyframe as an error *without consulting the table*, which keeps
  the all-or-nothing ordering **and** fails closed. Added an implementers note
  recording that the Go reference `messages.SnapshotState.Apply` does not yet
  carry this guard, so anyone matching it byte-for-byte knows.
- **`docs/API.md` cited two "reference implementations" that do not implement the
  algorithm it documents.** `Shared.GameLogic.Systems.SnapshotMerger` covers
  steps 2–4 only: it has no handle table and cannot have one, since `EntityState`
  has no `handle` field — interning is a wire concern and `Shared.GameLogic` is
  the pure simulation library. The layering is correct, but a client that copied
  the merger as-is for Protobuf would upsert every interned entity under an empty
  `Id` and silently collapse them into one bucket. Documented that handles must
  be resolved in the codec/transport layer *before* entities reach the merger,
  and which steps each reference actually covers.

### Added
- **`docs/API.md`: the heartbeat was entirely undocumented, and it disconnects
  clients.** The message-type table stopped at 10, so `ping` (11), `pong` (12)
  and `kick` (15) had no entry anywhere in the wire reference. The server pings
  every 10 s and closes any connection that has not answered within 30 s
  (`GameServer/Net/Connection.cs`, `PingInterval` / `PongTimeout`) — so a client
  built faithfully from this document joins, moves, receives snapshots, and is
  then dropped mid-session with no indication of why. It presents as a random
  disconnect rather than a missing message handler, and it survives every short
  test because nothing goes wrong for the first 30 seconds. Found the hard way:
  two runs of a Protobuf trace probe were killed mid-capture by exactly this,
  logging `Heartbeat timeout for <user>`. Added the three missing table rows, a
  normative Heartbeat section (echo `timestamp` unchanged, set `server_time`),
  the explicit warning that sending `input` does **not** substitute for a
  `pong`, and a note that 13/14 are reserved for map transfer.
- **`docs/API.md`: three more worked wire traces**, captured from live Protobuf
  connections and decoded field by field, covering the cases the single existing
  example could not show:
  - **multi-entity** — three players introduced in one keyframe with distinct
    handles, then all three carried by handle alone in the next delta (167 → 51
    bytes). States that the handle space is per-connection, not per-entity-type
    or global.
  - **removal beside surviving interned entities** — `removed` as field 5
    holding a 36-byte ID in the *same frame* where a surviving entity is
    referenced by bare handle, since that contrast is what gets misread. Adds
    that a removal does not "release" a handle, and that AOI exit and true
    despawn are indistinguishable to the client, so `removed` means "stop
    rendering", not "gone forever".
  - **mid-stream resync** — the handle-space reset with two bindings observed
    changing meaning across the keyframe (handle 1 and handle 3 rebound to
    different entities, handle 2 unchanged, handle 4 unbound). This is the
    concrete form of the "handle 1 after a keyframe is a different entity"
    trap: every handle still resolves, so a stale table renders the wrong
    entity with no error raised.
- **`docs/API.md` now documents the Protobuf-only behaviours a client has to
  implement, not just the message shapes.** The Protobuf path is the documented
  default and is enforced cross-language on every merge
  (`TestDotnetInterop_FullFlow` runs the whole flow once per encoding), but a
  client could read the whole reference, implement it faithfully against JSON,
  swap the codec, and still be wrong — because two of the differences are
  *behaviour*, not encoding. Four gaps closed:
  - **The normative client merge algorithm had no handle-resolution step at
    all.** Followed literally it breaks on every Protobuf delta, since deltas
    carry an empty `id`. It now resolves handles first, aborts the entire
    snapshot on an unresolvable one, and clears `handles` alongside `world` on a
    keyframe. Called out the three load-bearing ordering details: resolve before
    mutate, resolution reading the table *before* the keyframe clears it (safe,
    and not to be "fixed"), and `removed` carrying IDs rather than handles. One
    algorithm now covers both encodings instead of silently assuming JSON.
  - **The entity-type enum/`type_name` fallback was undocumented.** Added the
    rule (prefer `type`, read `type_name` when `UNSPECIFIED`), the five enum
    values with their string forms, and why reading only one field fails in both
    directions. The old prose listed `boss` as if it were an enumerated type; it
    is not in the enum, so it degrades through `type_name` — the entry now says
    so rather than implying an enum value that does not exist.
  - **Two client constraints on the encoding were implicit.** The payload must be
    Protobuf inside a Protobuf envelope (no JSON-in-proto hybrid), and `type = 0`
    must never be sent — spelled out with the `0x08` sniffing reason, since that
    is the non-obvious one and the fail-closed `0x12` case depends on it.
  - **Added a worked wire trace** captured from a live Protobuf connection: the
    same entity across a keyframe and a delta, broken down field by field. It
    demonstrates three rules at once that prose can only assert — `id` sent
    exactly once, `type_name` absent because the enum carried the category, and
    `full` absent from the delta because proto3 omits `false`. Includes the
    suite's own measured saving (json=127B, proto=61B, 52.0%).
  Documentation only; no behaviour change.
- **Golden vector `multiply_add_intermediate_rounding`** — the split-multiply in
  `MovementSystem.Integrate` is now covered by a test rather than by reasoning.

  Written as one expression, `position + direction * step` may be evaluated
  strictly in float32, with a wider (double) intermediate, or contracted into a
  single FMA that rounds once instead of twice. Splitting the multiply denies all
  three. Every other movement case rounds identically under all three, so the fix
  passed and failed nothing either way. These inputs separate the strict result
  (`0x401B4740`, what the fixed code produces) from the alternatives
  (`0x401B473F`).

  **The fix is load-bearing, not precautionary.** Running the unfixed expression
  shape directly under Unity's Editor Mono JIT — operands read from static fields
  so Roslyn could not constant-fold — produced `0x401B473F`, a different position
  from the server's.

  **What the case does not prove.** It cannot distinguish FMA contraction from
  double widening: both predict `0x401B473F`, so a pass rules out neither
  individually. The mechanism actually measured under Editor Mono is *widening* —
  on `sqrt_negative_components` an FMA would give the strict answer
  (`0x4203EB84`) while Unity produced the wide one (`0x4203EB85`). FMA
  contraction remains unobserved there, and unmeasured under IL2CPP, which is
  what ships. The case was originally named `fma_multiply_add_discriminator`,
  which overstated it; renamed before anyone could read a green suite as proof
  that no runtime fuses.

  Inputs were derived by hand from the algorithm on the client side; the
  committed expectation comes from running the real code through the generator.
  The two agree exactly, which is what makes the case evidence rather than
  circular.

### Fixed
- **The hostless-`GAMESERVER_PUBLIC_ADDR` warning no longer misses the case it
  was written for.** `publicAddr` is advertised to clients verbatim, so it must
  be dialable by them (`Program.cs`, contract comment on `publicAddr`). The
  startup guard only fired when the variable was *unset* and had fallen back to
  the listen address (`publicAddr == addr && addr.StartsWith(':')`). An operator
  who set it explicitly but still hostless — e.g. `GAMESERVER_PUBLIC_ADDR=:9200`
  on a container listening on `:9000` — tripped the exact failure the message
  describes and got no warning at all, because the two values differed. The
  guard now tests whether the advertised address has a host part, via a new
  `IsHostlessAddr` helper treating `""`, `0.0.0.0`, `::` and `[::]` as hostless
  (the same host list as the Go reference client's `NormalizeDialAddr` in
  `backend/smoketest/smoke/helpers.go`, so both sides agree on what counts as
  listen-style). The unset-and-fell-back case keeps its existing informational
  message; the explicitly-set-but-hostless case is a real `LogWarning` and names
  the corrective value. Still warn-only in both cases and registration is
  unchanged: a bare listen address *is* correct for host-mode deploys, so
  refusing to start would break a supported topology.
- **`NoopEventStream`'s justification was stale.** The comment on the wiring in
  `Program.cs` read "the C# server has no Redis client", which has not been true
  since the server started self-registering: it holds a `StackExchange.Redis`
  `IConnectionMultiplexer` (`Registry/RedisServerRegistry.cs`) and writes its own
  registry entry with it. The Noop decision itself is unchanged and still
  correct (ADR-5) — what is actually missing is the *producer* side, a
  Redis-backed `IEventStream`; the gateway's relay subscribes to `events:game`
  and nothing publishes to it. Comment now states that reason. No behaviour
  change.

### Fixed
- **`package.json` and the `.csproj` now ship `.meta` files too.** `sgl-v0.1.1`
  covered the folders, sources, fixtures and the asmdef, on the reasoning that
  Unity imports neither of those two. That reasoning was wrong: Unity logs a
  console error for *every* asset without a `.meta` inside an immutable package,
  including files it does not otherwise care about, so the client console was
  permanently red with two errors on every import. Both now carry a
  `DefaultImporter` meta with the same deterministic path-derived GUID scheme.

### Fixed
- **Float intermediates now rounded explicitly — the client and server disagreed
  by one ULP.** The golden vectors, on their first run inside Unity, failed 3 of
  96 cases. All three traced to one shape: `x * x + y * y`, in
  `Vec2.SqrMagnitude` and in `MovementSystem.ResolveDirection`.

  C# permits a float expression to be evaluated at higher precision than `float`
  (ECMA-334 §11.3.7). .NET 10's RyuJIT evaluates strictly in float32; Unity's
  Editor Mono JIT keeps double-precision intermediates and rounds once at the
  end. Both are conforming. The results differ by one ULP — and since that value
  feeds the deadzone and magnitude-clamp comparisons, the two runtimes could take
  **different branches**, not merely report slightly different numbers.

  Every arithmetic intermediate in `Vec2` and `MovementSystem` is now cast to
  `float` per operation. `MovementSystem.Integrate` gets an extra split: `a + b *
  c` can be contracted into a single FMA instruction that rounds once instead of
  twice, so the multiply is now its own `float` local to deny the contraction.

  **The server's own results are unchanged** — RyuJIT already evaluated in
  float32, so the casts are a no-op there and every existing golden vector still
  passes. The fix moves Unity onto the server's answer rather than the reverse.

  ADR-10 rule 5 has been amended: choosing IEEE-exact *operations* was necessary
  but not sufficient. Worth noting how this was found — the operations were
  already legal, the whole server suite passed, and nothing warned. Only
  replaying the vectors under the other runtime exposed it.

### Fixed
- **`Shared.GameLogic` produced no assembly in Unity — it now ships its `.meta`
  files.** `sgl-v0.1.0` imported cleanly as a UPM package and then did nothing:
  Unity treats a git-sourced package as **immutable** and will not generate
  `.meta` files for it, so an asset without one is silently ignored. The package
  cache contained zero `.meta` files, `Shared.GameLogic.asmdef` was therefore
  never registered, and no `Shared.GameLogic.dll` appeared in
  `Library/ScriptAssemblies`. No error, no warning — the package simply had no
  effect.

  19 `.meta` files are now committed: one per folder, per `.cs`, per golden-vector
  `.json`, and one for the asmdef. GUIDs are derived deterministically from the
  asset path (md5), so they are stable across regeneration and identical for every
  consumer. `package.json` and the `.csproj` get none, because Unity imports
  neither.

  This was only findable by opening the Editor, which is exactly why `sgl-v0.1.0`
  was tagged with "UPM resolution unverified" recorded in the tag message rather
  than assumed.

### Added
- **`GameServer.Tests/Aot/JsonReflectionGuardTests.cs`** — scans the compiled GameServer
  assembly's metadata for `JsonSerializer` member references and fails on any overload that
  does not take a source-generated `JsonTypeInfo`/`JsonSerializerContext`. This enforces the
  precondition that makes the `Collections.Pooled` AOT warnings unreachable; without it the
  justification in `GameServer.csproj` would silently expire the first time someone added a
  reflection-based JSON call. Verified to fire.
- **Audit of the `Collections.Pooled` IL2026/IL3050 warnings** (`docs/DESIGN.md`,
  "Collections.Pooled AOT warnings") plus the justifying comment in `GameServer.csproj`,
  matching the convention every other dependency there follows. The 37 individual
  diagnostics are all in `PooledEnumerableJsonConverter`, rooted by a `[JsonConverter]`
  attribute rather than by any call site, and unreachable: this assembly never uses the
  reflection-based System.Text.Json resolver, `Arch` has zero System.Text.Json references,
  and `Collections.Pooled` registers nothing globally. **Not suppressed** — no `NoWarn` was
  added, deliberately.
- **Arch ECS is the server's entity storage (ADR-10).** `GameServer/World/EcsWorld.cs`
  stores every entity in an [Arch](https://github.com/genaray/Arch) `2.1.0-beta` world:
  entity identity, component storage, queries and iteration all belong to Arch, with no
  second store. `EntityState` is decomposed into `EntityIdRef`, `EntityKind`, `Position`,
  `Health`, `Combat`, `Locomotion`, `InputCursor` and a `PlayerTag` archetype tag
  (`GameServer/World/Components.cs`). Iteration is chunk spans, not the closure-allocating
  delegate `Query` overloads.
- **`GameServer/World/ArchAotHints.cs`** — a `[ModuleInitializer]` that statically
  constructs one `T[]` per component type. Without it the NativeAOT binary publishes
  cleanly and then throws `NotSupportedException: 'T[]' is missing native code or
  metadata` on the first archetype creation (ADR-11).
- **`GameServer.Tests/World/ArchAotHintTests.cs`** — the guard ADR-11 requires. It
  enumerates every component type in the assembly (by namespace or `[EcsComponent]`) and
  fails when one is unhinted, plus a companion test rejecting stale hints. The hinted set
  is derived from the constructed arrays themselves, so it cannot drift from what it
  checks. Verified to fire by adding an unhinted component.
- **`GAMESERVER_NATIVE_BIN`** in `backend/integration_test/dotnet_interop_test.go` — points
  the cross-language E2E suite at a published NativeAOT binary instead of the JIT'd dll.
- **CI smoke-runs the published binary** (`.github/workflows/ci-dotnet.yml`, `publish` job),
  through the real gateway handshake. ADR-11 decision 4: a clean publish does not imply a
  working binary, and the throw happens on the first player spawn rather than at startup,
  so a liveness probe would not catch it.
- **`Shared.GameLogic/` is now a valid UPM package root** — `package.json`
  (`com.rpgmmo.shared-gamelogic`, `0.1.0`, `unity: 6000.3`, **no dependencies**)
  and `Shared.GameLogic.asmdef`. Without these the client cannot consume the
  library at all: the folder does not resolve as a package, and the sources would
  land in Unity's default assembly where the "no Unity references" rule is
  unenforceable.
  - The asmdef sets **`"noEngineReferences": true`** and an empty `references`
    list, which turns ADR-10's zero-engine-dependency rule from a review
    convention into a client-side compile error. `allowUnsafeCode` is false, and
    the csproj's `AllowUnsafeBlocks` was flipped from true to false to match —
    the two build systems compiling different subsets of C# is precisely the
    class of defect this arrangement has to avoid.
  - The same folder now feeds two build systems. Verified that MSBuild's
    `Compile` items are still exactly the 11 `.cs` files, with `package.json` and
    the asmdef landing in `None`, so no exclusion is needed. In the other
    direction, Unity compiles every `.cs` under the package root — which is safe
    only because `bin/` and `obj/` are gitignored and a UPM git fetch therefore
    never sees the generated `AssemblyInfo.cs`. **Consume this package via the
    git URL, never as a local path reference into a built working tree**, or
    Unity will compile the server build's generated sources and fail on duplicate
    assembly attributes.
  - **Version discipline**: `package.json`'s `version` must be bumped in the same
    commit that gets tagged. Tags are `sgl-vX.Y.Z` (no `/`, since a slash inside
    a UPM `#fragment` is unverified). A tag pointing at a commit whose
    `package.json` still carries the previous version yields a package that
    misreports its own version, and UPM does not warn about that — the client
    silently believes it has a release it does not have.
- **Golden vectors: `Shared.GameLogic/GoldenVectors/`, 77 committed cases.**
  ADR-10 makes conformance mechanical rather than editorial: without executable
  fixtures, "shared logic" means a shared *file*, not shared *behaviour* — the
  client can drift from the server and nothing fails until a player reports
  rubber-banding. `vec2.json` (15 cases aimed at the three `MathF.Sqrt` sites),
  `movement.json` (33 cases, every `MoveResult` branch: deadzone, accepted,
  clamped, rejected, blocked, plus the magnitude-1 branch boundary, bounds
  clamping, edge sliding and `dt` capping), `combat.json` (17: damage floor,
  death transitions, attack range/cooldown boundaries) and `validation.json` (12). The expected
  values are **generated by running the implementation**
  (`GOLDEN_REGEN=1 dotnet test --filter Regenerate`), not hand computed — they
  lock in today's behaviour rather than asserting an opinion about it.
  `GameServer.Tests/Golden/` replays them; the Unity Test Runner will read the
  same files from the package path.
  - Floats are stored as IEEE-754 bit patterns (`"0x40551EB8"`) and compared
    with `BitConverter.SingleToInt32Bits`, because decimal text does not
    round-trip identically through two serializers and a tolerance comparison
    would not test the property the vectors exist to protect.
  - The schema is a flat `{"cases": [...]}` of public fields — the subset Unity's
    built-in `JsonUtility` reads, so the client needs no JSON package. A test
    enforces the shape so it cannot quietly grow a dictionary or a nested object.
  - The `Sqrt` sites get their own file because a NativeAOT-x64 / IL2CPP-ARM64
    divergence surfaces at a `Sqrt` before it surfaces anywhere else — and two of
    the three were unreachable from a behaviour vector: `Vec2.Magnitude` and
    `Vec2.Normalized` have no caller inside the library, and `Vec2.Distance` is
    used only to format the out-of-range error message, whose float the combat
    vectors deliberately truncate away. `vec2.json` pins them directly.
  - `CommittedFixturesAreUpToDate` fails the build when the fixtures drift from
    what the current code produces, so a behavioural change surfaces as a fixture
    diff in the same PR — the review signal that client prediction changed.

### Changed
- **`GameWorld` is deleted, not wrapped.** `GameServer/World/GameWorld.cs` (a
  `Dictionary<string, EntityState>` behind a `ReaderWriterLockSlim`) is gone. `EcsWorld`
  keeps its API surface — `AddEntity`, `RemoveEntity`, `GetEntity`, `GetEntitiesInRange`,
  `Update`, `View`, `PushInput`, `DrainInputs`, `PlayerStates`, `EntityCount` — with
  identical semantics, so the tick loop, input processing, snapshot construction, AOI
  scan, reconnect/hold bookkeeping and persistence save/load are unchanged behaviourally.
  `GameWorldTests` became `EcsWorldTests` with every assertion intact.
- `AsyncSaver.SaveAllAsync`'s player sweep is now an archetype query on `PlayerTag`
  instead of a full scan with a per-entity string comparison.
- `TickLoop.TickOnce` opens with an explicit `_world.ApplyStructuralChanges()` phase.
  `Arch.Buffer.CommandBuffer` is **not** used anywhere — it throws under NativeAOT even
  with the array hints (ADR-11), so structural changes raised during iteration are queued
  and applied outside it.
- `GameServer.csproj` takes a `PackageReference` on `Arch`. This transitively pulls in
  `Collections.Pooled 2.0.0-preview.27`, which emits `IL3053`/`IL2104` AOT and trim
  analysis warnings on publish — the first dependency in this project that is not
  warning-clean. The binary is verified working; the warnings are unexamined.
- **`Shared.GameLogic` now multi-targets `netstandard2.1;net10.0`.** Unity cannot
  consume a `net10.0`-only library; the netstandard target is what proves nothing
  in the library reaches past Unity's runtime profile. Nothing needed a polyfill:
  `MathF.Min/Max/Abs/Sqrt`, `HashCode.Combine`, `float.IsFinite` and `Span<T>`
  are all present in netstandard2.1.
- **All 11 files converted to block-scoped namespaces, `LangVersion` pinned to
  `9.0`.** ADR-10 has the client compile these files as *source*, so Unity 6's
  compiler — which is C# 9 — is the real constraint, not the target framework.
  File-scoped `namespace X;` is C# 10 and would have failed at package-import
  time on the client. Pinning the language version moves that failure into the
  server build, where the person making the change sees it.
- **`ImplicitUsings` disabled in `Shared.GameLogic`; every file writes its own
  usings.** The sources relied on implicit usings (`MapBounds.cs` used `MathF`
  with no `using System;`). Unity has no implicit usings, so the same reasoning
  applies: disabling it here makes a missing using a server build error instead
  of a client discovery.
- **`AoiLogic.GetNearbyEntities` fills a caller-provided `Span<EntityState>` and
  returns the count**, instead of returning a fresh `List<EntityState>`. It runs
  once per entity per tick, so the old signature allocated exactly the garbage
  the Arch migration exists to remove. Overflow contract: **count, do not
  saturate** — when the buffer is too small the prefix that fits is written and
  the return value is the total number of matches, i.e. the size the buffer
  needed to be, so one resize-and-retry always succeeds. A saturating variant
  would make "exactly full" indistinguishable from "truncated", which is silent
  AOI loss: entities missing from a keyframe with no error anywhere. Two source
  overloads (`ReadOnlySpan<EntityState>` and `IReadOnlyList<EntityState>`) cover
  contiguous and non-contiguous storage.

### Removed
- **`System.Text.Json.Serialization` attributes on `InputData` and
  `SnapshotData`.** Unity does not ship System.Text.Json, so these blocked the
  client build — and they were dead metadata: ADR-9 made the generated Protobuf
  types the server's only message classes, with legacy JSON produced by a
  hand-written `Utf8JsonWriter` codec over *those*. Verified before deleting: no
  `[JsonSerializable]` names either type, and every use in the tree
  (`InputHandler`, `GameWorld.PushInput`, the tests) constructs and reads them
  directly. No relocation, no dependency, no behaviour change. The XML docs that
  claimed "JSON tags match the wire protocol" were false since ADR-9 and now say
  these are simulation types.
### Changed
- **Docs: Unity version pinned to Unity 6.** `docs/DESIGN.md` and `docs/README.md`
  described the `Shared.GameLogic` consumer as "a Unity 2022+ project". The client
  repo is Unity 6 (6000.3.9f1), so the open-ended floor invited integration advice
  aimed at an editor nobody runs. Both now say Unity 6. No code or constraint
  changed — `Shared.GameLogic` still targets standard .NET 10 with zero Unity
  dependencies.

### Fixed
- **`METRICS_ADDR=off` killed the server at startup.** The value the Go gateway
  documents as its off-switch reached `int.Parse` and took the process down with

  ```
  Unhandled exception. System.FormatException: The input string 'off' was not in a correct format.
     at GameServer.Observability.MetricsEndpoint.ParseAddr(String)
  ```

  A config value that reads like "turn this off" must not be a way to stop a game
  server from booting.
  - `off`, `none` and `disabled` (any case, surrounding whitespace ignored) now
    disable the endpoint, matching the gateway's `resolveMetricsAddr` vocabulary
    so one `METRICS_ADDR` means the same thing to both binaries.
  - An address that parses as none of those disables the endpoint and logs an
    **error**, rather than throwing. That matches how a failed *bind* is already
    handled a few lines further down: a mistyped metrics address costs metrics,
    not the game server. Logged loudly so the typo stays visible.
  - Found while building a probe container for the Kerberos fix below, not by a
    report — nothing in the deployed configs sets `off` today.
- **Every boot logged a Kerberos library error it could never use.** The server
  printed, outside the logger and immediately before `using postgres player store`:

  ```
  Cannot load library libgssapi_krb5.so.2
  Error: Error loading shared library libgssapi_krb5.so.2: No such file or directory
  ```

  Npgsql 10 defaults `GSS Encryption Mode` to `Prefer`, so every connect opens by
  attempting a Kerberos handshake. The runtime image is `runtime-deps:10.0-alpine`
  with no krb5 library, and the game DB authenticates with a password, so the
  attempt could only ever fail and fall back — after writing a genuine `[error]`
  line into the log summary of an otherwise healthy server.
  - `PostgresPlayerStore.BuildConnectionString` now sets `GSS Encryption Mode` to
    `Disable`, rather than shipping a Kerberos stack to satisfy a probe for a
    feature nobody uses.
  - **Only `Prefer` is rewritten.** `Require` and `Disable` are deliberate operator
    choices and pass through untouched — a `Require` on a deployment that does have
    Kerberos must still fail loudly instead of being quietly downgraded.
  - The "did the caller set this?" check is a value comparison, not
    `builder.ContainsKey`: Npgsql's connection-string builder answers `true` for
    every keyword it knows, set or not, which makes `ContainsKey` useless here.
  - Verified A/B on the real image against the deployed Postgres: the previously
    deployed image emits the two lines, an image built from this commit emits none,
    with both reaching `using postgres player store` and a live listener.
- **Shutdown could live-lock a thread and hold the process open forever.**
  `Connection.Close()` cancels the connection's `CancellationTokenSource`, and
  cancellation resumes everything parked on that token **inline, on the cancelling
  thread**. Since the heartbeat loop landed (`817c6ac`) that produced this stack:

  ```
  ShutdownAsync -> ConnectionManager.CloseAll -> Connection.Close -> _cts.Cancel()
    -> HeartbeatLoopAsync resumes inline
      -> HandleConnectionAsync runs its finally
        -> Connection.Dispose() -> spin until _closeState == StateClosed
  ```

  The `Close()` that writes `StateClosed` is a frame *further down that same
  stack*, so `Dispose()`'s spin blocked the very call it was waiting for. A
  thread burning a core forever, and a process that never exits.
  - `Close()` now records the managed thread id performing the teardown.
    `Dispose()` recognises a re-entry on that thread, defers the token-source
    disposal back to the in-flight `Close()` and returns instead of spinning.
    `Close()` frees it in its `finally`, once every use of `_cts` has returned.
  - Cross-thread behaviour is unchanged: a `Dispose()` on a *different* thread
    still spins until the close completes, which is what stops it freeing the
    token source out from under a concurrent `Close()`.
  - **How it showed up**: not as a failing test. Every test passed — the .NET
    test host then refused to exit, and CI ran to its 6-hour job timeout three
    runs in a row. `--blame-hang` named the last test to run, which was a
    bystander. The hang dump named the real thing.
  - Verified by the suite now exiting: two consecutive full runs finish in ~45s
    (409 passed, 9 skipped) where the same suite previously hung indefinitely
    after the last test completed.

### Changed
- **Test process helpers now honour the timeouts they declare.** `TestDocker`,
  `EphemeralPostgres` and `KcpInteropTests` each ran a child process with
  `StandardOutput.ReadToEnd()` *before* `WaitForExit(timeout)`. That read only
  returns when the child closes the pipe, so the timeout below it was unreachable
  — a child that never exits parked the caller forever, and a child that filled
  the 64 KiB stderr buffer deadlocked against a caller blocked on stdout. Both
  pipes are now read concurrently and the declared timeout is actually enforced.
  (Side effect worth knowing: the Postgres fixture works on more machines now, so
  local runs report ~9 skipped instead of ~28.)

### Added
- **Graceful drain notification on shutdown.** On SIGTERM, the server now sends
  `MsgDisconnect(reason="server_shutdown")` to all connected clients before
  closing connections. A 2s grace period lets TCP drain the send buffer so
  clients receive the notification and can reconnect to another server instead
  of timing out. Adds `WireProtocol.NewEnvelope` for `DisconnectMessage` (with
  reason) and the corresponding JSON serializer in `WireJson`
- **JTI replay protection.** `JtiTracker` rejects consumed join-token JTIs for
  60 seconds (2x the 30s token TTL). A replayed token returns "Token already used".
- **5-second clock skew tolerance.** `JwtValidator` now accepts tokens up to 5
  seconds past their `exp`. Constant: `JwtValidator.ClockSkewSeconds`.

### Changed
- **`JOIN_TOKEN_SECRET` is now mandatory (fatal if unset).** `Program.cs` exits
  with code 2 when the env var is empty. `EffectiveJoinTokenSecret` (fallback to
  `JWT_SECRET`) has been removed from `ServerOptions`.
- **Mandatory server ID check.** The game server now rejects join tokens with an
  empty `sid` claim or a `sid` that does not match `ServerId`. The previous
  double-empty bypass has been removed.
- **Map transfer handler (MsgTransferMap).** A connected player can request
  transfer to a different map. The server validates the target map, saves state
  via `AsyncSaver.SavePlayerAsync`, responds with `TransferMapResponse`, removes
  the entity (no reconnect hold), and closes the connection. The client then
  follows the existing `MsgEnterWorld` flow with the gateway.
- JSON codec for `TransferMapRequest` / `TransferMapResponse` (write + read),
  `NewEnvelope` overloads, and `GetPayload` cases for both types.
- xUnit tests: `TransferMapTests` (3 cases: success, same-map rejection,
  empty-map rejection) and `WireProtocolTests` round-trip tests for transfer
  messages.
- **Heartbeat loop (MsgPing/MsgPong) on player connections.** Each accepted
  connection sends MsgPing every 10 s after join. If no MsgPong is received
  within 30 s the connection is closed. Incoming MsgPing from a client is
  answered with a MsgPong echoing the sender's timestamp plus the server's
  wall clock. Heartbeat runs as a third task alongside read/write loops.

- **MsgKick support.** `WireProtocol.NewEnvelope` overload for `KickMessage`;
  `JsonWriter.Write(KickMessage)` and `JsonReader.ReadKickMessage` for JSON
  encoding; Protobuf encoding via generated `Wire.cs`.

### Fixed
- **`ObjectDisposedException` out of `ShutdownAsync` when `Close()` raced
  `Dispose()`.** Under Agones that means terminate throws instead of draining.

  `Close()` guarded itself with a single flag and `Dispose()` used that guard as
  a barrier. It is not one: the early return means "another thread STARTED
  closing", never "another thread FINISHED closing". So `Dispose()` could free
  the `CancellationTokenSource` while the other thread sat between its CAS and
  its `Cancel()`.

  Replaced with a three-state lifecycle (open → closing → closed); `Dispose()`
  waits for *closed* before disposing the CTS. A spin rather than a lock because
  `CancellationTokenSource.Cancel` runs registered callbacks inline and a lock
  across arbitrary callback code invites a deadlock. The state transition to
  *closed* is in a `finally`, so a throwing callback cannot strand it and spin
  `Dispose()` forever.

  **`KcpSession` had the identical shape and was also broken** — verified by
  reproducing the same exception against the unfixed file. Fixed at the same
  time rather than waiting for a KCP deployment to find it.

  This is the third appearance of one pattern: the 2026-08-06 blocker
  (`GameServerHost._cts?.Cancel()`, where `?.` guarded null but not disposed) was
  fixed at the one call site that threw, and the pattern was not swept. All six
  `CancellationTokenSource` sites in the module have now been audited — see the
  PR for the per-site verdict.

### Changed
- `MetricsEndpoint.DisposeAsync` is now idempotent. Single-owner today, so this
  is hardening rather than a fix, but an unguarded Cancel-then-Dispose is the
  exact shape that has now thrown twice.

### Added
- **`gameserver_resyncs_total`** (counter, `map_id`) — keyframes requested by a
  client via `MsgResync`. Expected value is approximately zero.

  This is the only field-visible signal that entity-id interning has gone wrong.
  A client sends `MsgResync` only when it cannot reconstruct state from the delta
  stream, and the likeliest cause is a snapshot referencing an entity handle it
  has no binding for — the two ends disagreeing about the interning table.
  Interning is backward compatible by construction and by test, but had no way to
  be observed failing in production; now it does.

  Counts client-initiated resyncs **only**, never the periodic keyframe. Folding
  the routine one in would bury the signal under a constant background rate.
  `docs/METRICS.md` says what a rising rate means and what it invalidates.

  No gateway-side equivalent exists, deliberately: `MsgResync` goes client to
  game server directly (ADR-3), so a gateway counter would always read zero —
  worse than absent, because a permanently-zero series looks healthy.

### Added
- **Entity-id interning**, gated on the connection's encoding. `SnapshotDeltaState`
  keeps a per-connection handle table reset at every keyframe, writes the id only
  on the message that introduces a handle, and never reuses a handle within an
  interval — reuse would let a client that missed a despawn attribute an update
  to the wrong entity, which is wrong state rather than absent state.

  `Encode` takes `intern:`; `TickLoop` passes `conn.Encoding == WireEncoding.Proto`.
  JSON has no handle field, so interning there would emit entities with an empty
  id and silently break every pre-interning client.

### Added
- **`GameServer/Net/EntityTypes.cs`** — maps the simulation's string entity types
  to the wire enum and back, the C# mirror of Go's `entityTypeToPB`. Unrecognised
  names travel in `EntitySnapshot.TypeName` instead of being dropped, so a new
  entity kind cannot silently break an older client.

### Changed
- The snapshot encoders set the entity type through `EntityTypes.SetType`, which
  writes the enum when the name is known (2 bytes) and the string only when it is
  not. The JSON codec still emits and parses the string form, so the legacy wire
  is byte-identical.

### Changed
- **The generated `wire.proto` types are now the server's only message classes.**
  The hand-written C# mirrors of the Go structs are deleted, which removes exactly
  the two-definitions drift `wire.proto` exists to prevent. They are imported as
  explicit global `Using` aliases so `RpgMmo.Wire.V1.Envelope` (the protobuf
  message) never collides with `GameServer.Net.Envelope` (the framing envelope
  that carries the encoding metadata).
- `Connection` latches the encoding of the client's first frame and every reply
  uses it, so a single binary serves JSON and Protobuf clients side by side and
  the server never chooses an encoding of its own.
- The JSON path no longer round-trips its own freshly serialized payload through
  `JsonDocument.Parse` just to nest it inside the envelope — pure waste that sat
  on the per-tick snapshot path.
- `SnapshotMessage.Removed` is a protobuf `RepeatedField`, so it is now empty
  rather than `null` when there are no removals. The JSON on the wire is
  unchanged: the field is still omitted when empty.

### Added
- Legacy JSON stays fully supported through a hand-written
  `Utf8JsonWriter`/`Utf8JsonReader` codec (`GameServer/Net/WireJson.cs`) that
  reproduces Go's `omitempty` rules byte for byte. Protobuf's own `JsonFormatter`
  was evaluated and rejected: it emits camelCase and drives descriptor
  reflection, so it matches neither this wire format nor NativeAOT.
- `Google.Protobuf` 3.29.3. `dotnet publish -c Release` with `PublishAot`
  succeeds with **zero trim/AOT warnings** — the generated serializers are used,
  not the reflection-based ones.

### Fixed
- **`DecodeBody` half-parsed garbage as a typeless envelope.** A body beginning
  `0x12` is valid Protobuf (field 2, length-delimited) and parsed cleanly with
  the type left at 0, so arbitrary bytes became a well-formed envelope with no
  error. Type 0 is now rejected on decode and at construction, and both decoders
  fail closed. Pinned by a 1..255 sweep of the prefix invariant rather than by a
  comment.
- **Replies fell back to legacy JSON after the join handshake.** The handshake
  runs on a throwaway `Connection` and the session `Connection` was then
  constructed fresh over the same socket, dropping the encoding the client had
  already demonstrated. The handshake now hands its latched encoding to the
  session connection. Caught by the new mixed-encoding integration test.
- **Entities leaked when a join was aborted.** `gameserver_players_online` returned to
  0 while `gameserver_entities` stayed at its peak indefinitely — 200 entities with 0
  players, still there minutes later, reproduced below.

  The hold mechanism was not at fault and the gauge was not lying. `AddEntity` has
  exactly one call site (the join path) and `RemoveEntity` only ever runs from the
  reconnect-hold task, so an entity whose hold is never *scheduled* is unreachable
  forever. `OnPlayerDisconnected` was called on the happy path only, at the end of the
  `try`. Any throw after the entity was attached — most easily the `WriteOneAsync` that
  sends `JoinTokenResp`, against a client that gave up during the handshake — skipped
  it entirely.

  The asymmetry in the symptom is what identified the path: `players_online` is an
  independent counter incremented *after* that write, so an abort before it leaves the
  player count correct and only the entity count wrong. That is exactly what was
  observed.

  Teardown now runs from a `finally` block, guarded by whether the entity was actually
  attached. A second flag tracks whether `PlayerJoined()` was recorded, so an aborted
  join cannot decrement a counter it never incremented and corrupt the count for
  players who really are online.

  Also in the same path, all reachable from an aborted or racing join:
  - A superseded hold's `CancellationTokenSource` was neither cancelled nor disposed,
    leaving a live timer for a removal the newer hold already owns.
  - The expiry task claimed its removal non-atomically. It now uses
    `TryRemove(KeyValuePair)`, which only succeeds while *its* hold is still registered,
    and additionally refuses to remove an entity that has a live connection — a
    reconnect during the pre-removal save must not have its entity deleted underneath
    it.
  - An unexpected exception in the expiry task was swallowed, silently leaking the
    entity it was responsible for. It now logs and removes anyway.
  - `holdCts` is disposed on every path.

  `GameServerHost.EntityCount` / `PendingHolds` are exposed so tests assert the number
  an operator sees rather than a parallel count that could agree while the gauge lies.

- **Keyframe stampede: per-connection keyframe counters are now staggered.** Every
  connection started its counter at zero, so clients that joined on the same tick
  keyframed on the same tick afterwards, forever, serializing full state for the whole
  cohort at once.

  `SnapshotDeltaState` takes a phase derived from the user id (FNV-1a, not
  `string.GetHashCode()`, which is randomized per process). Deterministic on purpose:
  a random offset would spread load equally well but make a replay of the same session
  produce different frames — the same reasoning that puts cooldowns on tick counts
  rather than wall clock.

  The phase is applied **once**, right after the join keyframe, shortening a single
  cycle. A permanent offset would shorten this client's cycle forever and hand it more
  keyframes, and more bandwidth, than everyone else. The parameterless constructor is
  unstaggered, so existing callers are unaffected.

  Note: no end-to-end latency improvement is claimed — see the PR. The dev box's
  run-to-run variance swamps the effect; the unit tests prove the keyframes are spread.

### Added
- **KCP transport for the gameplay hop (`--transport kcp` / `GAMESERVER_TRANSPORT`).**
  Until now this flag only selected what got *advertised*: the C# server had no KCP
  and always bound TCP, so a "KCP deployment" was half a deployment — the Go side
  shipped KCP for the client→gateway hop while the gameplay hop stayed TCP. The
  server now really listens with KCP over UDP, wire-compatible with
  `backend/shared/transport` (`github.com/xtaci/kcp-go/v5`).
  - `GameServer/Net/Transport/` — a port of kcp-go's protocol subset: the ARQ
    (`Kcp.cs`), kcp-go's crypt framing (`KcpCrypto.cs`), the UDP listener with
    per-endpoint session demultiplexing (`KcpListener.cs`, `KcpSession.cs`), and a
    `Stream` adapter (`KcpStream.cs`) so the length-prefixed JSON codec rides on
    top unchanged. kcp2k (Mirror's C# KCP) was evaluated and rejected: its
    handshake/cookie layer is not on the wire kcp-go speaks. Rationale and the
    interop evidence: `docs/DESIGN.md`, 2026-08-07.
  - Tuning matches the Go constants exactly (nodelay 1, interval 10ms, resend 2,
    congestion control off, 128/128 windows, MTU 1350, stream mode, FEC off).
  - `Connection` and `GameServerHost` now take an `ITransportConnection` /
    `ITransportListener` instead of `TcpClient` / `TcpListener`. TCP remains the
    default and its behaviour is unchanged; the `Connection(string, TcpClient,
    ILogger)` constructor is kept.
  - `interop/kcpprobe` — a Go client harness that dials through
    `backend/shared/transport` and completes a real join, so interoperability is
    asserted against the actual kcp-go implementation rather than a C#-to-C#
    loopback. Interop tests skip when no Go toolchain is present.

### Fixed
- **Cross-map position bleed on join.** `player_states` holds one row per player,
  overwritten by whichever server hosts them, and the join path restored its
  `x`/`y` unconditionally. A player who last stood at (480, 12) on `map_02` and
  then joined `map_01` was recreated at (480, 12) *on `map_01`* — a different
  place entirely. The row never converged either: each join wrote back the stale
  base plus whatever they walked, so the drift compounded.

  Placement now goes through `PlayerSpawn.Resolve`, which reuses saved
  coordinates only when the row's `map_id` matches the map being joined and
  otherwise places the player at that map's spawn point. HP and max HP carry
  across unchanged — they belong to the character, not to the ground under it.
  An empty `map_id` counts as a mismatch rather than a wildcard, because the
  column defaults to `''` and such a row has unknown provenance. The row
  converges on the next save with no extra write, since `AsyncSaver` already
  uses the hosting server's own `MapId`.

  Rationale and the full decision table: `docs/DESIGN.md` — "Position is
  map-scoped; carried stats are not". Policy is a pure function rather than
  inline join-handler code, so every branch is testable without a database, a
  socket or a running server.

  Covered by `PlayerSpawnTests` (policy) and `MapIdReloadIntegrationTests`
  (real PostgreSQL + real TCP join handshake, `[SkippableFact]` per the
  dependency-gating convention). Three of the four integration tests fail
  against the pre-fix join path — `Expected: 0 … Actual: 137.5` — so they
  pin the regression rather than decorating it.

  Not fixed here: `player_states` has no `dead` column, so a player persisted
  at `hp = 0` reloads with `Hp = 0` and `Dead = false`. That needs respawn rules
  and a schema change; this change preserves the existing HP behaviour exactly.
- The realtime transport published into the registry is now what the server
  actually listens with, so `EnterWorldResponse.Transport` tells clients the truth.
  It was previously whatever `--transport` said, regardless of the TCP listener
  underneath — a client that honoured the field would have dialled KCP at a TCP
  socket and simply hung.
- **The last three soft skips in the test suite now report as real skips.** Tests
  gated on an external dependency used to `Console.WriteLine("[SKIP] ...")` and
  `return`, which xUnit records as **PASSED** — so a run without the dependency
  reported the same totals as a full run and absence of coverage was
  indistinguishable from coverage. The postgres/redis fixtures were converted to
  `Skip.IfNot` earlier; these three were missed:
  - `MigratorTests.EmbeddedMigrations_MatchDeployCopies` and
    `MigratorTests.InitGamestateSql_MatchesFirstMigration` — gated on the deploy
    SQL being reachable in the repo tree; now `[SkippableFact]` + `Skip.If`.
  - `PostgresPlayerStoreTests.Save_AfterDatabaseGoesAway_SurfacesErrorAndIncrementsMetric`
    — its *dedicated* throwaway container (the one it kills mid-test) could fail to
    start after the shared-fixture gate had already passed, silently voiding the
    test; now `Skip.If`.
  No assertion was weakened and nothing skips unconditionally. Verified both
  directions: docker up → `Passed: 287, Skipped: 0`; docker off `PATH` →
  `Passed: 261, Skipped: 26`.
- Test convention documented in `CLAUDE.md` (§ Testing) so the soft-skip pattern is
  not reintroduced: dependency-gated tests must skip, never silently pass.

### Security
- **KCP traffic can be encrypted with the same pre-shared key as the Go side
  (`TRANSPORT_KEY`).** AES-256 is applied per datagram below the ARQ, so the join
  token and every snapshot are covered. Key derivation matches
  `shared/transport/crypto.go`: 64 hex characters verbatim, anything else stretched
  with HKDF-SHA256 under the info string `rpg-mmo/transport/kcp/aes-256` — asserted
  against the real Go implementation, because a silent derivation drift would look
  exactly like a network fault.
  - There is no negotiation and no downgrade: a peer without the key produces
    datagrams that fail the checksum and are dropped, so "encrypted server +
    plaintext client" fails closed rather than falling back to cleartext.
  - A KCP listener with no key logs a start-up WARNING mirroring the Go wording.
    `TRANSPORT_KEY` set with `--transport tcp` is ignored and warned about — TCP has
    no packet encryption here.
  - Scope, and what is still *not* covered end to end (the client↔gateway hop is a
    separate setting; a PSK gives no forward secrecy and no protection from a peer
    that holds the key): `docs/DESIGN.md`, 2026-08-07.

- **Join tokens are verified with `JOIN_TOKEN_SECRET`, not `JWT_SECRET`.** The join
  secret is distributed to every game-server pod; the Nakama auth secret is not.
  Sharing them meant one compromised pod could mint auth tokens for any user. This
  is the C# half of the split already merged on the Go side — until now, enabling
  `JOIN_TOKEN_SECRET` on the gateway alone would have broken **every** join, because
  this server only knew `JWT_SECRET`.
  - New config: `--join-token-secret` / `JOIN_TOKEN_SECRET`. Unset falls back to
    `JWT_SECRET` (pre-split behaviour) and logs the same start-up warning the
    gateway logs, so the two halves cannot silently drift. The fallback lives in
    `ServerOptions.EffectiveJoinTokenSecret`, mirroring Go's
    `config.Config.EffectiveJoinTokenSecret`.
  - `JwtKeyring` (`GameServer/Server/JwtKeyring.cs`) — secret rotation. Both
    secrets accept a comma-separated `"current,previous"` list: the gateway signs
    with the first entry, every entry verifies here, so a rotation drains the old
    population over the join-token TTL instead of logging everyone out. Port of
    Go's `shared/jwt.Keyring`, including whitespace trimming, dropping empty
    entries, failing **closed** on an empty keyring, and short-circuiting on an
    expired token instead of retrying the remaining keys.
  - `JwtValidator.Verify` gained a `VerifyStatus` overload (Ok / Invalid /
    BadSignature / Expired) so the keyring can tell "wrong key, try the next" from
    "right key, dead token" — the distinction the Go short-circuit depends on. The
    existing two-argument overload is unchanged.
  - Verified against the real Go gateway on high ports: matching secrets → join
    accepted; deliberately mismatched secrets → join rejected; gateway signing with
    the rotated key against a `"previous,current"` keyring → join accepted.

### Added
- **Server self-registration and heartbeat (`GameServer/Registry/`).** The server now
  publishes its own entry into the Redis registry the Go gateway reads, refreshes it
  every 5s against a 15s TTL, updates `player_count` on join/leave, and deregisters on
  graceful shutdown. Wire-compatible with `shared/storage/redisstore/registry.go` —
  same keys (`servers:id:{id}` hash + `servers:map:{map}` set index), same field
  names, same `constants.ServerHeartbeatTTL`. **No gateway change was needed**;
  verified end to end with the real smoke test.
  - `RedisServerRegistry` — StackExchange.Redis implementation. `UpdatePlayerCount`
    uses the same Lua `EXISTS`-guard as the Go side, so a late writer cannot
    resurrect an expired entry as a TTL-less immortal one.
  - `RegistrationService` — **every heartbeat is also a repair.** When the entry is
    missing (Redis wiped, failover onto an empty replica, TTL lapsed during an
    outage) the next heartbeat re-registers it rather than just logging. That is what
    makes a Redis outage self-heal in one heartbeat interval instead of requiring a
    human to run a script. Registry failures never touch gameplay: every call is
    wrapped and retried, and the connection uses `AbortOnConnectFail=false` so the
    server boots and keeps serving even with Redis down.
  - New config: `--redis`/`REDIS_ADDR`, `--redis-password`/`REDIS_PASSWORD`,
    `--transport`/`GAMESERVER_TRANSPORT`, and `--public-addr`/`GAMESERVER_PUBLIC_ADDR`
    — the address advertised to CLIENTS, which is **not** the listen address when a
    container maps ports (listens `:9000`, published `:9200`). Falls back to the
    listen address, which is correct in host mode.
  - Replaces `scripts/register-gameserver.sh` (deleted), which wrote the entry once at
    deploy time with a 3600s TTL and nothing to refresh it. Closes G1 and G2 in
    `backend/deploy/docs/DISASTER-RECOVERY.md`.
  - `StackExchange.Redis` 3.1.11 added. NativeAOT publish verified clean (zero IL trim
    warnings) and the published binary exercised against a real Redis.
- Registry test suite against a **real Redis** in a throwaway container
  (`GameServer.Tests/Registry/`): exact hash/index shape the gateway reads, TTL
  re-arming, real expiry, deregistration, the player-count resurrection guard, and
  two self-healing tests — a wiped key repaired by the next heartbeat, and a full
  container stop/start proving the service survives an outage and re-registers.
- `GameServer.Tests/Infrastructure/TestDocker.cs` — docker plumbing shared by the
  postgres and redis fixtures instead of duplicated.
- `GameServer.Tests/Observability/MetricsEndpointTests.cs` — starts the real endpoint
  and scrapes it over HTTP: wildcard (`:port`, `0.0.0.0:port`, `*:port`) and named
  (`localhost:port`) binds both serve `/healthz` and `/metrics`, empty address
  disables, plus a `ParseAddr` normalization table. The three wildcard cases fail with
  `Assert.NotNull() Failure: Value is null` against the unfixed code. The wildcard
  cases scrape whichever authority actually got bound: on Windows the `+` prefix needs
  an admin URL ACL, so `TryStart` falls back to `localhost` and `HttpListener` answers
  `400` to a `127.0.0.1` Host header that matches no registered prefix. On Linux — CI
  and the production target — the test additionally asserts the bind really is `+`, so
  the fallback can never quietly become the normal path there.
- `InternalsVisibleTo` for `GameServer.Tests` so tests can assert on internal helpers
  without widening the public API.
- **Delta snapshots.** Each connection now receives a full keyframe on join, on
  `MsgResync` (type 10) request, and every `--keyframe-interval` snapshots (default
  30 ≈ 2s at 15Hz); every other snapshot carries only entities whose visible state
  changed plus an explicit `removed[]` despawn list. Measured on 1 moving player +
  8 stationary mobs over 100 ticks: **592.2 → 126.6 bytes/tick/client (−78.6%)**.
  New `SnapshotDeltaState` (per `Connection`) holds the last-sent state; its scratch
  collections are reused across ticks and the entity list is allocated lazily, so an
  unchanged tick allocates only the message itself. `--keyframe-interval 0` disables
  delta encoding entirely (full snapshot every tick, the pre-delta wire shape).
- **Input acknowledgement on the wire.** `SnapshotMessage.ack_tick` carries the
  newest input tick accepted for the receiving player's own entity — the anchor a
  predicting client rewinds to. `EntityState.LastInputTick` was already tracked but
  never serialized, which made client-side reconciliation impossible.
- `Shared.GameLogic.Systems.SnapshotMerger` — the normative client-side merge of the
  keyframe/delta stream, shared with the Unity client (Go mirror:
  `messages.SnapshotState`).
- `MsgType.Resync` (10) — client asks for a full keyframe on the next tick.
- `--keyframe-interval` / `GAMESERVER_KEYFRAME_INTERVAL`.
- `docs/API.md` — precise wire reference for the Unity client: framing, every
  message, the delta/keyframe semantics, the normative merge algorithm and the
  reconciliation procedure.

### Changed
- **Docker-dependent tests now report a REAL xUnit skip instead of passing silently.**
  `PostgresFixture.SkipIfUnavailable` returned early and xUnit recorded the test as
  PASSED, so a machine without docker reported exactly the same totals as a full run —
  absence of coverage was indistinguishable from coverage, and per-test duration was
  the only honest signal. `SkipUnlessAvailable` now uses `Skip.IfNot`
  (`Xunit.SkippableFact`), and the affected tests are `[SkippableFact]`/
  `[SkippableTheory]`. With docker: `250 passed, 0 skipped`. Without:
  `224 passed, 26 skipped`. The summary can no longer lie.
- **Combat cooldowns are now tick-based, not wall-clock.** `EntityState.CooldownUntilTicks`
  (a `DateTime.Ticks` value) became `CooldownUntilTick` (a `ulong` simulation tick);
  `CombatLogic.ValidateAttack` and `ValidationLogic.ValidateInput` take the current
  tick instead of `nowTicks`. Length comes from `GameConstants.AttackCooldownTicks(tickRate)`
  = `ceil(500ms × tickRate / 1000)` = 8 ticks (533ms) at 15Hz — rounded up so the
  cooldown is never shorter than the wall-clock one it replaced. The simulation now
  has a single clock, so replaying an input sequence always yields the same outcome;
  a wall-clock gate could not guarantee that, which blocked both client prediction
  and server-side replay of disputed sequences.
  **Breaking for in-flight callers:** `InputHandler.ProcessInput`/`ProcessInputLocked`
  take a `currentTick` parameter before `applyMovement`.
- `SnapshotData` (Unity-facing mirror) gained `ack_tick`, `full` and `removed`.

### Fixed
- **SIGTERM never shut the server down gracefully.** Termination was wired through
  `AppDomain.CurrentDomain.ProcessExit`, which cancels the token but does **not** wait
  for `Main` to unwind — the runtime terminates the process while shutdown is still in
  flight. So on SIGTERM the final save never ran (losing up to `SaveInterval` = 30s of
  player position/HP) and connections were never drained. Only SIGINT (Ctrl-C, via
  `Console.CancelKeyPress`) shut down properly — the one signal production never
  sends, since Docker, Kubernetes and an Agones drain all send SIGTERM. Both signals
  now go through `PosixSignalRegistration` with `Cancel = true`, which suppresses the
  runtime's terminate-now behaviour so shutdown actually completes. Found while
  verifying registry deregistration, which was silently not happening for the same
  reason.
- **The metrics/health endpoint never started on Linux with a wildcard address.**
  `METRICS_ADDR=:9101` (the default, and the deployed value) becomes the HttpListener
  prefix `http://+:9101/`. OpenTelemetry builds its own prefix as
  `new UriBuilder("http", Host, Port).Uri`, and `UriBuilder` rejects `+`/`*` with
  `UriFormatException: Invalid URI: The hostname could not be parsed`, thrown inside
  the `PrometheusHttpListener` constructor — so `/metrics` **and** `/healthz` silently
  failed to bind on every Linux deployment. Windows masked it by falling back to
  `localhost`. The exporter is now given a `UriBuilder`-safe placeholder host, and the
  real wildcard prefix is installed on the listener through `ConfigureHttpListener`,
  which runs before `Start()`. `backend/deploy` can now drop its
  `GAMESERVER_METRICS_ADDR=gameserver-dotnet:9101` workaround and go back to `:9101`
  (owner: agent-devops).
  Found by the E2E integration suite the first time it was actually executed.
- **`GameServerHost.ShutdownAsync` is now idempotent and concurrency-safe.** It is
  called from two places on essentially every termination: `RunAsync` invokes it at
  its tail when the run token is cancelled, and the process owner (SIGTERM handler,
  Agones drain, a test harness) invokes it directly. Both racers walked the entity
  hold table with `foreach (var kvp in _holds)`, so one could call `Cancel()` on a
  `CancellationTokenSource` the other had already `Dispose()`d — a pod that should
  have drained cleanly threw `ObjectDisposedException` out of `RunAsync` instead.
  This surfaced as an intermittent `PlayerPosition_SurvivesServerRestart` failure
  (~2 runs in 3) but the defect was in the server, not the test. The first caller now
  wins an `Interlocked.Exchange` and performs the teardown; every other caller awaits
  that same teardown and observes the same outcome, so "shutdown returned" always
  means "the final save finished". Holds are drained by `TryRemove` so each CTS has
  exactly one owner (the reconnect path races for the same entries), and the linked
  `CancellationTokenSource` is disposed only in `DisposeAsync`, once the run loop is
  guaranteed done with it.
- Player state is now persisted when a reconnect hold expires, before the entity is
  removed from the world. Previously `OnPlayerDisconnected` removed the entity
  without saving, so once it left the world the periodic `AsyncSaver` sweep could no
  longer see it and everything the player did since the last 30s tick was discarded.
  New `AsyncSaver.SavePlayerAsync(userId)` saves a single entity by id.
  See `backend/docs/ARCHITECTURE-DECISIONS.md`, ADR-6.
- Removed a dead conditional that selected `NoopAgonesSdk` in **both** branches of
  the `--agones` / `AGONES_ENABLED` flag. The flag never had any effect; it now
  logs a warning saying so, instead of implying that Agones health reporting works.
  No real Agones SDK client exists for the C# server yet (ADR-6 follow-up).

### Added
- `GameServer.Persistence.Migrator` — numbered, checksummed, transactional schema
  migrations. Scripts live in `GameServer/Persistence/Migrations/NNN_*.sql` and are
  embedded as assembly resources (read via `GetManifestResourceStream`, which is
  NativeAOT-safe), so the binary carries its own schema history.
  - Each pending script commits in its own transaction together with its
    `schema_migrations` row, so a failing migration leaves no partial schema and
    no version record and can simply be fixed and re-run.
  - Checksums of already-applied migrations are verified on every run; editing a
    shipped migration fails loudly with `MigrationDriftException`. Checksums cover
    statements, not comments, so rewording a comment is safe.
  - Concurrent runners are serialised by a PostgreSQL advisory lock — a whole
    fleet can boot at once. A database ahead of the binary warns instead of
    failing, so rollbacks still start.
- `--migrate-only` / `GAMESERVER_MIGRATE_ONLY=true` — apply pending migrations and
  exit without listening (exit 0 applied/current, 1 failure, 2 no DSN). CD uses it
  to migrate at a deterministic point before restarting servers.
- `001_init.sql` — the existing `player_states` schema as the first migration.
  Ops copies live in `backend/deploy/db/migrations/gamestate/`; tests assert the
  embedded scripts, the ops copies and `db/init-gamestate.sql` all agree.

### Changed
- `PostgresPlayerStore.MigrateAsync` now runs the migration set instead of a single
  hardcoded `CREATE TABLE IF NOT EXISTS` block, and returns a `MigrationResult`.
  The `SchemaSql` constant is gone — schema lives in migration files now.

- `GameServer.Persistence.PostgresPlayerStore` — PostgreSQL-backed `IPlayerStore`
  restoring the player-state persistence that was lost in the Go -> C# migration
  (ported from the now-orphaned Go `shared/storage/pgstore`). Saves are upserts on
  `user_id` refreshing `updated_at`; loading a missing player returns `null`,
  matching `MemoryPlayerStore`. Pooling via `NpgsqlDataSource`, explicit timeout on
  every command, and idempotent schema migration on boot mirroring
  `backend/deploy/db/init-gamestate.sql` (a test asserts the two stay in sync)
- `--game-db-url` / `GAME_DB_URL` selects the postgres store; unset keeps the
  in-memory store. The active store is logged at startup and DSN passwords are
  masked in every log line. A configured-but-unreachable database is fatal at boot
  (exit 1) rather than a silent degrade to memory
- `Npgsql` 10.0.3 dependency — used through raw commands and explicitly typed
  parameters only, keeping the NativeAOT publish reflection-free
- Persistence tests run against a real PostgreSQL in an ephemeral
  `postgres:16.4-alpine` container on a random free port, and skip cleanly when
  docker is unavailable. Coverage: save/load roundtrip, upsert overwrite,
  missing-load semantics, delete, repeated migration, unreachable-database
  failure, save-after-database-loss surfacing an error and incrementing
  `gameserver_player_saves_total{status="error"}`, DSN parsing/masking, and an
  end-to-end join -> move -> disconnect -> reload-after-restart flow
- `Shared.GameLogic.Systems.MovementSystem` — server-authoritative, deterministic,
  allocation-free movement model shared with the Unity client for prediction:
  `ResolveDirection` (normalize/clamp/reject a raw input vector), `Integrate`
  (`position += direction * speed * dt`, bounds-clamped), `TryMove`,
  `DeltaTimeForTickRate`, `MaxDisplacementPerTick`, `IsDisplacementLegal`
- `MoveResult` enum (`None` / `Accepted` / `Clamped` / `Rejected` / `Blocked`) —
  validation results are returned by value, never thrown
- `Shared.GameLogic.Components.MapBounds` — axis-aligned play area with per-axis
  clamping (`FromSize`, `Default`, `Contains`, `Clamp`). Default 1000x1000 world
  units centered on the origin; configurable via `--map-width` / `--map-height`
  (`GAMESERVER_MAP_WIDTH` / `GAMESERVER_MAP_HEIGHT`) and `ServerOptions.MapBounds`.
  Positions restored from the player store are clamped on join
- `GameConstants`: `MaxInputMagnitude`, `InputDeadzoneSq`, `MaxDeltaTime`,
  `DisplacementTolerance`, `DefaultMapWidth`, `DefaultMapHeight`
- Movement tests: direction matrix, diagonal-vs-cardinal parity, dt scaling across
  tick rates, bounds clamping at all four edges + corners + edge sliding, validator
  accept/clamp/reject/block matrix, determinism, tick-loop integration with scripted
  input sequences and an input-spam anti-cheat regression test
- OpenTelemetry metrics (Meter `rpg.gameserver`) with a Prometheus scrape
  endpoint + `/healthz` on `--metrics-addr` / `METRICS_ADDR` (default `:9101`,
  empty disables). Instruments: tick duration histogram (66 ms budget buckets),
  processed inputs, players online, entities, snapshots sent, player saves by
  status, events published by type. Windows dev falls back to a localhost
  prefix when the wildcard bind needs an URL ACL. See docs/METRICS.md.

### Changed
- **Wire semantics (protocol format unchanged)**: `move_x`/`move_y` in `MsgInput`
  now carry a movement **direction**, not a per-message displacement. The server
  integrates `direction * speed * dt` (`dt = 1 / tickRate`) once per tick. Vectors
  with magnitude > 1 are normalized, so diagonal movement is no longer faster than
  cardinal; magnitude > 1.5, NaN and infinity are dropped and logged at Debug
- `EntityState.Speed` is now **world units per second** instead of a per-tick
  displacement multiplier; `ServerDefaults.DefaultPlayerSpeed` 1.0 → 5.0 u/s
- `TickLoop` coalesces buffered inputs: only the newest input per player per tick
  performs the movement integration (superseded inputs still resolve their attack).
  Closes the speed hack where movement scaled with client packet rate
- `InputHandler` takes the tick rate and map bounds; `ProcessInput` /
  `ProcessInputLocked` gained an `applyMovement` flag (defaults to `true`)
- `ValidationLogic.ValidateInput` audits the input direction via `MovementSystem`
  (timestep-independent) instead of a per-tick distance cap
- Travel distance is now tick-rate independent — tick rate is a smoothness knob,
  not a balance knob
- docs/DESIGN.md: dated "Movement Model" section (rationale, buffering choice,
  validation table, Unity prediction reuse plan); docs/README.md input semantics,
  flags, and Unity DOTS example updated

### Removed
- `Shared.GameLogic.Systems.MovementLogic` (`ApplyMove` / `ValidateMove`) and
  `GameConstants.MaxMovePerTick` — superseded by `MovementSystem`

### Fixed
- JWT claim field names (`user_id`/`server_id` → `sub`/`sid`) to match Go
  gateway wire format — cross-language token validation now works correctly

## [0.1.0] - 2026-08-04

### Added
- Initial C# port of Go game server
- `Shared.GameLogic` library with pure C# game logic (movement, combat, validation, AOI)
  - Designed for sharing between .NET server and Unity DOTS client
  - Zero Unity dependencies — standard .NET 10 class library
- `GameServer` .NET 10 console application
  - Wire protocol compatible with existing Go gateway (4-byte length prefix + JSON)
  - Server-authoritative tick loop at configurable rate (default 15Hz)
  - Thread-safe GameWorld with reader-writer locking
  - Input validation and anti-cheat (speed hack, range, cooldown)
  - Combat system (damage calculation, death handling)
  - AOI-filtered snapshot broadcasting
  - Async batch persistence (in-memory store, PostgreSQL interface ready)
  - Agones SDK interface (NoopSdk for local dev)
  - Event publisher interface for cross-server events
  - HS256 JWT validation (shared secret with gateway)
  - Entity hold on disconnect (30s map / 60s dungeon reconnect window)
  - Graceful shutdown on SIGINT/SIGTERM
- `GameServer.Tests` — comprehensive xUnit test suite
- NativeAOT publish support for minimal container images
- Docker multi-stage build (`deploy/docker/Dockerfile.gameserver-dotnet`)
- GitHub Actions CI pipeline (`ci-dotnet.yml`)
