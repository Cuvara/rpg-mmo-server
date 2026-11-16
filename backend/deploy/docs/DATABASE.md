# Database Operations

Schema migrations, backups, and disaster recovery for the two PostgreSQL
instances.

| Instance | Container | Default port | Owner | Who manages the schema |
|----------|-----------|--------------|-------|------------------------|
| Meta | `rpg-postgres` | 5432 | `nakama` / `nakama` | **Nakama** — it runs `nakama migrate up` at container start. Never hand-edit it. |
| Game state | `rpg-postgres-game` | 5433 | bootstrap `game` (admin); app login `gamestate`; database `gamestate` | **Us** — numbered migrations in this repo. |

Everything below is about the game-state instance. The meta instance is only
ever backed up and restored, never migrated by us.

## Per-game game-state database (ADR-33 D5)

`db/provision-gamestate.sh` provisions one non-default game's database on the
shared game Postgres instance (`postgres:16.4-alpine` in compose and Kubernetes).
It creates database **and login role** `gamestate_<game>`, makes that role the
database owner, and reapplies restricted role attributes and connection limits
on every run. The role has LOGIN, NOSUPERUSER, NOCREATEDB, NOCREATEROLE,
NOREPLICATION and NOINHERIT. Database creation is conditional and runs outside
a transaction. This creates no tables: the game's existing `Migrator` creates
them on first server start, using this role as their owner.

Game ids must match exactly `^[a-z][a-z0-9]{1,23}$`. The ids `gs`, `map` and
`dungeon` are reserved. The default game, `cuvara`, is refused because it keeps
the existing `gamestate` database; its restricted login is provisioned by
`db/provision-default-role.sh` instead (see "Default game's restricted login
(MG-01)" below). Invalid input exits 2 before invoking psql.

Both the role and database default to **20 connections**; override with
`--connection-limit N` (positive integer, at most PostgreSQL's 2147483647).
This is a starting budget, not a capacity guarantee: it limits one game's share
of the shared instance's connections (ADR-33 D7), leaving room for other games
and operations. Size fleet pools and the combined game budgets against the
instance's `max_connections`; CPU and query load remain shared.

The script revokes PUBLIC's CONNECT and TEMPORARY privileges on the new game's
database, then grants CONNECT to its owner role. PostgreSQL normally grants
PUBLIC CONNECT to other databases, so this alone does not isolate the login.
The final check uses `has_database_privilege` for **every other non-template
database**, including `postgres`, `gamestate` and other `gamestate_*` databases.
It fails nonzero and names accessible databases if any remain. An unsuccessful
run may already have created the role and database; fix the grants and rerun
to converge. **On stock PostgreSQL the flag below is normally required:** PUBLIC
holds CONNECT on `postgres` and `gamestate`, so a run without
`--revoke-public-connect` creates the role and database and then fails this
check.

`--revoke-public-connect` explicitly hardens those accessible databases by
revoking CONNECT from PUBLIC before the check, quoting identifiers with
`format('%I')`. **Other roles that depended on PUBLIC lose the ability to open
new connections there.** Owners and superusers keep access; the default game's
`gamestate` login owns `gamestate`. Existing connections are not terminated. Review other
service accounts before choosing this flag. The script grants nothing on the
default game's database. Explicit grants and role memberships can still allow
access after PUBLIC is revoked; the check catches effective CONNECT privileges
and requires the operator to resolve them.

This is **human-run operations work subject to the db-schema-write gate**;
review the dry run and obtain that gate before executing against a database.
It is not part of deployment or boot automation. These examples are operator
instructions, not commands run as part of implementing this feature:

```bash
# Offline SQL preview: no psql, docker, cluster or password required.
bash backend/deploy/db/provision-gamestate.sh foo --dry-run
bash backend/deploy/db/provision-gamestate.sh foo --connection-limit 20 \
  --revoke-public-connect --dry-run

# After the gate: enter the new role password privately, without shell tracing.
set +x
read -r -s -p 'New game role password: ' GAMESTATE_ROLE_PASSWORD; printf '\n'
export GAMESTATE_ROLE_PASSWORD

# Compose: local administrative connection inside the game Postgres container.
# -e passes the exported environment variable by name, never its value in argv.
PSQL_CMD='docker exec -i -e GAMESTATE_ROLE_PASSWORD rpg-postgres-game psql -U game -d postgres' \
  bash backend/deploy/db/provision-gamestate.sh foo --revoke-public-connect

# Kubernetes: kubectl exec has no environment-forwarding flag. Use a local
# wrapper which sends a shell-quoted export and then SQL over exec's stdin.
# Install this wrapper locally outside the repo; it stores no password value.
cat > /tmp/game-psql <<'WRAPPER'
#!/usr/bin/env bash
set +x
set -euo pipefail
{
  printf 'export GAMESTATE_ROLE_PASSWORD=%q\n' "$GAMESTATE_ROLE_PASSWORD"
  printf 'exec psql -U game -d postgres'
  printf ' %q' "$@"
  printf '\n'
  cat
} | kubectl --context k3d-rpg-dev -n rpg-k8s-data exec -i postgres-game-0 -- bash -s
WRAPPER
chmod 700 /tmp/game-psql
PSQL_CMD=/tmp/game-psql bash backend/deploy/db/provision-gamestate.sh foo \
  --revoke-public-connect
unset GAMESTATE_ROLE_PASSWORD
```

Adjust container, Kubernetes context/namespace, and administrative user for the
target instance. The Kubernetes wrapper requires Bash in the stock PostgreSQL
image and sends the export and SQL only over stdin; do not use a command
argument such as `env GAMESTATE_ROLE_PASSWORD=value` with `kubectl exec`.
`PSQL_CMD` defaults to `psql`; it is split on whitespace without shell evaluation.
Use a wrapper for arguments or paths needing quoting, and never include a
password or password-bearing connection string in it. Administrative
authentication should use the existing local connection or standard PostgreSQL
environment/passfile facilities, not a constructed password-bearing DSN.

The role password is required as exported `GAMESTATE_ROLE_PASSWORD` except in
dry-run mode. psql 15+ reads it with `\getenv`; the script never substitutes its
value into SQL text, argv, output, or the dry-run preview. It disables psql
startup files and SQL echo, and uses superuser-only session settings to turn
off stock PostgreSQL statement, error-statement and duration logging before
reading the password. Use the stock instance logging configuration; any added
SQL auditing must likewise exclude password statements. psql stderr is
suppressed because error diagnostics can contain interpolated SQL; failures
give a generic message plus any non-secret isolation diagnostics on stdout.

After success, the script prints this **shape**, with placeholders only:

```text
postgres://gamestate_foo:<password>@<host>:5432/gamestate_foo?sslmode=...
```

Complete the DSN privately (URL-encode password characters as needed), choose
the target host and TLS policy, and store it in **that game's fleet Secret,
key `game-db-url`**. Never commit the completed DSN or password. No database
provisioning, Secret creation, Docker startup, or cluster operation is performed
by the offline test `db/tests/provision_test.sh`, which uses a fake psql only.

### Default game's restricted login (MG-01)

The default game `cuvara` no longer connects with the PostgreSQL bootstrap
superuser. Its game servers use the non-superuser login **`gamestate`**, which
owns only the database `gamestate`. The bootstrap user (`POSTGRES_GAME_USER`,
`game` by default) is for provisioning, backups/restores and administrative
migrations only; it appears in no game server DSN the compose, k8s and
Agones manifests build. In CD's `DEPLOY_MODE=host`, `GAME_DB_URL` is the game
server's own DSN, an operator-set secret this repo cannot change: it must name
`gamestate` (see `VPS-SETUP.md`). The role name is fixed:
`POSTGRES_GAME_APP_USER` must be `gamestate`.

`db/provision-default-role.sh` converges an **existing** `gamestate` database
in place. It needs an administrative (superuser) connection, creates the role
if absent and reapplies its attributes on every run (LOGIN, NOSUPERUSER,
NOCREATEDB, NOCREATEROLE, NOREPLICATION, NOINHERIT, NOBYPASSRLS, 20
connections; `--connection-limit N` overrides, also applied to the database),
and sets its password from exported `GAMESTATE_ROLE_PASSWORD` (read by psql
with `\getenv`, never in argv). It then:

- runs `ALTER DATABASE gamestate OWNER TO gamestate`, revokes CONNECT and
  TEMPORARY from PUBLIC on it and grants CONNECT to the role;
- inside `gamestate`, transfers the owner of every non-system schema,
  relation, standalone type/domain and routine from the bootstrap user to the
  role. No database is dropped, recreated or copied and `REASSIGN OWNED` is not
  used, so existing data stays in place;
- revokes PUBLIC CONNECT on every other non-template database the role could
  reach, as `provision-gamestate.sh --revoke-public-connect` does. **Other
  roles relying on PUBLIC lose new connections there;** existing connections
  are not terminated;
- sets default privileges so tables and sequences the **bootstrap user** later
  creates in `public` (an admin-run migration) grant ALL to `gamestate`;
- fails (exit 1) if the role can still CONNECT to any other database or is a
  member of any role. A missing `gamestate` database is an error: this upgrades
  an existing volume, it does not create one. `--dry-run` prints the SQL and
  needs no password or psql. It refuses to run if the admin login is itself
  `gamestate`.

Because the script is idempotent, it can run on every deploy.

**How an existing volume upgrades**

- *Compose:* the one-shot `postgres-game-roles` service runs the script as the
  bootstrap user on every `up`, and the game servers wait for it to complete.
  Set `POSTGRES_GAME_APP_PASSWORD` in `.env` (see `.env.example`).
- *Kubernetes:* run the manual Job `default-role`
  (`k8s/data/job-default-role.yaml`) before starting game servers, on fresh and
  reused PVCs. `k8s/data/apply.sh` generates its script ConfigMap
  `gamestate-default-role` from the original script. Put the password in the
  `postgres-game` Secret as `GAMESTATE_ROLE_PASSWORD` (and `GAMESTATE_APP_USER:
  gamestate`); use the same password in the app tier's `game-db-url`. See
  `k8s/data/README.md`.
- *Bare host:* run `provision-default-role.sh` yourself under the
  db-schema-write gate, with `GAMESTATE_ROLE_PASSWORD` exported and `PSQL_CMD`
  pointing at an administrative connection, as for `provision-gamestate.sh`
  above. The script never runs from server startup.

Then point `GAME_DB_URL` at `postgres://gamestate:<password>@<host>:5432/gamestate?sslmode=...`.

**Migrations.** The game server's own `Migrator` runs as `gamestate`, so its
objects are owned by it. A migration run as the bootstrap user (for example
`--migrate-only` with the bootstrap DSN) leaves new objects owned by the
bootstrap user; the default privileges above give `gamestate` ALL on new
`public` tables and sequences, but other object kinds need a rerun of the
script. CD's `--migrate-only` step uses `GAME_DB_URL`. In `DEPLOY_MODE=host`
that is also the game server's DSN, so it must name `gamestate`, which owns
`gamestate` and can migrate it; this is an operator-set secret that this
change documents but cannot change. Running migrations as the bootstrap user
applies only where `--migrate-only` is given its own DSN.

**Isolation, both directions.** From the `gamestate` login, connecting to
`postgres` or to any `gamestate_<game>` database is refused, and the role
holds no SELECT privilege on a per-game table (asserted without the connection gate). From a `gamestate_<game>` login, connecting to
`gamestate` is refused. The bootstrap superuser and the other databases'
owners are unchanged.

**Tests.** The offline test uses a fake psql; the live test runs a private
PostgreSQL 16 only, never the shared stack. Both print `passed: N failed: M`:

```bash
export RPG_FACTORY_WORKSPACE=C:/Workspaces/UnityIndie
bash backend/deploy/db/tests/default_role_test.sh        # offline, fake psql
# live: a private cluster from PG_BIN_DIR (PostgreSQL 16 tools), otherwise a
# throwaway postgres:16.4-alpine container on its own port; exit 77 = skipped
# because neither PG_BIN_DIR nor a reachable Docker daemon exists
PG_BIN_DIR=/usr/lib/postgresql/16/bin bash backend/deploy/db/tests/default_role_live_test.sh
bash backend/deploy/db/tests/default_role_live_test.sh   # Docker
```

The live test builds a populated `gamestate` owned by the bootstrap user (with
per-game `gamestate_ember` data), converges it, checks data survives and the
role can read, write and create tables, checks ownership of every object kind,
the isolation above, and that a rerun changes nothing. It fails on the
pre-MG-01 scripts.

**Rollback.** Point `GAME_DB_URL` back at the bootstrap user and restart the
game servers. Ownership can stay with `gamestate`: the bootstrap superuser
retains access, and the PUBLIC CONNECT revocations are not undone.

## 0. Pull request restore rehearsal

`.github/workflows/restore-drill.yml` runs on pull requests using disposable
PostgreSQL `16.4-alpine` and Redis `7.4-alpine` services, matching the deployed
data-tier images. It applies every numbered game-state migration, inserts sample
player, character, item and grant rows, and writes two sample Redis keys. The
workflow calls `backup.sh`, `redis-backup.sh`, `restore.sh` and
`redis-restore.sh`, restoring PostgreSQL into a separate fresh service and Redis
into a fresh scratch container. It then checks the migrated tables, sample rows,
and exact Redis key values; the synthetic archives are retained as a CI artifact
for seven days.

For service containers and other TCP-accessible PostgreSQL instances, the scripts
accept `DB_CONNECTION_MODE=host` and the standard `PGHOST`, `PGPORT`, `PGUSER`
and `PGPASSWORD` variables. Redis backup accepts `REDIS_CONNECTION_MODE=host`
with `REDIS_HOST` and `REDIS_PORT`; Redis restore accepts `REDIS_IMAGE` to select
the matching scratch image. These modes are opt-in; existing Docker and
Kubernetes modes remain the default.

---

## 1. Migrations

### Layout

```
backend/deploy/db/migrations/gamestate/001_init.sql              ops copy (psql, review)
backend/deploy/db/migrations/gamestate/002_characters.sql        ops copy (psql, review)
backend/gameserver-dotnet/GameServer/Persistence/Migrations/001_init.sql         canonical, embedded
backend/gameserver-dotnet/GameServer/Persistence/Migrations/002_characters.sql   canonical, embedded
```

| Version | Name | Adds |
|---------|------|------|
| 1 | `001_init` | `player_states` (keyed by `user_id`) + `player_states_map_id_idx` |
| 2 | `002_characters` | ADR-31: `character_state` (keyed by `character_id`, adds `z`, `yaw`, `level`, `xp`; index on `user_id`), `character_items` (bag/equipment by `instance_id`, FK to `character_state` `ON DELETE CASCADE`, `quantity > 0`, `container IN ('bag','equipped')`, one item per equipped slot via a partial unique index), `item_grants` (idempotency ledger keyed by `grant_id`, no FK). Expand only: `player_states` untouched and still read as the first-load fallback. Locks: only on its own new tables (the FKs take SHARE ROW EXCLUSIVE on the new, empty `character_state`). |

The gameserver embeds its migrations as assembly resources, so the binary is
self-contained — nothing has to be shipped alongside it. The `deploy/` copies
exist so an operator can read or apply a migration with plain `psql` during an
incident. `MigratorTests.EmbeddedMigrations_MatchDeployCopies` fails the build if
the two ever diverge.

`backend/deploy/db/init-gamestate.sql` is a *first-boot seed only*, mounted into
the container's `/docker-entrypoint-initdb.d/`. It runs once on an empty volume
and must keep describing exactly what `001_init.sql` describes. Do not add
anything to it.

### How they are applied

`schema_migrations` tracks what has run:

```sql
version    integer     PRIMARY KEY
name       text        NOT NULL      -- "001_init"
checksum   text        NOT NULL      -- "sha256:..."
applied_at timestamptz NOT NULL DEFAULT now()
```

Rules the runner enforces:

- **One transaction per migration**, together with its `schema_migrations` row.
  A script that fails halfway leaves no partial schema and no version record, so
  re-running after a fix is safe.
- **Ascending order, exactly once.**
- **Checksums are verified on every run.** Editing a migration that has already
  been applied anywhere fails the boot with a drift error instead of letting
  environments diverge silently. Checksums cover SQL statements, not comments —
  rewording a comment is safe, changing a column type is not.
- **Concurrent runners are serialised** by a PostgreSQL advisory lock, so a whole
  fleet of gameservers can boot simultaneously against one database.
- **A database ahead of the binary is allowed** (warning, not error), so rolling
  back to an older build still starts.

Migrations run in two places, both idempotent:

1. **CD**, in the `db-migrate` job, before the new binaries are installed.
2. **Every gameserver boot**, as a safety net.

### Adding a migration

1. Create `backend/gameserver-dotnet/GameServer/Persistence/Migrations/002_<description>.sql`.
   The `NNN_` prefix is the version and must be unique.
2. Copy it verbatim to `backend/deploy/db/migrations/gamestate/002_<description>.sql`.
3. `cd backend/gameserver-dotnet && dotnet test` — the sync and well-formedness
   tests will catch a mismatch or a bad name.

No csproj edit is needed; `Persistence\Migrations\*.sql` is globbed.

**Write migrations backward compatible.** CD migrates *before* restarting the
servers, so for a short window the new schema runs against the old binary. Use
expand/contract: add a nullable column now, backfill, and only drop the old one
in a later deploy.

**Never edit a shipped migration.** The checksum will reject it everywhere it has
already run. Add a new one.

### Running by hand

```bash
# apply pending migrations and exit (this is what CD runs, with GAME_DB_URL).
# Use the app role gamestate: it owns the database. A bootstrap-user run is an
# administrative exception; see "Default game's restricted login".
gameserver-dotnet --migrate-only --game-db-url 'postgres://gamestate:...@host:5433/gamestate?sslmode=disable'

# from source
cd backend/gameserver-dotnet
dotnet run --project GameServer -- --migrate-only --game-db-url "$GAME_DB_URL"

# what has been applied?
docker exec rpg-postgres-game psql -U game -d gamestate \
  -c "SELECT version, name, applied_at FROM schema_migrations ORDER BY version;"
```

Exit codes: `0` applied or already current, `1` failure (unreachable, script
error, checksum drift), `2` no DSN supplied.

---

## 2. Backups

`backend/deploy/db/backup.sh` dumps both instances through `docker exec`, so no
psql client is needed on the host.

```bash
backend/deploy/db/backup.sh                        # both DBs, keep 7 each
backend/deploy/db/backup.sh --db gamestate         # one instance
backend/deploy/db/backup.sh --dir /tmp/b --keep 3  # custom destination/retention
backend/deploy/db/backup.sh --skip-missing         # containers absent -> warn, exit 0
```

Output — `pg_dump -Fc` (compressed, selectively restorable):

```
$BACKUP_DIR/meta/meta-20260805T073205Z.dump
$BACKUP_DIR/gamestate/gamestate-20260805T073206Z.dump
```

`BACKUP_DIR` defaults to `/var/backups/rpg-mmo`, `BACKUP_KEEP` to 7 per database.
Credentials come from `POSTGRES_USER`/`POSTGRES_DB` and
`POSTGRES_GAME_USER`/`POSTGRES_GAME_DB`.

Each dump is written to `.partial` first and only renamed after `pg_restore
--list` can read it back, so an interrupted or corrupt run never leaves a file
that *looks* like a usable backup. Retention prunes only after a successful dump.

### Scheduling (dev/alpha tier)

```cron
17 3 * * * /opt/rpg-mmo/deploy/db/backup.sh --skip-missing >> /var/log/rpg-backup.log 2>&1
```

CD also takes one before every migration, so deploys are already checkpointed.

### Redis

Redis is a system of record too — it holds the server registry and the event
stream (ADR-4), not a cache — so it gets the same treatment via
`db/redis-backup.sh` (BGSAVE + timestamped RDB + retention) and
`db/redis-restore.sh` (scratch-container rehearsal by default). CD runs the
Redis backup in the same `db-migrate` step. Full procedure, the AOF-vs-RDB trap
that makes a naive restore silently do nothing: **`DISASTER-RECOVERY.md`**.
(There is no registration follow-up any more — game servers rebuild their own
registry entries within one heartbeat interval.)

---

## 3. Restore

`backend/deploy/db/restore.sh` is **destructive** and refuses to run without
`--yes`.

```bash
# rehearsal: restore into a scratch database, live data untouched
backend/deploy/db/restore.sh \
  --file /var/backups/rpg-mmo/gamestate/gamestate-20260805T073218Z.dump \
  --db gamestate --target gamestate_scratch --yes

# real recovery: over the live database
backend/deploy/db/restore.sh --file <dump> --db gamestate --yes
```

Any `--target` other than the live database name is created automatically, which
is what makes rehearsals cheap. The script verifies the archive before touching
anything, stages it inside the container (streamed, so it works on WSL where the
docker CLI cannot see host paths), restores with `--clean --if-exists
--exit-on-error`, and prints row counts per table afterwards.

**Rehearse restores.** A backup nobody has restored is a hypothesis.

---

## 4. Disaster recovery runbook

### A. Game-state database lost or corrupted

Players lose position/HP progress back to the last save; accounts, inventory and
currency live in the meta DB and are unaffected.

1. **Stop writers** so nothing races the restore:
   ```bash
   sudo systemctl stop rpg-gameserver    # host mode
   # or: docker compose --profile realtime stop gameserver
   ```
2. **Take a forensic dump of the damaged database if it still responds** — you
   cannot re-examine it after the restore:
   ```bash
   backend/deploy/db/backup.sh --db gamestate --dir /var/backups/rpg-mmo-forensic
   ```
3. **Pick the newest good archive:**
   ```bash
   ls -lt /var/backups/rpg-mmo/gamestate/
   ```
4. **Rehearse into scratch, and check it:**
   ```bash
   backend/deploy/db/restore.sh --file <dump> --db gamestate --target gamestate_scratch --yes
   docker exec rpg-postgres-game psql -U game -d gamestate_scratch \
     -c "SELECT count(*), max(updated_at) FROM player_states;"
   ```
   The row count and `max(updated_at)` tell you how much progress the restore
   costs. If it is older than expected, try the previous archive.
5. **Restore for real:**
   ```bash
   backend/deploy/db/restore.sh --file <dump> --db gamestate --yes
   ```
6. **Rerun `provision-default-role.sh`** before migrations or any server start.
   `restore.sh` restores as the bootstrap user with `--no-owner --no-privileges`,
   so every restored object is owned by `game` and carries no grants for
   `gamestate`; the script moves ownership and grants back to the app role.
   - compose: `docker compose up postgres-game-roles`
   - k8s: delete and re-apply Job `default-role` (`k8s/data/`)
   - host: run `db/provision-default-role.sh` with `PSQL_CMD` pointing at an
     administrative connection and `GAMESTATE_ROLE_PASSWORD` exported
7. **Re-apply migrations** — the dump is from whenever it was taken, the binary
   may be newer:
   ```bash
   /opt/rpg-mmo/bin/gameserver-dotnet --migrate-only --game-db-url "$GAME_DB_URL"
   ```
8. **Start the servers, verify, then drop the scratch DB:**
   ```bash
   sudo systemctl start rpg-gameserver
   /opt/rpg-mmo/bin/smoketest        # end-to-end through the gateway
   docker exec rpg-postgres-game psql -U game -d postgres -c "DROP DATABASE gamestate_scratch;"
   ```

### B. Meta database lost

This is the serious one — accounts, wallets and leaderboards live here.

1. Stop Nakama: `docker compose stop nakama`.
2. Restore: `backend/deploy/db/restore.sh --file <meta dump> --db meta --yes`.
3. Start Nakama. It runs `nakama migrate up` itself on start, so a dump from an
   older Nakama version is upgraded automatically.
4. Verify the console at `:7351` and check a known account.

### C. Migration failed during deploy

The `db-migrate` job failed, so `deploy` never ran and the **old binaries are
still serving**. The failed migration rolled back — the schema is exactly as it
was.

1. Read the job log for the failing version and the postgres error.
2. Confirm the schema is intact:
   ```bash
   docker exec rpg-postgres-game psql -U game -d gamestate \
     -c "SELECT version, name, applied_at FROM schema_migrations ORDER BY version;"
   ```
3. Fix the migration **in the same numbered file** (it never committed, so its
   checksum was never recorded) and redeploy.
4. If it half-succeeded in a way the transaction could not undo — only possible
   with non-transactional statements such as `CREATE INDEX CONCURRENTLY` — restore
   from the backup the same job took immediately beforehand.

### D. Checksum drift on boot

```
migration 1 (001_init) was modified after it was applied: database recorded
sha256:..., binary carries sha256:...
```

Someone edited a shipped migration. **Do not** hand-patch `schema_migrations` to
make the error go away — that hides a genuine difference between environments.

1. `git log -p` the migration file and revert the edit.
2. Ship the intended change as a new numbered migration.
3. Only if the recorded checksum is known-wrong (e.g. it predates the checksum
   scheme) update that one row deliberately, after confirming the live schema
   matches the file.

---

## 5. CD integration

The `db-migrate` job runs between `bundle` and `deploy`:

```
bundle ──► db-migrate ──► deploy ──► post-deploy-smoke ──► summary
           │
           ├─ backup.sh --skip-missing      (both DBs; first-ever deploy has no containers)
           └─ gameserver-dotnet --migrate-only   (skipped when GAME_DB_URL is unset)
```

Why this order:

- **Backup before migrate** — a backup taken after a bad migration is useless.
- **Migrate before deploy** — the schema is current before any new binary starts,
  so no server ever serves against a schema it does not understand. The tradeoff
  is the backward-compatibility requirement described above.
- **Deploy depends on db-migrate** — a failed migration stops the deploy with the
  old version still running, rather than restarting servers onto a broken schema.

Relevant environment settings:

| Setting | Kind | Default | Purpose |
|---------|------|---------|---------|
| `GAME_DB_URL` | var | *(unset)* | Game-state DSN. Unset ⇒ migration skipped, gameserver uses the memory store. |
| `BACKUP_DIR` | var | `$RPG_DEPLOY_DIR/backups` | Where CD writes dumps. The script itself defaults to `/var/backups/rpg-mmo`; CD overrides it because that path is root-only and CD does not run as root. |
| `BACKUP_KEEP` | var | `7` | Archives kept per database. |
| `POSTGRES_USER` / `POSTGRES_DB` | var | `nakama` | Meta credentials for `pg_dump`. |

The gameserver still migrates at boot, so skipping this job degrades determinism
and loses the backup checkpoint — it does not break correctness.
