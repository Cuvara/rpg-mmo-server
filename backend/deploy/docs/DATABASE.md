# Database Operations

Schema migrations, backups, and disaster recovery for the two PostgreSQL
instances.

| Instance | Container | Default port | Owner | Who manages the schema |
|----------|-----------|--------------|-------|------------------------|
| Meta | `rpg-postgres` | 5432 | `nakama` / `nakama` | **Nakama** — it runs `nakama migrate up` at container start. Never hand-edit it. |
| Game state | `rpg-postgres-game` | 5433 | `game` / `gamestate` | **Us** — numbered migrations in this repo. |

Everything below is about the game-state instance. The meta instance is only
ever backed up and restored, never migrated by us.

---

## 1. Migrations

### Layout

```
backend/deploy/db/migrations/gamestate/001_init.sql              ops copy (psql, review)
backend/gameserver-dotnet/GameServer/Persistence/Migrations/001_init.sql   canonical, embedded
```

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
# apply pending migrations and exit (this is what CD runs)
gameserver-dotnet --migrate-only --game-db-url 'postgres://game:...@host:5433/gamestate?sslmode=disable'

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
6. **Re-apply migrations** — the dump is from whenever it was taken, the binary
   may be newer:
   ```bash
   /opt/rpg-mmo/bin/gameserver-dotnet --migrate-only --game-db-url "$GAME_DB_URL"
   ```
7. **Start the servers, verify, then drop the scratch DB:**
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
