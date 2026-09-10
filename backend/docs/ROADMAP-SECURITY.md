# Security roadmap — anti-cheat and transport confidentiality

Status: **plan, nothing here is implemented.** Written 2026-09-09.

Companion to [ADR-21](ARCHITECTURE-DECISIONS.md#adr-21--transport-confidentiality-what-exists-is-a-pre-shared-key-on-the-non-default-transport-and-it-is-not-a-session-key),
which records the measured posture. This document is the plan ADR-21 deferred, plus the
anti-cheat work that ADR-21 is **not**.

---

## 0. The thing to settle first: encryption is not anti-cheat

This roadmap was commissioned as "the security task, because it is anti-cheat". Those are
two different problems and the distinction decides what is worth building.

**A cheater is a legitimate player.** They hold the client binary, the session, and any key
that ships inside the client. Transport encryption defends the *path between* two endpoints
against a third party — someone on the same wifi, a hostile ISP, a middlebox. It does
nothing about the person at the other end sending dishonest input, because that person is
supposed to be sending input.

Concretely, transport encryption stops: packet sniffing of other players' positions,
tampering with packets in flight, replaying a captured packet at a third party.

It does **not** stop: speed hacks, teleport, infinite attacks, botting, packet crafting by
the player, memory editing, or a modified client. Every one of those is produced *by the
endpoint that holds the key*.

So the anti-cheat budget belongs in §1, and §2 is a genuine but separate concern.

---

## 1. Anti-cheat: what exists, and what is actually missing

### 1.1 What already protects the game, verified in code

The architecture is already doing the heavy lifting, and this is worth stating because it
means the remaining work is narrow rather than foundational.

**Server authority on movement.** `Shared.GameLogic/Systems/MovementSystem.cs` integrates
position as `direction * speed * dt`, where `speed` is the entity's server-side stat. Its
own comment is the guarantee: displacement depends on the entity's speed stat, *"never on
how many input packets a client sends."* A client that floods input does not move faster;
it just wastes bandwidth. **This is the single most important anti-cheat property in the
system and it is already true.**

**Direction normalisation.** `ResolveDirection` normalises the input vector, so a client
sending `(1000, 1000)` is clamped rather than obeyed. Diagonal movement is the same speed
as cardinal by construction.

**Input validation.** `ValidationLogic.Validate` rejects input from a dead entity, rejects
an unresolvable move direction, and for attacks resolves the target and defers to
`CombatLogic.ValidateAttack` (range and cooldown, against the server's own tick).

**Uplink flood guard.** `MaxInputsPerConnection` / `--max-inputs-per-tick` bounds how much
input one connection can submit per tick.

**Session binding.** The gateway issues a `JoinToken` with a `jti`, and duplicate-login
eviction is keyed by it (ADR-20).

### 1.2 What is missing, ranked by value against effort

These are the items that would actually reduce cheating. **This is where the budget should
go, ahead of §2.**

| # | Gap | Why it matters | Effort |
|---|---|---|---|
| A1 | **No telemetry on rejected input.** `ValidationLogic` returns an error string and the server drops the input; nothing counts rejections per player over time. | A cheater probing the rules generates a rejection pattern no honest client produces. Today that signal is discarded. **Counting it is cheap and it is the foundation every later detection rests on.** | Low |
| A2 | **No per-account rate/anomaly budget.** `MaxInputsPerConnection` is a per-tick cap, not a behavioural budget. | Distinguishes "hit the cap once on a lag spike" from "sat at the cap for ten minutes". | Low |
| A3 | **No replay protection on the game hop.** Nothing binds a packet to a session and a position in the stream. | A captured packet can be re-sent. A session-keyed MAC with a sequence number closes this — and this is the one place §2's work genuinely helps anti-cheat. | Medium |
| A4 | **Attack validation is per-attack, not per-rate.** `ValidateAttack` checks range and cooldown for one attack; nothing audits sustained attack rate against what the cooldown permits. | Cooldown bypass is the classic combat cheat. | Medium |
| A5 | **No server-side plausibility audit on position.** Integration is authoritative, so position cannot be forged directly — but there is no check that a client's *claimed* input pattern is physically plausible over time. | Defence in depth; low priority precisely because A-authority already holds. | Medium |
| A6 | **Client-side anti-cheat: none.** | Deliberate. Client-side anti-cheat on an IL2CPP build raises the cost of cheating, it does not prevent it, and it is defeated once and then defeated forever by everyone. **Do not invest here before A1-A4.** | High, low value |

**Recommendation: A1 and A2 first.** They are days, not weeks, they are pure server-side,
they need no protocol change, and without them nothing else can be measured. A detection
system built before the telemetry exists is a system nobody can tune.

---

## 2. Transport confidentiality: the actual plan

> **SUPERSEDED IN PART by [ADR-22](ARCHITECTURE-DECISIONS.md#adr-22--transport-crypto-chacha20-poly1305-over-an-authenticated-x25519-exchange-with-the-nonce-as-the-replay-counter) (2026-09-10).** The section below recommended **AES-CTR + HMAC-SHA256** and, before that, the built-in `AesGcm`. Both are wrong and the reasons are recorded rather than deleted: `AesGcm` **compiles and then throws** in a built IL2CPP player, and a hand-composed encrypt-then-MAC reintroduces every ordering and comparison error an AEAD removes. ADR-22 settles the model as **ChaCha20-Poly1305 over an authenticated X25519 exchange with the nonce as the replay counter**. The §2.1 insight that the gateway is already a trusted key-distribution point still holds, and shipped as #288 — but its derivation has **no forward secrecy** and is itself superseded. Read ADR-22 before implementing anything in this section.


Read ADR-21 first for the current posture. Summary of what is wrong today: encryption
exists (`KcpCrypto.cs`, AES-256-CFB, kcp-go compatible) but is **off by default twice**
(transport defaults to `tcp` which has no encryption path; the key variable defaults to
empty), uses **one pre-shared static key for every client**, has no rotation story, and
pairs CFB with a linear CRC32 — confidentiality without authentication.

### 2.1 The design insight that removes the hard part

The usual reason transport crypto is expensive is key exchange: two parties who do not know
each other must agree on a secret, which means Diffie-Hellman, certificates, or a PKI.

**This system does not have that problem.** ADR-3 already has the client authenticate to the
gateway over a separate, already-trusted channel, and receive `{ServerAddr, JoinToken}`.
The gateway is therefore **already a trusted key distribution point**.

So: have the gateway mint a **per-session random key** alongside the JoinToken, hand it to
the client in the same response, and hand it to the game server through the channel that
already carries session state. No DH, no certificates, no handshake round trip on the game
hop, and the static-shared-key problem disappears — each session gets its own key, and a key
extracted from one client binary is worthless because there is no key in the binary.

This is the single highest-leverage decision in §2 and it should be settled before any
library is chosen.

### 2.2 MEASURED: what the Unity client's runtime actually provides

**Run before choosing, because it eliminated the original recommendation.** Probe executed
2026-09-09 in Unity 6000.3.9f1, batchmode, `Mono 6.13.0`:

```
AesGcm                 : THREW PlatformNotSupportedException
ChaCha20Poly1305       : TYPE DOES NOT EXIST in this Unity BCL profile
Aes (raw block)        : OK   (this is what KcpCrypto already uses)
HMACSHA256             : OK
HKDF (RFC5869)         : TYPE ABSENT (derive manually from HMACSHA256)
AES-CTR+HMAC throughput: 155 us per 1200B packet (6k packets/s)
```

`AesGcm` **compiles and then throws at runtime** — the type is in the reference assembly and
the implementation is not there. It also carries the older .NET Standard 2.1 shape: no
`IsSupported`, no two-argument constructor. That is the worst failure mode available, because
it type-checks: an implementation written against it passes review, passes compilation, and
fails on a player's device.

**This kills the original recommendation.** The plan below is rewritten around the
measurement rather than around what the BCL is supposed to offer.

Caveat, stated rather than hidden: this is **Mono in the Editor**. IL2CPP player builds share
the same managed class libraries, so the same failure is expected, but it has NOT been
confirmed by a player build. Confirm before committing engineering time — the cost of being
wrong is one build, the cost of assuming is a rewrite.

The 155 us figure is a deliberately naive implementation (per-block `TransformBlock`, a fresh
`ComputeHash` allocation per packet) measured on a loaded machine. Treat it as an upper bound,
not a benchmark.

### 2.3 What the measurement means for the choice

The asymmetry that decides this: **a client handles very few packets and the server handles
many.**

- A client sends input at ~13-20 Hz and receives snapshots at 15 Hz — call it 35 packets/s.
  At the measured 155 us that is **5.4 ms per second, about 0.5% of one core.** Software
  crypto on the client is affordable even at the naive figure.
- A server at 200 players x 15 Hz is ~3000 packets/s outbound. That is where hardware
  acceleration matters — and the server is .NET 10 and Go, where `AesGcm` and
  `ChaCha20Poly1305` are real, hardware-accelerated and stdlib.

So the constraint is not performance. **It is that both ends must speak the same
construction**, and the client's runtime is the one with no AEAD.

### 2.4 Library options, re-ranked against the measurement

The constraint that eliminates most candidates: the client is Unity (IL2CPP), targeting
Android, Windows and Linux at minimum, and **a UPM package cannot declare a scoped
registry** — so any dependency must be source-only or a vendored binary, never an OpenUPM
reference.

#### Option A — .NET built-in AEAD (`AesGcm` / `ChaCha20Poly1305`) — **recommended**

Server uses `System.Security.Cryptography`; Go uses `crypto/cipher` GCM from the stdlib;
Unity uses the same BCL types.

- **Strengths.** No new dependency on any side — this alone is decisive given the UPM
  constraint. AES-GCM is hardware-accelerated on every current x86-64 and arm64 CPU (AES-NI
  / ARMv8 crypto extensions), so the per-packet cost is far below the current CFB software
  path. Authenticated by construction, which fixes the CRC-is-not-a-MAC problem in §2's
  summary. Go and .NET implementations are both stdlib and well tested against each other.
- **Weaknesses.** **Nonce management is the footgun** — GCM with a repeated nonce under the
  same key is catastrophic, not merely weak. With per-session keys (§2.1) a simple counter
  nonce is safe and simple, which is another reason to settle §2.1 first. Breaks kcp-go
  wire compatibility, so the Go and C# sides must move together. **`AesGcm` availability
  under IL2CPP on every target platform MUST be verified before committing** — this is the
  one claim in this section that is not established, and it is the go/no-go.
- **MEASURED VERDICT: unavailable on the client.** `AesGcm` throws
  `PlatformNotSupportedException` under Unity's Mono and `ChaCha20Poly1305` does not exist at
  all. **Still correct for the SERVER side** (.NET 10 and Go both have it, accelerated), but
  the server cannot use a construction the client cannot answer, so this cannot be the wire
  format on its own.

#### Option B — libsodium (XChaCha20-Poly1305) via a vendored native plugin

- **Strengths.** The hardest of these to misuse: XChaCha20's 24-byte nonce makes random
  nonces safe, removing the counter-management burden entirely. ChaCha20 is fast in software
  on devices without AES acceleration (older Android). Well-audited.
- **Weaknesses.** **A native binary per platform** — Android arm64 and armv7, Windows,
  Linux, macOS, iOS — each vendored into the package, each a build-matrix risk and an app
  size cost. Cannot work at all where native plugins cannot (a browser target). Upgrades are
  manual. For a benefit that Option A plus per-session keys already provides.
- **Verdict.** Choose this only if Option A fails the IL2CPP check, or if a target platform
  turns out to lack AES acceleration and profiling shows it matters.

#### Option C — BouncyCastle (pure managed C#)

- **Strengths.** Pure C#, so it runs anywhere IL2CPP runs with no native plugin and no
  platform matrix. Includes a DTLS implementation, if a full standard handshake is ever
  wanted.
- **Weaknesses.** **No hardware acceleration** — this is a per-packet cost at 60 Hz on
  mobile, the worst place to pay it. Large assembly to vendor. Adds a substantial dependency
  surface to a package that currently has almost none.
- **Re-ranked UP by the measurement.** With the built-in AEAD unavailable on the client, pure
  managed is no longer a compromise, it is one of only three ways to get an AEAD there at all.
  The performance objection is also weaker than it looked: at ~35 packets/s a client can
  afford software crypto. Still carries a large vendored assembly.


#### Option E — AES-CTR + HMAC-SHA256, encrypt-then-MAC, from primitives that are present

Not a new dependency and not a new primitive: `Aes` and `HMACSHA256` are both **measured
working** in Unity, and both are stdlib in Go and .NET. Encrypt-then-MAC is a standard,
well-specified composition; this is assembling two standard pieces in the standard order, not
inventing a cipher.

- **Strengths.** **Zero dependencies on every side** — decisive given a UPM package cannot
  declare a scoped registry, and given the measurement above. Available on every platform
  Unity targets, because it uses only what `KcpCrypto` already relies on. Authenticated, which
  is the actual defect being fixed. Affordable at client packet rates (§2.3).
- **Weaknesses.** **More code to get exactly right than calling an AEAD**, and the details are
  the kind that fail silently: MAC over ciphertext *and* nonce, constant-time comparison,
  encrypt-then-MAC ordering, no key reuse between the cipher and the MAC. A test vector suite
  shared with the Go side is mandatory, not optional. Slower than hardware AEAD, which matters
  on the server — though the server can keep a fast path only if both ends agree, so in
  practice the server pays software cost too.
- **Verdict.** **The leading candidate**, on the strength of having no dependency and being
  confirmed available. Choose it if a review of the construction is affordable; choose
  Option B if it is not, because misimplementing this is worse than a native plugin matrix.

#### Option D — terminate TLS at the infrastructure edge

- **Strengths.** No client crypto code at all, no key distribution problem, real PKI with
  forward secrecy, and rotation becomes certificate renewal — an ops task, not a redeploy of
  every client.
- **Weaknesses.** **Blocked on the hosting shape**, which ADR-15/16 leave unsettled. Awkward
  for KCP, which is UDP. Adds a hop in the gameplay data path, which ADR-3 deliberately kept
  short.
- **Verdict.** Not now — it is the right answer *if* the deployment ever grows a proper
  edge, and revisiting it then costs nothing if Option A is in place.

### 2.3 Sequencing

1. **DONE (2026-09-09).** **Report the posture.** The game server now derives its posture
   once at startup (`GameServer/Net/Transport/TransportPosture.cs`), logs it on every boot —
   at Warning whenever traffic is in cleartext — and publishes it on `/status` as
   `transport`, `transport_key_configured`, `transport_encrypted`,
   `transport_authenticated`, `transport_cipher` and `transport_posture`, mirrored as the
   `gameserver_transport_encrypted` / `gameserver_transport_authenticated` **gauges**.
   Gauges rather than counters because a never-incremented counter is absent from
   `/metrics`, and a security question must not be answered by a missing field.

   What this replaced warned about exactly two of the four combinations — KCP without a
   key, and a key set on TCP. **The default configuration, TCP with no key and no
   encryption of any kind, logged nothing at all**: the configuration most likely to be
   deployed by accident was the only one that produced no signal. Verified live on
   containers in all four combinations plus a loopback bind.

   `transport_authenticated` is published while permanently `false`, on purpose: AES-CFB
   with a CRC32 is confidentiality without integrity, and folding the two into one
   "secure" flag would let an operator read "encrypted" as "safe from tampering".

   **Still open: the Go gateway has the same shape** — `shared/transport/transport.go`
   warns only for KCP-without-a-key, so a gateway on plaintext TCP is as silent as the game
   server used to be. Not fixed here to keep this step independently mergeable.
2. **DONE, and it changed the answer.** `AesGcm` throws under Unity's Mono and
   `ChaCha20Poly1305` is absent (§2.2). Remaining verification is one IL2CPP player build to
   confirm the same holds there, which is expected but unproven.
3. **DONE (2026-09-09).** **Per-session keys**, and the shape changed during
   implementation. The original sketch had the gateway "hand the key to the game server
   through the channel that already carries session state". Two findings killed that:

   - **A key in the join-token claims is public.** Claims are base64, not encrypted —
     decoded from a freshly minted token with no secret to prove it. The token travels on
     the gameplay hop, so a key inside it is readable by the eavesdropper it is meant to
     stop.
   - **Delivering it server-side would tie encryption to Redis.** The session store defaults
     to in-memory, so the default stack could not encrypt at all — a security feature off in
     the configuration people actually run.

   Both ends therefore **derive** it instead:
   `HKDF-SHA256(ikm = JOIN_TOKEN_SECRET, salt = jti, info = "cuvara/session-key/v1", L = 32)`.
   The gateway returns it to the client in `EnterWorldResponse` (`wire.proto` field 5,
   Protobuf only); the game server computes the same value from the secret it holds and the
   `jti` it already verifies. Nothing carrying key material crosses the gameplay hop, no
   Redis, no extra round trip, and no key in the client binary. Rooting session keys in
   `JOIN_TOKEN_SECRET` adds no new class of failure — holding it already lets you mint a
   join token for any user — and the `info` string is the domain separation.

   **What it buys, stated precisely.** Before: one static key in every binary, so compromise
   was universal and permanent. After: a fresh key per join, so compromise requires an
   eavesdropper on the gateway hop and buys that one session. **It is not end-to-end
   confidentiality**, because the client cannot derive the key and the gateway hop is the
   same transport stack as the gameplay hop — plaintext TCP by default. Step 5 must
   therefore cover the gateway hop as well, or steps 3 and 4 buy much less than they appear
   to.

   Verified live: a real handshake against gateway and game-server containers returned a
   32-byte key equal to the independently derived one, the material appeared zero times in
   either container's logs, `/status` or `/metrics`, and 3/3 players then completed a full
   join. Cross-implementation golden vector shared between the Go and C# tests.
4. **DONE (2026-09-10).** **Swap CFB+CRC32 for the chosen construction.** ADR-22: an
   authenticated X25519 exchange into HKDF-SHA256 into ChaCha20-Poly1305, from
   `golang.org/x/crypto` and BouncyCastle — no hand-written primitive on any side.
   Published RFC vectors pass on both and a shared cross-implementation vector pins the
   bytes between them. Normative format: `backend/docs/SEALED-FRAMING.md`. Note this
   **supersedes** the CFB+CRC32 transport layer rather than replacing it in place: that
   layer still exists for KCP and is still unauthenticated, which is why
   `transport_authenticated` remains permanently `false`. The original wording below is
   kept for the reasoning.

   Original: Option E unless review capacity is
   short, then Option B. Go and C# together, with cross-implementation test vectors in both
   directions, generated by each side and verified by the other. Keep the fail-closed
   behaviour the current code has. For Option E the vectors are the deliverable, not an
   afterthought: an encrypt-then-MAC composition that is subtly wrong still round-trips
   against itself.
5. **DONE on the game server (2026-09-10), NOT YET IN ANY DEPLOYMENT.** `GAMESERVER_SEALED`
   defaults to `require`: a stock game server encrypts the gameplay hop and refuses every
   client that cannot seal. `off` is now a deliberate, reviewable choice rather than the
   value an operator gets by saying nothing.

   **What this does not yet buy, stated precisely**, because the sentence this replaces
   said the previous four steps "buy nothing in production" and that is still nearly true:

   - **The deploy verifier can now check a sealed stack. This blocker is closed.**

     It was real: `post-deploy-smoke` and `verify.sh`'s `flow.smoke` both run the Go
     smoketest against the stack they just deployed, and the smoketest sent JSON, which can
     never carry a sealed frame — so a `require` server refused it at the join with
     `encoding_cannot_seal`. Measured live, not inferred: the sealed arm failed with
     `read server hello: read length: EOF` and the server named the cause in its own log.

     **The diagnosis of WHY was wrong, and the error is worth more than the fix.** This
     document said the smoketest "hand-rolled `encoding/json` with no encoding switch", that
     its JSON-ness was "much of its value" as an independent second implementation, and that
     protobuf was therefore "a decision, not a chore". None of that was true. It calls
     `messages.NewEnvelope`, which is `NewEnvelopeAs(EncodingJSON, ...)` in
     `shared/messages` — the same codec the gateway and the load generator use. There was no
     hand-rolled encoder and no independence to lose. **Two sessions asserted it in writing,
     one restating the other's words more strongly, and neither opened the file.** The fix
     was an `-encoding` flag and four call sites.

     Verified live against a `require` server, all three populations, each refused or
     admitted for a distinct reason in the server's own log:

     | client | result |
     |---|---|
     | JSON | refused — `encoding_cannot_seal` |
     | protobuf, sealing | **`SMOKE=PASS`**, `sealed=true binding_verified=false`, 16 snapshots |
     | protobuf, no hello | refused — `sealed handshake failed (NoHello)` |

     `binding_verified=false` is the shipped-client state and is reported rather than
     implied: the smoketest receives its join token from the real gateway, so it holds no
     `JOIN_TOKEN_SECRET` and cannot verify the server's binding. The load generator, which
     mints its own tokens, is still the only peer that proves the man-in-the-middle defence.

     **What remains before an environment can be set to `require`** is only the CD
     generator: it must derive `SMOKE_SEALED=1` *and* `SMOKE_ENCODING=proto` together,
     because a sealed run must be a protobuf run. `backend/deploy/stack.sh` derives both;
     `cd.yml` does not yet, and says so at the line.

     Every deploy path now pins `off` explicitly — compose and its override, the two Agones
     fleet manifests (**dev runs `DEPLOY_MODE=k8s`, so the compose pin covers none of it**),
     host mode, and the CD `.env` generator, which is the one reviewable place an
     environment can opt in. So the flip changes the default only for servers started
     *outside* deployment config: the integration suite, a developer's `dotnet run`,
     `kcpprobe`. That is exactly the population the new tests cover, and none of the
     deployed ones.

     The local compose file pins `off` for its own reason: the Unity sample scenes that dial
     a live backend are the netcode package's acceptance path, and they gained a
     `requireSealedSession` toggle only in Netcode #132.
   - **The rollout is two artefacts.** A Unity client must set
     `NetworkSettings.RequireSealedSession`, and that ships in a built player, not in a
     deployment variable.
   - **The gateway hop is untouched**, as is the join handshake that precedes the sealed
     session. The residual named in step 3 — "step 5 must therefore cover the gateway hop
     as well" — is **not** closed by this step. The join token still crosses the gameplay
     hop in the clear before sealing begins.

     **MEASURED, 2026-09-10, and the residual as written above understates it.** A byte tap
     was placed on *both* hops of a fully sealed session — 26 sealed frames on the gameplay
     hop, `SMOKE=PASS`, `sealed=true` — and every JWT crossing either hop in the clear was
     decoded:

     | hop | credential | lifetime | single-use |
     |---|---|---|---|
     | gateway | **auth token** | **3600 s** | **no** |
     | gateway | join token | 30 s | yes |
     | gameplay | join token | 30 s | yes |

     **REPRODUCED 2026-09-10 and correct — but it was pointed at the wrong end of the
     path. See [ADR-23](ARCHITECTURE-DECISIONS.md#adr-23--the-gateway-hop-gets-tls-not-a-second-sealed-handshake-and-the-credential-it-exposes-leaks-one-hop-earlier).**
     Every row above re-measured true, and the single-use column — which a tap cannot
     show — was measured separately by replaying each captured credential from a fresh
     connection. Both taps are now committed as
     `integration_test/hop_confidentiality_tap_test.go` rather than being a one-off.

     What the table omits is the hop that **mints** the auth token. The client obtains it
     from Nakama's `gateway_token` RPC over **plain HTTP**, and a tap on that hop reads,
     in the clear:

     | hop | credential | lifetime | single-use |
     |---|---|---|---|
     | **meta (Nakama)** | **Nakama session token** | **7200 s** | **no** |
     | meta (Nakama) | Nakama refresh token | 3600 s | no |
     | meta (Nakama) | **the gateway auth token itself** | 3600 s | no |
     | meta (Nakama) | the Nakama server key (HTTP Basic) | n/a | no |

     `docker-compose.yml` starts Nakama with no `--socket.ssl_certificate`, `NAKAMA_URL`
     is `http://` everywhere, and `deploy/k8s/data/nakama.yaml` exposes 7350 with no
     Ingress and no TLS. **There is no environment in which this hop is encrypted.**

     That inverts consequence 2 below in one respect worth stating plainly: the auth token
     is *not* the most valuable credential on the path either. The **two-hour Nakama
     session token** is, because it mints fresh one-hour auth tokens on demand for as long
     as it lives, and it is exposed on a hop no bespoke protocol of ours can protect —
     Nakama is a third-party binary, so the answer there is TLS in front of it, not a
     handshake we write.

     **A caution about the instrument, because it was wrong first.** The initial capture of
     the meta hop found no auth token, and reporting that as good news would have been
     easy. The token was there and the response was **gzip-encoded**; a byte scan for
     `eyJ...` cannot see through gzip. Compression is not confidentiality. Re-running with
     `Accept-Encoding` suppressed showed all four credentials, and a real Unity client
     sends `Accept-Encoding: gzip` too — so any future tap on an HTTP hop must account for
     content coding before reporting an absence.

     Two consequences, and the first changes what step 5 should do:

     1. **Sealing the join exchange on the gameplay hop alone would buy nothing.** The
        *identical* join token — same `jti` — crosses the **gateway** hop in the clear
        first. An observer positioned to read one hop reads the other: it is the same
        client on the same path. Closing the gameplay-hop half leaves the value it was
        protecting already spent.
     2. **The exposure the residual names is the least valuable of the three.** The join
        token is 30 seconds and single-use, so capturing it wins a race the real client is
        already running. The **auth token** on the gateway hop is a **one-hour, reusable
        bearer credential**, and it was not recorded anywhere until this measurement.

     So the ordering is settled by evidence rather than preference: **the gateway hop is
     the work**, and the join exchange is not a separate item — it is closed as a
     side-effect of closing the gateway hop, and cannot usefully be closed before it.

     **For whoever takes the meta hop, from the tap that found it:**

     - **The Nakama server key crosses that hop as HTTP Basic**, in the same capture as
       the tokens. Unlike them it never expires — it is a **static shared secret present
       in every client build**. Decide whether it is in or out of scope up front rather
       than discovering it mid-task.
     - **Any tap on an HTTP hop must suppress `Accept-Encoding` before reporting an
       absence.** The first meta-hop capture found no auth token; it was there, gzipped,
       and a byte scan cannot see through gzip. A "clean" result on that hop is not
       believable until compression is ruled out. This trap is met first, not last.
     - **The probe that measures this is written and verified:**
       `backend/docs/tls-probe/`. It is self-contained — an embedded self-signed
       certificate and a loopback listener, no network — and it asserts the **refusal**,
       not the success. Every API was executed on .NET 10 first, and it was mutation-tested
       in both directions: degrading validation fails assertion 1, and a callback receiving
       `SslPolicyErrors.None` fails assertion 4.

       **RUN, and the answer is GO on Windows.** A real Windows IL2CPP player on Unity
       `6000.3.9f1`, at **both** `Minimal` and `High` stripping, passes all four
       assertions — including the refusal. High stripping changing nothing is the part
       that was in doubt: the linker keeps what TLS needs with no `link.xml` entry.
       Details in `backend/docs/tls-probe/README.md`, including three ways the player
       differs from .NET 10 (it negotiates **Tls12**, `CipherAlgorithm` reports **`None`**,
       and the refusal message does not name `UntrustedRoot`) — asserting on any of them
       would pass on .NET and fail in a player. **Android is still unanswered** and
       nothing here transfers to it, for the same reason ADR-22's BouncyCastle result is
       Windows-only.

     - **When `SslStream` under IL2CPP is measured, assert the NEGATIVE case.** ADR-22
       found `AesGcm` present in the reference assembly and throwing at runtime — it
       type-checks and then fails. Certificate *validation* is more likely to be quietly
       **degraded** than absent, and validation that silently accepts anything is
       indistinguishable from validation that works. Assert that a connection to a
       deliberately bad certificate is **REFUSED**, not only that a good one succeeds.

     **The reorder option, recorded so it is not re-proposed.** Sealing the join token on
     the gameplay hop requires the client to send its hello first, carrying the `jti` in
     the clear so the server can derive the binding key — from an unverified,
     attacker-chosen value. That moves a full X25519 + HKDF + HMAC *ahead of any token
     check*, where today an unauthenticated peer costs the server one failed HMAC. It is a
     real amplification, it was considered, and it is not worth taking for a benefit that
     (1) above shows to be zero while the gateway hop is plaintext.
   - **"Refuse to start unencrypted outside localhost" was not taken.** A server can still
     be started with `off` on any bind. That refusal was considered and rejected for this
     change: `TransportPosture.BindsBeyondLoopback` reads compose's `:9000` as beyond
     loopback, so it would break CI and every local stack while reading like a
     production-only guard — a disguised blast radius, which is worse than a wide one.
     The posture is instead reported loudly on every boot and on `/status`
     (`sealed_required`, `sealed_cipher`).
6. **The gateway hop is settled by [ADR-23](ARCHITECTURE-DECISIONS.md#adr-23--the-gateway-hop-gets-tls-not-a-second-sealed-handshake-and-the-credential-it-exposes-leaks-one-hop-earlier)
   (2026-09-10), and not the way this roadmap assumed.** It gets **TLS terminated in the
   gateway process**, behind `GATEWAY_TLS_CERT`/`GATEWAY_TLS_KEY`, defaulting off and
   pinned explicitly at every deploy path. A second sealed handshake for this hop was
   **rejected**: the Go server half of the sealed protocol does not exist (only the C# one
   does), the hop has no `jti` to anchor a transcript, and an unauthenticated exchange
   would make `BindingVerified` a field that is always false. Read ADR-23 before proposing
   a sealed gateway handshake again.

   **Implemented server-side; the flag is off everywhere and cannot be turned on yet.**
   The Unity client speaks raw TCP to the gateway and there is no plaintext fallback by
   design, so enabling it today refuses every player. **The client needs two things, in
   `Cuvara/Netcode`:** TLS on the gateway connection, verified in an IL2CPP *player* build
   with certificate validation ON; and `https://` for the Nakama base URL.

7. **The meta hop is settled by [ADR-24](ARCHITECTURE-DECISIONS.md#adr-24--the-meta-hop-gets-nakamas-own-tls-but-the-credential-worth-stealing-there-is-a-default-valued-static-key-not-a-token)
   (2026-09-10) — and the confidentiality half turned out not to be the important half.**

   **Nakama terminates TLS itself**, behind `NAKAMA_TLS_CERT`/`NAKAMA_TLS_KEY`, defaulting
   off and pinned at every deploy path, with `NAKAMA_URL` following the same decision
   because the **C# game server is a second consumer of this hop**
   (`GameServer/Nakama/NakamaClient.cs`). Measured coverage: `:7350` **including the `/ws`
   realtime socket**, TLS-only; the console `:7351` and metrics `:9100` are **not covered**
   and publish on `0.0.0.0` in compose.

   **The larger finding is an authentication bypass, not an eavesdrop.** Both Nakama static
   keys were at their published defaults and both authenticate — `defaulthttpkey` reaches
   the handler for the **server-only** `reward_kill` / `submit_kill` RPCs — and `cd.yml`
   **never wrote `NAKAMA_HTTP_KEY` at all**, so every deployed compose environment ran the
   default. Encrypting this hop while that was true would have closed the window and left
   the door unlocked, in a way that reads as "secure" in every log. CD now fails the deploy
   on a missing or default key.

   Two exposures TLS does **not** close, recorded rather than discovered later:

   - **The realtime WebSocket puts the session token in the QUERY STRING**, and
     `runtime.http_key` likewise. Both are upstream Nakama API shapes we cannot change
     without forking or proxying it. TLS protects the wire; it does not protect the access
     log.
   - **Session refresh re-issues both tokens over the same hop**, so the exposure repeats
     for the life of the client rather than being a login-time window.

   Still open on this hop, in priority order: the console and metrics ports (plaintext, and
   published on `0.0.0.0` in compose); the Nakama->Postgres DSN, which specifies no
   `sslmode` in either deployment; and the compose/k8s asymmetry that makes the reward path
   inert under Agones.

   **A caution for the next tap on an HTTP hop, learned twice now.** The first meta capture
   missed a gzipped token and would have reported the hop clean. The second under-reported
   the distinct-JWT count because Nakama's `exp` has second granularity, so a refresh in the
   same second returns a **byte-identical** token — a 2 s sleep produced the predicted five.
   Both were caught only because a number was written down before it was measured.

8. Only then revisit Option D, against whatever the hosting shape has become.

---

## 3. What this roadmap deliberately does not do

- **No client-side anti-cheat** (see A6). It is high effort, defeated once and then defeated
  for everyone, and it is worth nothing before A1-A4 exist.
- **No obfuscation of the client binary.** Raises cost, changes no guarantee, and complicates
  every crash report.
- **No claim that §2 reduces cheating**, except A3 (replay protection), which is the one
  overlap and is listed as an anti-cheat item rather than a crypto one.
