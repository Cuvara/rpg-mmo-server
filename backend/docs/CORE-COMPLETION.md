# Core completion — the gate before gameplay content

**Purpose.** `backend/TEAM.md` sequences this project as *core plumbing only — no gameplay
content*, on the reasoning that gameplay written before the flows are proven has to be
rewritten when a flow changes. This file is the checklist that says when that gate opens.
It exists because "is the core done?" was being answered from memory, and the two places
that recorded state — `CLAUDE.md` and the ADRs — have each been caught describing a state
that had changed under them.

**Rule for this file: a row is only ✅ when it has been *demonstrated*, not when the code
exists.** The distinction is ADR-14's own ("Stage 5 is the first point at which anything is
*proved*"), and it is the one this project keeps getting wrong.

Last audited: **2026-09-12**, against `origin/develop` and the live `k3d-rpg-dev` /
`k3d-rpg-stg` clusters.

---

## Done, and demonstrated

| Area | Evidence |
|---|---|
| Gateway auth + redirect (ADR-3) | `flow.smoke` in `verify.sh` — full client flow end to end, strict address — passing on dev and staging |
| C# game server, Arch ECS simulation (ADR-12) | 1170 unit tests; live under load |
| Protobuf wire + id interning (ADR-9) | 81% smaller than JSON, golden vectors shared with the client |
| Postgres + Redis implementations (ADR-4, ADR-5) | `data.pg_meta`, `data.pg_game`, `data.redis_ping`, `data.redis_policy` all passing |
| Agones allocation, ADR-14 stages 1-5 | dev and staging gateways run `ALLOCATOR=agones`; a pod reaches `Allocated` and a real client joins it — that is stage 5's own definition |
| Sealed transport (ADR-22/23/24) | `GAMESERVER_SEALED=require` on dev and staging; three default clients connect with no flags |
| Reward path (game server → Nakama) | wallet `{}` → `{"gold": 20}` measured on staging over a sealed session, with a negative control |
| Anti-cheat telemetry A1, A2, A3, A4 | see `ROADMAP-SECURITY.md` §1.2 |
| Client netcode, prediction, reconciliation | `com.cuvara.netcode`, ~170 Editor assertions, golden vectors shared with the server |

## Open — and these are what "core complete" is waiting on

| # | Item | Why it is core, not content | Size |
|---|---|---|---|
| C1 | ~~**Dungeon instancing — ADR-14 stage 6.**~~ **DONE 2026-09-12.** Measured on dev: a party created through Nakama, two members entering, **both handed `127.0.0.1:7031` — one instance** — an outsider refused with `not a member of that party`, and map entry unaffected on the same gateway (`cmd/dungeonprobe`). ADR-26: entry through `EnterWorld` with `party_id`, the instance keyed by party, membership checked against Nakama before allocating, a dungeon pod that registers no map and does not persist `map_id`/position, and self-shutdown when empty. | L |
| C2 | ~~**Party, and the Nakama social surface it needs.**~~ **DONE for what gates C1, 2026-09-12.** `backend/nakama/social/` ships `party_create` / `party_join` / `party_leave` / `party_get`, cap 4 held by a version check rather than a read-then-write (two joiners on a 3-member party both read 3 and both commit, otherwise). `party_get` answers server-to-server over `runtime.http_key`, which is the one thing ADR-26 decision 3 needs. **Friends, chat, guild, presence and matchmaking are still not started** -- they are not gating, because nothing in C1 asks them anything. | M |
| C3 | ~~**Android proven.**~~ **DONE 2026-09-12.** An Android player built with `ANDROID_ABIS=arm64,x86_64` ran on an x86_64 emulator against the dev cluster and went **IN WORLD**, then took the full ADR-22 escalation: kicked `no_sealed_session`, reconnected with sealing, `sealed session established`, back in world and stable, with the server reporting `players_online: 1` and `sealed_cipher: chacha20-poly1305`. Two client gaps were found and fixed getting there: the build was **arm64-only**, which installs on no usable emulator, and **an Android build could not be pointed at a backend at all** — every override was a CLI flag or an env var, and Android has neither. | M |
| C4 | **ADR-14 stages 7 and 8.** Buffer-based `FleetAutoscaler`; retire the superseded `deploy/agones/` manifests. | Fleet scaling policy is plumbing. Deliberately deferred, not forgotten — `verify.sh` currently asserts the *absence* of an autoscaler. | S |

## Blocked on something that is not code

These are **not** gate items. Writing more code does not advance them, and gameplay content
does not depend on them.

| Item | Blocked on |
|---|---|
| Per-game-server player ceiling (ADR-7) | A separate machine for the load generator. The load generator uses more CPU than the server under test; tick p99 read 67-71ms quiet and 225-241ms contended, a 3.3x swing. Every tick figure from this host is a lower bound of unknown tightness. |
| Enforcement thresholds for A2 and A4 | A baseline measured against **real players on real networks**. Every figure here is from loopback, where the latency that produces most rejections does not exist. `METRICS.md` states this limit in full. A detector that acts before anyone has seen its false-positive curve gets switched off after the first bad night, taking the telemetry with it. |
| Gateway-hop TLS enabled (ADR-23) | An owner decision: a trusted certificate, or a pin shipped in the client. The code is written and off by default. |
| Meta-hop TLS enabled (ADR-24) | Two measured blockers recorded at `deploy/k8s/data/nakama.yaml`. |
| Production hosting | Four placeholder values: `GAMESERVER_PUBLIC_ADDR=127.0.0.1:9210`, `GAME_DB_URL=…localdev@localhost`, `REDIS_ADDR=localhost:6389`, `RPG_DEPLOY_DIR=/mnt/e/…`. Production has never been deployed. |

---

## When the gate opens

## THE GATE IS OPEN, 2026-09-12

**C1 and C2 are both demonstrated, so gameplay content can start.** The proof is one line of
`cmd/dungeonprobe` output against the dev cluster:

```
DUNGEON OK: one party, one instance at 127.0.0.1:7031; outsider refused; maps unaffected
```

C3 (Android) is done and C4 (ADR-14 stages 7-8) remains, but neither changes the shape of any
flow content sits on: an Android client joins the same way a Windows one does — measured, not
assumed — and an autoscaler changes how many servers exist, not what a server does.

**One known gap, found by running it rather than by reading it.** A dungeon pod that is
allocated and then never joined never shuts itself down -- decision 6 requires
`everHadPlayer`, which is what stops a fresh pod dying at boot, and Agones never reclaims an
Allocated pod. Two probe runs consumed both replicas of the dev fleet permanently. In
production this is any client that receives an address and dies before dialling. It needs a
bounded join deadline; until then a dungeon fleet needs headroom over its real concurrency.
It does not block content, because content does not depend on the shape of that fix.

**What content can now be written against.** An open-world map session and a party-instanced
dungeon session, both server-authoritative, both sealed, with rewards going through Nakama
transactionally at grant time. What it must NOT be written against: durable position inside a
dungeon (ADR-26 decision 5 says it is not durable), resumable encounters (ADR-26 decision 4
defers encounter checkpointing), or a known per-server player ceiling (ADR-7: still unknown,
blocked on hardware).

C1 and C2 do change that shape — a dungeon run is a different lifecycle from a map session,
and content written against a map session does not survive the difference. That is exactly
the rewrite `TEAM.md` sequences to avoid.
