-- Game state schema (PostgreSQL "Game State" instance — NOT the Nakama meta DB).
--
-- FIRST-BOOT SEED ONLY. Mounted into the postgres-game container's
-- /docker-entrypoint-initdb.d/, so it runs once on an empty data volume.
--
-- The authoritative schema history lives in numbered migrations:
--   backend/deploy/db/migrations/gamestate/NNN_*.sql          (ops copies, psql-friendly)
--   backend/gameserver-dotnet/GameServer/Persistence/Migrations/  (embedded, applied by the server)
--
-- This file must describe exactly what 001_init.sql describes, or a fresh volume
-- and a migrated volume would disagree. Two tests enforce that:
--   MigratorTests.InitGamestateSql_MatchesFirstMigration (C#, compares statements)
--   TestSchemaMatchesDeployInitScript (Go, byte-compares this file against the
--     orphaned shared/storage/pgstore/schema.sql — keep those two byte-identical)
--
-- DO NOT add tables here. Add a new numbered migration instead; the workflow is
-- documented in backend/deploy/docs/DATABASE.md.
--
-- Every statement MUST be idempotent.

CREATE TABLE IF NOT EXISTS player_states (
    user_id    text        PRIMARY KEY,
    map_id     text        NOT NULL DEFAULT '',
    x          real        NOT NULL DEFAULT 0,
    y          real        NOT NULL DEFAULT 0,
    hp         integer     NOT NULL DEFAULT 0,
    max_hp     integer     NOT NULL DEFAULT 0,
    updated_at timestamptz NOT NULL DEFAULT now()
);

-- Lookup of "who was last on this map" for map-transfer / respawn tooling.
CREATE INDEX IF NOT EXISTS player_states_map_id_idx ON player_states (map_id);
