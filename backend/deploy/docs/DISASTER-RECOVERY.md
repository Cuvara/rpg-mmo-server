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
| **Redis** (`rpg-redis`) | ✅ unaffected — the C# game server holds no Redis client at all (`GameServer/Program.cs:153-155`) | ❌ dead: auth and `MsgEnterWorld` both fail | Sessions (expendable), server registry (**not** rebuildable automatically), event stream backlog | see drill | ≤1s of writes (`appendfsync everysec`) |
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

> **Status: BLOCKED — not yet executed.** Docker Desktop was paused for the
> whole of the scheduled window, so the stack could not be brought up. Every row
> in this section is therefore **estimated from code**, marked as such, and must
> be replaced with measured values when the drill runs. The procedure below is
> the exact script to run; it is written to be repeatable.

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

_To be filled by the next drill run. Record: wall-clock stop time, first failed
login, wall-clock start time, first successful login, `servers:*` key present
y/n, relay resumed y/n, and paste the relevant gateway log lines._

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
`$BACKUP_DIR/redis/`. Cron it on the VPS the same way as the DB backups:

```cron
17 3 * * * cd /opt/rpg-mmo/deploy && db/redis-backup.sh --skip-missing >> /var/log/rpg-backup.log 2>&1
```

### The AOF trap (why a naive restore silently does nothing)

The server runs with `--appendonly yes`. **On startup Redis prefers the AOF over
the RDB.** Dropping a `dump.rdb` next to an existing `appendonlydir` restores
*nothing*: the server comes back with the old dataset and the operator believes
the restore worked. `redis-restore.sh` therefore removes
`appendonlydir`/`appendonly.aof` before injecting the RDB, in both modes; Redis
7 then loads the RDB and rebuilds a fresh AOF from it. Do not hand-roll this
with `docker cp`.

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

**RTO estimate: 2–5 minutes** for the restore path, dominated by the operator
noticing. Restore itself is seconds — the dataset is tiny.

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
  hypothesis, not a backup.
- **Per release milestone**: re-run the [Redis failure drill](#failure-drill-redis)
  and update the measured table. The numbers only stay true until the next
  change to the gateway's Redis paths.
