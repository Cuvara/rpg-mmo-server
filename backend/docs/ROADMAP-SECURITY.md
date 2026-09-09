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

### 2.2 Library options, with concrete trade-offs

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
- **Verdict.** Best performance, least dependency risk, given §2.1. Verify the IL2CPP
  question first.

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
- **Verdict.** Reject for the per-packet path. Reconsider only if a standards-compliant DTLS
  handshake becomes a requirement.

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

1. **Report the posture.** Make the server log and export which transport it speaks and
   whether a key is in force. **This does not depend on any decision above and should be
   done first**, because today a deployment that believes it is encrypted and is not has
   nothing telling it so — the exact silent-failure class this repo keeps recording.
2. **Verify `AesGcm` under IL2CPP on every shipping target.** A throwaway build, not a
   reasoning exercise. This is the go/no-go between Option A and Option B.
3. **Settle per-session keys (§2.1)** — a gateway change and a game-server change, no
   crypto yet.
4. **Swap CFB+CRC32 for the chosen AEAD**, Go and C# together, with cross-implementation
   test vectors in both directions. Keep the fail-closed behaviour the current code has.
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
