# Changelog

All notable changes to the Shared.GameLogic library.

Format: [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Fixed

- **`simkill_target_survives_high_defense` did neither: the target did not survive and there
  was no high defence.** It was generated with attack 10 against defence **0** on a 2 HP
  target, so the target died — making it a duplicate of `simkill_both_die_hp1` under a name
  promising the opposite outcome. All three `simultaneous_kill` vectors were "both die", so
  the asymmetric case was covered by **nothing**, while a reader scanning names would
  reasonably believe it was covered.

  Regenerated with defence 9 against attack 10, which is 1 damage (exactly `MinDamage`, so
  not clamped) into 5 HP: the target lives at 4 and swings back for 10 into a 1 HP attacker.
  The name is now what the case does, and the outcome one entity dead and one alive is
  covered for the first time.

  Found by the client-side runner when `sgl-v0.4.0` delivered these vectors to a client for
  the first time — they had sat unreleased since `4eb0ba5`. The values are produced by
  running `CombatLogic`, not written by hand, so the expectations are derived rather than
  asserted.

## [0.4.0] — 2026-09-09

Released as `sgl-v0.4.0`. The client pins this library by exact tag, so this
version exists because the Unity client needs API that `sgl-v0.3.1` does not
have — `EntityAction` and the widened `EntitySnapshotData` constructor.

### Added

- **`EntityAction`** — a coarse, level-triggered description of what an entity is
  doing, for a renderer to choose an animation from. Mirrors the `EntityAction` enum
  in `shared/proto/wire.proto`; **the numeric values are on the wire and are FROZEN**.

  **Zero is reserved for "not sent" and `Idle` is 1.** proto3 elides a zero enum, so
  making idle the zero value would put "this entity is standing still" and "this
  sender does not know about actions" on the wire as identical bytes — the same
  ambiguity `EntitySnapshotData.Speed` has to document its way around, avoidable here
  for free. `EntityType` already reserves zero this way, so this follows the
  established idiom rather than inventing a rule.

  Level-triggered, not edge-triggered: it says what state an entity is in, not that a
  state was entered. A renderer needing to retrigger the same action twice in a row
  cannot get that edge from this value alone; that needs a sequence number, which is
  an animation-system concern and is deliberately out of scope.

- **Per-entity facing and action on `EntityState` and `EntitySnapshotData`.** The
  snapshot constructor widens to nine arguments. **This is a source-breaking change
  for any caller using the positional constructor**, which is why this is a minor
  bump and not a patch.

  The wire *encoding* of facing (16-bit binary radians, biased so wire zero stays
  reserved) is deliberately NOT in this library — it lives at the wire layer. What is
  shared here is the value and its meaning, per the ADR-10 boundary: this library
  holds pure data and simulation rules, not transport concerns.

- **Golden vectors for AOI and snapshot merging** (`aoi.json`, `snapshot_merger.json`),
  plus the combat edge cases below.

  **Known gap, recorded rather than fixed:** the Unity side does not currently replay
  `snapshot_merger.json` — it loads only the vec2, movement, combat and validation
  fixtures. So for that file "shared golden vectors" is half true: the server is gated
  by it and the client is not. Widening the client loader was out of scope for the
  change that added it; until that happens, the merger contract has no client-side gate.

- **Combat golden vector edge cases (ADR-10).** 13 new vectors in combat.json.
