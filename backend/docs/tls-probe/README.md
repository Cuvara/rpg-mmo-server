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

### Do not trust the exit code, and do not trust the .exe either

Batchmode Unity returned **0** for a build it reported internally as `result=Failed`, and
the output folder still held a `TlsProbe.exe` of plausible size. That .exe is a shell: the
IL2CPP step had died with `fatal error C1085: ... No space left on device`, so
`GameAssembly.dll` — the file that actually holds the compiled game — was never linked.
Running it pops a modal `Fatal error / Failed to load il2cpp` dialog and writes a
**zero-byte** log, which from a script is indistinguishable from a player that has not
started yet.

So assert three things, in this order:

| check | what it catches |
|---|---|
| `BuildReport.summary.result == Succeeded` | the exit code lying |
| `GameAssembly.dll` exists next to the .exe | a stale or half-linked .exe |
| the probe's own log has a terminating line | a player that started and then died |

A full IL2CPP build of this project needs roughly **25 GB** of scratch in `Library/Bee`.
Check free space before starting; `Library/Bee` is regenerable, so deleting it is the
cheapest way to get that space back.

## The answer

Run in a **real Windows IL2CPP player**, Unity `6000.3.9f1`, at both stripping levels.
Not the Editor: IL2CPP and Mono use different class-library profiles, so an Editor pass
would have answered a different question.

| | Minimal stripping | High stripping |
|---|---|---|
| `GameAssembly.dll` | 105.1 MB | 69.1 MB |
| 1. untrusted certificate **refused** | OK | OK |
| 2. trusted certificate completes | OK | OK |
| 3. validation callback invoked | OK | OK |
| 4. callback sees a real `SslPolicyErrors` | OK — `RemoteCertificateChainErrors` | OK — `RemoteCertificateChainErrors` |

```
[tls-probe] runtime: WindowsPlayer  unity: 6000.3.9f1
[tls-probe] [ OK ] default validation refuses an untrusted certificate
                   (refused: AuthenticationException: Authentication failed, see inner exception.)
[tls-probe] [ OK ] an explicitly trusted certificate completes  (Tls12 / None)
[tls-probe] [ OK ] the validation callback reports a real error  (Tls12 / None)
[tls-probe] [ OK ] callback saw: RemoteCertificateChainErrors
[tls-probe] ALL PASS on WindowsPlayer
```

**Go** for ADR-23's gateway-hop TLS and for the client's `https://` Nakama hop, **on
Windows**. High stripping changes nothing, so the linker keeps what TLS needs without a
`link.xml` entry — the answer that was actually in doubt.

Three details differ from the .NET 10 run and would break anyone who asserted on them:

- It negotiates **Tls12**, not Tls13. `SslProtocols.None` asks for the platform default
  and that is what Unity's stack chooses.
- `SslStream.CipherAlgorithm` reports **`None`**. The obsolete property is simply not
  populated here, so it says nothing about the cipher actually in use — do not gate on it.
- The refusal message is the generic `Authentication failed, see inner exception.`, not
  the .NET 10 text naming `UntrustedRoot`. Matching on that string would pass on .NET and
  fail in a player.

**Android remains unanswered**, for the same reason ADR-22's BouncyCastle result is
Windows-only. Nothing here transfers to it.

## Verified before shipping

Every API the probe calls was **executed on .NET 10 first**, so it does not throw for a
reason unrelated to the question. That is not the same as compiling in Unity, and the
first player build proved it: `SslProtocols.Tls13` does not exist in Unity's
netstandard2.1 profile, so the probe failed to compile with `error CS0117` while Unity
still exited 0. The pinned protocol list was the wrong thing to assert anyway — it
measures the list, not the platform — so the probe now passes `SslProtocols.None` and
lets the platform choose. Read the run below as "these assertions hold on .NET 10",
never as "this file compiles everywhere". On .NET 10 all four assertions hold:

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
