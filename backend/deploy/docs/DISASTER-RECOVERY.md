# Disaster Recovery

What breaks when each dependency dies, what a player sees, how to get it back,
and how long that takes. Companion to `DATABASE.md` (backup/restore mechanics)
and `RUNBOOK-local-dev.md` (day-to-day operations).

Scope: the compose stack in `backend/deploy/docker-compose.yml` — the same
topology CD deploys (`docs/CICD.md`). k3s/Agones changes the recovery commands
but not the blast radii.

> **Read this first.** The single most important finding in this document is not
> about Redis persistence. It is that **no game server ever registers itself**.
> The registry entry is written once, by hand, by `scripts/register-gameserver.sh`
> at deploy time. Nothing heartbeats it and nothing rewrites it. So any event
> that empties Redis — a crash without a usable AOF, a `FLUSHALL`, a volume
> loss, a fresh container — makes **every game server invisible forever** and
> every login fails with "no available server for map …" until a human re-runs
> that script. Redis restarting cleanly is survivable; Redis losing its data is
> an unbounded outage, not a blip. See [Code gaps](#code-gaps-open) G1.
>
> **This is no longer a code reading — it was measured on 2026-08-06.** A clean
> Redis restart put the world back in **2.3 seconds** with no human involved. A
> deleted registry left a perfectly healthy game server invisible for as long as
> anyone cared to watch. Same containers, same processes; the only difference is
> whether Redis kept its keys. [Measured results](#measured-results).

---

## Terminology

- **RTO** — recovery time objective: how long from failure to service restored.
- **RPO** — recovery point objective: how much data loss is acceptable/expected.

Numbers below are marked **measured** (from the drill in
[Failure drill](#failure-drill-redis)) or **estimated** (derived from code and
config, not yet exercised). Do not size a launch on an estimate — that is the
standing rule from `backend/docs/ARCHITECTURE-DECISIONS.md` ADR-7.

---

## Blast radius at a glance

| Dependency | In-progress gameplay | New logins / map join | Data at risk | RTO | RPO |
|---|---|---|---|---|---|
| **Redis** (`rpg-redis`) | ✅ unaffected — the C# game server holds no Redis client at all (`GameServer/Program.cs:153-155`) | ❌ dead: auth and `MsgEnterWorld` both fail | Sessions (expendable), server registry (**not** rebuildable automatically), event stream backlog | **2.3s measured** if the volume is intact; **unbounded** if the dataset is lost (nothing re-registers — G1) | 0 on a clean restart (measured); ≤1s on a host crash (`appendfsync everysec`) |
| **Game PostgreSQL** (`rpg-postgres-game`) | ✅ continues — the tick loop never blocks on the DB; the 30s save sweep just fails | ⚠️ new players cannot load their saved state | ≤30s of position/HP (ADR-6) | minutes (restore.sh) | last backup, or ≤30s if the instance survives |
| **Meta PostgreSQL** (`rpg-postgres`) | ✅ continues (combat/movement are not meta) | ❌ Nakama cannot authenticate → no JWT → no gateway auth | Accounts, currency, inventory, leaderboards — **the valuable data** | minutes (restore.sh) | last backup (daily cron / per-deploy) |
| **Nakama** (`rpg-nakama`) | ✅ continues | ❌ no new JWTs issued; existing valid JWTs still work until expiry | none (state is in meta PG) | ~30s (container restart) | 0 |
| **Gateway** (`rpg-gateway`) | ✅ continues — the gateway is a redirector, not a proxy (ADR-3) | ❌ no auth, no map assignment | none (state is in Redis) | ~10s (container restart) | 0 |
| **Game server** (`rpg-gameserver`) | ❌ **everyone on that map drops** | ⚠️ that map is unjoinable; registry entry goes stale but is not removed | ≤30s of unsaved state (ADR-6) | ~30s restart **+ manual re-register** | ≤30s |
| **lgtm** (`rpg-lgtm`) | ✅ | ✅ | observability only — you fly blind | ~1min | metrics gap for the outage |

Two things the table makes obvious:

1. **Redis and the gateway are not in the gameplay path.** Players already in a
   world keep playing through both outages. This is the ADR-3 redirector design
   paying off, and it is the one genuinely good resilience property of the
   stack today.
2. **Every failure is a "cannot join" failure, not a "cannot play" failure —
   except the game server itself, which is a hard single point of failure per
   `map_id` (ADR-2, one live server per map).**

---

## Failure drill: Redis

> **Status: EXECUTED 2026-08-06 10:03–10:11 UTC.** See
> [Measured results](#measured-results). The estimate table below is kept as-is,
> unedited, so the delta between what the code was expected to do and what it
> actually did stays visible — two of its rows were wrong, and one of them was
> wrong in a way that destroys data.

### Procedure

```bash
cd backend/deploy                                    # cwd MUST be here (docker.exe rejects absolute /mnt paths)

# 0. baseline
docker.exe compose --profile realtime ps
docker.exe exec rpg-redis redis-cli DBSIZE
docker.exe exec rpg-redis redis-cli --scan --pattern 'servers:*'
../../scripts/…/smoketest                            # a full auth -> enter-world -> join -> tick flow

# 1. kill redis, keep a client in-world
date -u +%H:%M:%S; docker.exe stop rpg-redis
#    observe: does the in-world client keep receiving MsgSnapshot?   (expected: YES)
#    observe: does a NEW client get past MsgAuth?                    (expected: NO)
#    observe: gateway process alive?                                 (expected: YES, degraded)
docker.exe logs --since 2m rpg-gateway
docker.exe logs --since 2m rpg-gameserver

# 2. bring it back
date -u +%H:%M:%S; docker.exe start rpg-redis
#    observe: seconds until a new login succeeds again
#    observe: docker.exe exec rpg-redis redis-cli --scan --pattern 'servers:*'   <- is the server back?
#    observe: does the event relay resume, or hot-loop on NOGROUP?
```

### Expected results (estimated from code — replace with measurements)

| Observation | Expectation | Evidence |
|---|---|---|
| In-world clients keep receiving snapshots | **Yes**, indefinitely | `GameServer/Program.cs:153-155` — no Redis client exists; `EventStream = new NoopEventStream()` |
| New `MsgAuth` | Fails after ~5s (go-redis default DialTimeout) with `MsgAuthResp{OK:false, Error:"session creation failed"}` | `gateway/server/server.go:296-301` |
| Existing authenticated connections | **De-authenticated on their next frame** — a Redis error is indistinguishable from an expired session, so the gateway resets `State`/`UserID` and replies `"session expired"` | `gateway/server/server.go:246-278` |
| `MsgEnterWorld` | Fails; the client receives the **raw internal error string**, e.g. `assign map: find servers: … dial tcp …: connection refused` | `gateway/server/server.go:347` (`sendEnterWorldError(cc, err.Error())`) |
| Gateway crashes? | **No if Redis dies after boot** (degrades). **Yes — crash loop — if Redis is down at boot**: `Run()` → `relay.Start` → `XGROUP CREATE` fails → `os.Exit(1)` | `gateway/cmd/gateway/main.go:187-190`, `gateway/server/server.go:120-124` |
| Game server crashes? | No. It never talks to Redis | `GameServer/GameServer.csproj:14-21` (no Redis package) |
| Automatic reconnect when Redis returns | **Yes** for the connection itself — go-redis reconnects transparently on the next command; no process restart needed | `shared/storage/redisstore/session.go:22-27` |
| Time to healthy after `docker start` | Redis boot + AOF load (sub-second on this dataset) + next client attempt. **Estimated <5s** *if the data survived* | `docker-compose.yml:134-153` healthcheck `interval: 5s` |
| Registry entry back? | **Yes if the AOF survived** (it is just a key). **NO, permanently, if the data was lost** — nothing re-registers | `scripts/register-gameserver.sh:83-91`, called once from `scripts/deploy-local.sh:269` |
| Event-stream consumer group survives? | Survives a clean restart (it is in the AOF). **After data loss the group is gone and `XREADGROUP` fails `NOGROUP` in a silent 2 Hz retry loop forever** — the relay is dead but the process looks healthy | `shared/storage/redisstore/stream.go:118-121` (created once), `:132-168` (loop never re-creates, never logs) |
| Does `/healthz` reflect any of this? | **No.** Liveness only, on both services | `gateway/metrics/metrics.go:194-205`, `GameServer/Observability/MetricsEndpoint.cs:151-172` |

### Measured results

**Drill run: 2026-08-06, 10:03–10:11 UTC.** All timestamps below are UTC and
come from `date -u +%H:%M:%S.%3N` taken immediately around each action.

#### What was under test

| | |
|---|---|
| Deployed commit | `4c4c58ae4522f84520febbd5ee6d2252e446fc44` — the tag on **both** `rpg-gateway` and `rpg-gameserver` images, i.e. gateway and game server are provably from one build |
| How it was determined | `docker inspect <c> --format '{{.Config.Image}}'`. There is **no `$RPG_DEPLOY_DIR/COMMIT`** on this host: `/opt/rpg-mmo` does not exist because this stack is a plain `docker compose` bring-up, not a `deploy-local.sh` install. The image tag is the equivalent artifact and is the one to record on a compose host |
| Redis | 7.4.10, `run_id af6e15c0f39c4debdc1679bc34aab22295970a93`, uptime 1617s at baseline |
| Client used | `backend/smoketest` — real `MsgAuth` → `MsgEnterWorld` → `MsgJoinToken` → `MsgInput`/`MsgSnapshot` flow, not a synthetic Redis client |

> **`develop` has moved since these numbers were taken.** They were measured
> against `4c4c58a`; `develop` is now `184a779`, which added gateway rate
> limiting, split secrets and client→gateway KCP encryption (#22). None of that
> touches the Redis client or the persistence path, so §0–§4 stand — but **G11
> (no `MsgAuth` response while Redis is down) sits in the auth path that #22
> changed**, so re-confirm G11 against `184a779` before anyone acts on it.

> **This is a pre-G1 baseline, deliberately.** The drill measures the world as it
> is *before* game-server self-registration and heartbeating land. Everything
> below about "the registry never comes back on its own" is a measurement of the
> G1 gap, not a prediction of it. When G1 ships, re-run the drill: the
> [forced-DEL](#g1-evidence-forced-del-not-expiry) result is the one that should
> change, and it is the whole point of the fix.

#### 0. Baseline

`DBSIZE` **6**, full keyspace with type and TTL:

```
servers:id:gs-dotnet-map_01   hash     ttl=3268
servers:map:map_01            set      ttl=-1
servers:map:map_kcp           set      ttl=-1     <- stale, see below
events:game                   stream   ttl=-1
events:global                 stream   ttl=-1
pixel:events                  stream   ttl=-1
```

Smoke test green:

```
PASS  gateway_auth                3ms  transport=tcp map=map_01 server=:9200 (tcp)
PASS  gameserver_join          1.122s  snapshots=15 (keyframes=1 deltas=14) final_x=3.33 ack_tick=10
SMOKE=PASS
```

**Finding B1 — only the id hash carries a TTL; the map index set does not.**
`servers:id:*` expires (3600s, `register-gameserver.sh:55`), `servers:map:*` is
a set with `ttl=-1`. The baseline caught this mid-divergence: an older
registration `gs-kcp-final` had **already expired naturally** — no operator
action, just the 3600s TTL elapsing — leaving `servers:map:map_kcp` holding a
member whose hash was gone (`EXISTS servers:id:gs-kcp-final` → `0`).

Joining that map behaves correctly and self-heals:

```
FAIL  gateway_auth   3ms  error: enter world rejected: assign map: no available server for map map_kcp
# afterwards:  SMEMBERS servers:map:map_kcp -> (empty),  DBSIZE 6 -> 5
```

So the gateway `SREM`s dead members when it looks a map up, and an emptied set
disappears with it. The leak is real but bounded and lazy: an index set for a
map **nobody ever queries** is never cleaned. Error handling is graceful — no
crash, no stack trace to the client, correct "no available server" message.

#### 1. Redis killed (natural, non-destructive outage)

`docker stop rpg-redis` issued **10:04:45.830**, returned **10:04:46.725**.
Restarted at 10:05:44 → outage window ≈ **58s**.

| Observation | Measured | vs estimate |
|---|---|---|
| In-world client keeps receiving snapshots | ✅ **Yes.** A client that joined at ~10:04:43 stayed in-world for **42.2s spanning the entire outage** and received **286 `MsgSnapshot`** with correct movement. (Its `SMOKE=FAIL` line is my own `--min-snapshots 300` threshold being set above the ~6.8 snapshots/s the server actually delivers — not a server fault) | ✅ matches |
| Game server crashes | ✅ No. Container up throughout, tick loop uninterrupted | ✅ matches |
| Gateway crashes | ✅ No. Degraded, stayed up (Redis died *after* boot — the boot-time crash-loop G3 was not exercised) | ✅ matches |
| **New `MsgAuth`** | ❌ **Estimate was wrong.** Predicted `MsgAuthResp{OK:false, Error:"session creation failed"}` after ~5s. Actual: **the gateway sends no response at all.** The client hung and died on its own 10s deadline: `gateway auth: recv: read length: read tcp 127.0.0.1:48574->127.0.0.1:8000: i/o timeout` (attempt at 10:04:49.3, gave up 10:04:59.4) | ❌ **worse than estimated** — a real client gets a hang, not a rejection, so it cannot show the player an error or fail over |
| Operator-visible logging | ❌ **Zero application-level logs.** The gateway logged nothing about the failed auth. The only output was go-redis's own pool chatter, ~every 6–14s | new finding |

Gateway log for the whole outage — this is *everything* it said:

```
redis: 10:04:46 pool.go:762: redis: connection pool: failed to dial after 5 attempts: dial tcp 172.19.0.3:6379: connect: connection refused
redis: 10:05:00 pool.go:762: redis: connection pool: failed to dial after 5 attempts: dial tcp: lookup redis on 127.0.0.11:53: no such host
redis: 10:05:06 pool.go:762: ... no such host
redis: 10:05:20 pool.go:762: ... no such host
redis: 10:05:28 pool.go:762: ... no such host
redis: 10:05:39 pool.go:762: ... no such host
redis: 10:05:45 pool.go:762: ... connect: connection refused
```

**Finding B2 — the error changes shape mid-outage.** `connection refused` while
the container exists, then `lookup redis on 127.0.0.11:53: no such host` once
Docker deregisters the name. Any alert or retry logic that pattern-matches on
"connection refused" will miss most of a real outage.

#### 2. Recovery (clean restart, data intact)

| Event | Time | Δ |
|---|---|---|
| `docker start rpg-redis` issued | 10:05:44.377 | — |
| `docker start` returned | 10:05:45.532 | +1.15s |
| **First login attempt after start → `SMOKE=PASS`** | 10:05:46.707 | **+2.33s from issue** |

`gateway_auth` on that first attempt took 35ms (vs 3ms baseline — one
reconnect), the next run was back to 3ms. **No gateway restart was needed**;
go-redis reconnected transparently, as estimated. Measured RTO for a clean
Redis restart is **~2.3s**, comfortably inside the "<5s" estimate.

State after the restart:

```
DBSIZE 5
servers:id:gs-dotnet-map_01   ttl=3123      # was 3268 at 10:03; 145s elapsed -> TTL is
servers:map:map_01                          # absolute and persisted, it did NOT reset
XINFO GROUPS events:game    -> name gateway  consumers 1  pending 0
XINFO GROUPS events:global  -> name gateway  consumers 0  pending 0
grep -c NOGROUP <gateway logs>  -> 0
```

✅ Registry survived, ✅ consumer groups survived, ✅ relay resumed with no
`NOGROUP` hot-loop. **G4 is confirmed to be a data-loss-only failure mode, not a
restart failure mode** — a clean restart is safe.

#### G1 evidence (forced DEL, not expiry)

Everything above came from a **natural** event (a container stop, and a TTL that
expired on its own). This section is the opposite: a **deliberate, operator-issued
`DEL`** simulating data loss. Stated explicitly because the two are easy to
conflate and only one of them is a real-world spontaneous event.

```
DEL servers:id:gs-dotnet-map_01 servers:map:map_01   -> (integer) 2      DBSIZE 5 -> 3
```

Immediately after:

```
FAIL  gateway_auth   2ms  error: enter world rejected: assign map: no available server for map map_01
SMOKE=FAIL
```

The `rpg-gameserver` container was **healthy and ticking throughout**. It was
simply invisible. Polled every 10s for **70s**:

```
t+10s servers keys: []   t+20s []   t+30s []   t+40s []   t+50s []   t+60s []   t+70s []
```

**G1 confirmed by measurement: nothing re-registers. Ever.** The world stayed
unjoinable until a human restored it. This is the difference between a 2.3s
outage (§2, data survived) and an unbounded one (here, data lost) — same
process, same containers, same everything except whether Redis kept its keys.

#### 3. Backup and restore scripts — first real runtime exercise

Both scripts had been written but never run against a live stack until now.

`db/redis-backup.sh --dir /tmp/rb --keep 3` — **worked first try, 1.24s**:

```
[redis-backup] live dataset: 5 keys
[redis-backup] issuing BGSAVE (lastsave=1786010745)
[redis-backup] BGSAVE ok
[redis-backup]   ok: redis-20260806T100603Z.rdb (4.0K, 5 keys)
[redis-backup]   retention: 1 kept (limit 3)
```

`db/redis-restore.sh --file <rdb>` (scratch rehearsal) — **worked first try,
2.49s**, live instance untouched:

```
[redis-restore] RDB loaded cleanly
[redis-restore] restored dataset: 5 keys
[redis-restore]     2  servers:*    2  events:*    1  pixel:*
[redis-restore] rehearsal OK -- this backup is restorable
```

#### 4. The bug the drill found: `--mode live` restored nothing, and said `done`

Running the **same, just-rehearsed, provably-good 5-key RDB** through the live
path destroyed the dataset and reported success:

```
[redis-restore] wiping AOF + RDB on volume 'rpg-mmo-meta_redis-data' and injecting the snapshot
[redis-restore] starting 'rpg-redis'
[redis-restore] restored dataset: 0 keys          <-- five keys went in
[redis-restore] WARNING: the restored dataset is EMPTY -- was the backup taken from an idle stack?
[redis-restore] done                              <-- exit code 0
```

`DBSIZE` **0**. The dataset that existed before the restore was gone, the
snapshot had not been loaded, and the operator was told the restore was `done`.

**Root cause.** The header comment on the old script was half right. It knew the
AOF has to be deleted, and deleted it. What it got wrong was the next step: with
`--appendonly yes` and **no AOF manifest on disk**, Redis 7 does *not* fall back
to `dump.rdb`. It initialises an empty dataset and writes a fresh AOF base from
it. The startup log is unambiguous — no `Done loading RDB` line at all:

```
10:08:08.551 * Server initialized
10:08:08.555 * Creating AOF base file appendonly.aof.1.base.rdb on server start
10:08:08.561 * Ready to accept connections tcp
```

(Not a permissions problem — the container runs as uid 0, `dump.rdb` was present
at 460 bytes with a valid `REDIS0012` magic, `dir=/data`, `dbfilename=dump.rdb`.
Redis simply never opened it. This is the same reason the Redis manual says to
enable AOF with a runtime `CONFIG SET appendonly yes` rather than by restarting
into it.)

**Why the rehearsal did not catch it — the important part.** Scratch mode starts
its throwaway container with `--appendonly no`, so it loads the RDB and reports
5 keys. Live mode starts the compose container, which has `--appendonly yes`.
The two modes exercised *different Redis startup paths*, so a green rehearsal
was evidence about the file only, never about the restore. **A rehearsal that
does not run the production code path is not a rehearsal.**

**Fix (same PR).** `--mode live` no longer hands the RDB to the live container.
After wiping the AOF and injecting the snapshot it starts a short-lived **seed**
container over the live volume with `--appendonly no` (which does load the RDB),
issues `CONFIG SET appendonly yes` so Redis rewrites `appendonlydir` *from the
loaded dataset*, waits for `aof_rewrite_in_progress:0` +
`aof_last_bgrewrite_status:ok`, shuts it down, and only then starts the real
container — which now finds an AOF containing the snapshot. A **hard
verification gate** compares the live key count against the seed's and `die`s on
mismatch, because the failure mode was silent and silence is what made it
dangerous.

Re-run of the identical command with the fix, which is also how the stack was
recovered from the forced `DEL` above:

```
[redis-restore] seeding the AOF from the snapshot (temporary container, appendonly off)
[redis-restore] snapshot contains 5 keys; rewriting AOF from it
[redis-restore] AOF seeded
[redis-restore] starting 'rpg-redis'
[redis-restore] restored dataset: 5 keys
[redis-restore]     2  servers:*    2  events:*    1  pixel:*
```

**8.5s wall clock**, and end-to-end verification afterwards:

```
servers:map:map_01 / servers:id:gs-dotnet-map_01 present (ttl=2862)
aof_enabled:1   aof_last_write_status:ok
XINFO GROUPS events:game -> name gateway consumers 1 pending 0
PASS  gateway_auth      3ms  transport=tcp map=map_01 server=:9200 (tcp)
PASS  gameserver_join  1.12s  snapshots=15 (keyframes=1 deltas=14) final_x=3.33 ack_tick=10
SMOKE=PASS
```

#### Summary of RTO/RPO, now measured

| Scenario | RTO | RPO | Confidence |
|---|---|---|---|
| Redis container restart, volume intact | **2.3s** (measured, `docker start` → verified join) | **0** — AOF replayed, TTLs preserved absolutely | measured |
| Redis dataset lost, restore from backup | **~8.5s** of script + however long the operator takes to notice and pick a file | age of the newest RDB (per-deploy + cron) | measured |
| Redis dataset lost, **no** re-registration (today) | **unbounded** — measured at 70s and counting, ends only when a human runs `register-gameserver.sh` | — | measured (forced DEL) |
| In-progress gameplay, any of the above | **0** — 286 snapshots delivered across a 58s Redis outage | 0 | measured |

#### Corrections this drill forces on the rest of this document

1. The "New `MsgAuth` fails after ~5s with `MsgAuthResp{OK:false}`" row in the
   estimate table is **wrong**: there is no response, the client hangs to its
   own timeout. Worth filing alongside G6/G7 — call it **G11: the gateway
   silently stops responding to `MsgAuth` when Redis is down, instead of
   rejecting.** Evidence: this drill, §1.
2. `db/redis-restore.sh --mode live` **could not restore anything** before this
   PR. Any statement elsewhere that Redis backups were verified restorable was
   true only of the scratch path.

---

## Data durability: Redis

Config in effect (`docker-compose.yml:134-143`):

```
--appendonly yes          # AOF on: every write appended
--appendfsync everysec    # fsync once per second  -> worst-case loss window = 1s
--save 60 1000            # RDB snapshot if >=1000 keys changed in 60s
--maxmemory-policy noeviction   # ADR-4: this is a system of record, not a cache
```

Verify it is actually applied (config drift is real — the compose file is not
proof):

```bash
cd backend/deploy
docker.exe exec rpg-redis redis-cli CONFIG GET appendonly appendfsync save maxmemory-policy maxmemory
docker.exe exec rpg-redis redis-cli INFO persistence | grep -E 'aof_enabled|rdb_last_bgsave_status|aof_last_write_status'
```

- **RPO on a clean container restart: 0.** The AOF is fsynced and replayed.
- **RPO on an unclean host crash: ≤1s** of writes (`appendfsync everysec`).
- **RPO on volume loss: total** → and total loss of the registry is the
  unbounded outage described at the top. Back Redis up (below).

`maxmemory` is unset, so `noeviction` currently has nothing to enforce; it is
set explicitly so that adding a memory cap later cannot silently turn this into
an LRU cache and evict a live server out of matchmaking (ADR-4).

---

## Backup and restore: Redis

New in this change, mirroring the PostgreSQL pair in `DATABASE.md`:

| Script | What it does |
|---|---|
| `backend/deploy/db/redis-backup.sh` | `BGSAVE`, wait for `LASTSAVE` to advance, verify `rdb_last_bgsave_status=ok`, stream `/data/dump.rdb` out over `docker exec cat`, verify the `REDIS` magic, timestamp, prune to `--keep` |
| `backend/deploy/db/redis-restore.sh` | Loads an RDB into a **throwaway scratch container** (default) or over the live instance (`--mode live --yes`) |

```bash
cd backend/deploy

db/redis-backup.sh                          # -> $BACKUP_DIR/redis/redis-<UTC>.rdb, keep 7
db/redis-backup.sh --dir /tmp/rb --keep 3
db/redis-backup.sh --skip-missing           # container absent -> warn, exit 0

db/redis-restore.sh --file /tmp/rb/redis-20260806T041500Z.rdb          # rehearse (safe)
db/redis-restore.sh --file <rdb> --mode live --yes                     # real recovery
```

CD runs `redis-backup.sh --skip-missing` in the `db-migrate` job alongside the
PostgreSQL dumps, so every deploy leaves a Redis checkpoint under
`$BACKUP_DIR/redis/`.

**The Redis step is deliberately non-fatal; the PostgreSQL step is not.** The PG
dump gates a schema migration — deploying past a failed dump risks
unrecoverable data — so a failure there aborts the deploy. Redis holds only
transient or reconstructible state per ADR-4: sessions expire and clients
re-auth, and the registry and event streams are rebuilt by running servers. A
missing Redis checkpoint is worth a `::warning::`, never a blocked deploy.

(Caveat worth stating plainly, because it is the one thing that makes "Redis is
reconstructible" less true than it sounds: the registry is only rebuilt by
running servers *once G1 is fixed*. Today nothing re-registers, so a lost
registry needs a manual `register-gameserver.sh`. That is an argument for
fixing G1, not for blocking deploys on a Redis dump.)

Cron it on the VPS the same way as the DB backups:

```cron
17 3 * * * cd /opt/rpg-mmo/deploy && db/redis-backup.sh --skip-missing >> /var/log/rpg-backup.log 2>&1
```

### The AOF trap (why a naive restore silently does nothing)

The server runs with `--appendonly yes`. **On startup Redis prefers the AOF over
the RDB.** Dropping a `dump.rdb` next to an existing `appendonlydir` restores
*nothing*: the server comes back with the old dataset and the operator believes
the restore worked. `redis-restore.sh` therefore removes
`appendonlydir`/`appendonly.aof` before injecting the RDB, in both modes. Do not
hand-roll this with `docker cp`.

**And deleting the AOF is still not enough** — the trap inside the trap, found
by the [drill](#4-the-bug-the-drill-found---mode-live-restored-nothing-and-said-done)
and fixed in the same change. With `appendonly yes` and *no* AOF manifest on
disk, Redis 7 does **not** fall back to `dump.rdb`: it starts **empty** and
writes a fresh AOF base from that empty dataset. The naive sequence "wipe AOF →
drop RDB → start" therefore destroys the dataset and loads nothing, with a
zero exit code. This document previously asserted the opposite; it was wrong.

`redis-restore.sh --mode live` never hands the RDB to the live container. It
runs a short-lived **seed** container over the live volume with `--appendonly
no` (which *does* load the RDB), flips `CONFIG SET appendonly yes` so Redis
rewrites the AOF **from the loaded dataset**, waits for the rewrite to finish,
then starts the real container — and finally compares key counts, failing hard
on mismatch.

**Consequence for rehearsals:** scratch mode starts Redis with `--appendonly no`
and live mode does not, so the two exercise different startup paths. A green
rehearsal proves the *file* is restorable. It does not prove the *live restore
path* works. Both matter; never read one as the other.

(`docker cp` is also avoided for a second reason: under WSL, `docker.exe`
translates host paths and absolute `/mnt/*` destinations fail. Both scripts move
bytes through `docker exec`/`docker run -i` stdio instead.)

### After any Redis restore — the step people forget

Restoring the dataset does **not** make the world joinable. Sessions in the
snapshot are stale (clients re-auth anyway, which is fine), but the **server
registry is only correct if the snapshot happened to contain a still-valid
entry.** Always finish with:

```bash
docker.exe exec rpg-redis redis-cli --scan --pattern 'servers:*'
# empty, or pointing at a dead address? re-register by hand:
scripts/register-gameserver.sh register
```

---

## Per-dependency recovery procedures

All commands assume `cd backend/deploy` (compose needs a cwd-relative context;
`docker.exe` rejects absolute `/mnt/*` paths).

### Redis

| Symptom | Action |
|---|---|
| Container down, volume intact | `docker.exe start rpg-redis` → AOF replays → verify `redis-cli DBSIZE` and `--scan --pattern 'servers:*'` |
| Container up, dataset empty | `db/redis-restore.sh --file <latest rdb> --mode live --yes`, **then** `scripts/register-gameserver.sh register` |
| Volume lost entirely | `docker.exe compose up -d redis` → restore as above → re-register → restart `rpg-gateway` (its event-stream consumer group is gone and the loop will not re-create it — G4) |
| Gateway crash-looping at boot | Redis is unreachable. Fix Redis first; the gateway cannot start without it (G3) |

**RTO, measured 2026-08-06:** `docker start` on an intact volume → verified
joinable in **2.3s**. A full `--mode live` restore from an RDB takes **8.5s** of
script. Both are dominated by the operator noticing, so 2–5 minutes remains the
right planning figure end to end — but the mechanical part is seconds, and that
is now measured rather than assumed. See [Measured results](#measured-results).

### PostgreSQL (meta and game)

Full procedure in `DATABASE.md`. Short form:

```bash
db/backup.sh --db gamestate --dir /var/backups/rpg-mmo-forensic   # snapshot the broken state FIRST
db/restore.sh --file <dump> --db gamestate --target gamestate_scratch --yes   # rehearse
db/restore.sh --file <dump> --db gamestate --yes                              # real
```

RPO = age of the newest backup (per-deploy + daily cron). **This is the weakest
RPO in the stack and it covers the most valuable data (currency, inventory,
accounts).** Anything of value must be written through Nakama transactionally at
grant time, never left to the 30s sweep (ADR-6).

### Nakama

Stateless — all state is in meta PG. `docker.exe compose up -d nakama`. If it
will not start, the fault is meta PG or the plugin build (`nakama-plugin.Dockerfile`).

### Gateway

Stateless. `docker.exe compose --profile realtime up -d gateway`. Reconnecting
clients re-auth. Note that a restart also re-issues `XGROUP CREATE`, which is
the current workaround for a dead event relay (G4).

### Game server

**Hard SPOF per map** (ADR-2: one live server per `map_id`). Everyone on that
map drops and loses ≤30s of position/HP.

```bash
docker.exe compose --profile realtime up -d gameserver-dotnet
scripts/register-gameserver.sh register     # REQUIRED — it does not self-register
```

Until that second command runs, the map is unjoinable even though the server is
healthy. This is the same manual step the Redis recovery needs, for the same
reason (G1).

### lgtm (Grafana/Prometheus/Loki/Tempo)

`docker.exe compose --profile monitoring up -d lgtm`. Losing it costs
observability only; the metrics gap for the outage window is unrecoverable
(Prometheus scrapes are not backfilled). Grafana at `:3001`.

---

## Code gaps (open)

Infrastructure cannot fix these — they are in the Go/C# modules and are out of
DevOps scope. Filed here with evidence so they can be assigned.

| # | Gap | Evidence | Impact |
|---|---|---|---|
| **G1** | **No game server registration or heartbeat in any running code.** Registration is a one-shot shell script at deploy time with `REGISTRY_TTL=3600`; nothing refreshes it | `scripts/register-gameserver.sh:83-91,55`; called once at `scripts/deploy-local.sh:269`. No production caller of `RegistryService.RegisterServer` (`gateway/registry/registry.go:148-150`) | **Highest severity.** Any Redis data loss = permanently unjoinable world until a human intervenes. Also means a crashed game server keeps a stale registry entry for up to an hour, black-holing joins |
| **G2** | TTL mismatch: code assumes a 15s liveness window, deployment writes 3600s | `shared/constants/ttl.go:9` (`ServerHeartbeatTTL = 15s`) used at `registry.go:50,59` vs `register-gameserver.sh:55` | Stale entries survive ~240× longer than designed |
| **G3** | Gateway **crash-loops** if Redis is unavailable at boot | `gateway/server/server.go:120-124` → `gateway/cmd/gateway/main.go:187-190` (`os.Exit(1)`) | Redis blip during a deploy takes the gateway with it; no degraded start |
| **G4** | Event-stream consumer group created **once**; after a Redis wipe the loop retries `NOGROUP` at 2 Hz **forever, silently** — no logging, no re-create, no backoff growth | `shared/storage/redisstore/stream.go:118-121` (create), `:132-168` (loop), `:164` (`_ = XAck`) | Cross-server events silently stop while every health signal stays green |
| **G5** | Redis client has **zero** tuning: no dial/read/write timeouts, no retry policy, no pool config | `shared/storage/redisstore/session.go:22-27` — `redis.NewClient(&redis.Options{Addr, Password})` | All-defaults behaviour (5s dial, 3s read); a slow Redis stalls the gateway accept path with no backpressure |
| **G6** | A transient Redis error is indistinguishable from an expired session, so it **de-authenticates live connections** | `gateway/server/server.go:246-278` — resets `cc.State`/`cc.UserID`, replies `"session expired"` | A 1s Redis hiccup logs out everyone who sent a frame during it |
| **G7** | Raw internal errors are sent to clients verbatim | `gateway/server/server.go:347` — `sendEnterWorldError(cc, err.Error())` | Info leak: clients see `dial tcp 172.x.x.x:6379: connection refused` |
| **G8** | No registry caching — every `MsgEnterWorld` hits Redis; no last-known-good fallback | `gateway/registry/registry.go:82`, `shared/storage/redisstore/registry.go:135-163` | Redis down = 100% join failure, where a 15s cache would absorb most blips |
| **G9** | `/healthz` is liveness-only on both services and never reflects dependency health; no `redis_up` gauge | `gateway/metrics/metrics.go:194-205`; `GameServer/Observability/MetricsEndpoint.cs:151-172`; metric registrations at `metrics.go:83,105` | Alerting cannot distinguish "up" from "up and useless" |
| **G10** | With `REDIS_ADDR` unset the gateway **silently** selects the in-memory backend | `gateway/cmd/gateway/main.go:215-233` | A misconfigured deploy comes up "healthy" but stateless and single-process |
| **G11** | With Redis down the gateway **does not answer `MsgAuth` at all** — no `MsgAuthResp{OK:false}`, no error frame, nothing. The client hangs until its own deadline | **Measured** in the [drill §1](#1-redis-killed-natural-non-destructive-outage): 10s client-side `i/o timeout`, and the gateway logged nothing but go-redis pool chatter | A real client cannot distinguish "backend down" from "network dead", cannot show the player an error, and cannot fail over. Also invisible to operators — zero application-level logs for the whole 58s outage |
| **G12** | `servers:map:*` index sets carry **no TTL** while `servers:id:*` hashes do, so an expired registration leaves an orphan member behind | **Measured**: `servers:map:map_kcp` held `gs-kcp-final` after its hash expired naturally | Bounded — the gateway `SREM`s dead members on lookup and drops the emptied set — but an index for a map nobody queries leaks forever. Low severity; fold into the G1 fix |

Suggested order: **G1 → G3 → G4 → G6 → G8 → G5 → G9 → G2/G7/G10.** G1 alone
converts the worst-case Redis outage from "unbounded, needs a human" into "one
heartbeat interval, self-healing", and it is a small change: a periodic
`Register`/`Heartbeat` call from the C# server (which would need a Redis client
it does not currently have — see ADR-5) or, cheaper as an interim, a sidecar
loop re-running the existing shell script on the `ServerHeartbeatTTL` cadence.

---

## Production upgrade path

Today: **one Redis, no replica, no Sentinel, no failover.** That is defensible
for Dev/Alpha and Beta given that Redis is not in the gameplay path — but only
once G1 is fixed, because until then a Redis restart is not a degraded-join
event, it is a manual-recovery event.

| Tier | CCU | Redis posture | Trigger to adopt |
|---|---|---|---|
| Dev/Alpha | <200 | Single instance, AOF, nightly + per-deploy RDB backup (today) | — |
| Beta | 200–500 | Add a replica on the second VPS (`replicaof`), reads still go to the primary; promote by hand | When a manual Redis recovery would exceed the acceptable downtime |
| Soft Launch | 500–2000 | **Sentinel, 3 nodes** (primary + replica + a quorum-only third), clients use the Sentinel-aware go-redis constructor | When "no logins for 5 minutes" stops being acceptable |
| Growth | 2000+ | Sentinel across k3s nodes with anti-affinity, or a managed Redis; consider splitting the event stream onto its own instance | When registry + streams contend, or when Redis restarts start costing money |

Sketch for the Beta replica (compose):

```yaml
  redis-replica:
    image: redis:7.4-alpine
    container_name: rpg-redis-replica
    restart: unless-stopped
    command:
      - /bin/sh
      - -ec
      - |
        exec redis-server \
          --replicaof redis 6379 \
          --appendonly yes \
          --appendfsync everysec \
          --maxmemory-policy noeviction \
          --replica-read-only yes \
          $${REDIS_PASSWORD:+--requirepass "$${REDIS_PASSWORD}"} \
          $${REDIS_PASSWORD:+--masterauth "$${REDIS_PASSWORD}"}
    depends_on:
      redis:
        condition: service_healthy
    volumes:
      - redis-replica-data:/data
```

Note this buys **durability and a fast manual promote, not automatic failover** —
the gateway points at a fixed `REDIS_ADDR` (`shared/config/config.go:54`). Real
failover needs Sentinel plus a `redis.NewFailoverClient` in
`redisstore.NewRedisClient`, which is code work (extends G5).

**Do not add a replica before G1.** A replica protects against data loss; it
does not help at all with the failure that actually hurts today, which is that
nothing rewrites the registry.

---

## Drill cadence

Rehearse, do not assume. Minimum:

- **Per deploy** (automatic): CD takes PG + Redis backups in `db-migrate`.
- **Monthly**: `db/redis-restore.sh --file <newest>` and
  `db/restore.sh --target <scratch> --yes` — a backup nobody has restored is a
  hypothesis, not a backup. **The monthly scratch rehearsal is not sufficient on
  its own**: it starts Redis with different flags than production does, which is
  exactly how a completely broken `--mode live` survived code review (drill §4).
  At least once per release, rehearse `--mode live` against a throwaway stack.
- **Per release milestone**: re-run the [Redis failure drill](#failure-drill-redis)
  and update the measured table. The numbers only stay true until the next
  change to the gateway's Redis paths.
