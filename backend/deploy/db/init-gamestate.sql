-- Game state schema (PostgreSQL "Game State" instance — NOT the Nakama meta DB).
--
-- This file initialises a fresh postgres-game volume (mounted into the
-- container's /docker-entrypoint-initdb.d/) before any gameserver connects.
--
-- The same statements are duplicated as PostgresPlayerStore.SchemaSql in
-- backend/gameserver-dotnet/GameServer/Persistence/PostgresPlayerStore.cs and
-- applied by PostgresPlayerStore.MigrateAsync on every server boot. Keep the two
-- in sync — PostgresPlayerStoreTests.SchemaSql_MatchesInitGamestateSql asserts
-- they match.
--
-- (The Go shared/storage/pgstore package that used to embed this schema via
-- go:embed is orphaned since the C# gameserver migration.)
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
