# Release runbook — cross-repo release order, verification and rollback

**Scope.** One release spans five repositories that are coupled by tags, not by a shared
build: `rpg-mmo-server` (Shared.GameLogic, gateway, game server), `Netcode`, `UnityDots`,
`IndieRPGMMOAdventure` (the Unity client) and `rpg-factory` (testlab and checks). This file
is the order to do things in, what to read back after each step, and how to undo each one.

**Status: PROPOSED, NOT DRILLED.** Written 2026-10-24 from audit A4 OE-05. No cross-repo
rollback has been demonstrated (A4: "this is a proposed runbook, not a verified rollback
test"). Rollback steps for components that have their own documented procedure link to it;
the rest are the obvious inverse of the release step and are marked UNDRILLED. Do the first
drill on dev, not on the day you need it.

**Rules that apply to every step**

- Verify by reading remote and live state, not exit codes (`TEAM.md`, `MEASUREMENT.md`). After
  a push, `git ls-remote`. After a deploy, read the revision label or `/status` off the running
  thing.
- A tag push to `Netcode` or `UnityDots` publishes to GitHub Packages (npm). **A published
  version cannot be withdrawn, only superseded.** Do not push a `v*` tag to try something.
- Tags, releases and merges are human gates. Agents prepare "ready to tag"; a person pushes.
- Never skip a step by trusting a green run. Read the counters (`Verify test counters`).

---

## 1. Release order

```
1. SGL tag            rpg-mmo-server   sgl-vX.Y.Z
2. Netcode tag        Netcode          vX.Y.Z   (only if its code or the wire changed)
   UnityDots tag      UnityDots        vX.Y.Z   (only if its code changed)
3. Client pins        IndieRPGMMOAdventure   manifest.json + packages-lock.json, one PR
4. Server image       rpg-mmo-server   ghcr.io/cuvara/{gateway,gameserver-dotnet}:<short-sha>
```

Why this order:

- The client resolves SGL, Netcode and UnityDots **by git tag**. A pin to a tag that does not
  exist is a broken client build, so tags come before pins.
- SGL is first because Netcode's and the client's tests replay golden vectors from the
  *pinned* SGL; an unreleased SGL change is one the client can never receive, while every
  build stays green (`sgl-release-reminder.yml`).
- The server image is last in this list only because the client pin PR is the human
  checkpoint. **When the wire protocol version changes, the server image must be deployable
  before clients that need it ship.** The server accepts a window
  `MinSupportedProtocolVersion <= v <= ProtocolVersion` (`gameserver-dotnet/docs/API.md`), so
  deploy the server first, then release the client. Protocol bumps follow the
  `wire-contract` skill, which owns that ordering. This runbook does not replace it.
- `rpg-factory` has no release workflow and no deployable. It is not a step; update its
  testlab checks in the same window if a threshold or scenario depends on the release.

### Step 1 — Shared.GameLogic tag (`sgl-vX.Y.Z`)

1. Bump `version` in `backend/gameserver-dotnet/Shared.GameLogic/package.json` and add the
   matching `CHANGELOG.md` heading in the same PR.
2. Merge to `develop`, then promote to `main`. `publish-shared-gamelogic.yml` runs on `main`,
   **derives the tag from `package.json`, and skips silently if the tag already exists.**
   A change without a version bump publishes nothing and the run is green.
3. `verify-sgl-tag.yml` runs on every `sgl-v*` tag. `sgl-notify-client.yml` then tells the
   client repo; it bumps nothing.

### Step 2 — Netcode / UnityDots tag (`vX.Y.Z`)

1. `package.json` `version`, the `CHANGELOG.md` `## [X.Y.Z]` heading and the tag must agree.
2. Push `vX.Y.Z`. `release.yml` rejects a mismatch between `package.json` and the tag, cuts
   the GitHub Release from the changelog section, and runs `npm publish` to GitHub Packages.
   **Irreversible.** Check CI test-count floors on the exact commit first.
3. Note that `release-reminder.yml` warns about unreleased commits. Netcode at the A4 baseline
   had two commits on `develop` after `v0.48.0`: those are not in the pin the client resolves.

### Step 3 — Client pins

One PR in `IndieRPGMMOAdventure` moves `Packages/manifest.json` **and**
`Packages/packages-lock.json` together. They are two files that must change as a pair.
Pin by tag (`#vX.Y.Z`, `#sgl-vX.Y.Z`), never by branch. The `pin-bump` skill drives this.
Reusable-workflow refs (`unity-build-workflows`) are pinned by `@vN`; A4 found `@main` refs
too, which are mutable. Do not add new ones.

### Step 4 — Server image

`publish-images.yml` builds and pushes both images on a push to `develop`
(`:<short-sha>`, `:develop`), on a `core-baseline-*` tag (`:<short-sha>`, `:<tag>`), or by
`workflow_dispatch` for any ref. `cd.yml` deploys. The server has **no** `release.yml`:
a server release is a commit, identified by the image revision label.

---

## 2. Verification per step

Run these after the step, before starting the next. "Read back" means from the remote or the
running system.

| Step | Check | Command / source | Pass means |
|---|---|---|---|
| 1 SGL | Tag SHA | `git ls-remote origin refs/tags/sgl-vX.Y.Z` and `git rev-parse sgl-vX.Y.Z^{commit}` | Commit is the intended `main` commit, and the same tree as the release commit |
| 1 SGL | package.json | `git show sgl-vX.Y.Z:backend/gameserver-dotnet/Shared.GameLogic/package.json` | `version` equals the tag minus `sgl-v` |
| 1 SGL | CHANGELOG heading | `git show sgl-vX.Y.Z:backend/gameserver-dotnet/Shared.GameLogic/CHANGELOG.md` | A `## [X.Y.Z]` heading exists with content |
| 1 SGL | Workflow | `verify-sgl-tag.yml` run on the tag | Green with a non-empty summary |
| 2 package | Tag SHA | `git ls-remote origin refs/tags/vX.Y.Z` | Peeled commit equals the audited commit; **not** `develop` HEAD unless intended |
| 2 package | package.json | `git show vX.Y.Z:package.json` | `version` is `X.Y.Z` |
| 2 package | CHANGELOG heading | `git show vX.Y.Z:CHANGELOG.md` | `## [X.Y.Z]` heading present |
| 2 package | Registry version | `npm view @cuvara/<dots|netcode> version --registry=https://npm.pkg.github.com` (needs a read token; the scoped name is a publish-only rewrite in `release.yml`) or the repo's Packages page | Lists `X.Y.Z` |
| 2 package | GitHub Release | `gh release view vX.Y.Z` | Notes equal the changelog section |
| 3 client | Manifest | `git show <client-sha>:Packages/manifest.json` | Pins are the new tags |
| 3 client | Lock hash | `git show <client-sha>:Packages/packages-lock.json` | Each `hash` equals the tag's commit SHA from steps 1-2 |
| 3 client | Resolution | Unity batch EditMode tests (`-testCategory "!LiveBackend"`) | Counters show tests ran; golden vectors replay the new SGL |
| 4 image | Revision label | `docker inspect --format '{{ index .Config.Labels "org.opencontainers.image.revision" }}' ghcr.io/cuvara/gameserver-dotnet:<short-sha>` | Equals the release commit |
| 4 image | Digest | Record `docker inspect --format '{{index .RepoDigests 0}}'` for **both** images before deploy | Needed for rollback (§3) |
| 4 deploy | Live state | `/status` on the running server (revision, `tick_rate`, protocol); the environment's deploy verification step in `cd.yml` | Reports the new revision and the expected protocol version |

If a check reads "no output", treat it as a failure until shown otherwise.

---

## 3. Rollback per component

| Component | Rollback | Notes |
|---|---|---|
| **Server image** (gateway, game server) | Redeploy the **previous image digest** recorded in §2 step 4. Documented paths: `deploy/docs/CICD.md` §7 (re-run the last good CD run; dispatch an older ref; `GATEWAY_IMAGE=rpg-mmo/gateway:<old-sha>` and `docker compose up -d`) | Pin by digest, not by `:develop`, which moves. Keep the previous digest in the release notes. |
| **Client pins** | Re-pin the previous tags in `manifest.json` **and** `packages-lock.json`, in one PR, and rebuild the client | UNDRILLED. A shipped client build cannot be recalled; rollback is the next build. |
| **Netcode / UnityDots** | Do **not** try to withdraw. Re-pin clients to the previous tag. Ship a fixed version as a new `vX.Y.Z+1` | npm publish is permanent. |
| **Shared.GameLogic** | Re-pin the client to the previous `sgl-v*` tag and redeploy the previous server image built from it | Server and client must run the same simulation version; roll both or neither. Never delete a published `sgl-v*` tag. |
| **Wire protocol bump** | Roll back the **client** first (it is the side that needs the newer protocol), then the server, so the server window always covers the clients in the field | A server older than a deployed client's protocol refuses that client. Follow `wire-contract`. |
| **Database** | **Migrations are additive-only** (audit A2 rule; `DATABASE.md` does not yet state it, so add it there when this lands). A schema rollback is not an operation: the previous image must run against the newer schema. Do not write a migration the previous image cannot tolerate (no dropped or renamed column, no tightened constraint in the same release) | The migrator runs one transaction per migration with a `schema_migrations` row (`deploy/docs/DATABASE.md`). Data loss recovery is restore from backup (`deploy/docs/DISASTER-RECOVERY.md`), not "down" migrations. |
| **rpg-factory** | `git revert` on `main` | No deployable. |

Rollback is not complete until the same §2 read-backs confirm the **previous** versions are
the live ones.

---

## 4. Compatibility matrix — core-baseline-v2 candidate

Source: re-derived on 2026-10-29 with the §2 commands from `origin` refs (`git ls-remote`,
`git show <ref>:Packages/packages-lock.json`). The audited v1.1 values are in
`CORE-BASELINE-V1.md`. Values are claims about named commits; **re-derive them** before
relying on them.

| Component | Baseline | Released pin | Tag commit | Lock hash (client `6dce2c2`) | Notes |
|---|---|---|---|---|---|
| Server | `a69af59` (`origin/develop`) | n/a (commit-based) | n/a | n/a | No `release.yml`. `ProtocolVersion` = 3, `MinSupportedProtocolVersion` = 2. |
| Shared.GameLogic | server lineage | `sgl-v0.7.0` | `86a654f7c491` | `86a654f7c491` | `develop` differs from the tag by a doc-comment-only commit (`ec06d9a`). |
| Netcode | develop `f9d9e96` | `v0.49.0` | `2ea640130e3e` | `2ea640130e3e` | `develop` is **3 merges ahead** of the tag (#206-#208); the client does not have them. `Wire.cs` md5 equals the server's. |
| UnityDots | `1ac175d` = `v0.33.0` | `v0.33.0` | `1ac175ddb245` | `1ac175ddb245` | `main` equals the tag. |
| UIToolkit | `ac871bb` | `v0.7.2` | `3c6fde9ac818` | `3c6fde9ac818` | 1 commit ahead of tag, package version unchanged. |
| Client | `6dce2c2` (`develop`) | n/a | n/a | n/a | Pins Netcode `v0.49.0`, UnityDots `v0.33.0`, UIToolkit `v0.7.2`, SGL `sgl-v0.7.0`. |
| rpg-factory | `dc81773` (`origin/main`) | n/a | n/a | n/a | No `release.yml`. |

Open items this matrix exposes (A4 OE-05, not fixed here):

- Netcode's version label `0.48.0` describes the tag, but `develop` carries unreleased source
  past it. Do not read the package version as describing the working tree.
- `rpg-factory` and the server lack a release workflow that asserts tag / changelog /
  version parity; the two package repos have one.
- Workflow dependencies are pinned inconsistently (`@v7` and `@main`).
- No cross-repo rollback drill exists. Doing one on dev closes this file's UNDRILLED marks.

## 5. Keeping this file honest

1. Update the matrix in the same change as any pin move, and re-derive it from the §2
   commands, not from this table.
2. A step stops being UNDRILLED only when a named drill ran and its read-backs are recorded.
3. If a workflow named here changes its trigger or checks, change this file in that PR.

Related: `READINESS-GATES.md`, `CORE-BASELINE-V1.md` (§6, re-baseline rule),
`deploy/docs/CICD.md` §7, `deploy/docs/DATABASE.md`, `deploy/docs/DISASTER-RECOVERY.md`,
`gameserver-dotnet/docs/API.md` (protocol window), `ARCHITECTURE-DECISIONS.md`.
