-- Game state schema (PostgreSQL "Game State" instance — NOT the Nakama meta DB).
--
-- SINGLE SOURCE OF TRUTH FOR THE SCHEMA IS THIS FILE.
-- It is embedded into the binary (see migrate.go, go:embed) and applied on
-- startup by PostgresPlayerStore.Migrate. A byte-identical copy lives at
-- backend/deploy/db/init-gamestate.sql so a fresh docker-compose volume is
-- initialised even before any gameserver connects. Keep the two in sync —
-- pgstore_test.go asserts they match.
--
-- Every statement MUST be idempotent: Migrate runs on every boot.

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
