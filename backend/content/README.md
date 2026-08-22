# Game content

Game data lives here as JSON. This directory is the **source of truth** for every content
value the simulation uses — item stats, and whatever content types follow.

## The loop

```
edit items.json  ->  restart the game server  ->  clients pull the new set
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
