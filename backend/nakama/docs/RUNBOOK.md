# Nakama Module — Runbook

> Stub. Expand as economy/leaderboard/social land.

## Deploy a new plugin build

1. Build the `.so` with the plugin builder image matching the running Nakama
   version (see `README.md` → *Building the plugin `.so`*).
2. Copy the artifact into the Nakama modules volume (`/nakama/data/modules`).
3. Restart the Nakama container. Nakama loads plugins only at start-up.
4. Check the logs for `rpg-mmo nakama module loaded in <n>ms`.

## Rollback

Restore the previous `.so` from the modules volume backup and restart Nakama.
The plugin is stateless; no migration is involved for the auth scope. Rolling
back *past* the authoritative-leaderboard change is safe: an older build will
happily use an authoritative board, it just stops enforcing the caller guard.

## Migrate a non-authoritative `kills_alltime` leaderboard

Builds before this change created `kills_alltime` with `authoritative=false`,
which lets any client post its own score through Nakama's public
`WriteLeaderboardRecord`. The board is now created authoritative, but Nakama's
`LeaderboardCreate` is idempotent and **never alters an existing board**, so an
existing deployment keeps the old flag until it is migrated. On start-up the
module looks the board up (`nk.LeaderboardsGetId`) and, if it finds it
non-authoritative, **refuses to load** with:

```
setup leaderboards: leaderboard kills_alltime exists with authoritative=false, so clients can write their own scores; fix: …
```

Pick one:

**A. Keep the records (production).** Nakama has no runtime or Console call
that flips the flag in place, so change the row directly and restart — Nakama
caches leaderboards in memory at start-up, the restart is what makes it take
effect:

```bash
docker compose exec postgres psql -U "${POSTGRES_USER:-nakama}" -d "${POSTGRES_DB:-nakama}" \
  -c "UPDATE leaderboard SET authoritative = true WHERE id = 'kills_alltime';"
docker compose restart nakama
```

Verify: `SELECT id, authoritative FROM leaderboard WHERE id = 'kills_alltime';`
shows `t`, and the Nakama log reads `Leaderboard kills_alltime ready (authoritative, …)`.

**B. Discard the records (dev / staging).** Set `LEADERBOARD_MIGRATE=recreate`
in Nakama's runtime env (`--runtime.env LEADERBOARD_MIGRATE=recreate`, or the
process env) and restart once. The module calls `nk.LeaderboardDelete` then
`nk.LeaderboardCreate(…, authoritative=true, …)`, logs a WARN, and every
existing `kills_alltime` record is gone. Remove the variable afterwards; it is
a one-shot switch, not a setting. Equivalent by hand: delete the leaderboard in
the Nakama Console (Leaderboards → `kills_alltime` → Delete) and restart.

Either way the guarded RPCs (`reward_kills` etc.) keep working unchanged: they
run inside the runtime, which is exactly the writer an authoritative board
admits.

## Troubleshooting

| Symptom | Likely cause | Action |
|---------|--------------|--------|
| Nakama fails to start: `plugin was built with a different version of package …` | Plugin built with a toolchain/dep set different from the server binary | Rebuild with the `nakama-pluginbuilder` tag matching the server version |
| Nakama starts but no hooks fire | `.so` not in the modules path, or `InitModule` symbol missing | Verify the volume mount and that the plugin is `package main` |
| An RPC returns `RPC function not found`, or a leaderboard returns `Leaderboard not found`, while older RPCs work | The mounted `nakama.so` predates the code that registers it — nothing rebuilds the module automatically | Compare the `.so` mtime against `git log -- backend/nakama/`; rebuild with `./scripts/build-all.sh --skip-tests --plugin` **with the Nakama container stopped** (the bind mount holds a file lock and the build fails with `rename … Access is denied`), then restart and look for `rpg-mmo nakama module loaded` in the log |
| Nakama fails to start: `setup leaderboards: leaderboard kills_alltime exists with authoritative=false` | Board created by a pre-authoritative build | Follow *Migrate a non-authoritative `kills_alltime` leaderboard* above |
| Game server logs `Nakama reward_kills failed … 403 … server-only rpc` | The game server reached Nakama with a client session token instead of `?http_key=` — misconfigured `NakamaClient` or a proxy rewriting the request | Verify `NAKAMA_HTTP_KEY` matches Nakama's `--runtime.http_key` and that the call goes to `/v2/rpc/reward_kills?http_key=…`; the guard rejects before anything is granted, so nothing to roll back |
| A client gets `403 server-only rpc` from `reward_kill`/`reward_kills`/`submit_kill` | Working as intended — these RPCs are server-only | Clients never call them; rewards are granted by the game server |
| Kills never reach the leaderboard, no errors anywhere | The game server logs `Nakama: disabled (NAKAMA_URL unset)` at startup and silently skips the kill-reward flush (`reward_kills`) | Set `NAKAMA_URL` in the game server's environment (compose files already set it; hand-rolled launch scripts are where it goes missing) |
| Game server logs `Nakama reward_kills timed out … will resend the same batch id` repeatedly, `gameserver` `PendingKills` climbing | Nakama slow or down; the game server holds the batches and resends them with backoff (3s doubling to 60s) under the same ids | Investigate Nakama latency/health. Nothing is lost while the game server stays up; the receipts make every resend exactly-once |
| A player disputes a grant ("got gold twice" / "never got gold") | Every granted batch has a receipt | Look up storage collection `reward_receipts`, owner = the user, key = the `batch_id` from the game-server log line: present ⇒ granted exactly once (its `gold`, `kills`, `granted_at`, `leaderboard_done`); absent ⇒ never granted, the game server should still be resending it |
| Nakama logs `receipt update failed for batch …: a replay may double-count the score` | Score committed but the receipt could not be flipped to `leaderboard_done` | Bounded score drift, accepted (ADR-6). If it recurs, check storage/DB health; gold is never affected |
| Gateway rejects every realtime token (`invalid signature`) | `JWT_SECRET` mismatch between Nakama and Gateway | Align the env var in both deployments and restart |
| Gateway rejects tokens with `token expired` | Client cached a token past `expires_in` (3600s), or clock skew | Have the client re-call `gateway_token`; check NTP on both hosts |
| `gateway_token` returns code 16 | Client called the RPC without a Nakama session | Authenticate first (device/email) and send the session token |
| Players log in with no profile | `StorageWrite` failing (DB pressure / permissions) | Check Nakama logs for `after authenticate …: ensure profile:` errors and PostgreSQL health |
