# Changelog

All notable changes to the Shared.GameLogic library will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Added

- **Combat golden vector edge cases (ADR-10).** 13 new golden vectors in
  `GoldenVectors/combat.json` covering boundary conditions: range boundary,
  cooldown boundary, simultaneous kill, zero/negative damage floor,
  max stat overflow, and death with int.MinValue HP.
