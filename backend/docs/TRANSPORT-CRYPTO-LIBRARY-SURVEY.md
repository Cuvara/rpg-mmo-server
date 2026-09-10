# Transport crypto: which pure-C# library — surveyed and verified

**Closes the second open question in [ADR-22](ARCHITECTURE-DECISIONS.md#adr-22--transport-crypto-chacha20-poly1305-over-an-authenticated-x25519-exchange-with-the-nonce-as-the-replay-counter):
*"Which pure-C# library for the client, and whether it passes RFC 8439 and RFC 7748
vectors under IL2CPP in a player build."*** The first half is answered here with
measurements. The second half needs a player build and is specified here as a probe.

**Recommendation: `BouncyCastle.Cryptography` 2.7.0, one library for both runtimes.**
Verified against published RFC vectors and cross-checked against Go `x/crypto` in both
directions. The cost is size, and it is real: 4.7 MB and 2 350 public types.

---

## 1. What had to be true

From ADR-22, restated as the test each candidate had to pass:

| # | Requirement | Why it is not negotiable |
|---|---|---|
| 1 | ChaCha20-Poly1305 (RFC 8439) **and** X25519 (RFC 7748) | X25519 is absent from **two of three** runtimes — Unity IL2CPP *and* .NET 10 |
| 2 | Pure managed, no P/Invoke | A UPM package cannot declare a scoped registry, and a native binary is a per-platform build-matrix cost |
| 3 | Licence shippable in a game client | Stated explicitly per candidate, not assumed |
| 4 | Small dependency surface | The netcode package has almost none today |
| 5 | Maintenance signal | Reported as findings, not as a verdict |
| 6 | No AOT-hostile constructs | IL2CPP breaks on things the Editor never shows |

## 2. How each claim was checked

Nothing here is taken from a package description.

- **"Pure managed"** — the assembly metadata was read directly and every method scanned
  for the `PinvokeImpl` flag. Zero P/Invoke is proof, a README is not.
- **"No AOT-hostile constructs"** — assembly references and type references were scanned
  for `System.Runtime.Intrinsics`, `System.Runtime.CompilerServices.Unsafe`,
  `System.Numerics.Vector` and `System.Reflection.Emit`.
- **Correctness** — **published RFC vectors**, never a round trip. An implementation that
  is subtly wrong still round-trips against itself and reports no error anywhere.
- **Interop** — encrypt in Go with `x/crypto`, decrypt in C#, and the reverse, over a real
  X25519 exchange with random keys.

> **One transcription error, recorded because it is the point.** The first run reported the
> RFC 8439 tag as `…cbd0060691` and **both** libraries produced `…cbd0600691`. Two
> independent implementations agreeing against the expectation means the expectation is
> wrong: the RFC's tag is `1a:e1:0b:59:4f:09:e2:6a:7e:90:2e:cb:d0:60:06:91` and a digit
> pair had been transposed in transcription. Go then confirmed the libraries. **A single
> implementation checked against a hand-copied vector would have "failed" and been
> discarded.** This is the argument for the cross-check, not a footnote to it.

## 3. Result — ranked

### 1st — `BouncyCastle.Cryptography` 2.7.0 — **recommended**

| Property | Measured |
|---|---|
| ChaCha20-Poly1305 | `Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305` — **RFC 8439 §2.8.2 PASS** |
| X25519 | `X25519Agreement`, `X25519PrivateKeyParameters` — **RFC 7748 §6.1 PASS**, both directions |
| HKDF-SHA256 | `HkdfBytesGenerator` — **RFC 5869 A.1 PASS** |
| Tamper rejection | Throws on a flipped tag bit — **PASS** |
| Go interop | C#→Go and Go→C# over a real X25519 exchange — **PASS** |
| Pure managed | **0 P/Invoke methods** |
| Dependencies | **none** — `netstandard2.0` build references only `netstandard` |
| AOT-hostile refs | none of Intrinsics / Unsafe / Vector / Reflection.Emit |
| Licence | **MIT** (the "Bouncy Castle Licence", which the project states is to be read as MIT) |
| Released | **2026-07-30**, 23 versions |
| Size | **4 771 KB**, 2 350 public types |

**It is the only candidate that satisfies requirement 1 at all**, which is the finding that
decides this survey. Everything else either lacks X25519 or fails its vectors.

**The cost, stated plainly.** 4.7 MB and 2 350 public types is a lot to add to a package
that currently has almost no dependencies, and under IL2CPP every type the linker keeps is
converted to C++. There is also a **third-party report of an Android IL2CPP build failing
at the Unity CIL Linker step on BouncyCastle.Cryptography 2.x** — which is why §5's probe
must be run at `High` stripping with a `link.xml`, not only at `Minimal`.

### 2nd — `NaCl.Core` 2.2.0 — AEAD only, does not solve the problem

| Property | Measured |
|---|---|
| ChaCha20-Poly1305 | **RFC 8439 §2.8.2 PASS**, tamper rejection PASS |
| **X25519** | **ABSENT** — 12 public types, none related |
| Pure managed | **0 P/Invoke** |
| Dependencies | **none** on `netstandard2.1` and `net10.0` |
| Licence / released | **MIT** / **2026-07-03** |
| Size | **33 KB** (`netstandard2.1`) |
| Extra signal | tests against **Project Wycheproof** (Google's known-attack suite) |

An excellent AEAD in 33 KB — 145× smaller than BouncyCastle, and if the AEAD were all that
were needed this would be the recommendation. It is not: **the client needs X25519 too**,
and pairing NaCl.Core with a separate X25519 library is where the lightweight route dies
(§4).

## 4. Rejected, with the reason

| Candidate | Reason |
|---|---|
| **NSec.Cryptography** (16.2M downloads), **Sodium.Core**, **Geralt** | libsodium wrappers — **native**. Disqualified by requirement 2, not by quality |
| **Rebex.Elliptic.Curve25519** 1.2.2 | **Fails RFC 7748 §6.1 through its public API.** Derived Alice's public key as `d23c65b6…` against the RFC's `8520f009…`, and the two sides did not even agree with each other (`4d7e7399…` vs `f28f2e58…`). The raw `Elliptic.Curve25519` class is `internal`, so the correct primitive — if it is in there — is not reachable. Apache-2.0, 25 KB, and unusable as it stands |
| **NaCl.Net** 0.1.13 (15.2M downloads) | **MPL-2.0** (file-level copyleft, weaker fit than MIT) and **last released 2020-07-24** — six years without a release for a crypto dependency |
| **Easy-X25519, nebulae.dotX25519, Whyvra.Crypto.X25519, Curve25519.NetCore, P3.Elliptic.Curve25519, ofcoursedude.Curve25519** | Download counts from **378 to ~40 000**, single-author, no audit or Wycheproof signal. Rejected on maintenance for the one component whose failure mode is silent |
| Hand-writing a curve, cipher or MAC | Forbidden by ADR-22 decision 4. Not evaluated |

**The finding worth carrying:** the two-small-libraries route does **not** fail on the
AEAD, where NaCl.Core is excellent. It fails on **X25519**, where there is no credible
small pure-C# implementation — the same primitive that is missing from two of the three
runtimes. That is what makes BouncyCastle's size worth paying.

## 5. Verified output

`ChaCha20-Poly1305`, `X25519`, `HKDF-SHA256`, on .NET 10.0.10:

```
RFC 8439 s2.8.2 -- ChaCha20-Poly1305 AEAD
  [PASS] BouncyCastle ciphertext
  [PASS] BouncyCastle tag
  [PASS] BouncyCastle decrypt
  [PASS] BouncyCastle rejects tampered tag
  [PASS] NaCl.Core ciphertext
  [PASS] NaCl.Core tag
RFC 7748 s6.1 -- X25519
  [PASS] BouncyCastle derives Alice public
  [PASS] BouncyCastle derives Bob public
  [PASS] BouncyCastle shared (A x Bpub)
  [PASS] BouncyCastle shared (B x Apub)
RFC 5869 A.1 -- HKDF-SHA256
  [PASS] BouncyCastle HKDF OKM

ALL VECTORS PASS
```

Go `x/crypto` v0.57.0 on the identical vectors, as the reference:

```
Go x/crypto -- RFC 8439 s2.8.2
  [PASS] Go ciphertext
  [PASS] Go tag
Go x/crypto -- RFC 7748 s6.1
  [PASS] Go shared secret
Go x/crypto -- RFC 5869 A.1 HKDF-SHA256
  [PASS] Go HKDF OKM
```

Cross-implementation, **random** keys and a real X25519 exchange rather than the fixed
vector, both directions:

```
=== C# -> Go ===
  [PASS] Go opened the C# frame: "the quick brown fox jumps over the lazy dog, 0123456789"
=== Go -> C# ===
  [PASS] C# opened Go's frame: "frame produced by Go x/crypto for the C# client"
```

## 6. What is still unproven, and the probe that settles it

**Everything above was measured on .NET 10, not under IL2CPP.** Per ADR-22's own lesson —
`AesGcm` compiled and then threw on a device — that is not sufficient, and this survey does
not claim otherwise.

`crypto-probe/Il2cppCryptoProbe.cs` in this directory is the probe. It asserts the same
published vectors in a built player and reports a throw as a result rather than losing it.
It is written for Unity's profile deliberately: **C# 9 at most** (no file-scoped namespaces,
no `u8` literals) and **no `Convert.FromHexString`/`ToHexString`**, which are .NET 5+ and
absent from the `netstandard2.1` profile Unity compiles against.

**Every API call the probe makes was executed on .NET 10 first**, so it cannot fail to
compile on an overload that does not exist:

```
  [PASS] NaCl.Core Encrypt(nonce,pt,ct,tag,aad) overload + vector
  [PASS] NaCl.Core Decrypt(nonce,ct,tag,pt,aad) overload round trip
  [PASS] NaCl.Core throws on a tampered tag
  [PASS] BC X25519KeyPairGenerator + SecureRandom
  [PASS] BC throws on a tampered tag
```

### RESULT — run 2026-09-10, and it passed

Built a Windows IL2CPP player twice and ran both. **Identical output at both stripping levels:**

```
Minimal stripping                              High stripping + link.xml
[PASS] ChaCha20-Poly1305 (RFC 8439 s2.8.2)     identical
[PASS] rejects a tampered tag                  identical
[PASS] X25519 (RFC 7748 s6.1)                  identical
[PASS] HKDF-SHA256 (RFC 5869 A.1)              identical
[PASS] key generation uses a working RNG       identical
```

The reported Android CIL-Linker failure **did not reproduce** with the `link.xml` below.

**The limit, which this document will not let drift:** that result is **Windows** IL2CPP. The
report is **Android**. An Android IL2CPP APK builds from this project — proved the same day — but
there was no device to run it on, so **the Android question is open and merely no longer blocking**.
Nothing here claims Android is confirmed.

BouncyCastle is therefore adopted, and ADR-22's library question is closed.

### How to run it

1. Copy `lib/netstandard2.0/BouncyCastle.Cryptography.dll` from the NuGet package into
   `Assets/Plugins/`. Optionally also `lib/netstandard2.1/NaCl.Core.dll`.
2. Add scripting define `CUVARA_BC` (and `CUVARA_NACL` if that DLL is present).
3. Attach `Il2cppCryptoProbe` to a GameObject, build a **Windows IL2CPP player**, run it,
   read the log.
4. **Run it twice: `ManagedStrippingLevel.Minimal` and `High`.** Minimal answers "does the
   maths work under AOT". High answers "does the linker keep what we need", which is the
   failure mode reported against BouncyCastle 2.x on Android and the one a `Minimal` build
   cannot see.

For the `High` run, `Assets/link.xml`:

```xml
<linker>
  <assembly fullname="BouncyCastle.Cryptography">
    <type fullname="Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305" preserve="all" />
    <type fullname="Org.BouncyCastle.Crypto.Agreement.X25519Agreement" preserve="all" />
    <type fullname="Org.BouncyCastle.Crypto.Parameters.X25519PrivateKeyParameters" preserve="all" />
    <type fullname="Org.BouncyCastle.Crypto.Parameters.X25519PublicKeyParameters" preserve="all" />
    <type fullname="Org.BouncyCastle.Crypto.Generators.X25519KeyPairGenerator" preserve="all" />
    <type fullname="Org.BouncyCastle.Crypto.Generators.HkdfBytesGenerator" preserve="all" />
    <type fullname="Org.BouncyCastle.Crypto.Digests.Sha256Digest" preserve="all" />
    <type fullname="Org.BouncyCastle.Security.SecureRandom" preserve="all" />
  </assembly>
  <assembly fullname="NaCl.Core">
    <type fullname="NaCl.Core.ChaCha20Poly1305" preserve="all" />
  </assembly>
</linker>
```

**A `link.xml` naming only the entry types is a guess about what the linker keeps
transitively.** If the `High` run fails, widen to `<assembly fullname="BouncyCastle.Cryptography" preserve="all" />`
and record the size cost rather than hunting types one at a time.

### What would change the recommendation

- The probe **throws or produces wrong bytes under IL2CPP** → BouncyCastle is out, and the
  problem becomes hard, because nothing else supplies X25519. Escalate rather than
  substituting a micro-package.
- The probe **passes at `Minimal` but fails at `High`** → keep BouncyCastle, widen the
  `link.xml`, record the build-size cost.
- **Build size proves unacceptable** → the AEAD can move to NaCl.Core (33 KB) but X25519
  cannot move anywhere, so BouncyCastle still ships. Splitting saves nothing; do not split.

## 7. Where each runtime gets each primitive, if this is adopted

| | ChaCha20-Poly1305 | X25519 | HKDF-SHA256 |
|---|---|---|---|
| **Go** (gateway, loadtest) | `x/crypto` — already a dependency | `x/crypto/curve25519` | stdlib |
| **.NET 10** (game server) | **stdlib** (`System.Security.Cryptography`) | **BouncyCastle** | **stdlib** |
| **Unity IL2CPP** (client) | **BouncyCastle** | **BouncyCastle** | **BouncyCastle** |

The server takes only X25519 from the library, because .NET 10 has the other two — measured
in ADR-22 and re-confirmed here, where `ChaCha20Poly1305` had to be disambiguated against
`System.Security.Cryptography.ChaCha20Poly1305` to compile at all.

**Using BouncyCastle for all three on the server instead is also defensible** and would make
client and server run the identical implementation, which is worth something for a protocol
whose failure mode is silent divergence. Not decided here; it is an implementation choice
and ADR-22 decision 5 (cross-implementation vectors) catches divergence either way.

## 8. Open

- **IL2CPP player verification** — the probe above. Not run; requires an Editor build.
- **Build-size impact** of a 4.7 MB managed dependency on the shipped player, at `High`
  stripping with the `link.xml`. Measure during implementation.
- **Whether the server uses BouncyCastle or stdlib** for the AEAD and HKDF (§7).
