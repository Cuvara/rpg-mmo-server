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
4. **Swap CFB+CRC32 for the chosen construction** — Option E unless review capacity is
   short, then Option B. Go and C# together, with cross-implementation test vectors in both
   directions, generated by each side and verified by the other. Keep the fail-closed
   behaviour the current code has. For Option E the vectors are the deliverable, not an
   afterthought: an encrypt-then-MAC composition that is subtly wrong still round-trips
   against itself.
5. **Make it default-on**, or refuse to start unencrypted outside localhost. Until this
   step the previous four buy nothing in production.
6. Only then revisit Option D, against whatever the hosting shape has become.

---

## 3. What this roadmap deliberately does not do

- **No client-side anti-cheat** (see A6). It is high effort, defeated once and then defeated
  for everyone, and it is worth nothing before A1-A4 exist.
- **No obfuscation of the client binary.** Raises cost, changes no guarantee, and complicates
  every crash report.
- **No claim that §2 reduces cheating**, except A3 (replay protection), which is the one
  overlap and is listed as an anti-cheat item rather than a crypto one.
