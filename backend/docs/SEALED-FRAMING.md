# Sealed framing — normative wire format for realtime confidentiality

Status: **implemented, wired, and proved on a live server.** Off by default
(`GAMESERVER_SEALED=off`); `require` turns it on for the gameplay hop. ADR-22's library question is closed — ChaCha20-Poly1305 (RFC 8439), X25519
(RFC 7748) and HKDF-SHA256 (RFC 5869), from `golang.org/x/crypto` on the Go side and
BouncyCastle.Cryptography 2.7.0 on the C# side. Published RFC vectors pass on both, and a
shared cross-implementation vector pins the bytes between them.

Implementations: `backend/shared/sealed` (Go), `GameServer/Net/Sealed` (C#). A Unity
implementation follows. **All three must agree byte for byte** — the golden transcript
vector in each test suite is what holds them together.

---

## 1. Where the sealing happens

The existing frame is:

```
[4-byte big-endian length][Envelope protobuf]
```

Sealing wraps the **Envelope**, not the datagram, and the length prefix stays in the clear
because it is what finds the frame boundary.

**Why not at the packet layer.** The KCP path already has a packet-crypt layer under the
ARQ (`KcpCrypto`), and it cannot be used for this:

- it is **per-listener** — kcp-go takes one `BlockCrypt` for every datagram and offers no
  per-remote key selection, so it cannot carry a per-session key;
- **TCP is the default transport and has no such layer at all.**

Sealing above the transport is therefore identical on TCP and KCP and independent of the
ARQ, which is also what lets one implementation serve both hops (§6).

## 2. Sealed frame layout

```
[4B  length          ]  cleartext, existing framing
[1B  0xC1 marker     ]  ─┐
[1B  version = 1     ]   │ additional authenticated data
[8B  sequence, BE    ]  ─┘
[N   ciphertext      ]  AEAD(plaintext = Envelope bytes, aad = the 10 header bytes)
[16B tag             ]
```

**The marker.** The codec picks an encoding by sniffing `body[0]`: `0x08` is Protobuf (an
Envelope always begins with field 1, `type`, and type 0 is refused precisely so that byte
is stable) and `0x7B` is JSON's `{`. A sealed body begins with ciphertext, which is
indistinguishable from either, so it needs a marker of its own. `0xC1` cannot begin a
well-formed Envelope — as a protobuf tag it names field 24, and field 1 is mandatory.

**Everything in the header is authenticated.** An attacker cannot renumber a frame to
replay it, nor lower the version byte to reach an older format: either edit invalidates the
tag.

**Cost.** 10 header bytes + 16 tag bytes per frame. At 15 Hz that is ~390 B/s per client
against the 45.9 KB/s measured at 200 players — **0.85%**.

## 3. Nonce

```
nonce = 00 00 00 00 || sequence (8B big-endian)
```

The four zero bytes are not padding: they leave room for an explicit direction or rekey
epoch without changing the nonce length or the frame layout.

> **A bare counter is safe only because each direction has its own key.** Reusing a
> (key, nonce) pair with any AEAD in this family is catastrophic — it leaks the XOR of two
> plaintexts and, for Poly1305, can expose the authentication key. The client's sequence 7
> and the server's sequence 7 are encrypted under **different keys**, so they never
> collide. **If a future change ever makes one key serve both directions, the nonce must
> grow a direction byte on the same day, or the scheme is broken.**

The counter must never wrap. At 8 bytes and one frame per tick this is unreachable, but a
rekey — not a wrap — is the correct response if it ever approaches the limit.

## 4. Replay

The sequence must be checked by a validator, **after the tag verifies and never before**.
The sequence is cleartext, so acting on it first lets an attacker advance a peer's window
with forged frames and lock out the real sender — denial of service that costs nothing, and
which would present as a connectivity bug in the wrong layer.

**This ordering is enforced by structure, not by this paragraph.** `Session` (Go) and
`SealedSession` (C#) perform both steps themselves, in the only correct order, and expose
no way to do one without the other — there is no call site left that can reorder them. A
comment does not survive an optimisation pass; a type with no reordering API does. Each
side has a test that fails if the validator is consulted for a frame whose tag did not
verify, and both were checked against a deliberate mutation that reverses the order.

Two implementations exist behind one interface, because the choice depends on a transport
property still being measured — whether the ARQ can reorder at this layer:

| | rule | when it is correct |
|---|---|---|
| **StrictMonotonic** | accept only strictly increasing | transport cannot reorder (TCP; KCP in the reliable ordered mode configured here) |
| **SlidingWindow(64)** | accept anything unseen within 64 of the high water — the IPsec/DTLS rule | transport can reorder |

**Measured, and settled: strict counter, no window.** Zero inversions across 22 374 frames
on both transports under hostile `tc netem`.

Three conditions attach, because the ordering this rests on is **inherited, not owned** —
TCP guarantees it, and KCP gets it from a hand-ported reassembly path of roughly ten lines:

1. **The requirement is asserted, not assumed.** Each validator declares
   `RequiresOrderedTransport`, and a session refuses to construct when a strict counter is
   paired with a transport that does not promise ordering. A future QUIC-datagram or
   raw-UDP path therefore **fails closed on day one** instead of quietly dropping
   legitimate frames and presenting as packet loss.
2. **Rejections are counted, not merely performed.** A validator that refuses silently is
   indistinguishable from one that was never wired in, and under attack these counters are
   the only thing in the system that changes. Counted by cause — not authenticated,
   replayed, forward jump — and returned identically, so a peer learns nothing about which
   rule it hit.
3. **The forward jump is bounded** (`MaxForwardJump`, 1024). "Reject anything at or below
   the highest seen" stops replays and says nothing about a leap *forward*, which burns
   nonce space and, with a strict counter, is **irreversible**: every later legitimate
   frame carries a lower sequence and is refused for ever, so the session dies quietly
   after authenticating perfectly well. The exposure is bounded to begin with — a forged
   frame cannot advance anything, because it does not authenticate — so this defends
   against a confused or compromised peer rather than an outsider. On an ordered reliable
   transport the expected delta is exactly 1, so 1024 is slack, not a budget.

Both refuse what they cannot judge: a frame older than the window is rejected, because a
validator that cannot prove freshness must not claim it. `wire-contract`'s measurement
lands as a one-line change at the call site, not a rewrite.

## 5. Handshake

Both hellos are sent **in the clear** — there is no key yet.

```
client → server   ClientHello { client_public (32) }
server → client   ServerHello { server_public (32), binding (32) }
```

Shared secret from X25519 over the two ephemeral keys; per-direction keys derived from it
so §3's counter is safe. **Ephemeral per connection**: reusing a key across sessions
forfeits the forward secrecy that is the entire reason ADR-22 supersedes the earlier
derived-key scheme.

### The binding, and why it is not the join token

```
transcript = "cuvara/sealed-handshake/v1" || 0x00 || jti || 0x00
             || client_public (32) || server_public (32)

binding    = MAC(key derived from JOIN_TOKEN_SECRET and jti, transcript)
```

**The join token proves nothing to an eavesdropper's victim.** Its claims are base64, not
encrypted, so an attacker reads it off the wire and replays it. What an attacker cannot do
is compute a MAC keyed by material derived from `JOIN_TOKEN_SECRET`, which they do not
hold.

**Binding that MAC to the two ephemeral public keys is what defeats the
man-in-the-middle.** An attacker who substitutes their own key changes the transcript, so
the binding they can replay no longer verifies. Without the public keys in the transcript,
a replayed binding would authenticate the attacker's exchange as well as the real one, and
the MITM would be clean and undetectable.

**The NUL separators are load-bearing.** Without them the transcript is a concatenation
whose pieces can be re-split: a `jti` ending in one byte of what should be the next field
produces the same bytes as a different (jti, key) pair, and a MAC over it authenticates
both readings equally. Two bytes remove the whole class.

`Verify` must be **constant-time**. A byte-by-byte comparison leaks the position of the
first mismatch, which is enough to forge a tag one byte at a time against a peer that keeps
answering.

## 6. Does this apply to the gateway hop?

**The framing: yes, unchanged.** §2–§4 wrap an `Envelope`, and both hops speak Envelopes
over the same codec and the same transport stack. One implementation serves both.

**The handshake: no.** §5 is anchored in the join token's `jti` and `JOIN_TOKEN_SECRET`,
and on the gateway hop neither exists yet — the client has not been assigned a server and
holds only a Nakama JWT, which is as readable as the join token and so cannot anchor a
binding either.

The gateway hop needs a different anchor, and the adopted one is a **pinned gateway
identity key**:

```
1. Client pins the gateway's PUBLIC identity key at build time.
2. Client <-> gateway : X25519 authenticated by a signature under that key
                        -> the gateway hop becomes authenticated AND confidential.
3. Over that channel   : the gateway delivers the per-session binding key
                        (HKDF from JOIN_TOKEN_SECRET and jti -- shared/sessionkey).
4. Client <-> server   : X25519 bound by proof of possession of that key (§5).
```

**Solving the gateway hop dissolves the key-delivery problem rather than working around
it.** There is no third mechanism and no new wire field on the game hop: once step 2 makes
that channel confidential, step 3 is simply a value sent over it.

### This is not the pre-shared-key mistake wearing a hat

It will look like one, so state it plainly:

- The **old** scheme shipped a **secret** in the binary. Extracting it decrypted everyone,
  for ever.
- This ships a **public** key. Extracting it gains an attacker **nothing** — it is public
  by construction. It lets the client recognise the gateway; it does not let anyone
  impersonate it.

That difference is total, and a reviewer who misses it will reject the design for a
right-sounding wrong reason.

### Pin a set, not a key

**Pin current *and* next**, from the first version. Rotation otherwise becomes a flag day
on which every client that has not updated is locked out. The mechanism costs almost
nothing to build now and **cannot be retrofitted during the emergency that is the only
time it is wanted**.

Residual, named rather than softened: **compromise of the gateway's private identity key
is total for the gateway hop until clients update.** It is a genuine single point of
failure, and pinning a set shortens the window rather than removing it.

**This matters for sequencing.** ADR-22 decision 8 holds the model until the gateway hop is
confidential, and §5's binding is why: the client cannot compute the binding key itself, so
something must reach it over the gateway hop. Until that hop is confidential:

- a **passive** eavesdropper is defeated by X25519 — recorded traffic stays unreadable even
  if the long-lived secret leaks later, which the superseded derived-key scheme could not
  offer;
- an **active** attacker who can read the gateway hop can still mount a MITM.

So the gain is real and bounded, and it should be described that way rather than as
end-to-end confidentiality.

## 7. Refusal — no negotiation

A peer that does not complete the sealed handshake gets **no session**. Never a cleartext
session, never a weaker cipher, never a retry without encryption.

**A protocol that can be talked down to cleartext will be.** An attacker who can modify the
handshake removes the offer, both ends conclude the other could do no better, and the
session proceeds in the clear looking entirely healthy. The only reliable defence is to
have nothing to downgrade to — which is why the requirement has **two** values, not three.
A "preferred" mode is a downgrade attack with a friendly name.

**The JSON consequence.** The legacy JSON encoding cannot carry a sealed session: the frame
is a binary layout with a marker byte JSON has no room for, and the handshake fields are
deliberately absent from the JSON message set so that key material can never be rendered
into a human-readable payload. So once encryption is required, **a JSON client is refused,
not served in the clear** — which effectively deprecates the JSON encoding for any
deployment that requires encryption. That is a consequence to state, not to discover.

## 8. Libraries, and what is verified

| | Go | C# |
|---|---|---|
| AEAD | `x/crypto/chacha20poly1305` | BouncyCastle `ChaCha20Poly1305` |
| Curve | `x/crypto/curve25519` | BouncyCastle `X25519Agreement` |
| KDF | `x/crypto/hkdf` | BouncyCastle `HkdfBytesGenerator` |
| MAC | `crypto/hmac` (+ `hmac.Equal`) | BouncyCastle `HMac` (+ `Arrays.FixedTimeEquals`) |

Nothing in this repository implements a cipher, a MAC or a curve.

**Why BouncyCastle for all three on C# when .NET 10 has two of them.** .NET has
`ChaCha20Poly1305` and `HKDF` built in and both are measured working on 10.0.10, but it has
**no X25519 at all**, so BouncyCastle is required regardless. One library for all three
means the server and the Unity client run the *same* implementation, which removes a class
of interop question rather than answering it three times. The cost is real and worth
knowing: BouncyCastle's AEAD is managed code while .NET's is the platform's and
hardware-assisted. If the AEAD appears in a tick profile, swapping `SealedAead` alone is a
contained change — the wire format does not care which library produced the bytes, which is
exactly what the RFC vectors on both sides are for.

### Verified

- **Published RFC vectors on both sides**: RFC 8439 §2.8.2, RFC 7748 §6.1, RFC 5869 A.1.
  A round-trip proves an implementation agrees with itself, which a subtly wrong one also
  does — silently.
- **A shared cross-implementation vector**: one complete handshake (both publics, shared
  secret, transcript, both direction keys, binding tag) and one complete sealed frame,
  asserted value-by-value in both suites. Go seals the frame the C# test opens; C# seals
  the byte-identical frame the Go test opens.
- **Tampering rejected** in ciphertext, tag, additional data and length — the AAD case
  being the one an implementation can fail while every round-trip still passes.
- **Low-order X25519 points refused**, since accepting one forces a shared secret the
  attacker knows and both sides agree on: a complete break dressed as a successful
  handshake.

### Proved live, on real containers

Same image (`bf0ae5552b35`) both runs, one real client through the real gameplay hop, one
`tcpdump` inside the server's network namespace. A capture of ciphertext alone would only
show bytes are unreadable; the **pair** shows this change made them so.

| | `GAMESERVER_SEALED=off` | `GAMESERVER_SEALED=require` |
|---|---|---|
| cleartext Envelope frames | **13** | **6** |
| sealed frames (`0xC1`) | **0** | **11** |
| `probe-player` readable in payload | **3** | **1** |

The server logged `Sealed session established for probe-player (cipher=chacha20-poly1305)`,
and the client verified the server's binding — proving the server holds
`JOIN_TOKEN_SECRET`-derived material for that session.

**Wrong key: no session.** A client that completed the handshake correctly and then
corrupted one byte of its send key had every frame refused and the connection closed:
`session ended as required (read length: EOF)`. Nothing was accepted in cleartext.

> **The remaining readable bytes are the join exchange, and that is by design, not a
> gap left open by accident.** The handshake runs *after* `MsgJoinToken`, so the join
> token and the join response are still in the clear on the gameplay hop — which is the
> `probe-player ×1` and the `eyJhbGci…` above. An eavesdropper on that hop can therefore
> still capture the join token. It is short-lived and single-use (the jti tracker consumes
> it), so the exposure is a replay window measured in seconds rather than a durable
> credential — but it is real, it is not closed by this change, and sealing the join frame
> itself would require the key to exist before the client has spoken, which is the
> chicken-and-egg §5 exists to break.

### Still to do

- **A real Unity client in the loop**, and the **Android** IL2CPP question: the probe that
  passed was **Windows** IL2CPP, and the third-party CIL-Linker report was Android. Open,
  not blocking, and must not be described as confirmed.
- **The netcode package needs the same two messages and BouncyCastle vendored** before a
  Unity client can speak this.
- **Default-on** is a separate operational decision: turning it on refuses every client
  that cannot seal, which is every JSON client.
