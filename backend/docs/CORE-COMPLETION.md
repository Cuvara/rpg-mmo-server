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
| C1 | **Dungeon instancing — ADR-14 stage 6.** Allocate per party, lifecycle, shutdown. Today `--mode=dungeon` changes exactly one thing, the hold TTL (`Program.cs`: 60s vs 30s), `ALLOCATOR_FLEET_DUNGEON` is empty on every environment, no dungeon fleet manifest exists, and **the word "checkpoint" appears in no `.cs` or `.go` file in the repo**. | The project's one-line description is "open-world maps + instanced dungeons". Half of that has no plumbing. Any dungeon content written now targets a flow that does not exist. | L |
| C2 | **Party, and the Nakama social surface it needs.** `backend/nakama/` contains `auth/` and `economy/` only — there is no `social/` or `matchmaking/` directory. | C1 allocates *per party*. Without a party there is nothing to allocate per. | M |
| C3 | **Android proven.** The client CI builds Android, but no Android build has been run and joined a server, so the mobile transport path (TLS, sealed session, reconnect) is unmeasured on the platform the game targets. | "Mobile/PC" is in the project description; a transport that works only on Windows is not a proven transport. | M |
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

**When C1 and C2 are demonstrated.** C3 and C4 are real work but they do not change the
shape of any flow gameplay sits on: an Android client joins the same way a Windows one
does, and an autoscaler changes how many servers exist, not what a server does.

C1 and C2 do change that shape — a dungeon run is a different lifecycle from a map session,
and content written against a map session does not survive the difference. That is exactly
the rewrite `TEAM.md` sequences to avoid.
