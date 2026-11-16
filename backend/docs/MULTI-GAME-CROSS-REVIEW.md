# ADR-33 multi-game cross-review

Reviewed `git diff 633a792..30464b3`, with ADR-33 (the last section of
`backend/docs/ARCHITECTURE-DECISIONS.md`) as the specification. This is an
independent Codex security and contract review. All source references below
are repository-relative **at `30464b3e00dc704a1cf9110a26ad0494e2455597`**;
the relevant implementations were read at that revision, not inferred solely
from diff hunks.

There are **4 findings: 2 high and 2 medium**. The ordinary authenticated map
and dungeon paths preserve the game identity. The findings concern database
credentials, a missing per-game secret check, delayed registry eviction, and
onboarding into a stack with Nakama TLS enabled. These are source-traced
failure scenarios; no exploit, deployment, or test suite was executed.

## Resolution status after integration

The original review below describes revision `30464b3`. The integration fixes
have since closed MG-01 through MG-04 in code; rollout to an actual stack
remains an operator step. The follow-up QA checked integration HEAD `1694014`;
this round corrects its remaining deployment ordering, CLI and Windows issues.

| Finding | Implemented resolution | Evidence and remaining scope |
| --- | --- | --- |
| MG-01 | Dedicated `gamestate` role; in-place ownership and database isolation; compose/k8s wiring | Prior QA: 24 live isolation checks passed; this round covers configurable database names and waits for provisioning before CD migrations. Default app-password hardening remains follow-up F7. |
| MG-02 | Per-key auth/join and cross-game separation before workloads, with Go-compatible trimming | Offline compose and k8s secret harness; runtime gateway checks remain in place. |
| MG-03 | Game-aware atomic registry eviction and matching watcher cleanup | Shared storage tests; stale events within the same game's incarnation remain outside this change. |
| MG-04 | Explicit TLS decision for rendered manifests; repair/check each game namespace and copy public pins | Renderer and offline TLS harness; this round also rejects certificate bundles containing private keys. |

## Findings

| ID | Severity | File:line at 30464b3 | Concrete failure scenario: inputs/state -> wrong outcome | Suggested fix |
| --- | --- | --- | --- | --- |
| MG-01 | high | `backend/deploy/db/provision-gamestate.sh:34`; `backend/deploy/docker-compose.yml:60`; `backend/deploy/docker-compose.yml:658` | **The default game retains the database bootstrap superuser.** Start the supplied compose stack on an empty PostgreSQL volume: the official `postgres:16.4-alpine` image creates `POSTGRES_GAME_USER` (normally `game`) as the bootstrap superuser, and compose gives exactly that user's credentials to the cuvara game server. Provision `gamestate_ember` with its own restricted role, including the PUBLIC CONNECT revocations. A compromised cuvara server can still connect as the bootstrap superuser to `gamestate_ember` and read or modify its state; superusers bypass those database ACLs. The new provisioner explicitly exempts cuvara and preserves its existing user, so adding restricted non-default roles does not provide isolation in this direction. This scenario uses the supplied compose credential wiring; it does not assume a production Secret has these values. | Give cuvara a dedicated, non-superuser login owning only `gamestate`, migrate its application grants/ownership as needed, and change the default fleet/compose DSN to that login. Keep the bootstrap administrator credential in database/admin tooling. Verify effective cross-database access in both directions for all application roles, including cuvara. |
| MG-02 | high | `backend/gateway/cmd/gateway/main.go:150`; `backend/deploy/k8s/dev-up.sh:525` | **The Kubernetes auth/join key separation check covers only cuvara.** Configure `GAMES=cuvara,ember`, `JWT_SECRET=auth-new,auth-old`, a distinct default join key, and `JOIN_TOKEN_SECRET_EMBER=join-ember,auth-old`; give the ember fleet the same ember join keyring. The deploy check reads only `join-token-secret`, not `join-token-secret-ember`. The gateway compares the ember and auth settings as whole strings, so the shared rotation key is missed; its per-key comparison only compares join keyrings with other join keyrings. The deployment starts and ember's pod possesses `auth-old`. Compromise of that pod now permits minting a valid gateway JWT for any user and `gid`, followed by receiving the target game's legitimate join token. Removing the `JWT_SECRET` environment variable from the pod does not close this path. | Parse every configured game's join keyring and the auth keyring with identical split/trim semantics, and reject any intersection before applying workloads or admitting that game's traffic. Also reject intersections between games' join keyrings, and validate that each fleet receives its matching keyring. Extend the Kubernetes check to non-default Secret keys; keep diagnostics limited to game/setting names. |
| MG-03 | medium | `backend/gateway/registry/watcher.go:169`; `backend/gateway/server/server.go:428`; `backend/shared/storage/redisstore/registry.go:156` | **A delayed `server_down` ignores the owning game and can delete another game's live registration.** A cuvara server uses the permitted custom ID `ember-one`, expires, and its watcher queues `{game_id:"cuvara",server_id:"ember-one"}`. Before that event is consumed, an ember server starts with the valid `ember-one` ID and registers against the now-absent hash. S1's registration check allows this transition. Delivery of the old cuvara event calls `Evict` with only the server ID; Go `Deregister` unconditionally deletes the ember hash and removes the ember map-index member. Ember joins then fail or trigger unnecessary allocation until its next repair heartbeat. No Redis credential theft is required, and the event's new `game_id` would distinguish the owners. | Carry the event's normalized game through `Evict` into an atomic game-checked deregistration operation, mirroring the C# ownership guard. A missing event game must mean cuvara. Remove index membership and watcher tracking only for the matching owner; consider an incarnation guard separately for stale events within one game. |
| MG-04 | medium | `backend/deploy/k8s/games/render.py:353`; `backend/deploy/k8s/app/20-configmaps.yaml:110`; `backend/deploy/k8s/dev-up.sh:479` | **Rendering a new game's configuration restores plaintext against a TLS-enabled Nakama.** Enable the supported Nakama TLS mode; `dev-up.sh` repairs cuvara's live `gameserver-config` to HTTPS and a certificate pin. Now onboard ember using the renderer. It reads the repository ConfigMap and copies every data key except `game-id`, so ember receives `http://nakama...:7350` and an empty pin even though that shared listener requires TLS. Its servers can register and accept gameplay, but their reward RPCs fail, leaving players unpaid. The existing repair only patches `rpg-k8s-realtime`; it never repairs the generated game namespace. | Make Nakama URL and pin settings explicit validated renderer/onboarding inputs derived from the stack's TLS decision. Ensure the public pin Secret is provisioned in each game namespace when required. Validate the generated per-game configuration against the shared Nakama listener before starting fleets, rather than requiring an undocumented edit of generated output. |

## Checked and sound

The following checks describe what holds in the source under the documented
shared-stack trust assumptions. Where an area also has a finding, the sound
portion is stated separately; it is not a blanket pass for that area.

### 1. Game identity and JWT contracts

- Go and C# agree on `DefaultGameID = "cuvara"`, exact ASCII game/map syntax,
  and the reserved game IDs `gs`, `map`, `dungeon`, and `other`:
  `backend/shared/constants/game.go:5`, `backend/shared/gameid/gameid.go:13`,
  `backend/gameserver-dotnet/GameServer/Games/GameId.cs:6`.
  Colons, slashes, dots, whitespace, uppercase game letters, and non-ASCII
  characters cannot enter the admitted game namespace. Map IDs exclude key
  separators and paths (`backend/shared/gameid/gameid.go:35`).
- Go signs `gid` as an optional string, omitting cuvara; C# reads the same
  property. Missing/empty values normalize to cuvara:
  `backend/shared/jwt/jwt.go:39`, `backend/shared/jwt/jwt.go:78`,
  `backend/gameserver-dotnet/GameServer/Server/JwtValidator.cs:47`.
  Verification helpers themselves are not game allow-lists: the gateway
  checks the verified claim against `GAMES` before creating a session
  (`backend/gateway/server/server.go:898`).
- Server startup validates its configured game and the non-default ID prefix
  (`backend/gameserver-dotnet/GameServer/Games/ServerGame.cs:15`). On join,
  C# checks `sid`, then compares normalized `gid` before consuming the JTI or
  admitting a player (`backend/gameserver-dotnet/GameServer/Server/GameServer.cs:1660`).
  A signed token lacking `gid` cannot join a non-default server; a different
  game's token cannot join it even if verified with the same key. Missing
  `gid` selecting cuvara is the required compatibility behavior, not itself
  a downgrade finding. Changing a signed claim invalidates its HMAC.

### 2. Gateway join, map assignment, and dungeon assignment

- Authentication latches game, user, and character together; re-authentication
  retires the old local tracking key:
  `backend/gateway/server/connection.go:275`,
  `backend/gateway/server/server.go:970`.
  `EnterWorld` derives the game from that identity, validates the map/dungeon
  content ID before lookup or allocation, and rejects a conflicting requested
  character (`backend/gateway/server/server.go:1138`, `:1158`, `:1182`).
- Map lookup, allocation single-flight, and mismatch memory use `(game,map)`;
  returned map and game are checked before a token is minted:
  `backend/gateway/registry/registry.go:404`, `:538`, `:604`, `:678`;
  `backend/gateway/transfer/map_assign.go:109`.
  Agones selects namespace/fleet per game and kind and checks the allocated
  server's prefix (`backend/gateway/registry/agones_allocator.go:265`, `:327`).
- Dungeon membership includes the authenticated game in `party_get`
  (`backend/gateway/transfer/party.go:191`). Both freshly allocated and cached
  dungeon instances are checked against the requested game before signing
  (`backend/gateway/registry/registry.go:855`,
  `backend/gateway/transfer/dungeon.go:128`, `:177`). Globally random party IDs
  explain the unchanged dungeon-index keys; the game check remains on reuse.
- Signing selects only the requested game's keyring, with no fallback for a
  missing non-default key; the join token carries the same `gid` and `cid`
  (`backend/gateway/transfer/join_token.go:39`, `:79`). MG-03 concerns a later
  eviction path, not an observed cross-game token issuance on these paths.

### 3. Redis registry, S1, Lua, and CommandMap

- Both registration scripts compare the existing normalized `game_id` before
  writing any hash, TTL, or index member; a legacy hash without the field
  belongs to cuvara. The eight hash fields, Lua argument order, and optional
  map-index key agree:
  `backend/shared/storage/redisstore/registry.go:36`, `:52`, `:126`;
  `backend/gameserver-dotnet/GameServer/Registry/RedisServerRegistry.cs:39`,
  `:59`, `:226`.
- Map keys preserve `servers:map:{map}` for cuvara and use
  `servers:map:{game}:{map}` otherwise; both readers filter foreign ownership
  (`backend/shared/storage/redisstore/registry.go:117`, `:218`;
  `backend/gameserver-dotnet/GameServer/Registry/RedisServerRegistry.cs:223`,
  `:326`). C# registration heartbeats, count updates, and shutdown use the
  game-checked overloads (`backend/gameserver-dotnet/GameServer/Registry/RegistrationService.cs:218`,
  `:284`, `:320`). The remaining Go deletion gap is MG-03.
- C# connection options disable `CLUSTER` discovery, without altering the
  registry's ownership script:
  `backend/gameserver-dotnet/GameServer/Registry/RedisServerRegistry.cs:193`,
  `:217`. Event and kick connections reuse these options
  (`backend/gameserver-dotnet/GameServer/Events/RedisEventStream.cs:137`,
  `backend/gameserver-dotnet/GameServer/Events/RedisKickConsumer.cs:98`).
  The two-key script requires standalone Redis or a Sentinel-managed primary;
  this change does not make the layout Redis Cluster compatible. Runtime
  topology detection was not retested.

### 4. Kick/supersede payloads and consumers

- Go and C# agree on `user_id`, `server_id`, `jti`, `old_gateway`,
  `new_gateway`, and additive `game_id`, with absent game meaning cuvara:
  `backend/gateway/server/kick.go:13`,
  `backend/gameserver-dotnet/GameServer/Events/KickEvents.cs:20`.
  The publisher explicitly fills game even for cuvara
  (`backend/gateway/server/kick.go:37`).
- Local tracking and lookup use a `(game,user)` struct key. The gateway
  consumer checks its destination gateway and passes game/user/JTI; the
  close operation requires the currently tracked JTI
  (`backend/gateway/server/server.go:31`, `:606`, `:634`;
  `backend/gateway/server/kick_consumer.go:129`). Thus a normal supersede in
  one game does not close the account's connection in another.
- The C# consumer first filters `server_id`, logs/counts a game mismatch,
  and retains the JTI-protected kick behavior, as ADR-33 specifies
  (`backend/gameserver-dotnet/GameServer/Events/RedisKickConsumer.cs:230`,
  `backend/gameserver-dotnet/GameServer/Server/GameServer.cs:2393`).
  `entity_killed` also gains `game_id`
  (`backend/gameserver-dotnet/GameServer/Events/EventPublisher.cs:16`).

### 5. Session keys and lifecycle

- `session:{game}:{user}` is used for non-default games; the intentional
  compatibility exception is cuvara's existing `session:{user}`
  (`backend/shared/constants/keys.go:29`). Validated game IDs and Nakama UUID
  user IDs keep these compositions unambiguous on the external login path.
- Creation, duplicate-login lookup, assignment updates, heartbeat refresh,
  session checks, and disconnect deletion use the connection's game:
  `backend/gateway/session/manager.go:73`, `:98`, `:150`;
  `backend/gateway/server/server.go:694`, `:790`, `:811`, `:921`, `:1247`.
  Same-account sessions in distinct games therefore occupy distinct store
  keys and local connection entries.

### 6. Nakama authentication, refresh, data, wallets, and ledger

- All registered authentication methods have before-hooks; game resolution
  repeats exact allow-list validation at RPC entry:
  `backend/nakama/main.go:44`, `backend/nakama/auth/hooks.go:14`,
  `backend/nakama/gamectx/gamectx.go:83`.
  A session's `game` var is authoritative, and a differing payload game is
  refused. A sessionless HTTP-key call uses the validated payload game
  (`backend/nakama/gamectx/gamectx.go:90`). Server-only economy RPCs reject
  session callers before parsing the beneficiary/payload
  (`backend/nakama/economy/caller.go:30`).
- Refresh with no vars passes the authenticated refresh token through;
  non-empty replacement vars are checked, including unknown-game rejection
  (`backend/nakama/auth/hooks.go:121`). The committed real-Nakama test checks
  preserved `vrs.game` and subsequently minted gateway `gid` with omitted vars
  and `{}` (`backend/nakama/auth/refresh_localstack_test.go:145`, `:185`).
  This review read that evidence but did not rerun it; the default-free
  configuration question below remains.
- Roster ownership checks use the resolved game's collection before minting
  `cid` (`backend/nakama/auth/token.go:126`, `:145`). Profiles, character
  rosters, parties, memberships, and reward receipts all select scoped names,
  preserving default names without falling back from another game:
  `backend/nakama/auth/profile_game.go:23`,
  `backend/nakama/character/character.go:136`,
  `backend/nakama/social/party.go:173`, `:196`, `:399`,
  `backend/nakama/economy/reward_batch.go:212`, `:292`, `:310`.
- Currency keys are `gold` / `<game>.gold`. Both reward methods attach
  non-default `game_id` ledger metadata; its absence retains the default
  meaning (`backend/nakama/economy/reward.go:72`,
  `backend/nakama/economy/reward_batch.go:196`). Batch receipt and wallet
  updates commit together with create-only receipt semantics; replay reads
  only the selected game's receipt and retries only its scoped leaderboard
  (`backend/nakama/economy/reward_batch.go:178`, `:209`, `:255`, `:274`).
- Board initialization, `submit_kill`, batch score writes, and client reads
  select the game's authoritative board
  (`backend/nakama/economy/leaderboard.go:68`, `:114`, `:160`, `:199`).
  C# sends its configured `game_id` on all three economy RPCs
  (`backend/gameserver-dotnet/GameServer/Nakama/NakamaClient.cs:56`, `:75`, `:111`).

### 7. Metrics and cardinality

- Gateway game labels are restricted to configured games plus `other`, on
  exactly four families: authentication, EnterWorld, allocation, and session
  checks. Unverified requests use `other`; `other` is reserved as a game ID
  (`backend/gateway/metrics/metrics.go:162`, `:277`, `:315`;
  `backend/gateway/cmd/gateway/main.go:195`). With the existing result sets,
  these families have at most `9 * (configured games + 1)` series combined.
  Arbitrary token game strings cannot create arbitrary labels.
- C# derives all recording/observable tag sets from `{map_id,game}`, including
  GC generation tags, transport/KCP paths, lazy rejection tags, events, saves,
  and entity observations
  (`backend/gameserver-dotnet/GameServer/Observability/GameMetrics.cs:185`,
  `:662`, `:675`, `:917`, `:944`, `:1093`, `:1452`, `:1484`). The game is
  constant per validated server process. A committed meter-wide audit exists
  (`backend/gameserver-dotnet/GameServer.Tests/Observability/GameMetricsGameLabelTests.cs:200`);
  it was not executed here.
- Dashboard aggregation and reward-alert joins retain `(game,map_id)` rather
  than combining two games' identically named maps
  (`backend/deploy/monitoring/dashboards/rpg-gameplay.json:35`,
  `backend/deploy/monitoring/alerts.yaml:38`, `:72`).

### 8. Configuration and secrets

- Nakama and gateway agree on `GAMES`: empty means cuvara only; otherwise it
  is the exact validated, deduplicated list, not a list with an implicit
  cuvara entry (`backend/nakama/gamectx/gamectx.go:34`,
  `backend/gateway/session/games.go:46`). Gateway and Nakama manifests expose
  that setting (`backend/deploy/k8s/app/40-gateway.yaml:276`,
  `backend/deploy/k8s/data/nakama.yaml:148`).
- Default and per-game join keys remain distinct configuration channels.
  Missing/non-parseable non-default settings leave only that game unroutable
  (`backend/gateway/cmd/gateway/main.go:140`,
  `backend/gateway/server/server.go:1165`). No default/auth fallback exists
  for another game's missing join ring. Key intersections are MG-02.
- The game-server binary no longer reads `JWT_SECRET`, and compose/default
  fleets supply only `JOIN_TOKEN_SECRET`. The renderer refuses source fleets
  containing that auth environment variable and strips `jwt-secret` from the
  game Secret template (`backend/gameserver-dotnet/GameServer/Program.cs:154`,
  `backend/deploy/docker-compose.yml:406`,
  `backend/deploy/k8s/app/50-fleet-map.yaml:200`,
  `backend/deploy/k8s/app/60-fleet-dungeon.yaml:247`,
  `backend/deploy/agones/fleet-map-dotnet-dev.yaml:284`,
  `backend/deploy/k8s/games/render.py:278`, `:380`).
- The added key comparisons print setting/game names rather than secret
  values. Database provisioning reads its password through `psql \getenv`,
  disables SQL echo/logging before the password statement, and suppresses
  password-bearing error output (`backend/deploy/db/provision-gamestate.sh:64`,
  `:79`, `:136`). No new literal production credential or bearer-token log
  was identified in the reviewed changes. Live credentials were not read.

### 9. Deployment and CI

- The renderer validates game/namespace/name budgets, uses `<game>-` fleets
  in `<stack namespace>-<game>`, supplies required `GAMESERVER_GAME_ID`, adds
  quota/sidecar request defaults, and grants the gateway allocation rights
  only in the new namespace (`backend/deploy/k8s/games/render.py:126`, `:267`,
  `:285`, `:324`, `:357`). Its output is 11 workload/config/RBAC documents,
  one separate Secret template, and a separate gateway env snippet; templates
  do not contain live values. TLS configuration is MG-04.
- Non-default database/role identifiers derive from a restricted slug; SQL
  passwords use literal quoting rather than shell interpolation, and the
  provisioner bounds connections and verifies the new role's effective
  CONNECT access (`backend/deploy/db/provision-gamestate.sh:30`, `:75`, `:87`,
  `:104`). The default-role gap is MG-01.
- Per-game image builds pass a content directory to the runtime-stage COPY;
  the boot manifest checks game identity and Shared.GameLogic major version
  (`backend/deploy/docker/Dockerfile.gameserver-dotnet:87`,
  `backend/gameserver-dotnet/GameServer/Content/GameManifest.cs:36`).
  Publishing pins each checkout to the resolved SHA, prefixes non-default
  game tags/cache scopes, and reserves unprefixed compatibility tags for
  cuvara (`.github/workflows/publish-images.yml:193`, `:215`, `:250`;
  `.github/workflows/cd.yml:291`, `:332`, `:1196`). The configured build lists
  currently contain only cuvara; no second game's actual rollout is proven
  by those workflows.

## Questions for the owner

1. **Does per-game isolation include confidentiality through Nakama's built-in
   client APIs, or only selection by the custom RPCs?** Profiles are publicly
   readable (`backend/nakama/auth/profile_game.go:53`), and rosters are readable
   by their shared account owner (`backend/nakama/character/character.go:174`).
   Scoped collection/board names alone do not bind built-in storage and
   leaderboard reads to the session's game. This review does not assume that
   visibility of the same account's other-game data violates the intended
   shared-account policy.
2. **Must game profiles mark meta-only users and every authentication method?**
   Only device/email have after-auth profile hooks
   (`backend/nakama/main.go:38`); other non-default games gain a profile on
   `gateway_token` (`backend/nakama/auth/token.go:156`). Confirm whether a user
   who only authenticates through Steam/custom auth and uses character/party
   RPCs must appear in the hub before requesting a realtime token.
3. **Is `GAMES` without cuvara a supported deployed configuration?** For a
   non-nil empty refresh vars map, the hook validates cuvara although Nakama
   preserves the existing token's vars (`backend/nakama/auth/hooks.go:111`).
   The real-stack refresh test configures cuvara plus another game, so it
   does not establish whether an explicit `{}` unnecessarily refuses a valid
   non-default session on a default-free stack.
4. **Are registry writers a strictly internal, validated boundary?** Both
   registration APIs perform ownership checks but do not call the format/
   prefix validators themselves (`backend/shared/storage/redisstore/registry.go:126`,
   `backend/gameserver-dotnet/GameServer/Registry/RedisServerRegistry.cs:236`).
   Current production C# startup and Agones allocation validate the prefix.
   Confirm that future Go registrants cannot bypass that boundary before
   calling these reusable APIs.
5. **What is the required second-game release evidence?** Non-default games
   are manually onboarded and must be added to three build lists. Confirm a
   required verification of rendered image references/imports, per-game DB
   permissions, secret separation, TLS posture, and concurrent account sessions
   before enabling that game's `GAMES` entry. No cluster state was available
   to establish completion of those operational steps.

## Validation, limitations, and scope

- Coverage: **9/9 requested areas**, **4/4 findings** with severity,
  revision-specific file:line, failure scenario, and fix; **5 owner questions**.
- The original review at `30464b3` only added this document and made no code or
  deployment changes. Subsequent integration commits implement the resolutions above.
- Edited-file checks: Markdown structure **PASS** (4 finding rows with 5
  columns each, 9 coverage sections, 5 numbered owner questions); whitespace
  **PASS** (0 errors), using `git -c core.autocrlf=false diff --no-index --check
  -- NUL backend/docs/MULTI-GAME-CROSS-REVIEW.md` (exit 1 for the new-file
  difference, with no whitespace diagnostics). No executable source was edited.
  Source-reference syntax/bounds validation **PASS**: 101 explicit references
  across 62 files resolve to valid line numbers at `30464b3`; this is a
  reference check, not a behavioral test.
- **NOT_RUN:** `go test ./... -race` in each of `backend/shared`,
  `backend/gateway`, and `backend/nakama`; `dotnet test` in
  `backend/gameserver-dotnet`; `go test -tags integration ./...` in
  `backend/integration_test`; `go test -tags localstack ./auth` in
  `backend/nakama`; `python backend/deploy/k3s/validate-manifests.py`;
  `bash backend/deploy/db/tests/provision_test.sh`;
  `bash backend/deploy/k8s/verify/tests/secret_check_test.sh` (repository-root
  paths for these last three commands). The dispatch explicitly reserves test
  execution for the verification session. **0 test suites executed**; Docker
  and the local backend stack were not started.
- Existing tests were read as intended-behavior evidence, not counted as
  passing executions. PostgreSQL privilege consequences follow the shipped
  official image's bootstrap-user semantics; live DB roles, live Secrets,
  Nakama refresh behavior, Redis topology detection, image publication, and
  Prometheus collection were not independently exercised.
- Accepted ADR-33 shared-stack risks remain: shared Redis credentials can
  modify any game's registry/events; the shared Nakama HTTP key can grant any
  game's rewards; Redis/stream/CPU capacity and the transport PSK remain shared.
  Those explicit owner decisions are not new findings. The DB and auth-secret
  scenarios above concern the additional isolation the ADR says should exist.
- Out-of-scope observations: pre-existing JWT parser hardening differs between
  Go and C# (Go requires `typ=JWT`, C# checks only `alg`; C# only expires a token
  when `exp > 0`: `backend/shared/jwt/jwt.go:133`,
  `backend/gameserver-dotnet/GameServer/Server/JwtValidator.cs:111`, `:138`).
  Current issuers produce `typ=JWT` and positive expirations, so no new
  multi-game exploit through those differences was established. Registry
  incarnation races within one game and general Nakama reward replay/score
  semantics were not expanded into unrelated fixes.
