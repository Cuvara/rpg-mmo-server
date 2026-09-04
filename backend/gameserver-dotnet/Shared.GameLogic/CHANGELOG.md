# Changelog

All notable changes to the Shared.GameLogic library will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Added

- **Combat golden vector edge cases (ADR-10).** 13 new golden vectors in
  `GoldenVectors/combat.json` covering boundary conditions that were previously
  untested:
  - **Range boundary**: `attack_range_plus_epsilon` (range + 0.001, should fail)
  - **Cooldown boundary**: `attack_one_tick_before_cooldown_expires` (tick = cd - 1,
    should fail), `attack_tick_zero_no_cooldown` / `attack_tick_zero_with_cooldown`
    (ulong zero-boundary guard)
  - **Simultaneous kill**: `simkill_both_die_hp1`, `simkill_both_die_asymmetric`,
    `simkill_target_survives_high_defense` (attacker + target exchange lethal blows)
  - **Zero/negative damage**: `damage_zero_raw_floors_to_min` (0 atk, 0 def),
    `damage_negative_raw_floors_to_min` (1 atk, 100 def)
  - **Max stat overflow**: `damage_max_attack_zero_defense`,
    `damage_zero_attack_max_defense`, `damage_max_attack_max_defense`
    (int.MaxValue boundary — subtraction must not wrap)
  - **Death edge**: `death_hp_min_int_clamps_to_zero` (int.MinValue HP clamps to 0)
