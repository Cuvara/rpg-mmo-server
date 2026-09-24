# Core baseline v1 — what gameplay may build on

**Purpose.** `CORE-COMPLETION.md` answers *is the core done?*. This file answers the question
that comes next: **which parts of it are stable enough to write gameplay against, and which
are not.** It is the contract between the plumbing and the content that sits on it.

`backend/TEAM.md` sequences this project as *core plumbing only — no gameplay content*, on
the reasoning that gameplay written before the flows are proven has to be rewritten when a
flow changes. That gate is **open on both sides** as of 2026-09-13. This file exists so that
what was proven can be relied on without re-deriving it.

**Baselined:** 2026-09-24.

| | |
|---|---|
| `rpg-mmo-server` | `develop` @ **`ac1eec2`** — CI green, **CD green** (see §0) |
| `IndieRPGMMOAdventure` | `develop` @ **`285a5a2`** — CI green |
| `com.cuvara.netcode` | **v0.44.0** (manifest **and** lock, hash `2ed3df16`) |
| `com.rpgmmo.shared-gamelogic` | **sgl-v0.6.0** (manifest **and** lock, hash `c33bfd10`) |
| Released as | tag **`core-baseline-v1`** in both repositories |
| Wire protocol | **version 2** (Protobuf default; legacy JSON still accepted, distinguished by the first body byte) |
| Simulation rates | `SIM_CRITICAL_HZ=60`, `SIM_WORLD_HZ=15` (ADR-13) |

**The rule this file inherits from `CORE-COMPLETION.md`: a row is only stable when it has
been *demonstrated*, not when the code exists.** Everything below names its evidence.

---

## 0. The baseline was cut on a green deploy, not a green build

`CD — Build & Deploy` on `develop` had been red on **every run from 2026-09-13 to 2026-09-24**
while `CI` stayed green beside it. Getting it green found six defects, three of them silent,
and one of them was a gameplay bug the red CD had been hiding. The baseline commit `ac1eec2`
is the first develop commit since 2026-09-13 whose **own** CD run deployed to the dev cluster
and passed verification:

```
checks: 24  PASS 20  FAIL 0  SKIP 2  WARN 2
VERIFY=PASS
SMOKE=PASS with --strict-addr, persistence checks REQUIRED,
           SEALED gameplay hop (chacha20-poly1305 over protobuf),
           TLS gateway hop, certificate PINNED
```

The two skips are by design (Unity PlayMode needs an operator-produced result file; the
unknown-map refusal allocates a server that is never reclaimed, #424). This matters for the
rest of the file: a row below that says *demonstrated* means demonstrated by that pipeline
or by a built player, and "the build is green" was never that.

## 1. What gameplay may rely on

These flows have been driven end to end by **built players**, not probes. That distinction
cost this project a correction once already — "measured on dev" had meant measured with the
tool that was easiest to write, and a probe that speaks the protocol directly can prove a
backend and prove nothing about whether anyone can reach it.

| Flow | Demonstrated by |
|---|---|
| **Gateway auth → redirect → direct game-server session** (ADR-3) | `flow.smoke` in `verify.sh`, strict address, passing on dev and staging. The gateway is a redirector and is never in the gameplay data path, so client netcode holds **two** connections. |
| **Open-world map session** | Three built Windows players on one map, 60 Hz server tick, ~300 entities. |
| **Party → dungeon instance** (ADR-26) | Two built Unity players created a party through Nakama, joined by id, and **both landed on `127.0.0.1:7019`** — one instance — with `players_online: 2`. An outsider was refused with `not a member of that party`, and map entry on the same gateway was unaffected. |
| **Sealed transport** (ADR-22/23/24) | `GAMESERVER_SEALED=require` is the shipped default. Both players above were refused on first join for not sealing and escalated themselves; `sealed_cipher: chacha20-poly1305`. |
| **Reconnect within the grace restores the player** | A dropped connection holds the entity for `HoldTtl` (30 s map, 60 s dungeon), and during the hold it is **out of reach** — not chased, attacked or counted by enemies (`PlayerTag.Linkdead`). Before this, a held player was killed, respawned at the origin and saved there. Demonstrated by the develop CD smoke: walked to `x=5.9167`, disconnected, waited out the hold, rejoined **at `x=5.9167`** with `hp=100/100` — the same step that had read `x=0 y=0 hp=79`. |
| **Server-authoritative reward** | Wallet `{}` → `{"gold": 20}` measured on staging over a sealed session, **with a negative control**. |
| **Android parity** | An Android player (`ANDROID_ABIS=arm64,x86_64`) on an x86_64 emulator went IN WORLD against the dev cluster and took the full ADR-22 escalation. An Android client is configured by `backend.env` in `persistentDataPath` — Android has neither argv nor env. |
| **Prediction and reconciliation** | `LocalMovePredictor` + reconciliation in `com.cuvara.netcode`, ~170 Editor assertions, **golden vectors shared with the server**. |
| **Shared simulation** (ADR-10) | `Shared.GameLogic` compiles into both sides: `MovementSystem`, `CombatLogic`, `AoiLogic`, `SnapshotMerger`, `ValidationLogic`. Client and server therefore agree on movement, combat, **visibility** and snapshot merge semantics. |

Test surface behind it, at `ac1eec2`: **1685** C# tests (**1661 passed, 0 failed, 24 skipped**
— the skips are Redis/Postgres-gated and run in CI), **~234** Go tests, **~170** netcode
Editor assertions. CI fails a run that selected or executed nothing (`Verify test counters`),
so a green here is not an empty selection.

---

## 2. The numbers gameplay should design against

### Bandwidth is solved. Tick time is what binds now, and its ceiling is unknown

The authority is **ADR-7's `⛔ CURRENT STATE (2026-08-07, final)` block**, which exists
because this exact question kept being answered from stale figures.

| Question | Answer |
|---|---|
| Downstream bandwidth | **Solved to the threshold.** 45.9 KB/s per client at 200 players, inside ADR-7's `< 50 KB/s`. **Ceiling is above 200 and no longer bracketed.** |
| How reliable | **0.3% spread across six runs.** Bytes on the wire do not care what else the host is doing. |
| Players per game server (tick) | **UNKNOWN, and unknowable on this machine.** |
| What binds now | **Tick time.** Bandwidth used to bind at ~⅓ of the tick ceiling; three wire changes removed 81% of the wire and tick now breaks first. |

**Two figures in `BENCHMARK.md` are superseded and must not be quoted as current:**

- **~93 players** (the mobile bandwidth ceiling) — measured **before id interning**.
  `BENCHMARK.md` says so itself at ADR-7's threshold table: *"breached at ~41 players ⚠️
  **SUPERSEDED — now passes above 200**"*. The summary box at the top of `BENCHMARK.md`
  still tells a reader to size a fleet on it; that box predates the final ADR-7 block.
- **~150 players** (the old tick ceiling) — predates Protobuf, the entity-type enum and id
  interning.

**So a fleet cannot be sized from a measured ceiling today.** Bandwidth will not be what
stops you below 200 players per server; tick time might, at a number nobody has. Plan
capacity as an open question (#205), not as ~93.

### The one bandwidth relationship content can design against

Downlink is **exactly linear in AOI population**: bytes per entity per snapshot are flat at
24.77–25.00 across a 4× population range, so

```
KB/s per client = AOI population × 24.9 B × SIM_WORLD_HZ / 1000
```

At 200 players that predicts 74.7 against a measured 75.0 in the dense-crowd shape.

**What this means for content design:** entity count inside a player's AOI is the lever, and
`GAMESERVER_AOI_RADIUS` is a square law. Doubling the mobs in a fight doubles that fight's
bandwidth — linearly, predictably, and *this* part is trustworthy because it is a
relationship rather than a ceiling.

### The client survives a worse link than most players will have

Measured on a real Unity client at 40 ms one-way with ±60 ms of jitter:

| | clean | jittered |
|---|--:|--:|
| `snapshotsApplied` | 14.8/s | **14.8/s — unchanged** |
| `resyncs` / `rejected` / `dropped` / `clamped` | 0 | **0 / 0 / 0 / 0** |
| `rtt` | 5 ms | 110 ms median, 179 ms p95 |

Transport and merge hold. **This is not a statement about rendering** — every counter there
is upstream of the view (see #423).

### Client interpolation budget: 150 ms, and it bounds the server

`TargetDelay` 100 ms + `MaxExtrapolation` 50 ms = **150 ms** of cover, and the constant lives
in the **netcode package**, in the other repository. `ReplicationSchedule.ClientInterpolationBudgetMs`
names it server-side with its derivation, and `ScheduleFitsTheClientBudgetTests` asserts it.

**If gameplay changes either side of that, change both.** Nothing in this build fails when a
band is widened past what the client can absorb, and nothing in the client's build fails when
its buffer is narrowed. Grep `ClientInterpolationBudgetMs`.

---

## 3. Defaults gameplay inherits, and why they are what they are

| Setting | Default | Why it matters to content |
|---|---|---|
| `GAMESERVER_REPLICATION_SCHEDULE` | `off` | Every dirty entity is due every world tick. `tiered` is **inert at 60/15** and refused at startup there (ADR-27 decision 11), so content never sees deferral today. |
| `GAMESERVER_IMPORTANCE` | `legacy` | Importance orders what is shed; it does not budget and does not gate interest. |
| `GAMESERVER_MAX_SNAPSHOT_BYTES` | 8192 | The per-connection byte budget. **This one has bitten:** `snapshot_entities_shed` reached 11,827 when ~316 entities shared one AOI, and keyframes are budgeted too — an entity omitted from a keyframe **disappears** from the client's world until a later delta reintroduces it, visible as a pop. Content that puts many entities in one place must be checked against this. |
| `GAMESERVER_SEALED` | `require` | A stock client is refused and must escalate. |
| `GAMESERVER_FIELD_DELTA` | on | A controlled **32.2%** saving. The kill switch exists for measurement, not policy. |

**Edges are never deferred.** Health and the action retrigger counter are *occurrences*, not
states: withholding one is a dropped event, not a late update. Position and facing are safe
to defer; these are not. Gameplay that adds a new occurrence-shaped field must say so
(ADR-27 decision 5).

**The observer's own entity is never deferred**, because for self the position *is* the
reconciliation anchor (ADR-27 decision 9).

**A held player is out of reach.** Any gameplay system that picks targets among players must
honour `PlayerTag.Linkdead`, the way `PlayerTargetBuffer` does for the enemy systems. A new
system that queries `PlayerTag` directly and forgets it will reintroduce "killed for losing
your connection". Not yet covered: a *player* attacking a held player by id still resolves,
because there is no PvP content to exercise it.

---

## 4. What is NOT settled — do not design around these

| | Status |
|---|---|
| **Per-server player ceiling (tick)** | **Unknown, and this is now the binding constraint.** The load generator shares this machine with the server under test and uses more CPU than it; tick p99 read 67–71 ms quiet and 225–241 ms contended — a **3.3×** swing. Every tick figure from this host is a lower bound of unknown tightness. Both the old **150-player** tick figure and the **~93-player** bandwidth figure are **stale** and must not be quoted (§2). Tracked: #205. |
| **Anti-cheat enforcement thresholds** | Telemetry (A1–A4) runs and collects. **Thresholds are not set**, because every figure is from loopback where the latency that produces most false positives does not exist. |
| **Client rendering through a deferral gap** | Never verified (#423). The 150 ms budget is arithmetic, not an observed smoothness result. |
| **`tiered` replication at a faster world rate** | Asserted viable at 30 Hz, never measured over a link (#420), and it stacks with byte-pressure shedding in a way nothing measures (#421). |

---

## 5. Release gate

**Gameplay content is not gated on any of this.** Release is. The full list with evidence is
**#427**; the two hard blockers:

- **#425** — production has never been deployed off this box. `GAMESERVER_PUBLIC_ADDR=127.0.0.1:9210`
  is handed to clients verbatim and the guard warns only on a *hostless* value, so loopback
  passes silently.
- **#424** — an allocated dungeon pod that is never joined is never reclaimed. Any client that
  receives an address and dies before dialling holds a slot forever.

Plus two owner decisions (gateway-hop and meta-hop TLS, both written and off by default).

---

## 6. How to keep this file honest

It goes stale the same way `CLAUDE.md` and the ADRs each did — by describing a state that
changed under it. Three rules:

1. **A change to the wire protocol, `Shared.GameLogic`, or the interpolation budget is a
   change to both repositories.** Update the version table in this file in the same change.
2. **Re-baseline when a pin moves**, not when someone remembers. The table at the top is a
   claim about two specific commits and two specific tags.
3. **Never promote a row on the strength of code existing.** Name the run that demonstrated
   it. Every correction this project has had to publish came from skipping that step.

Related: `CORE-COMPLETION.md` (the gate before gameplay), `ARCHITECTURE-DECISIONS.md` (ADR
rationale), `BENCHMARK.md` (current measured numbers), `MEASUREMENT.md` (how to take a
measurement here without fooling yourself), `backend/TEAM.md` (cross-module contracts).
