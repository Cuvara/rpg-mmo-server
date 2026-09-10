# IL2CPP TLS probe

The **go/no-go** for ADR-23's gateway-hop TLS and for the client's `https://` Nakama hop.

## The question

Does `System.Net.Security.SslStream` work under IL2CPP — **and does certificate validation
actually enforce**?

The second half is the point. ADR-22 caught `AesGcm` present in the reference assembly and
throwing `PlatformNotSupportedException` at runtime: it type-checks, then fails loudly.
Certificate validation is likelier to fail **quietly** — degraded rather than absent — and

> validation that silently accepts anything is indistinguishable from validation that
> works, if you only ever test a good certificate.

So the assertion that decides this is a **refusal**, not a success. A probe that only shows
a good certificate connecting would pass on a platform where TLS is worthless.

## What it asserts

| # | assertion | why it is there |
|---|---|---|
| 1 | **an untrusted certificate is REFUSED** by default validation | if this fails, every TLS guarantee in ADR-23 is void |
| 2 | an explicitly trusted certificate **completes**, and reports what was negotiated | without it, (1) failing could equally mean "SslStream is broken", a different answer |
| 3 | the validation callback is **invoked** | a pinning client that is never called cannot pin |
| 4 | the callback receives a **real** `SslPolicyErrors`, not `None` | pinning code that trusts `None` for an untrusted certificate accepts anything — the same silent degradation as (1), one layer up |

## Self-contained on purpose

The probe starts its own TLS listener on loopback with an **embedded** self-signed
certificate. No network, no `badssl.com`, no DNS: a player on a locked-down device or an
offline machine still gives an answer, and the answer cannot be perturbed by someone else's
certificate expiring.

The certificate is embedded rather than generated at runtime because `CertificateRequest`
under IL2CPP is a *separate* question — a failure there would look like a TLS failure and
would not be one.

## Running it

1. Copy `Il2cppTlsProbe.cs` into `Assets/` in the Unity project.
2. Attach it to a GameObject in a scene.
3. Build a **Windows IL2CPP player**, run it, read the log.
4. Repeat with `ManagedStrippingLevel.High`. Stripping removes reflection-only paths and
   TLS pulls in a lot of them — `Minimal` answers "does the maths work", `High` answers
   "does the linker keep what we need".
5. Repeat on **Android** if a device is available. Android is the open question, for the
   same reason ADR-22's BouncyCastle result is Windows-only.

## Verified before shipping

Every API the probe calls was **executed on .NET 10 first**, so it cannot fail to compile
or throw for a reason unrelated to the question. On .NET 10 all four assertions hold:

```
[ OK ] default validation refuses an untrusted certificate
       (AuthenticationException: ... UntrustedRoot)
[ OK ] an explicitly trusted certificate completes  (Tls13 / Aes256)
[ OK ] the validation callback reports a real error (Tls13 / Aes256)
[ OK ] callback saw: RemoteCertificateChainErrors
```

And the probe was **mutation-tested**, because a probe that cannot fail proves nothing:

| mutation | result |
|---|---|
| client accepts any certificate by default (validation degraded) | assertion 1 **FAILS**, naming the consequence |
| callback receives `SslPolicyErrors.None` | assertion 4 **FAILS** |

The verdict line names the platform it actually ran on. A probe that reports "works under
IL2CPP" when run in the Editor is the same overclaim it exists to catch.

## What a pass does NOT mean

- It is one platform. IL2CPP and Mono use different class-library profiles, and Android is
  a separate answer from Windows.
- It says nothing about the certificate a real deployment presents, about rotation, or
  about a CA story. It answers only whether the client-side machinery works and enforces.
