-- 001_init — initial game-state schema.
--
-- CANONICAL COPY. This file is embedded into the gameserver binary and applied
-- by Migrator.ApplyAsync. Ops copies that must stay in sync:
--   backend/deploy/db/migrations/gamestate/001_init.sql  (psql / manual use)
--   backend/deploy/db/init-gamestate.sql                 (postgres initdb seed)
-- MigratorTests.EmbeddedMigrations_MatchDeployCopies asserts they match.
--
-- Applied migrations are checksummed. Editing this file after it has shipped
-- will fail every server boot with a drift error — add 002_*.sql instead.
--
-- Statements are IF NOT EXISTS so this applies cleanly to volumes that were
-- already seeded by init-gamestate.sql before migrations existed.

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
