# Core baseline v2 — release candidate (DRAFT, not tagged)

**Status: CANDIDATE.** This file is a draft. No `core-baseline-v2` tag exists. A row below
is only a claim when it names a run; rows marked **OPEN** or **HUMAN** are not met yet and
this file must not merge as "ready" until each is closed or explicitly waived by the project
owner. `CORE-BASELINE-V1.md` stays the contract for everything this file does not change
(§1 flows, §3 defaults, §6 honesty rules). Read V1 first; this file is the delta.

Why a separate file: V1 was edited in place for v1.1 because one pin moved. v2 moves the wire
protocol, the transport and most of the evidence, so an in-place edit would bury the v1.1
record. V1 gets a one-line pointer here.

## 0. Version table (candidate)

| | |
|---|---|
| `rpg-mmo-server` | `develop` @ **`a69af59`** (+ this docs commit; the tag goes on the merge commit of this PR, as in v1.1 where the tag sat on the docs commit `a154f18`) |
| `IndieRPGMMOAdventure` | `develop` @ **`6dce2c2`** |
| `com.cuvara.netcode` | **v0.49.0** (manifest **and** lock, hash `2ea640130e3e7e74a173396f0457561589e7950d`) |
| `com.cuvara.dots` | **v0.33.0** (hash `1ac175ddb245970f72fd35e9dd9db97f4e90f8a4`) |
| `com.cuvara.uitoolkit` | **v0.7.2** (hash `3c6fde9ac818cbfa8fc85a1bdf0c57b223607a47`) |
| `com.rpgmmo.shared-gamelogic` | **sgl-v0.7.0** (hash `86a654f7c491002813499ad4e968d0caab290490`) — see §4, one comment-only diff on `develop` |
| `rpg-factory` (testlab) | `main` @ **`dc81773`** (no tag; harness used for every run in §2) |
| Wire protocol | **version 3**, server window `Min 2 … 3`; `Wire.cs` identical in server, Netcode v0.49.0 and Netcode `develop` (md5 `8e69d4db6599a2136d160b696e901cb1`) |
| Simulation rates | `SIM_CRITICAL_HZ=60`, `SIM_WORLD_HZ=15` (unchanged) |
| Released as | tag **`core-baseline-v2`** in both repositories — **not yet cut** |

## 1. What changed since v1.1 (`a154f18` / `17b7737`)

This is a **real** baseline move, not a pin-only one. Gameplay authors must re-read V1 §1-§3
against the following.

| Area | Change | PR / source |
|---|---|---|
| Wire | Protocol 3 (core v3, 3D world, skillshots, extensible state) | #444 |
| Wire | Effective movement speed replicated with explicit presence: absent = unknown, present `0` = immobile. Netcode v0.49.0 consumes it | #488, Netcode #204 |
| Transport | Gameplay is **KCP/UDP only** (ADR-32); bulk AES-CFB removes the 95-player receive collapse | #446, #452, #448 |
| Server | Graceful leave saves and releases at once; a drop keeps the 30 s / 60 s hold | #482 |
| Server | Windows tick pacing: high-resolution sleep with bounded spin and a fallback; Linux path unchanged | #498 |
| Server | Zero-allocation guards: snapshot write 32 B to 0, gather flake, SC-003 attribution | #487, #484, #499 |
| Server | Unknown-message counter; sealed-refusal and kick-drain flakes fixed | #490, #489, #491 |
| Server | KCP clock seam (default unchanged) and dead-link characterization | #501 |
| Test harness | Autonomous playtest, server debug hooks, snapshot-delivery diagnostics | #472, #475, #474 |
| Ops | Restore drill in CI; guarded `backup-dev.yml` (dispatch only); dev runner docs | #486, #497 |
| Docs | Readiness gates, release runbook, operating profile (provisional), gameplay core contract | #485, #496, #500, #480 |
| Netcode | v0.49.0: speed presence, subscriber isolation, KCP crypto disposal race | Netcode #203, #204 |
| UnityDots | v0.33.0 | UnityDots release |
| Client | 41 commits past `core-baseline-v1.1`, pins above | client `develop` |

Netcode `develop` (`f9d9e96`) is **3 merges ahead of v0.49.0**: a configurable keyframe retry
interval (runtime, #207), a tick-rate-step characterization test (#208) and a CI pin. They are
**not in the tag** and not in the client. v2 is cut on v0.49.0, which is what every run in §2
used.

## 2. Evidence

Qualification on every row: **shared host or a single Windows host, bots or built players,
never an independent-host load run.** None of it is capacity (V1 §2, §4; ADR-7).

| Gate | Evidence | Run id / source | Server SHA | Client SHA | State |
|---|---|---|---|---|---|
| CI green, server | CI Backend, CI GameServer .NET, Publish images | runs 40671600637, 40671600714, 40671600626 | `a69af59` | n/a | PASS |
| CD deployed to dev | `CD — Build & Deploy` on `develop` | run 40671600721 (`Back up databases` job queued, no `dev` runner); 28 of the last 40 develop CD runs are `cancelled`; the last green develop CD was 2026-09-25 on `a154f18`, the v1.1 commit | `a69af59` | n/a | **OPEN / HUMAN** |
| CI green, client | CI / Validate & Test | run 40096465164 | n/a | `6dce2c2` | PASS |
| Package CI | Netcode / UnityDots / factory | 40082310324 (Netcode `2ea6401`), 40086967732 (UnityDots `1ac175d`), 40694839512 (factory `dc81773`) | n/a | n/a | PASS |
| Readiness batch (17 scenarios S01-S18 + S07/S07P sweeps) | F17 final batch | 22 runs, ids in `F17-LAB-FINAL.md` (for example S07 `20261024T133353Z-S07-3cyz`, S07P `20261024T133533Z-S07P-ypxc`) | `a3ba2ce` | `6dce2c2` | PASS, **but on an older server** |
| Readiness batch on the candidate | fresh batch on the exact candidate SHAs | **not run** | `a69af59` | `6dce2c2` | **OPEN** |
| One-hour soak (#473), native | S09 v5, snapshot trace on, quiet band | `20261028T201636Z-S09-nwdv`: 15 PASS, 0 FAIL, 0 INCONCLUSIVE; host guard G1-G6 none; 60.0003 ticks/s; zero receive gaps over 250 ms; largest 129.9 ms. Warm-up S01 A/B `20261028T200018Z-S01-s2xx`, `20261028T200724Z-S01-9gik` PASS | `a69af59` (native, ReadyToRun) | `6dce2c2` | PASS per runner; **independent review RV-SOAK11 pending** |
| Backup / restore rehearsal | R1 on dedicated `rpgr-*` resources | not run | n/a | n/a | **OPEN** |
| Independent host capacity | bracketed sweep, separate generator | none | n/a | n/a | OPEN, **not required for v2**, and no ceiling is claimed |

### Soak qualification

`nwdv` is one clean run, so it is **"not reproduced in one clean run"**, not "fixed". Issue
#473 (delivery gaps up to 1.2 s, RTT creep in the last 15 min) stays open until the project
owner closes it. Earlier attempts (`9tgw`, `ibz3`, `ez9e`, `k4n5`) were INVALID on host
contamination; none of them is counted. The soak ran on the native Windows server, so it
does not exercise the Linux/Docker image that CD deploys.

## 3. Known limitations and open human decisions

| Item | State |
|---|---|
| **Per-server player ceiling** | Still **unknown** (V1 §4, #205). No number in this file is a capacity claim. |
| Operating profile (READINESS-GATES §2 and §5) | Four defaults approved as **provisional** by the project owner on 2026-10-25: 25 players per map, 25 players + up to 300 enemies, mid-range Android floor, RTT p95 100 ms / p99 150 ms. Reference device names remain open. Nothing is validated. |
| G1-G6 gates | Provisional; G1 (tick) and G4 (device frame time) have **no valid reading**. |
| Combat-logout policy | Graceful leave releases immediately (#482). The alternatives (hold on combat, hold on all logout) are **undecided**. |
| Enemy defaults | Budget 300, separation 0.8, scatter 3 are placeholders, not tuned gameplay values. |
| Dead-link budget | Owner decision: keep unchanged. KCP closes after 3.18 / 10.45 / 31.35 s of ACK silence at 30 / 100 / 300 ms RTO (#501). Four product questions remain open (`D1-DEADLINK-REC.md`): longest background that must resume without reconnect, acceptable interruption after a 5 s freeze, dungeon hold length, and whether a Unity-level reconnect test is needed. Observed 5 s freezes recovered by reconnect in 8.2-9.4 s. |
| WSL clock drift | WSL2 monotonic about 7-9 % slow. One-way delay stays unavailable by design. The soak used a native Windows server to avoid it; Docker/WSL runs still carry it. |
| Dev environment | `dev` is protected (required reviewer, `develop` only, no admin bypass). **No `dev` target exists**: the kube context `k3d-rpg-dev` is absent and there is no self-hosted runner labelled `dev`. This is why develop CD is not green. |
| Test users on the normal backend | Throw-away Nakama device users from earlier LiveBackend PlayMode runs; cleanup needs owner approval (never done). Batch PlayMode now runs with `-testCategory "!LiveBackend"`. |
| HYP-1 (AnyDesk incoming session causes multi-second freezes) | Experiment E1 is approved but **not run**. Until it runs, freezes seen under AnyDesk are correlation, not cause. The `nwdv` pass happened with no AnyDesk resident process, which differs from the usual remote-access state. |
| Pending cross-repo items | Netcode `develop` ahead of v0.49.0 (§1); client PR #160 (submodule bump) is open; `CORE-BASELINE-V1.md` §5 blockers (#425 production host) are unchanged. |
| Runbook | `RELEASE-RUNBOOK.md` is still **PROPOSED, NOT DRILLED**. Rollback below is the documented inverse, not a tested one. |

## 4. Shared.GameLogic

`sgl-v0.7.0` is pinned. `develop` differs from that tag by one commit, `ec06d9a`, which only
rewrites a doc comment in `Components/SnapshotData.cs`; there is no code change in
`Shared.GameLogic`, so the golden vectors and simulation are the same. `package.json` stays
`0.7.0`. No `sgl-v*` tag is required for v2. If the owner prefers the comment to be pinned,
the runbook's step 1 (version bump, `main` promotion) applies and the client pin must move
with it; that is a separate change and is **not** assumed here.

## 5. Rollback

Rollback is the documented inverse (`RELEASE-RUNBOOK.md` §3) and is **UNDRILLED**.

1. Server: redeploy the previous image digest. Record the digests of both images before any
   deploy. The previous baseline is `core-baseline-v1.1` (`a154f18`).
2. Client: re-pin the previous tags in `manifest.json` and `packages-lock.json` in one PR.
   A shipped build cannot be recalled.
3. Do not delete `core-baseline-v2`. Supersede it with a new tag or a corrected doc.
   Netcode and UnityDots are untouched by this release, so there is nothing to withdraw.
4. Wire protocol 3 is already the server default. v1.1 clients speak protocol 2, which the
   server window (`Min 2`) still accepts; a rollback of the server to a v1.1 image would
   **refuse** protocol 3 clients, so roll the client back first (runbook §3, "Wire protocol
   bump").
5. Read back the live revision (`/status`, image revision label) to confirm.

## 6. Keeping this file honest

Same rules as `CORE-BASELINE-V1.md` §6. In particular, when a row above changes state, edit
the row and name the run. Remove this "DRAFT" status only in the PR that records the closed
gates.

Related: `CORE-BASELINE-V1.md`, `READINESS-GATES.md`, `RELEASE-RUNBOOK.md`, `MEASUREMENT.md`,
`BENCHMARK.md`, `ARCHITECTURE-DECISIONS.md`.
