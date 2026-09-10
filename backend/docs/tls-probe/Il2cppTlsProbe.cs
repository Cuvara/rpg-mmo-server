// IL2CPP TLS probe — the go/no-go for ADR-23's gateway-hop TLS and for the client's
// Nakama https:// hop.
//
// PURPOSE
//   Decide, IN A BUILT PLAYER, whether System.Net.Security.SslStream works under IL2CPP
//   *and whether certificate validation actually enforces*. The Editor is not evidence:
//   Mono and IL2CPP use different class-library profiles (unityjit vs unityaot), and
//   ADR-22 already caught AesGcm compiling and then throwing PlatformNotSupportedException
//   on a device.
//
// WHY THE NEGATIVE CASE IS THE POINT
//   AesGcm failed loudly: the type was present in the reference assembly and threw at
//   runtime. Certificate validation is more likely to fail QUIETLY — degraded rather than
//   absent — and validation that silently accepts anything is indistinguishable from
//   validation that works, if you only ever test a good certificate.
//
//   So the assertion that matters is REFUSAL:
//
//       a connection to an untrusted certificate must FAIL
//
//   A probe that only shows a good certificate succeeding would pass on a platform where
//   TLS is worthless. That is the whole reason this file exists rather than a one-line
//   "does SslStream connect" check.
//
// SELF-CONTAINED ON PURPOSE
//   The probe starts its own TLS listener on loopback using an embedded self-signed
//   certificate. No network, no badssl.com, no DNS — a player on a locked-down device or
//   an offline machine still gives an answer, and the answer cannot be perturbed by
//   someone else's certificate expiring.
//
// HOW TO RUN
//   1. Copy this file into Assets/ in the Unity project.
//   2. Attach it to a GameObject in a scene.
//   3. Build a Windows IL2CPP player, run it, read the log.
//   4. Repeat with ManagedStrippingLevel.High — stripping is what removes reflection-only
//      paths, and TLS pulls in a lot of them.
//   5. Repeat for Android if a device is available. Android is the open question: ADR-22's
//      BouncyCastle result is Windows-only for exactly this reason.
//
// COMPATIBILITY (deliberate, do not "modernise")
//   - C# 9 at most: Unity 6 does not accept file-scoped namespaces or u8 literals.
//   - No Convert.FromHexString: .NET 5+, absent from the netstandard2.1 profile.
//   - Synchronous SslStream APIs: the async ones are fine too, but sync keeps the probe
//     readable and its failure modes obvious.

using System;
using System.Collections;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using UnityEngine;

public sealed class Il2cppTlsProbe : MonoBehaviour
{
    // A self-signed certificate for CN=localhost, generated once and embedded so the probe
    // needs no key generation at runtime. CertificateRequest is netstandard2.1, but whether
    // it works under IL2CPP is a SEPARATE question this probe deliberately does not couple
    // itself to — a failure there would look like a TLS failure and would not be one.
    private static readonly string[] PfxChunks =
    {
            "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"
    };

    private const string PfxPassword = "probe";
    private const string HostName = "localhost";

    private int _failures;
    private readonly StringBuilder _log = new StringBuilder();

    private void Start()
    {
        Report("=== IL2CPP TLS probe ===");
        Report("runtime: " + Application.platform + "  unity: " + Application.unityVersion);

        X509Certificate2 cert;
        try
        {
            cert = new X509Certificate2(Convert.FromBase64String(string.Concat(PfxChunks)), PfxPassword);
            Report("[ OK ] loaded the embedded certificate: " + cert.Subject);
        }
        catch (Exception ex)
        {
            Fail("could not load the embedded PFX", ex);
            Finish();
            return;
        }

        int port;
        TcpListener listener;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Report("[ OK ] TLS listener on 127.0.0.1:" + port);
        }
        catch (Exception ex)
        {
            Fail("could not start a loopback listener", ex);
            Finish();
            return;
        }

        // ---- 1. THE ASSERTION THAT MATTERS: default validation must REFUSE -------------
        //
        // The certificate is self-signed and its issuer is in no trust store, so a client
        // performing real validation must reject it. If this connection SUCCEEDS, the
        // platform is not validating and every TLS guarantee in ADR-23 is void.
        RunOne(listener, cert, port, "default validation refuses an untrusted certificate",
            validationCallback: null, expectSuccess: false);

        // ---- 2. The machinery works at all --------------------------------------------
        //
        // Same certificate, accepted explicitly. Proves the handshake itself completes
        // under IL2CPP, and reports what was negotiated. Without this, a refusal in (1)
        // could equally mean "SslStream is broken", which is a different answer.
        RunOne(listener, cert, port, "an explicitly trusted certificate completes",
            validationCallback: (s, c, ch, e) => true, expectSuccess: true);

        // ---- 3. A callback that inspects gets a real error to inspect ------------------
        //
        // A pinning implementation needs SslPolicyErrors to be populated. If it arrives as
        // None for an untrusted certificate, pinning code that trusts it would accept
        // anything -- the same silent degradation as (1), one layer up.
        SslPolicyErrors observed = SslPolicyErrors.None;
        bool sawCallback = false;
        RunOne(listener, cert, port, "the validation callback reports a real error",
            validationCallback: (s, c, ch, e) => { sawCallback = true; observed = e; return true; },
            expectSuccess: true);

        if (!sawCallback)
        {
            Fail("the validation callback was never invoked -- a pinning client would never run", null);
        }
        else if (observed == SslPolicyErrors.None)
        {
            Fail("the callback reported SslPolicyErrors.None for an UNTRUSTED certificate. "
                 + "Pinning code that trusts this value would accept anything.", null);
        }
        else
        {
            Report("[ OK ] callback saw: " + observed);
        }

        try { listener.Stop(); } catch (Exception) { }
        Finish();
    }

    private void RunOne(TcpListener listener, X509Certificate2 cert, int port, string what,
                        RemoteCertificateValidationCallback validationCallback, bool expectSuccess)
    {
        Exception serverError = null;
        var serverDone = new ManualResetEventSlim(false);

        var server = new Thread(() =>
        {
            try
            {
                using (TcpClient c = listener.AcceptTcpClient())
                using (var ssl = new SslStream(c.GetStream(), false))
                {
                    ssl.AuthenticateAsServer(cert, false, SslProtocols.Tls12 | SslProtocols.Tls13, false);
                    ssl.Write(new byte[] { 0x2A }, 0, 1);
                }
            }
            catch (Exception ex) { serverError = ex; }
            finally { serverDone.Set(); }
        });
        server.IsBackground = true;
        server.Start();

        bool connected = false;
        string detail = "";
        string negotiated = "";
        try
        {
            using (var client = new TcpClient("127.0.0.1", port))
            using (var ssl = validationCallback == null
                       ? new SslStream(client.GetStream(), false)
                       : new SslStream(client.GetStream(), false, validationCallback))
            {
                ssl.AuthenticateAsClient(HostName);
                negotiated = ssl.SslProtocol + " / " + ssl.CipherAlgorithm;
                var b = new byte[1];
                ssl.Read(b, 0, 1);
                connected = b[0] == 0x2A;
            }
        }
        catch (Exception ex)
        {
            detail = ex.GetType().Name + ": " + Trim(ex.Message);
        }

        serverDone.Wait(5000);

        if (connected == expectSuccess)
        {
            Report("[ OK ] " + what
                   + (expectSuccess ? "  (" + negotiated + ")" : "  (refused: " + detail + ")"));
        }
        else if (expectSuccess)
        {
            Fail(what + " -- expected the handshake to complete, it did not: " + detail
                 + (serverError != null ? "  server side: " + Trim(serverError.Message) : ""), null);
        }
        else
        {
            Fail(what + " -- THE HANDSHAKE SUCCEEDED AGAINST AN UNTRUSTED CERTIFICATE. "
                 + "Certificate validation is not enforcing on this platform, and TLS here "
                 + "buys confidentiality from a passive listener and NOTHING from an active "
                 + "one. Do not ship a client that relies on it.", null);
        }
    }

    private static string Trim(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        s = s.Replace("\r", " ").Replace("\n", " ");
        return s.Length > 160 ? s.Substring(0, 160) + "..." : s;
    }

    private void Report(string line)
    {
        _log.AppendLine(line);
        Debug.Log("[tls-probe] " + line);
    }

    private void Fail(string line, Exception ex)
    {
        _failures++;
        string full = "[FAIL] " + line + (ex != null ? "  " + ex.GetType().Name + ": " + Trim(ex.Message) : "");
        _log.AppendLine(full);
        Debug.LogError("[tls-probe] " + full);
    }

    private void Finish()
    {
        // Names the platform it actually ran on rather than the one it was written for.
        // A probe that says "works under IL2CPP" when run in the Editor is the same
        // overclaim it exists to catch.
        string where = Application.platform.ToString();
        string verdict = _failures == 0
            ? "ALL PASS on " + where + " -- SslStream works and certificate validation enforces. "
              + "This says nothing about any OTHER platform: IL2CPP and Mono use different "
              + "class-library profiles, and Android is a separate answer from Windows."
            : _failures + " FAILED on " + where + " -- read the lines above before trusting TLS here.";
        Report(verdict);
        Debug.Log("[tls-probe] ===== summary =====\n" + _log);
    }
}
