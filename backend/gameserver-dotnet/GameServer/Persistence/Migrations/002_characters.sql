-- 002_characters — character state, bag/equipment and item-grant idempotency (ADR-31).
--
-- CANONICAL COPY. This file is embedded into the gameserver binary and applied
-- by Migrator.ApplyAsync. Ops copy that must stay in sync:
--   backend/deploy/db/migrations/gamestate/002_characters.sql  (psql / manual use)
-- MigratorTests.EmbeddedMigrations_MatchDeployCopies asserts they match.
-- init-gamestate.sql is NOT touched: it describes 001 only.
--
-- Expand only (expand/contract, CD migrates before the new binary starts):
-- three NEW tables, nothing altered or dropped. player_states is left exactly
-- as 001 made it and stays readable — a character's first load falls back to
-- the account's player_states row until protocol 2 is retired (ADR-31.2).
-- An old binary never reads these tables, so it runs unchanged on this schema.
--
-- Locks: CREATE TABLE / CREATE INDEX on tables created in this same
-- transaction. The two REFERENCES clauses take SHARE ROW EXCLUSIVE on
-- character_state, which is also new and empty here, so nothing a running
-- server touches (player_states, schema_migrations aside) is locked.
--
-- Single writer (ADR-1/ADR-31.3): the game server hosting the character is the
-- only writer of all three tables.

-- One row per character, keyed by the roster character id Nakama minted (the
-- `cid` claim). user_id is denormalised for the ownership check on load/save
-- and for "all characters of this account" lookups.
CREATE TABLE IF NOT EXISTS character_state (
    character_id text        PRIMARY KEY,
    user_id      text        NOT NULL,
    map_id       text        NOT NULL DEFAULT '',
    x            real        NOT NULL DEFAULT 0,
    y            real        NOT NULL DEFAULT 0,
    z            real        NOT NULL DEFAULT 0,
    yaw          real        NOT NULL DEFAULT 0,
    hp           integer     NOT NULL DEFAULT 0,
    max_hp       integer     NOT NULL DEFAULT 0,
    level        integer     NOT NULL DEFAULT 1,
    xp           bigint      NOT NULL DEFAULT 0,
    updated_at   timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS character_state_user_id_idx ON character_state (user_id);

-- Bag and equipment, one row per item instance. Written through at grant /
-- consume / move time in one transaction (ADR-6, ADR-31.4), never by the 30 s
-- sweep.
--
-- ON DELETE CASCADE: an item instance has no meaning without its character, and
-- the contract step that eventually deletes characters (roster delete ->
-- game-state cleanup) should not have to know about every child table.
-- item_grants deliberately has NO foreign key (below).
CREATE TABLE IF NOT EXISTS character_items (
    instance_id  text        PRIMARY KEY,
    character_id text        NOT NULL REFERENCES character_state (character_id) ON DELETE CASCADE,
    item_id      text        NOT NULL,
    quantity     integer     NOT NULL CHECK (quantity > 0),
    container    text        NOT NULL CHECK (container IN ('bag', 'equipped')),
    slot         text,
    bag_index    integer,
    granted_by   text,
    updated_at   timestamptz NOT NULL DEFAULT now(),
    -- An equipped item names its slot; a bag item has none.
    CONSTRAINT character_items_slot_matches_container CHECK (
        (container = 'equipped' AND slot IS NOT NULL) OR (container = 'bag' AND slot IS NULL)
    )
);

CREATE INDEX IF NOT EXISTS character_items_character_id_idx ON character_items (character_id);

-- At most one item per equipment slot per character.
CREATE UNIQUE INDEX IF NOT EXISTS character_items_equipped_slot_uq
    ON character_items (character_id, container, slot)
    WHERE container = 'equipped';

-- Idempotency ledger for item operations keyed by a grant id (grant, consume).
-- A replayed grant id is a no-op. No foreign key on purpose: the ledger must
-- outlive the item rows it produced (a consumed stack is deleted, its grant id
-- must still read as applied) and must not cascade away with a character.
-- kind / instance_id let a replay report what the original did.
CREATE TABLE IF NOT EXISTS item_grants (
    grant_id     text        PRIMARY KEY,
    character_id text        NOT NULL,
    kind         text        NOT NULL DEFAULT 'grant' CHECK (kind IN ('grant', 'consume')),
    instance_id  text,
    applied_at   timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS item_grants_character_id_idx ON item_grants (character_id);
