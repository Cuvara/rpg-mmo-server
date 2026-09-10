# Sealed framing — normative wire format for realtime confidentiality

Status: **specification only.** No cipher, MAC or curve is implemented yet; ADR-22 has not
settled the library. Everything here is the part of the design that does not depend on that
choice, so that when the library lands the only new code is the primitive itself.

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
with forged frames and lock out the real sender — denial of service that costs nothing.

Two implementations exist behind one interface, because the choice depends on a transport
property still being measured — whether the ARQ can reorder at this layer:

| | rule | when it is correct |
|---|---|---|
| **StrictMonotonic** | accept only strictly increasing | transport cannot reorder (TCP; KCP in the reliable ordered mode configured here) |
| **SlidingWindow(64)** | accept anything unseen within 64 of the high water — the IPsec/DTLS rule | transport can reorder |

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

The gateway hop needs a different anchor. The obvious one, and the standard answer for game
clients, is a **static gateway identity key**: a long-term X25519 public key pinned in the
client build, with the same §2–§5 machinery and the binding replaced by a signature under
the gateway's identity key. No PKI, no certificate chain, and rotation is a client update —
which is the real cost and the reason it is a decision rather than an obvious win.

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

## 8. What is deliberately not here

No cipher, no MAC, no curve, and no stub of any of them. An implementation that "worked"
would let every test above it pass while proving nothing about the bytes — which is the
failure this repository keeps recording in other forms. When the library lands, the
deliverable is:

- **cross-implementation vectors**, Go ↔ C# ↔ Unity in every direction, plus the published
  RFC vectors. An implementation that is subtly wrong round-trips against itself and
  reports nothing;
- **live proof on real containers** that the traffic is actually ciphertext — a packet
  capture, or a peer with the wrong key failing to form a session. A green test is not that.
