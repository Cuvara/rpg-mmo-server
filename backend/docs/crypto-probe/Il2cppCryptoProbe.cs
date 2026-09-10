// IL2CPP crypto library probe — ADR-22.
//
// PURPOSE
//   Decide, in a BUILT PLAYER, whether the candidate pure-C# crypto libraries produce
//   the RFC vectors under IL2CPP. The Editor is not evidence: Mono and IL2CPP use
//   different class-library profiles (unityjit vs unityaot), and ADR-22 already caught
//   AesGcm compiling and then throwing PlatformNotSupportedException on a device.
//
// HOW TO RUN
//   1. Import ONE of the candidates as a managed DLL under Assets/Plugins/ (see the
//      survey doc for which files):
//        A) BouncyCastle.Cryptography.dll   (lib/netstandard2.0/)   -> define CUVARA_BC
//        B) NaCl.Core.dll                   (lib/netstandard2.1/)   -> define CUVARA_NACL
//      Both may be imported at once; the probe reports whichever is defined.
//   2. Add the scripting define symbol(s) above in Player Settings.
//   3. Attach this component to a GameObject in a scene, build a Windows IL2CPP player
//      with ManagedStrippingLevel.Minimal, run it, and read the log.
//   4. Repeat with ManagedStrippingLevel.High and the link.xml from the survey doc —
//      the whole point is to find out whether stripping removes what reflection cannot see.
//
// WHAT IT ASSERTS
//   Published vectors, never a round trip against itself: RFC 8439 s2.8.2 for
//   ChaCha20-Poly1305, RFC 7748 s6.1 for X25519, RFC 5869 A.1 for HKDF-SHA256. An
//   implementation that is subtly wrong still round-trips against itself and reports no
//   error, so a round trip proves nothing about interoperating with the Go server.
//
// COMPATIBILITY NOTES (deliberate, do not "modernise")
//   - C# 9 at most: Unity 6 does not accept file-scoped namespaces or u8 literals.
//   - No Convert.FromHexString / Convert.ToHexString: those are .NET 5+ and are ABSENT
//     from the netstandard2.1 profile Unity compiles against. Hand-rolled below.
//   - Every case is wrapped: a throw must be reported, not lost. A PlatformNotSupported
//     or TypeLoad here is the actual result we are looking for.

using System;
using System.Text;
using UnityEngine;

namespace Cuvara.Netcode.Diagnostics
{
    public class Il2cppCryptoProbe : MonoBehaviour
    {
        private int _pass;
        private int _fail;
        private readonly StringBuilder _log = new StringBuilder();

        private void Start()
        {
            Line("=== ADR-22 IL2CPP crypto probe ===");
            Line("platform : " + Application.platform);
            Line("runtime  : " + SystemInfo.processorType);
            Line("il2cpp   : " +
#if ENABLE_IL2CPP
                "YES (ENABLE_IL2CPP defined)"
#else
                "NO  -- THIS IS A MONO BUILD, THE RESULT DOES NOT TRANSFER"
#endif
            );
            Line("stripping: check Player Settings; run this at Minimal AND at High");
            Line("");

#if CUVARA_BC
            RunBouncyCastle();
#else
            Line("[skip] BouncyCastle  -- CUVARA_BC not defined");
#endif

#if CUVARA_NACL
            RunNaClCore();
#else
            Line("[skip] NaCl.Core     -- CUVARA_NACL not defined");
#endif

            Line("");
            Line(_fail == 0 && _pass > 0
                ? "RESULT: ALL " + _pass + " CHECKS PASSED IN THIS PLAYER"
                : "RESULT: " + _pass + " passed, " + _fail + " FAILED");
            Debug.Log(_log.ToString());
        }

        // ---------------- vectors ----------------

        private const string Key = "808182838485868788898a8b8c8d8e8f909192939495969798999a9b9c9d9e9f";
        private const string Nonce = "070000004041424344454647";
        private const string Aad = "50515253c0c1c2c3c4c5c6c7";
        private const string Plain =
            "Ladies and Gentlemen of the class of '99: If I could offer you only one tip for the future, sunscreen would be it.";
        private const string ExpectCipher =
            "d31a8d34648e60db7b86afbc53ef7ec2a4aded51296e08fea9e2b5a736ee62d63dbea45e8ca9671282fafb69da92728b" +
            "1a71de0a9e060b2905d6a5b67ecd3b3692ddbd7f2d778b8c9803aee328091b58fab324e4fad675945585808b4831d7bc" +
            "3ff4def08e4b7a9de576d26586cec64b6116";
        private const string ExpectTag = "1ae10b594f09e26a7e902ecbd0600691";

        private const string APriv = "77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a";
        private const string APub = "8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a";
        private const string BPriv = "5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb";
        private const string BPub = "de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f";
        private const string ExpectShared = "4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742";

        private const string HkdfIkm = "0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b";
        private const string HkdfSalt = "000102030405060708090a0b0c";
        private const string HkdfInfo = "f0f1f2f3f4f5f6f7f8f9";
        private const string ExpectOkm =
            "3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865";

#if CUVARA_BC
        private void RunBouncyCastle()
        {
            Line("-- BouncyCastle.Cryptography --");

            Case("BC ChaCha20-Poly1305 (RFC 8439 s2.8.2)", () =>
            {
                var aead = new Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305();
                aead.Init(true, new Org.BouncyCastle.Crypto.Parameters.AeadParameters(
                    new Org.BouncyCastle.Crypto.Parameters.KeyParameter(Hex(Key)), 128, Hex(Nonce), Hex(Aad)));
                byte[] pt = Encoding.ASCII.GetBytes(Plain);
                byte[] outBuf = new byte[aead.GetOutputSize(pt.Length)];
                int n = aead.ProcessBytes(pt, 0, pt.Length, outBuf, 0);
                n += aead.DoFinal(outBuf, n);

                byte[] ct = new byte[pt.Length];
                byte[] tag = new byte[16];
                Array.Copy(outBuf, 0, ct, 0, pt.Length);
                Array.Copy(outBuf, pt.Length, tag, 0, 16);
                return Expect(ExpectCipher, Str(ct)) + Expect(ExpectTag, Str(tag));
            });

            Case("BC rejects a tampered tag", () =>
            {
                var aead = new Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305();
                aead.Init(true, new Org.BouncyCastle.Crypto.Parameters.AeadParameters(
                    new Org.BouncyCastle.Crypto.Parameters.KeyParameter(Hex(Key)), 128, Hex(Nonce), Hex(Aad)));
                byte[] pt = Encoding.ASCII.GetBytes(Plain);
                byte[] sealedBuf = new byte[aead.GetOutputSize(pt.Length)];
                int n = aead.ProcessBytes(pt, 0, pt.Length, sealedBuf, 0);
                aead.DoFinal(sealedBuf, n);
                sealedBuf[sealedBuf.Length - 1] ^= 0x01;

                var dec = new Org.BouncyCastle.Crypto.Modes.ChaCha20Poly1305();
                dec.Init(false, new Org.BouncyCastle.Crypto.Parameters.AeadParameters(
                    new Org.BouncyCastle.Crypto.Parameters.KeyParameter(Hex(Key)), 128, Hex(Nonce), Hex(Aad)));
                try
                {
                    byte[] outBuf = new byte[dec.GetOutputSize(sealedBuf.Length)];
                    int m = dec.ProcessBytes(sealedBuf, 0, sealedBuf.Length, outBuf, 0);
                    dec.DoFinal(outBuf, m);
                    return "tampered tag was ACCEPTED -- this is a fatal result";
                }
                catch (Exception)
                {
                    return "";
                }
            });

            Case("BC X25519 (RFC 7748 s6.1)", () =>
            {
                var aPriv = new Org.BouncyCastle.Crypto.Parameters.X25519PrivateKeyParameters(Hex(APriv), 0);
                var bPriv = new Org.BouncyCastle.Crypto.Parameters.X25519PrivateKeyParameters(Hex(BPriv), 0);
                string r = Expect(APub, Str(aPriv.GeneratePublicKey().GetEncoded()))
                         + Expect(BPub, Str(bPriv.GeneratePublicKey().GetEncoded()));

                var agr = new Org.BouncyCastle.Crypto.Agreement.X25519Agreement();
                agr.Init(aPriv);
                byte[] s1 = new byte[agr.AgreementSize];
                agr.CalculateAgreement(new Org.BouncyCastle.Crypto.Parameters.X25519PublicKeyParameters(Hex(BPub), 0), s1, 0);

                agr.Init(bPriv);
                byte[] s2 = new byte[agr.AgreementSize];
                agr.CalculateAgreement(new Org.BouncyCastle.Crypto.Parameters.X25519PublicKeyParameters(Hex(APub), 0), s2, 0);

                return r + Expect(ExpectShared, Str(s1)) + Expect(ExpectShared, Str(s2));
            });

            Case("BC HKDF-SHA256 (RFC 5869 A.1)", () =>
            {
                var hk = new Org.BouncyCastle.Crypto.Generators.HkdfBytesGenerator(
                    new Org.BouncyCastle.Crypto.Digests.Sha256Digest());
                hk.Init(new Org.BouncyCastle.Crypto.Parameters.HkdfParameters(Hex(HkdfIkm), Hex(HkdfSalt), Hex(HkdfInfo)));
                byte[] okm = new byte[42];
                hk.GenerateBytes(okm, 0, 42);
                return Expect(ExpectOkm, Str(okm));
            });

            Case("BC key generation uses a working RNG", () =>
            {
                var gen = new Org.BouncyCastle.Crypto.Generators.X25519KeyPairGenerator();
                gen.Init(new Org.BouncyCastle.Crypto.Parameters.X25519KeyGenerationParameters(
                    new Org.BouncyCastle.Security.SecureRandom()));
                var pair = gen.GenerateKeyPair();
                byte[] pub = ((Org.BouncyCastle.Crypto.Parameters.X25519PublicKeyParameters)pair.Public).GetEncoded();
                if (pub.Length != 32) return "public key was " + pub.Length + " bytes, expected 32";
                bool allZero = true;
                for (int i = 0; i < pub.Length; i++) { if (pub[i] != 0) { allZero = false; break; } }
                return allZero ? "generated public key was all zero -- RNG is not working" : "";
            });
        }
#endif

#if CUVARA_NACL
        private void RunNaClCore()
        {
            Line("-- NaCl.Core (AEAD only; it has no X25519) --");

            Case("NaCl.Core ChaCha20-Poly1305 (RFC 8439 s2.8.2)", () =>
            {
                var aead = new NaCl.Core.ChaCha20Poly1305(Hex(Key));
                byte[] pt = Encoding.ASCII.GetBytes(Plain);
                byte[] ct = new byte[pt.Length];
                byte[] tag = new byte[16];
                aead.Encrypt(Hex(Nonce), pt, ct, tag, Hex(Aad));
                return Expect(ExpectCipher, Str(ct)) + Expect(ExpectTag, Str(tag));
            });

            Case("NaCl.Core rejects a tampered tag", () =>
            {
                var aead = new NaCl.Core.ChaCha20Poly1305(Hex(Key));
                byte[] pt = Encoding.ASCII.GetBytes(Plain);
                byte[] ct = new byte[pt.Length];
                byte[] tag = new byte[16];
                aead.Encrypt(Hex(Nonce), pt, ct, tag, Hex(Aad));
                tag[0] ^= 0x01;
                byte[] back = new byte[ct.Length];
                try
                {
                    aead.Decrypt(Hex(Nonce), ct, tag, back, Hex(Aad));
                    return "tampered tag was ACCEPTED -- this is a fatal result";
                }
                catch (Exception)
                {
                    return "";
                }
            });
        }
#endif

        // ---------------- harness ----------------

        private void Case(string name, Func<string> body)
        {
            string problem;
            try
            {
                problem = body();
            }
            catch (Exception e)
            {
                // A throw here is the interesting result, not an accident: it is exactly
                // how AesGcm failed on a device while compiling cleanly.
                problem = e.GetType().Name + ": " + e.Message;
            }

            if (string.IsNullOrEmpty(problem))
            {
                _pass++;
                Line("  [PASS] " + name);
            }
            else
            {
                _fail++;
                Line("  [FAIL] " + name);
                Line("         " + problem);
            }
        }

        private static string Expect(string expect, string got)
        {
            return expect == got ? "" : ("expected " + expect + " got " + got + "; ");
        }

        private void Line(string s)
        {
            _log.Append(s).Append('\n');
        }

        // netstandard2.1 has no Convert.FromHexString / ToHexString.
        private static byte[] Hex(string s)
        {
            byte[] b = new byte[s.Length / 2];
            for (int i = 0; i < b.Length; i++)
            {
                b[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
            }
            return b;
        }

        private static string Str(byte[] b)
        {
            var sb = new StringBuilder(b.Length * 2);
            for (int i = 0; i < b.Length; i++)
            {
                sb.Append(b[i].ToString("x2"));
            }
            return sb.ToString();
        }
    }
}
