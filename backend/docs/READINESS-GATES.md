# Readiness gates — provisional engineering gates for the gameplay-development phase

**Status: PROVISIONAL.** Written 2026-10-24 from audit finding A4 OE-01 / OE-02 / OE-05
(baseline `cc7884f`). Every number below is either quoted from a named source or labelled
as an assumption. Nothing here is a validated target and **nothing here is a capacity
ceiling**.

**This file never states a players-per-server figure.** ADR-7 and `CORE-BASELINE-V1.md` §2/§4
record that the per-server tick ceiling is unknown (#205): the load generator shares the
host with the server under test, and tick p99 swung 3.3x with host load. The old 150 and
~93 figures are stale. A capacity number appears in this project only after an
independent-host bracketed sweep, with the run id beside it.

How to read the tables:

- **Source** — where the value comes from, or `ASSUMPTION` when nobody has measured it.
- **Instrument** — the thing that produces the reading. A gate with no instrument is a wish.
- **HUMAN DECISION** rows are open on purpose. Each carries a proposed default and why. The
  default is a starting point for the decision, not the decision.
- Read the instrument's **summary counters**, not its exit code (`MEASUREMENT.md` §1): a run
  that sampled nothing exits 0.
- A gate result is only valid with its **qualifications**: host load, whether the load
  generator was co-located, and the run id. A shared-host run can support "no defect seen at
  this scale", never "capacity".

---

## 1. Gates

### G1. Server tick

| | |
|---|---|
| Gate | Base-tick p99 `<` budget fraction (see D2) of `1 / SIM_CRITICAL_HZ`, **and** zero sustained overruns at steady state |
| Budget | `1 / SIM_CRITICAL_HZ`. At the 60 Hz default that is **16.67 ms**. The 66.67 ms figure is the *world* interval (15 Hz) and governs snapshot cadence, not the base tick. `BENCHMARK.md` Part XIII (item 30) records the harness grading a 60 Hz server against 66.67 ms. |
| Source | ADR-7 acceptance table: p99 `<` half the budget "for GC and spikes", overruns zero at steady state. The "half the budget" fraction is ADR-7's; applying it to the 60 Hz base tick (8.33 ms) is the A4 OE-01 proposal and is **ASSUMPTION**. |
| Instrument | `gameserver_tick_duration_seconds` (histogram) and `gameserver.tick.overruns` (counter) on the game server metrics endpoint; `/status` `tick_rate`. Testlab `server_tick` check (`p99_budget_ratio` 1.0 = the whole `1 / SIM_CRITICAL_HZ`, `min_rate_ratio` 0.98). `backend/loadtest` for the sweep. |
| Valid only when | Load generator on a **separate host** from the server. A co-located run is a lower bound of unknown tightness. |
| Status | **No valid reading exists.** All existing tick figures are shared-host (X12, T62). |

### G2. Per-client downstream bandwidth

| | |
|---|---|
| Gate | `< 50 KB/s` per client at the declared entity mix |
| Source | ADR-7 acceptance table (mobile-network assumption). |
| Instrument | `snapshot_bytes` on `/status`; `backend/loadtest` per-client downlink. Predictive model from `CORE-BASELINE-V1.md` §2: `KB/s = AOI population x 24.9 B x SIM_WORLD_HZ / 1000`. |
| Evidence | 45.9 KB/s at 200 players, 0.3% spread over six runs (`BENCHMARK.md`). Bandwidth does not depend on host load, so this reading is **valid from a shared host**. |
| Watch | `GAMESERVER_MAX_SNAPSHOT_BYTES` (8192) sheds entities silently; read `snapshot_entities_shed` beside the bandwidth figure. A low bandwidth reading with a high shed count is a pass for the wrong reason. |

### G3. Snapshot delivery gap

| | |
|---|---|
| Gate | No gap above the testlab thresholds: `snapshot_cadence` `max_gap_ms` 250; `snapshot_update_gap` `max_gap_ms` 200 (S02); delivery analysis `gap_ms` 250 |
| Source | Testlab `docs/THRESHOLDS.md` (rpg-factory, generated). The doc itself marks all three **policy, unvalidated**. The client absorbs 150 ms (`TargetDelay` 100 + `MaxExtrapolation` 50), so a 200-250 ms gap is already past the render cover. |
| Instrument | Testlab checks `snapshot_cadence`, `snapshot_update_gap`, `snapshot_order`; `analyze --delivery`; server gauge `snapshot_max_update_gap_ms`; trace file `GAMESERVER_SNAPSHOT_TRACE` (dev/test only). |
| Status | #473 (delivery gap) stays open until a clean quiet-host 1 h soak (S09) passes. The T62 1 h soak failed four assertions from one 4695 ms freeze, attributed to host load. That attribution does not convert the run to a pass. |

### G4. Client frame time

| | |
|---|---|
| Gate | Frame time p95 `<=` 16.7 ms and p99 `<=` 33.3 ms **at the target connected workload on the minimum device tier** (see D3, D4) |
| Source | A4 OE-02 proposal. **ASSUMPTION**: no product frame or GPU target exists today. |
| Instrument | Client `docs/DEVICE-BENCHMARK.md` (250/500/1000 synthetic entities; GC median target 0). Testlab `render_jumps`, `render_freshness`, `host_stalls` for smoothness. |
| Status | The device benchmark is synthetic; connected-client mode is instrument-only. It does not yet establish shipped-gameplay performance on a device. GPU time must be measured on the device, not inferred from CPU frame time. |

### G5. Reconnect within the hold

| | |
|---|---|
| Gate | A dropped client that rejoins inside the hold window resumes the same entity, position and hp, 100% of attempts in the scenario matrix |
| Source | Hold windows are `constants.EntityHoldTTL` 30 s (map) and `DungeonHoldTTL` 60 s (dungeon). Position/hp continuity is the demonstrated row in `CORE-BASELINE-V1.md` §1. |
| Instrument | Testlab S08 (reconnect), S08G (graceful leave), S11 (server restart/resync), S10 (regression replay); `PlayerTag.Linkdead` handling. |
| Gap | No standalone disconnect/rejoin churn matrix (A4 OE-04). Success is per selected failure mode, not across the matrix. |

### G6. Session lifecycle latency

| | |
|---|---|
| Gate | Gateway auth p99 `< 100 ms`. Enter-world and join-token-to-in-world latency: **no target exists** (D5) |
| Source | Auth p99 from ADR-7 acceptance table. Enter-world/join: **ASSUMPTION**, proposal in D5. |
| Instrument | `backend/loadtest` login -> enter-world -> join loop with latency histograms (ADR-7 benchmark plan item 5); testlab S01 bootstrap/connect timings. The gateway has no auth-latency histogram of its own at this baseline; if one is needed for production alerting, that is a separate change. |
| Status | Auth p99 was measured under ADR-7 follow-up work; no run at the current baseline is recorded here. |

### G7. Memory (carried from ADR-7)

`< 128Mi` per pod at target capacity (the Agones pod limit). Instrument: pod RSS; measured
~30 MiB idle, ~82 MiB at 200 players. Listed so the set is complete; it is not one of the six
requested gates and has no open decision.

---

## 2. HUMAN DECISIONS — left open

None of these may be settled by an engineer or an agent from the code. Each blocks a gate
above from being more than provisional.

| # | Decision | Blocks | Proposed default | Reasoning for the default |
|---|---|---|---|---|
| D1 | **Target CCU per map**, and entity mix (players / mobs / projectiles per AOI) | G1, G2, G7 | **Leave unset** until an independent-host bracketed sweep exists. Then set it to a point the sweep passed, with headroom chosen by the owner. | Any number chosen now is a guess dressed as a requirement. The only defensible placeholder for *measurement* is to sweep over a declared range and publish the passing points. |
| D2 | **Tick budget fraction** and which rate it is graded against: 60 Hz base (16.67 ms period) vs the 15 Hz world interval (66.67 ms) | G1 | Grade the **base tick** against `1 / SIM_CRITICAL_HZ`. Gate p99 at **half** that (8.33 ms at 60 Hz). | The base tick is what the harness times. Half is ADR-7's own headroom rule carried to the real rate. The looser 33 ms (half of 66.67) would pass a server running 2x over its base budget. The stricter reading may prove unreachable; if so, the decision is to relax the fraction with evidence, not to quietly switch rate. |
| D3 | **Minimum device tier** (and a named reference device) | G4 | One named mid-range Android device as the floor, plus one current iPhone. | The client targets mobile and PC; the floor device sets the frame budget. A tier named in prose with no device cannot be measured. |
| D4 | **Frame budget and workload** on that tier | G4 | p95 `<=` 16.7 ms, p99 `<=` 33.3 ms (60 fps target, one dropped frame tolerated at the tail), at the D1 entity mix in connected mode. | 60 fps is the stated render rate (`CLAUDE.md` netcode model). A 30 fps floor is a legitimate alternative for the lowest tier and is the owner's call. |
| D5 | **Gameplay RTT budget** (input-to-render and server round-trip, p95/p99) and enter-world latency | G3, G6 | Network RTT p95 `<=` 100 ms for full-quality play; enter-world p95 `<=` 3 s. | Interpolation cover is 150 ms and measured clients held at 110 ms median / 179 ms p95 RTT with no resyncs or drops (`CORE-BASELINE-V1.md` §2). 100 ms keeps the p95 inside the cover with room for tick and render delay. The 3 s enter-world figure is a plain ASSUMPTION; nothing measures it. |

A decision is recorded by editing the row: replace the proposal with the decided value, the
name of who decided it, and the date. Until then the gate reads PROVISIONAL.

---

## 3. What would make each gate valid

| Gate | Needed | Tracked as |
|---|---|---|
| G1 | Independent-host server + load generator; bracketed sweep (find the first failing point, then the last passing one); host telemetry kept | #205, A4 OE-01/OE-03 |
| G2 | Nothing further for the 200-player shape; re-run if wire encoding or AOI changes | `BENCHMARK.md` |
| G3 | Clean quiet-host 1 h S09 with the sampler on; bracketed server clock exchange before any one-way-latency claim | #473, A4 OE-03 |
| G4 | D3/D4 decided; connected workload run on the reference device | A4 OE-02 |
| G5 | Disconnect/rejoin churn matrix scenario | A4 OE-04 |
| G6 | D5 decided; loadtest run at the current baseline | A4 OE-02 |

Missing scenarios named in A4 OE-04 (packet loss/jitter injection, malformed-input
survival, churn) are not gates yet because no instrument exists. Add the gate when the
scenario lands.

## 4. Keeping this file honest

1. A gate row changes only when a **named run** changed it. Cite the run id.
2. Never promote an `ASSUMPTION` to a source because it was repeated. Edit the cell when it
   is measured or decided.
3. Do not copy a number from here into another document as a ceiling. These are pass/fail
   limits for a run, not statements about what the server can carry.
4. When the testlab `THRESHOLDS.md` changes a value quoted in G3, update G3 in the same
   change.

Related: `ARCHITECTURE-DECISIONS.md` ADR-7, `CORE-BASELINE-V1.md`, `BENCHMARK.md`,
`MEASUREMENT.md`, `RELEASE-RUNBOOK.md`.
