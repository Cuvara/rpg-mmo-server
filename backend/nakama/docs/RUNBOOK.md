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
The plugin is stateless; no migration is involved for the auth scope.

## Troubleshooting

| Symptom | Likely cause | Action |
|---------|--------------|--------|
| Nakama fails to start: `plugin was built with a different version of package …` | Plugin built with a toolchain/dep set different from the server binary | Rebuild with the `nakama-pluginbuilder` tag matching the server version |
| Nakama starts but no hooks fire | `.so` not in the modules path, or `InitModule` symbol missing | Verify the volume mount and that the plugin is `package main` |
| Gateway rejects every realtime token (`invalid signature`) | `JWT_SECRET` mismatch between Nakama and Gateway | Align the env var in both deployments and restart |
| Gateway rejects tokens with `token expired` | Client cached a token past `expires_in` (3600s), or clock skew | Have the client re-call `gateway_token`; check NTP on both hosts |
| `gateway_token` returns code 16 | Client called the RPC without a Nakama session | Authenticate first (device/email) and send the session token |
| Players log in with no profile | `StorageWrite` failing (DB pressure / permissions) | Check Nakama logs for `after authenticate …: ensure profile:` errors and PostgreSQL health |
