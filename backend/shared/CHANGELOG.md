# Changelog — Shared Module

All notable changes to the shared module will be documented in this file.
Format based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Added

- **`shared/sealed`: the wire format, replay rule and refusal policy for realtime
  confidentiality**, specified and tested without a cipher. Normative spec:
  `backend/docs/SEALED-FRAMING.md`.
  - **Sealing happens above the transport, around the Envelope**, not at the packet layer.
    The KCP packet-crypt layer cannot be used: it is per-*listener* (kcp-go takes one
    `BlockCrypt` for every datagram, with no per-remote key selection) so it cannot carry a
    per-session key — and TCP, the default transport, has no such layer at all. Above the
    transport, one implementation serves both.
  - Frame: `[4B length][0xC1 marker][1B version][8B sequence][ciphertext][16B tag]`, with
    the whole 10-byte header as additional authenticated data, so a frame cannot be
    renumbered to replay it nor rolled back to an older format. `0xC1` cannot begin a
    well-formed Envelope, so it cannot be confused with the `0x08`/`0x7B` encoding sniff.
    Overhead is ~390 B/s per client at 15 Hz — 0.85% of the measured 45.9 KB/s.
  - **Nonce is a bare counter, and that is safe only because each direction has its own
    key.** Documented at the function, with the consequence stated: if one key ever serves
    both directions, the nonce must grow a direction byte the same day or the scheme is
    broken.
  - **Two replay validators behind one interface** — strict-monotonic and a 64-frame
    sliding window (the IPsec/DTLS rule) — because whether the ARQ can reorder at this
    layer is still being measured. The finding lands as a one-line change at the call site
    rather than a rewrite. Both refuse what they cannot judge.
  - **Handshake transcript** `label || 0x00 || jti || 0x00 || client_pub || server_pub`,
    with a golden vector shared with the C# implementation. The NUL separators stop two
    different (jti, key) pairs producing identical bytes; including both ephemeral public
    keys is what stops a replayed binding authenticating a man-in-the-middle's exchange.
  - **Refusal has two states, not three.** A "preferred" mode is a downgrade attack with a
    friendly name, so a peer that does not seal gets no session.

### Changed

- **`EnterWorldResponse.session_key` (field 5) is removed and the number reserved.** ADR-22
  supersedes the derived session key with an authenticated X25519 exchange, which gives
  forward secrecy the derivation could not. The number is reserved rather than reused: a
  peer built against the old schema would read whatever replaced it as 32 bytes of key
  material and fail in a way that looks like a key mismatch rather than a schema mismatch.
- **`shared/sessionkey` records its superseded purpose.** The bytes and the golden vector
  are unchanged; what moved is what the value is *for* — it is now the handshake binding
  key, proving possession of `JOIN_TOKEN_SECRET`-derived material, not an encryption key.

> **Nothing here encrypts anything yet.** No cipher, MAC or curve is implemented, and none
> is stubbed: an implementation that "worked" would let every test above it pass while
> proving nothing about the bytes. ADR-22's library choice is still open.

### Added

- **`shared/sessionkey`: per-session keys, derived rather than distributed.** Transport
  encryption used ONE pre-shared key — the same value in every client binary and every
  server — so extracting it from a single client decrypted every player's traffic for ever,
  and rotating it meant redeploying everything at once. The key is now per join:
  `HKDF-SHA256(ikm = JOIN_TOKEN_SECRET, salt = join token jti, info = "cuvara/session-key/v1", L = 32)`.
  - **The game server is never sent the key.** It derives the same value from the secret it
    holds and the `jti` in the token it already verifies, so nothing carrying key material
    crosses the gameplay hop, and nothing is stored. Only the client is sent one, because
    only the client cannot derive it.
  - `sessionkey.Key` redacts itself through `fmt`, `slog`, `%#v` and `encoding/json`. The
    realistic leak is not a deliberate log call but a struct handed to a formatter by code
    that did not know it held a secret, and a test asserts every one of those paths.
  - A golden vector is shared with the C# implementation. Two implementations that each
    round-trip against themselves can still disagree with each other, and a disagreement
    here produces no error anywhere — the client encrypts with one key, the server decrypts
    with another, and the session simply never forms.
  - `Info` and `Size` are pinned by a test: they are wire contract, and changing either
    silently breaks every peer.
- **`EnterWorldResponse.SessionKey`** (`wire.proto` field 5, Protobuf only). Tagged
  `json:"-"` **by design**: the legacy JSON encoding cannot carry a key, because exempting
  the field from redaction would put the material back on the path the redaction exists to
  close — and JSON is the encoding a human is most likely to paste into an issue. The
  consequence is stateable: a JSON client cannot be encrypted.

> **Limitation recorded with the feature, not beneath it.** The client cannot derive the
> key, so it must travel gateway → client, and the gateway hop is the *same transport
> stack* as the gameplay hop — plaintext TCP by default. In the default configuration an
> eavesdropper on the gateway hop reads the key and can decrypt that session. This turns
> "compromise one binary, decrypt everyone for ever" into "eavesdrop the gateway hop,
> decrypt one session": strictly better, and not end-to-end confidentiality.

### Added

- **`transport.Posture(kind, key, addr)`** — the confidentiality posture of a listener as
  one computed fact: transport, whether a key is configured, whether packets are actually
  ciphertext, whether they are authenticated, the cipher in force, whether the bind is
  beyond loopback, and a one-line summary. Mirrors the C# `TransportPosture` field for
  field so both halves of the backend describe themselves the same way.
  - Exists because encryption is off by default **twice** — the transport defaults to
    `tcp`, which has no packet-crypt layer, and the key defaults to empty — so no single
    value answers "is this encrypted", and the combination that answers "no" most
    emphatically is the default one.
  - `Encrypted` and `Authenticated` are separate fields, and `Authenticated` is hard-coded
    `false` with the reason stated: the KCP path is AES-256-CFB with a CRC32, and a CRC32
    is linear, not a MAC, so a modified datagram is not detectable. One "secure" boolean
    would let a reader take confidentiality for integrity.
  - `KeyIgnored()` names the configuration most easily mistaken for working encryption: a
    key set on TCP, where it is accepted and then does nothing.
  - Bind classification is deliberately pessimistic — wildcard binds (`:8000`,
    `0.0.0.0:8000`, `[::]:8000`) and anything unparseable count as beyond loopback, because
    wildcard is the container shape and exempting it would exempt exactly the deployments
    this reporting is for.

### Added
- **`EntitySnapshot.facing_brad` (field 10) and `EntitySnapshot.action` (field 11).**
  The snapshot carried `id, type_name, x, y, hp, max_hp, type, handle, speed` and
  nothing else — no facing, no rotation, no action state. A character could not be
  made to face the direction it was walking, and an attack could not be animated,
  without a schema change across both repos, so the "core plumbing is closed"
  claim was not true of the first thing any renderer needs.
- **Facing is a BIASED 16-bit binary radian value, not a float, and that is the
  point.** proto3 elides a zero and 0.0 radians is a perfectly ordinary facing
  (due east), so a float would put "facing east" and "field not sent" on the wire
  as identical bytes — the trap `speed` has to document its way around because
  a zero speed is genuinely meaningful. Facing has no such excuse, so wire 0 is
  reserved and a real angle is `(v-1) * 2*Pi / 65536`: every representable
  direction has a non-zero encoding, by construction rather than by asking every
  implementer to remember a rule. It is also 1-3 bytes against a float's 5, on
  the hottest message in the protocol.
- **`EntityAction` reserves 0 for "not sent" and numbers IDLE as 1**, for the same
  reason and following `ENTITY_TYPE_UNSPECIFIED`'s precedent. A receiver that read
  0 as "idle" would let an old server freeze every entity into an idle pose.
- **`messages.FacingBradFromRadians` / `RadiansFromFacingBrad`** — the reference
  codec the C# server and the Unity client mirror; `messages.EntityAction` mirrors
  the enum. Neither field bumped `WireProtocolVersion`: both are additive with a
  documented zero rule and degrade visibly rather than diverging silently, which
  the bump rules explicitly call a non-bump. Rationale, the rejected encodings
  (`optional float`, `float`+`bool`, a direction vector) and what was deliberately
  left out (velocity, an action sequence number): `docs/DESIGN.md`, "Entity facing
  and action state on the wire".

### Added
- **Wire protocol version negotiation (`protocol_version`).** `wire.proto` had no
  version field of any kind: client and server agreed on the meaning of the wire
  by convention, and a version-skewed build was not refused — it connected,
  parsed every byte and was confidently wrong. Adds `protocol_version` to
  `AuthRequest`/`AuthResponse` (fields 2/4) and
  `JoinTokenRequest`/`JoinTokenResponse` (fields 2/5), the constant
  `messages.WireProtocolVersion` (currently **1**), `ProtocolVersionUnversioned`,
  the reason token `ReasonProtocolVersionMismatch`
  (`"protocol_version_mismatch"`), and `messages.CheckProtocolVersion` — the one
  decision function both Go peers share.
- **Semantics.** The number names what the schema MEANS, not its shape (proto3
  already skips unknown fields) nor its encoding (already sniffed from byte 0).
  It rides the two handshake requests, never `Envelope` — an envelope field would
  be paid on every snapshot of every tick to restate a per-connection constant.
  Matching is EXACT: a peer one version ahead is refused as firmly as one behind,
  because a single integer carries no compatibility range.
- **Zero means "did not advertise", and is admitted by default.** proto3 elides a
  zero `uint32`, so a pre-versioning peer is indistinguishable from one sending
  0 — the trap already documented on `EntitySnapshot.speed`. Versions start at 1
  and 0 is reserved. Unversioned peers are admitted **on trust** and counted, so
  the trust is visible; the migration is one flag once that counter goes flat.
  Rationale and the rejected alternatives: `docs/DESIGN.md`, "Wire protocol
  version".

## [0.9.0] - 2026-09-05

### Added
- **`constants.KickEventStream` (`"kick"`) and `constants.EventSessionSuperseded`
  (`"session_superseded"`)** — the gateway → game-server duplicate-login kick
  channel (ADR-20), the Streams rebuild of what #211 deleted. One shared stream
  (`events:kick`) rather than a key per server, because server ids churn and
  this Redis runs `noeviction` (ADR-4); every game server consumes it through
  its own consumer group with explicit ACK (ADR-5 — never the Pub/Sub shape the
  removed `GatewayKickChannel` described). The C# consumer mirrors both
  literals (`GameServer/Events/KickEvents.cs`); payload contract in
  `gameserver-dotnet/docs/API.md`.

### Removed
- **`storage/pgstore/` package deleted** (ADR-1 follow-up). No Go binary imported
  it — the C# game server has its own `PostgresPlayerStore`. The `pgx/v5`
  dependency is also removed from `go.mod`.

### Changed
- **docs**: `docs/DESIGN.md` no longer says the C# game server publishes into a
  noop — `gameserver-dotnet`'s `RedisEventStream` now feeds `events:game` with
  this module's publisher contract whenever `REDIS_ADDR` is set (ADR-5
  follow-up; see `backend/gameserver-dotnet/CHANGELOG.md`). No Go code changes.

### Fixed
- **`redisstore.EventStream` now reclaims the Pending Entries List**
  ([#234](https://github.com/Cuvara/rpg-mmo-server/issues/234)). The consumer
  only ever read `>`, and consumer names are pod names, so an entry delivered
  to a pod that crashed between handler and `XACK` stayed pending under a name
  no replacement would ever use — the redelivery half of at-least-once was
  missing, and the type comment described recovery no code performed. On
  Subscribe and every 30s (`SetReclaimInterval`) the consumer now walks the
  group's PEL with `XAUTOCLAIM`, claims entries idle longer than 60s
  (`SetReclaimMinIdle`) to itself, and redelivers them to the handler. Entries
  past 5 deliveries (`SetMaxDeliveries`) are dead-lettered: ACKed unhandled,
  logged loudly, and counted via the new `DeadLetters()` — same pattern as
  `GroupLosses` for NOGROUP. The cap has deliberately no off switch. Rationale
  and failure-mode analysis: `docs/DESIGN.md`, "PEL reclaim and the delivery
  cap".

### Changed
- **`EventStream` ACKs once per read batch instead of once per message** —
  one `XACK` round trip per `XREADGROUP` batch (Count 16). A consumer dying
  mid-batch re-receives the whole batch via the reclaim path, which the
  required idempotent-handler discipline already covers; a failed batch ACK is
  logged and left pending for reclaim (duplicate delivery, never loss).

### Removed
- **`GatewayKickChannel` (`"gateway:kick"`) is gone from `constants/keys.go`**
  ([#211](https://github.com/Cuvara/rpg-mmo-server/issues/211)). It named a Redis
  Pub/Sub channel for coordinating duplicate-login kicks between gateway
  instances. Nothing in either module ever read it: `grep` across the whole
  backend returned the declaration and nothing else — no publisher, no
  subscriber, no test. The gateway-side machinery it was declared for
  (`KickPublisher`/`KickSubscriber`, `handleKickEvent`, the two options) was
  never constructed by `cmd/gateway/main.go` and is removed in the same change;
  see `backend/gateway/CHANGELOG.md` for the full reasoning.

  **The comment was the most expensive part of it, and is the reason this is a
  removal rather than a tidy-up.** Six lines of rationale explained that message
  loss is acceptable because the old session expires by TTL, and concluded that
  "Pub/Sub rather than Streams is the right transport". That is a reasoned
  architectural claim sitting in the constants file, it contradicts ADR-5
  ("Streams, not pub/sub"), and it was attached to a constant no code used. A
  future engineer building cross-instance kick would have found it, found it
  persuasive, and built the wrong thing — with a plausible-looking precedent to
  cite. Deleting the constant without deleting that argument would have kept the
  trap; deleting both is the point.

  **This does not remove a capability**, because there was none: with one gateway
  replica (ADR-17) there is no second instance to coordinate with, and the
  publisher that would have used this channel was a no-op in every build ever
  shipped. When a second replica is planned, the transport is a Redis Stream with
  a consumer group and explicit ACK per ADR-5, whose key would go through
  `EventStreamPrefix` like every other stream in this file rather than being a
  bare channel name. `SessionKeyPrefix`, `ServerRegistryKey`, `EventStreamPrefix`
  and `GameEventStream` are unchanged; all four have live readers.

### Fixed
- **`EventStream.Publish` now bounds `events:*` with `XADD ... MAXLEN ~ 30_000`,
  so an untrimmed stream can no longer be what gets Redis OOM-killed**
  ([#202](https://github.com/Cuvara/rpg-mmo-server/issues/202)). Redis here runs
  `maxmemory-policy noeviction` deliberately — ADR-4 argues correctly that this
  instance is a system of record and that evicting a `servers:*` hash removes a
  live game server from matchmaking with no error anywhere. What was missing was
  the ceiling that makes the policy *safe* rather than merely strict: `XAdd`
  carried no `MaxLen`, no `maxmemory` was configured, and the Redis pod is capped
  at `limits.memory: 256Mi`. The only ceiling that actually existed was the
  kernel's, and the kernel does not refuse a write — it kills the process whole,
  taking sessions, the registry and the stream together. That is precisely the
  outcome `noeviction` was chosen to prevent, reached by a route the ADR did not
  close. The deploy side of the pair (`maxmemory 128mb`, half the pod limit) is in
  `backend/deploy/CHANGELOG.md`; this entry is the publisher-side half, which is
  the one that runs on every write.

  **The length is derived from consumer lag, not from a round number or a memory
  figure.** The dominant event is `entity_killed`, one per mob death, from every
  game server into the single shared `events:game` stream. Taking the 200
  players-per-server figure `backend/docs/BENCHMARK.md` actually measures, and
  assuming a kill roughly every 10s per player, that is ~20 events/s per server;
  two live servers plus headroom for the smaller types (`boss_killed`,
  `rare_drop`, `inventory_changed`) gives a planning rate of **50 events/s**. The
  only consumer group is the gateway relay, and it falls behind only while it is
  down — a CD deploy restarts the gateway (ADR-18 calls those outages) and
  Kubernetes caps `CrashLoopBackOff` at 5 minutes, so **10 minutes** covers a
  deploy, a backoff cycle and a manual restart. `50/s x 600s = 30_000 entries`.
  The two assumptions in that chain (the kill rate and the outage window) are
  stated in the constant's doc comment so the number can be re-derived rather
  than guessed at when either changes.

  Cross-checked against the ceiling it is meant to stay clear of: an
  `entity_killed` entry is a short type string and a small JSON payload, under
  256 bytes including stream node overhead, so the trimmed stream tops out near
  **7.3MiB — about 6% of the 128mb `maxmemory`**. That relation is the point. The
  stream is bounded by how far a consumer may fall behind, and is nowhere near
  large enough to be what exhausts the instance; if Redis ever does refuse a
  write, `XLEN events:game` is the thing to rule out first, not the thing to
  blame.

  **Consequence, stated plainly:** past that window entries are dropped rather
  than delivered, so at-least-once delivery is now explicitly a promise to a
  consumer that is *running*. A relay down for more than ten minutes at full rate
  comes back to a gap, not a backlog — and it will not be told about the gap,
  because a trimmed entry leaves no trace. That is the deliberate trade: these
  events (world announcements, cross-map loot) are worth delivering because they
  are timely, and a ten-minute-old `boss_killed` has already lost the property
  that made it worth the write.

  The approximate form (`~`) is used rather than exact: Redis trims whole
  radix-tree nodes and stops at the first one it may not drop, so it removes
  entries in cheap batches and may leave somewhat more than N. Exact trimming
  would make every publish pay for entry-precise deletion in order to enforce a
  number that is itself a rounded-off lag budget — real cost for false precision.

  `SetMaxLen` is available for tests and for an operator who needs different
  retention, and deliberately **cannot** be used as an off switch: a
  non-positive value keeps the default instead of removing the bound, since an
  unbounded stream against a `noeviction` Redis is the whole failure being fixed.
  This lands *before* a real publisher exists — the C# side still publishes into
  `NoopEventStream` (ADR-5), which is why the bug has not bitten. That was luck,
  not design: the window between wiring the relay up and filling 256Mi is however
  long it takes to fill 256Mi, and nobody wiring up an event relay expects to be
  making a memory-exhaustion change.

### Added
- **`JoinTokenResponse.TickRate` — the simulation tick rate on the wire
  (`wire.proto` field 4).** Closes
  [#93](https://github.com/Cuvara/rpg-mmo-server/issues/93), the same defect as #91
  one field over. The tick rate was never sent: server and client agreed on 15Hz
  only because two hardcoded literals, in two repositories, happened to match — and
  the server could be moved off that value by configuration the client had no way to
  observe. Set `SIM_CRITICAL_HZ=30` and you get a server that starts cleanly, a
  client that joins and renders and logs nothing unusual, and a player who reports
  rubber-banding. **Nothing in that chain fails**, and no test could catch it,
  because the client was not wrong about anything it could see — it was never told.

  `uint32 tick_rate = 4` on `JoinTokenResponse`, purely additive: no existing field
  number or order moved, so old and new peers interoperate in both directions. The
  value is the **CRITICAL** rate — the cadence of input, movement and combat, which
  is what a client replays when it predicts — not the world rate that governs
  snapshot cadence.

  It rides the join response rather than the snapshot because the rate is
  session-constant: the server reads it once at startup and never changes it, so
  putting it on `SnapshotMessage` would pay bytes per player per tick forever to
  re-send a number that cannot move. It is not on `EnterWorldResponse` because that
  comes from the gateway, which does not run the simulation (ADR-3) and would become
  a second source of truth for a value the game server owns.

- **`tick_rate` absent or `0` means "not supplied", and a client must refuse to
  predict.** proto3 elides a zero, so a pre-0.x server is indistinguishable from one
  reporting 0Hz — which is not a rate. Receivers must **not** fall back to 15:
  silently defaulting is exactly the assumption this field removes, and would
  reintroduce the bug while looking like it had been fixed. The Go
  `messages.JoinTokenResponse` mirror carries the field as
  `TickRate uint32 \`json:"tick_rate,omitempty"\`` — the Go side only ever decodes
  this message, since the game server, not the gateway, produces it.

- **`EntitySnapshot.Speed` — per-entity movement speed on the wire (`wire.proto`
  field 9), for client-side prediction.** Closes
  [#91](https://github.com/Cuvara/rpg-mmo-server/issues/91), which was the last
  silent failure mode in the client's prediction loop: prediction replays local input
  through the same `Shared.GameLogic` movement code the server runs, that code needs
  a speed, and nothing on the wire carried one. The client could only assume
  `ServerDefaults.DefaultPlayerSpeed`. The assumption holds until anything changes a
  player's speed — a buff, a mount, a slow — and then the two sides integrate
  different distances every tick with **no error on either side**. It presents as
  rubber-banding, which reads as a network problem and gets debugged in the wrong
  layer.

  `float speed = 9` — fixed32, so 5 bytes per entity per message with no varint
  shrink for common values. Written on **every** mention including handle-only ones:
  a receiver that resolves a handle expects complete state, and sending it only
  beside the id would leave it correct once per keyframe interval and stale between.
  Deliberately not interned — that would buy ~5 bytes and inherit the whole
  handle-lifecycle contract for a value with no identity.

- **`speed <= 0` means "not sent", not "immobile" — a rule, not an accident.**
  proto3 elides a zero float, so a sender predating this field is indistinguishable
  from a stationary entity. Receivers must fall back to a configured default;
  trusting the value outright means an old server pins a client's predicted speed to
  zero and the local player stops moving. `TestSpeedZeroMeansNotSent` pins the wire
  behaviour the rule rests on, across both encodings.

- **`TestSpeedSurvivesBothEncodings`** — guards against one codec dropping the field
  and not the other, which would surface only as prediction drift on whichever
  encoding a client happened to negotiate.

### Changed
- **`TestJSONWireShapeUnchanged` rebaselined.** `speed` is written unconditionally,
  like every other entity value field in `EntitySnapshot` — no `omitempty` — so the
  legacy JSON shape gains a key on every entity. Additive and safe for a legacy
  reader: both codecs skip unknown keys. The round-trip fixture now carries a
  non-zero speed, because a zero would round-trip identically through a codec that
  dropped the field entirely and the test would pass vacuously.


### Removed
- **`constants.PlayerLocationKey` (`"player:location:"`), which had neither a reader
  nor a writer in either repository** ([#210](https://github.com/Cuvara/rpg-mmo-server/issues/210)).
  A one-line deletion earns a changelog entry because this constant had already cost
  a real verification run. It is the same shape as #204: something declared,
  plausible, referenced in documentation, and never wired — and like #204 the
  declaration was invisible to every test, because a constant with no users cannot
  fail one. What made it findable was documentation: the client repo's multi-client
  checklist told an operator to expect three `player:location:*` keys in Redis after
  three clients join, and a live run against `k3d-rpg-dev` on 2026-08-22 found
  **zero** while every other row of that checklist passed. The checklist manufactured
  its own false negative, and an operator working it top to bottom had every reason
  to call the run broken. That row is corrected separately in
  Cuvara/IndieRPGMMOAdventure#38.

  **Deleted rather than implemented**, which was the other legal ending. Cross-server
  player lookup ("which server is this player on", for whispers, party join and admin
  tooling) is a real need but is not planned work, and the game server already owns
  per-player position inside its own world — nothing today has to ask Redis where a
  player is. A declared key that no code path honours is a claim the codebase does
  not keep, and the cost of the claim is not zero: it misled one verification run and
  would have misled the next. Restoring it, if cross-server lookup is ever wanted, is
  one commit — and it would then arrive with the writer on join/leave, the TTL
  aligned to `SessionTTL`, and the reader that were always missing.

  No code change accompanies the deletion because there was no code to change; the
  rest of this change is documentation that still believed in the key.
  `backend/docs/ARCHITECTURE-DECISIONS.md` (ADR-1's "also dead" note and its
  follow-up item), `backend/docs/CORE_FLOW.md` (the unused-constants item),
  `backend/gateway/docs/DESIGN.md` (which listed the tracking as a "stub", though
  nothing was ever stubbed) and `backend/gateway/CLAUDE.md` (which instructed the
  gateway to "update player location in Redis: `player:{user_id}:location =
  server_id`", a step no gateway has ever performed) are all corrected here rather
  than left for the next person to rediscover.


### Added
- **`SnapshotState.Apply` now rejects a keyframe that carries a bare interned
  handle, instead of resolving it against the outgoing interval's table.**
  To be plain about the status: this is **latent, not a live bug**. No sender in
  this repo can produce the triggering frame — `SnapshotDeltaState.EncodeFull`
  clears its handle table and restarts numbering at 1 *before* encoding, so every
  entity in a keyframe carries both `id` and `handle`, and `wire.proto` states
  the same contract. Nothing was breaking. This is defence against a future or
  third-party sender.
  It is worth guarding because of *how* it fails rather than how likely it is.
  Handles reset at every keyframe, so a bare handle on a keyframe still resolves
  — against bindings from the interval the keyframe is ending. The lookup
  succeeds, no error is raised, and the entity is silently rebound to whatever
  last held that number: one entity's updates rendered as another's, undetectable
  downstream. The guard therefore does **not** consult the table at all when
  `Full` is set; consulting it is the failure.
  Ordering is unchanged — the check still runs in the resolve pass, before any
  mutation, so a rejected keyframe leaves state untouched rather than clearing
  the world and refilling it a resync later. Returns `ErrUnknownHandle` (wrapped,
  so existing `errors.Is` recovery paths keep working) with a message naming the
  keyframe case.
  Raised by the Unity client team while auditing their implementation against
  `gameserver-dotnet/docs/API.md`; the doc was corrected in the same change and
  now describes this behaviour normatively.
  Covered by `TestKeyframeWithBareHandleIsRefusedNotResolvedAgainstStaleTable`,
  which sets up a *resolvable* stale binding specifically so the test fails if
  the guard is removed and the lookup is allowed to succeed.
- `GatewayKickChannel` constant (`"gateway:kick"`) in `constants/keys.go` for
  cross-gateway duplicate-login Pub/Sub coordination
- **MsgPing/MsgPong (type 11/12) heartbeat messages.** `PingMessage{timestamp}`
  carries the sender's monotonic clock; `PongMessage{timestamp, server_time}`
  echoes it back with the responder's wall clock. Both JSON and Protobuf
  encode/decode are implemented. Wire type numbers are frozen.

- **MsgKick (type 15) server→client forced disconnect.** `KickMessage{reason}`
  carries a machine-readable reason string. Defined reasons: `duplicate_login`,
  `server_shutdown`, `session_expired`, `rate_limited`.

- **JTI claim in join tokens.** `SignWithServer` now generates a UUID v4 `jti`
  claim when `serverID` is non-empty. Enables per-server replay protection.
- **5-second clock skew tolerance.** `Claims.IsExpired()` now accepts tokens up
  to 5 seconds past their `exp`, so small clock differences between gateway and
  game server do not cause spurious rejections. Constant: `jwt.ClockSkew`.

### Changed
- **BREAKING (Go API):** `Config.EffectiveJoinTokenSecret` removed. `JOIN_TOKEN_SECRET`
  is now mandatory; there is no fallback to `JWT_SECRET`.
- **MsgTransferMap / MsgTransferMapResp (types 13/14).** Wire messages for
  client-driven map transfer. `TransferMapRequest` carries a `map_id`;
  `TransferMapResponse` carries `ok` and `error`. Protobuf + JSON codec support
  added in `messages/` and `proto/wire.proto`.

### Added
- **Entity-id interning on the protobuf wire.** A realistic entity id costs ~17
  bytes on every mention; repeat mentions now carry a varint `handle` instead.
  Measured at ~51% of downstream bandwidth.

  Handles are per connection, allocated from 1, **reset at every keyframe**, and
  **never reused within an interval**. The id is sent once per interval, on the
  message that introduces the handle. `handle = 0` means "not interned", so JSON
  connections and any peer that does not implement this are unaffected.

- `ErrUnknownHandle`. `SnapshotState.Apply` now returns an error and **applies
  nothing** when a snapshot references a handle it has no binding for.
  Resolution happens before any mutation, because a half-applied snapshot is
  worse than an unapplied one — it looks like valid state. The caller's correct
  response is to request a keyframe, which re-introduces every binding.

### Changed
- **BREAKING (Go API):** `SnapshotState.Apply` returns `error`. Ignoring it means
  silently accepting a desynchronised stream.

### Added
- **`EntityType` enum on the protobuf wire.** A string entity type costs 8 bytes
  ("player" = tag + length + 6 characters) for a value drawn from a set of two;
  the enum costs 2. Measured at ~15% of a whole 50-entity snapshot payload and
  ~19% of a packed entity.

  `EntitySnapshot.type_name` remains as a string fallback, used ONLY when the
  enum cannot express the value, so a simulation that grows a new entity kind
  before the schema does degrades to the old cost rather than dropping the type.
  Exactly one of the two fields is ever set.

  JSON is unchanged — the enum is a protobuf-wire optimisation, not a
  protocol-wide change of meaning, and a pre-enum client still parses
  `"type":"player"` as text.

  The Go mapping (`entityTypeToPB`) and the C# one (`EntityTypes`) are
  hand-mirrored and both pin the exact name set, because a name added to one and
  not the other would silently degrade that type to the string fallback in one
  language only.

### Added
- `ErrInvalidMsgType`. Message type 0 is now rejected both when an envelope is
  constructed and when one is decoded. This is a correctness guard, not a
  formality: sniffing narrows a body to one of two decoders but cannot tell a
  real Protobuf envelope from arbitrary bytes that happen to be valid Protobuf —
  a body beginning `0x12` parses cleanly as an `Envelope` carrying only field 2
  and leaves the type at 0. That previously produced a typeless envelope and **no
  error**, a silent half-parse. Decoding now fails closed. Rejecting type 0 at
  construction protects the other half of the invariant: proto3 elides a zero
  field 1, so a type-0 envelope would encode without the `0x08` prefix and be
  sniffed as the wrong encoding by the peer.
- **`shared/proto/wire.proto` — the single source of truth for the realtime wire
  format.** The Go bindings are generated into `shared/proto/gen` (package
  `wirepb`) by `shared/proto/generate.sh`, which also emits the C# side into the
  game server. Generated code is committed, so no CI runner needs `protoc`
  installed to build or test either module.
- The `messages` package now speaks **both JSON and Protobuf**. `Envelope` gained
  an `Enc` field — transport metadata, never serialized — and `Payload` is now
  `[]byte`; custom `MarshalJSON`/`UnmarshalJSON` keep the JSON wire shape
  byte-identical to what shipped before.
- Encoding is detected from the first byte of the frame body, not negotiated: a
  JSON body always starts with `{` (0x7B), a Protobuf `Envelope` always starts
  with 0x08 (the tag for field 1, `type`, which proto3 never elides because
  `type` is >= 1 for every real message). Those cannot collide, so there is no
  handshake, no version field, and the 4-byte length framing is unchanged.
- New API: `Encoding` (with `ParseEncoding`/`String`), `SniffEncoding`,
  `EncodeBody`, `DecodeBody`, `NewEnvelopeAs`, and `Envelope.Reply` — the last
  two are how a caller answers in the encoding it was addressed in.
- JSON remains the default. On its own this changes no bytes on the wire.

### Changed
- **BREAKING (Go API only, not the wire):** `messages.UnmarshalPayload(payload,
  v)` is gone as a free function and is now a method, `env.UnmarshalPayload(v)`.
  The encoding travels with the bytes it describes, so a payload can no longer be
  decoded with the wrong codec by forgetting to thread the encoding through the
  call. Every caller in the repo is updated.

### Added
- `redisstore.ClientOptions` + `NewRedisClientWithOptions` — explicit timeout,
  retry and pool configuration for every Redis client this package builds
  (`Default*` constants; the zero value of each field falls back to the default,
  and a negative value is preserved because go-redis reads it as "disabled").
  The client was previously built with only `Addr`/`Password`, so every call
  inherited go-redis defaults and an unreachable Redis could occupy a caller for
  tens of seconds. Verified against a stopped Redis container: a `Get` now fails
  in ~1.4s instead of hanging (DR audit **G5**)
- `redisstore.Ping` — liveness probe with a bounded timeout independent of the
  caller's context, for readiness handlers
- `redisstore.EventStream.SetLogger` / `GroupLosses` — observability for
  consumer-group recovery

### Fixed
- **Event relay could die silently after a Redis wipe.** `XREADGROUP` against a
  missing consumer group returns `NOGROUP`, which the consume loop treated as a
  generic transient error: it retried at 2Hz forever, logged nothing, and never
  re-created the group, so the process looked healthy while the relay was
  permanently dead. `NOGROUP` is now detected specifically, the group is
  re-created, and the event is counted (`GroupLosses`) and logged. Proven
  against a real Redis with `FLUSHALL` mid-subscription: recovery is automatic
  and delivery resumes (DR audit **G4**)
- **Memory and Redis session stores disagreed on the "missing key" contract.**
  `redisstore` returned `storage.ErrNotFound` while `storage/memory.go` returned
  a bare `fmt.Errorf(... not found)`, so no `errors.Is` check could tell a
  missing key from an infrastructure failure on the memory backend. Every
  memory-store "not found" now wraps `storage.ErrNotFound`. This is what makes
  the gateway's Redis-blip fix work on both backends (DR audit **G6**)
- `errors.Is(err, code)` in `shared/errors` used a bare type assertion, so it
  returned false for any wrapped `GameError` — and the repo-wide convention is
  to wrap with `%w`. It now uses `errors.As`, so classification survives
  wrapping instead of silently falling through to the default branch
- `NewEventStream` gives its client a read timeout above the `XREADGROUP` block
  duration; with the new bounded defaults an equal-or-smaller read timeout would
  turn every idle poll into a spurious i/o timeout

### Added
- `ratelimit` package — shared token-bucket limiter. `Bucket` (lock-free, zero
  allocation, 10.8 ns/op — embed per connection) and `Limiter` (keyed per
  IP/user, mutex-guarded, TTL eviction with `StartCleanup`/`Stop`). A nil
  `*Limiter` and the zero `Bucket` both allow everything, so "disabled" needs no
  nil checks. Per-process scope; Redis-backed is the production upgrade (ADR-8)
- `jwt.Keyring` — secret rotation. `JWT_SECRET`/`JOIN_TOKEN_SECRET` accept a
  comma-separated list: the first entry signs, every entry verifies, so rotating
  a secret no longer invalidates every live token. The zero `Keyring` fails
  closed (rejects everything, refuses to sign). Adds `Claims.IsZero()`
- `transport.WithKey` / `WithLogger` / `Encrypted` / `DeriveKey` / `KeyEnvVar` —
  opt-in KCP encryption using kcp-go's AES-256 `BlockCrypt`. `TRANSPORT_KEY`
  takes 64 hex chars (used verbatim) or a passphrase (HKDF-SHA256 stretched).
  Empty = plaintext, and a KCP listener now logs a WARN when it starts
  unencrypted. Encryption fails closed: a peer with the wrong key never
  establishes a session
- `config`: `JoinTokenSecret` (`JOIN_TOKEN_SECRET`), `TransportKey`
  (`TRANSPORT_KEY`), `GatewayConnRatePerMin`/`GatewayConnBurst`/
  `GatewayMsgRatePerSec`/`GatewayMsgBurst`, and
  `Config.EffectiveJoinTokenSecret()`
- `messages.SnapshotMessage` gained three `omitempty` fields for the delta snapshot
  protocol: `ack_tick` (newest client input tick the server accepted for this player —
  the client's reconciliation anchor), `full` (this snapshot is a keyframe carrying
  the complete AOI set) and `removed` (entity IDs that left the AOI/world on a delta).
  All are omitted when default, so a keyframe-only stream is byte-identical to the
  previous wire format and pre-delta readers keep working.
- `messages.MsgResync` (type 10) — client → gameserver request for a full keyframe.
- `messages/snapshot_state.go` — `SnapshotState`, the Go reference implementation of
  the client-side keyframe/delta merge (keyframe replaces, delta upserts + removes,
  `tick`/`ack_tick` monotonic). Used by the smoke test and the integration tests so
  the merge rule is not reimplemented per consumer. C# mirror:
  `Shared.GameLogic.Systems.SnapshotMerger`. Wire reference:
  `backend/gameserver-dotnet/docs/API.md`.

### Changed
- `transport.Listen`/`Dial` take a variadic `...Option` tail. **Source
  compatible** — every existing call site compiles unchanged
- `storage/pgstore/schema.sql` — header comment only, no SQL change. The game-state
  schema is now owned by numbered migrations in `backend/deploy/db/migrations/gamestate/`
  applied by the C# gameserver; this package has been orphaned since that migration.
  The file is kept byte-identical to `deploy/db/init-gamestate.sql` because
  `TestSchemaMatchesDeployInitScript` compares them exactly.
- `Envelope.Payload` type changed from `[]byte` to `json.RawMessage` for C#
  interop — `System.Text.Json` on the gameserver-dotnet side expects a raw JSON
  object, not a base64-encoded byte array.

### Added
- `transport` package — pluggable realtime transport. `Listen(kind, addr)` /
  `Dial(kind, addr, timeout)` over `tcp` and `kcp`
  (`github.com/xtaci/kcp-go/v5`), plus `Normalize`/`Validate`/`Kinds`. KCP
  sessions get a documented game profile (nodelay 1/10/2/1, window 128/128,
  MTU 1350, stream mode, no FEC, no encryption — see `docs/DESIGN.md` for the
  production encryption TODO). Table-driven tests cover both kinds:
  round-trip through the `messages` codec, 50 sequential frames, 16 concurrent
  connections, a payload 8x the MTU (fragmentation), and dead-port dial
  semantics.
- `config`: `GatewayTransport` (`GATEWAY_TRANSPORT`) and `GameServerTransport`
  (`GAMESERVER_TRANSPORT`), both defaulting to `tcp`.
- `messages.EnterWorldResponse.Transport` — tells the client which transport
  the assigned game server speaks. `omitempty`; empty means `tcp`.
- `storage.ServerInfo.Transport` — the transport a registered game server
  listens with, persisted by `redisstore.ServerRegistry` as the `transport`
  hash field. `omitempty`; empty means `tcp`, so entries written by older game
  servers stay valid.


### Fixed
- `pgstore` container tests were flaky in CI: the postgres entrypoint runs
  initdb against a temporary unix-socket-only server before restarting the real
  one, so `pg_isready` reported ready while TCP clients still got
  "connection reset by peer". `newTestStore` now retries `NewPlayerStore` for up
  to 60s instead of failing on the first connect.
- Cross-server event stream name mismatch: gameserver published to
  "events:game" (double-prefixed to "events:events:game" by the store) while
  the gateway relay subscribed to "global" — events never arrived. Both sides
  now share constants.GameEventStream ("game", store adds the prefix once).

### Added
- `storage/pgstore` — PostgreSQL implementation of `storage.PlayerStore`
  (`PostgresPlayerStore`, pgx v5 / `pgxpool`) targeting the game state database:
  upsert on save (`ON CONFLICT (user_id) DO UPDATE`), `storage.ErrNotFound`
  mapping on a missing row, connect-time `Ping` so a bad DSN fails at boot, and
  an idempotent `Migrate(ctx)` that applies the embedded `schema.sql`
  (`player_states` + `player_states_map_id_idx`). `SchemaSQL()` exposes the SQL.
  New dependency: `github.com/jackc/pgx/v5` (isolated in its own package, like
  `redisstore`, so in-memory-only modules are unaffected).
- `config.Config.GameDBURL` from `GAME_DB_URL`, default empty — empty means "no
  PostgreSQL configured" and services fall back to their in-memory store.
- `pgstore` tests run against a real `postgres:16.4-alpine` started through the
  docker CLI on a random host port (save/load roundtrip, upsert overwrite +
  single-row assertion, delete, missing row → `ErrNotFound`, repeated `Migrate`,
  bad DSN). They `t.Skip` when no working docker CLI is available. A further test
  asserts `storage/pgstore/schema.sql` and
  `backend/deploy/db/init-gamestate.sql` stay byte-identical.

### Security
- `jwt.Verify` now validates the token header: `alg` must be `HS256` and `typ` must be
  `JWT`, checked before signature verification. Rejects `alg: none`, `HS512`, `RS256`,
  wrong/missing `typ`, and non-base64 headers. Public API unchanged.

### Added
- `storage/redisstore` package — Redis-backed implementations of the shared storage
  interfaces, kept out of `storage` so modules only pull in `go-redis` when they import it:
  - `redisstore.SessionStore` — `SET/GET/DEL/EXPIRE`, keys used verbatim
    (`constants.SessionKeyPrefix` + user id), TTL `constants.SessionTTL`
  - `redisstore.ServerRegistry` — server hash `servers:id:{server_id}` with
    `constants.ServerHeartbeatTTL` expiry + map index set `servers:map:{map_id}`;
    heartbeat-based liveness, lazy index pruning, Lua-guarded player-count updates
  - `redisstore.EventStream` — Redis Streams (`events:{stream}`) with consumer groups:
    `XADD` / `XGROUP CREATE MKSTREAM` / `XREADGROUP` / `XACK` after the handler returns
    (at-least-once delivery)
  - `redisstore.NewRedisClient(addr, password)` helper aligned with
    `config.Config.RedisAddr` / `RedisPassword`
- `storage.ErrNotFound` sentinel error, wrapped by the Redis implementations
  (`errors.Is`-testable)
- `SessionStore.Refresh(ctx, key, ttl)` — extend a session TTL without rewriting the value
- `ServerRegistry.Heartbeat(ctx, serverID)` and `ServerRegistry.GetServer(ctx, serverID)`
- `storage.NewMemoryServerRegistryWithTTL(ttl)` — in-memory registry with the same
  heartbeat-expiry semantics as Redis
- Table-driven tests for all Redis implementations using `miniredis` (TTL expiry asserted
  via `FastForward`, ACK asserted via `XPENDING`); tests for the new JWT header checks and
  the new in-memory methods
- `docs/API.md` and `docs/DESIGN.md`

### Changed
- `constants.ServerHeartbeatTTL` and `constants.EventStreamPrefix` are now actually used
  (registry liveness window, event stream key prefix)
- `MemoryServerRegistry` stores entries with a `lastSeen` timestamp. Expiry is opt-in:
  `NewMemoryServerRegistry()` keeps the previous never-expires behaviour
- Bump Go version to 1.26 (align with CI and gameserver)

### Dependencies
- Added `github.com/redis/go-redis/v9`
- Added `github.com/alicebob/miniredis/v2` (test-only)

### Added (earlier)
- Initial module setup with go.mod (`github.com/duycuong/rpg-mmo/shared`)
- CLAUDE.md agent instructions for Shared Architect role
