# Game content

Game data lives here as JSON. This directory is the **source of truth** for every content
value the simulation uses — item stats, and whatever content types follow.

## Files

| File | Required | Key | Served at `/content` | What |
|---|---|---|---|---|
| `items.json` | **yes** | `items` | yes | Item definitions |
| `abilities.json` | no | `abilities` | yes | Abilities (protocol 3 effect lists; legacy form still accepted) |
| `stats.json` | no | `stats` | yes | Content stats: the replicated stat block (ADR-30) |
| `statuses.json` | no | `statuses` | yes | Status effects: DoT/HoT, modifiers, crowd control (ADR-30) |
| `loot.json` | no | `loot` | **never** | Loot tables. Server-only: loot is a server-side roll |
| `maps/<map_id>.json` | no | — | no | Map geometry for one map (ADR-28) |

Each top-level key may be defined in **one** file only; defining `stats` in two files refuses
the boot (which one wins would depend on file order). A `"$comment"` key is ignored anywhere.

> **PLACEHOLDER NUMBERS.** `abilities.json`, `stats.json`, `statuses.json`, `loot.json` and
> `maps/dev_arena.json` exist so the Core v3 vertical slice runs end to end. Every number in
> them — power, cooldowns, durations, drop chances, despawn times, geometry — is a placeholder
> pending game design. Gameplay numbers are a human decision; do not balance against these.

**What clients get.** With only `items.json` present the file is served byte for byte, as
before. With more files the server serves ONE composed document,
`{"items":…,"abilities":…,"stats":…,"statuses":…}`, whose values are the source files' JSON
token streams (comments and whitespace dropped, nothing re-modelled). The hash is over the
served bytes. `loot` is never in it.

## The loop

```
edit a content file  ->  restart the game server  ->  clients pull the new set
```

No rebuild. No `sgl-v` tag. No `manifest.json` or `packages-lock.json` bump. That is the
whole reason content lives here rather than inside `Shared.GameLogic` — see
[ADR-19](../docs/ARCHITECTURE-DECISIONS.md).

## What happens when you get it wrong

The server **refuses to start**, and prints every problem in one pass:

```
crit: Content in '/srv/content/items.json' is invalid — 4 problems:
        - item 'ok_item': rarity 'mythic' is not recognised. Valid: common, uncommon, rare, epic, legendary.
        - item 'Iron_Sword': id may contain only lowercase letters, digits and underscores. ...
        - item 'Iron_Sword': name is empty. It is what the player sees.
        - item 'Iron_Sword': is equippable (Weapon) but stackMax is 4. Equipment must not stack: ...
      The server will not start on content it cannot vouch for.
```

One restart clears every fault, rather than one restart per typo. A server that booted on
content it could not parse would serve some unknowable subset of the intended game, and
every downstream symptom would be blamed on whichever system noticed first.

## Rules

Enforced by `Shared.GameLogic/Content/ContentValidation.cs`, shared with the client:

| Rule | Why |
|---|---|
| `id` is lowercase letters, digits, underscore; unique; ≤ 64 chars | Ids appear in URLs, file names and log lines. A mixed-case id compares unequal to itself across those surfaces |
| `stackMax` ≥ 1 | An item that cannot occupy one slot cannot exist |
| Anything with a `slot` has `stackMax: 1` | There is no rule for which copy of a stack is the one being worn |
| No negative `attack`, `defense`, `levelRequirement` | Not supported by the combat maths |
| `name` non-empty, ≤ 128 chars | It is what the player sees |

`slot`: `none`, `weapon`, `head`, `chest`, `legs`, `trinket`
`rarity`: `common`, `uncommon`, `rare`, `epic`, `legendary`

Spell them out. A numeric `"slot": "3"` is refused deliberately — accepting it would make
the content file depend on enum declaration order, which a reordering would silently change.

## `id` is permanent

Inventories, loot tables and saved rows store the **id** and nothing else. Renaming `name`
is always safe. Renaming an `id` silently repoints every stored copy of that item, and there
is nothing to detect it afterwards.

Retire an item by removing it and never reusing the id.

## How the server finds this directory

`--content-dir`, or `CONTENT_DIR`, defaulting to `../../content` so `dotnet run` from
`gameserver-dotnet/` works with no flags. Deployments set `CONTENT_DIR` to the path baked
into the image.

## How clients get it

`GET /content` on the game server's metrics port, beside `/metrics`, `/healthz` and
`/status`. The response carries the content hash in both `ETag` and `X-Content-Hash`; a
client that sends `?hash=<what it has>` gets `304 Not Modified` and no body.

```bash
curl -s -D- http://127.0.0.1:9100/content | head -5
curl -s -o /dev/null -D- "http://127.0.0.1:9100/content?hash=ceb2ad305246e76d"   # 304
```

Both headers carry the hash because `UnityWebRequest` and several proxies rewrite or strip
`ETag` — a client that cannot read back its hash cannot ask for a 304, and every join
silently becomes a full download.

## No hot reload

`ContentDatabase` is immutable for the life of the process. Changing the rules underneath a
running simulation would make every desync unreproducible, so a content change means a
restart.

## Abilities (`abilities.json`)

Protocol 3 form (ADR-30): a **delivery** and an ordered list of **effects**.

```json
{ "id": 1, "name": "Fire Bolt", "delivery": "projectile",
  "effects": [ { "kind": "damage", "power": 15 }, { "kind": "apply_status", "statusId": 1 } ],
  "projectile": { "speed": 20, "radius": 0.3, "range": 25 },
  "cooldownTicks": 45 }
```

| Key | Meaning |
|---|---|
| `id` | Permanent, ≥ 1 (a proto3 zero is elided on the wire) |
| `name` | What the player sees |
| `delivery` | `self` · `entity` (named target, needs `range`) · `ground` (aim point, needs `range` and `radius`) · `projectile` (needs the `projectile` block) |
| `effects[]` | In order. `{"kind":"damage","power":N}` · `{"kind":"heal","power":N}` · `{"kind":"apply_status","statusId":N}` |
| `projectile` | `speed` (units/s), `radius` (sphere), `range` (units travelled). Only for `projectile` |
| `range`, `radius` | World units. Range is how far the caster may place / reach; radius the area size |
| `cooldownTicks` | **Base simulation ticks** (60 per second at the default critical rate), never ms |

Damage is `caster attack + power - target defense` (minimum 1), with attack and defense read
through active statuses. Ground and projectile deliveries affect entities of a **different
type** than the caster (players hit mobs, mobs hit players), never the caster — a placeholder
rule until there is a faction design. The legacy form (`targeting` + `effect` + `power`) still
loads as a one-effect ability; mixing the two forms in one ability is refused.

## Stats (`stats.json`)

```json
{ "id": 2, "key": "mana", "default": 100 }
```

`id` permanent and ≥ 1; `key` lowercase letters, digits, underscores, unique; `default` the
value every actor (player, mob, npc, boss) spawns with. The stat block replicates in
`EntitySnapshot.stats`. The stat keyed **`level`** is persisted as `character_state.level`.
Attack, defense, speed and max HP are NOT content stats; statuses modify them directly.

## Statuses (`statuses.json`)

```json
{ "id": 1, "key": "burning", "durationTicks": 180, "maxStacks": 3,
  "periodic": { "kind": "damage", "intervalTicks": 60, "amount": 4 },
  "modifiers": [ { "target": "defense", "multiplierPermille": -200 } ],
  "crowdControl": [ "slow" ], "slowPermille": 300 }
```

| Key | Meaning |
|---|---|
| `durationTicks` | Required. Base ticks; `0` = until removed (death removes everything) |
| `maxStacks` | Default 1. Re-applying adds a stack up to this, and refreshes the duration |
| `periodic` | Optional DoT/HoT: `kind` `damage`/`heal`, `intervalTicks` ≥ 1, `amount` ≥ 1 per stack. First tick one interval after applying; a tick due on the expiry tick still happens. DoT damage is applied as written (no defense) |
| `modifiers[]` | `target`: `attack` · `defense` · `speed` (multiplier only) · `max_hp` · `stat` (with `statId`). `add` flat per stack; `multiplierPermille` a DELTA per stack (+200 = ×1.2) |
| `crowdControl` | Any of `stun` (no move, cast, attack) · `root` (no move) · `silence` (no cast) · `slow` (with `slowPermille` 1..1000; strongest slow wins) |

## Loot (`loot.json`, server-only)

```json
{ "id": "mob_default", "entityType": "mob", "despawnTicks": 3600,
  "entries": [ { "itemId": "wolf_pelt", "chancePermille": 600, "min": 1, "max": 2 } ] }
```

One table per entity type (`entityType` is `EntityState.Type`: `mob`, `npc`, `boss`, ...).
Each entry is rolled **independently**: `chancePermille` 1..1000, quantity uniform in
`[min, max]` with `max` ≤ the item's `stackMax`. A drop lies in the world as an `item` entity
for `despawnTicks` base ticks. `itemId` must exist in `items.json`. Rolls are deterministic per
server (seeded, never the wall clock).

## Map geometry (`maps/<map_id>.json`)

Loaded at boot for the server's `--map-id`. **Absent:** the map is the flat protocol 2 world of
`--map-width` × `--map-height`. **Present but invalid:** the server refuses to start and prints
every problem. Shared rules: `Shared.GameLogic/World/MapGeometryValidation.cs` (the client
validates the same file with the same rules). Example: `maps/dev_arena.json`.

```json
{
  "bounds": { "minX": -20, "minY": -20, "maxX": 20, "maxY": 20 },
  "heightfield": { "originX": -20, "originY": -20, "cellSize": 20, "columns": 3, "rows": 3,
                   "heights": [0, 0, 0,  0, 0, 0,  0, 0, 2] },
  "boxes":   [ { "minX": -4, "minY": 4, "minZ": 0, "maxX": -2, "maxY": 6, "maxZ": 0.3 } ],
  "spawns":  [ { "name": "default", "x": 0, "y": -10, "z": 0 } ],
  "portals": [ { "name": "to_map_01", "x": 15, "y": -15, "z": 0, "radius": 1.5, "height": 3,
                 "targetMapId": "map_01", "targetSpawn": "default" } ]
}
```

| Key | Meaning |
|---|---|
| `bounds` | Required. The play area; positions are clamped into it. Replaces `--map-width/--map-height` |
| `heightfield` | Optional terrain: a regular grid, row-major (`index = row * columns + column`), sample (0,0) at (`originX`, `originY`), bilinear between samples, height 0 outside it. At least 2 × 2, at most 1025 × 1025 |
| `boxes[]` | Static axis-aligned colliders (at most 4096). Every axis needs a thickness. A box top within 0.4 units of the feet is a step; higher blocks |
| `spawns[]` | Named points (`z` defaults to 0), inside the bounds, unique names. **`default`** is where new players and respawns appear |
| `portals[]` | Named cylinders (`radius`, `height`) with a target map and spawn. Loaded and validated; not triggered yet |

Axes: x/y are the ground plane, z is up (a Unity renderer maps `(x, y, z)` to `(x, z, y)`).
Map ids that are not plain file names (letters, digits, `_`, `-`, `.`) are never read from disk.
