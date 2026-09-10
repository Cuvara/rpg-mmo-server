using System.Text;
using GameServer.Net.Sealed;

namespace GameServer.Tests.Net;

/// <summary>
/// Cross-implementation vector: one complete handshake and one sealed frame, fixed end to
/// end. The identical constants are asserted by <c>shared/sealed/interop_test.go</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a shared vector and not two round-trips.</b> Each side round-tripping against
/// itself proves only that it agrees with itself, which a subtly wrong implementation also
/// does — silently. These values pin the bytes BETWEEN the implementations: the derived
/// keys, the transcript, the binding tag and a complete sealed frame. If Go and C# ever
/// disagree about any of them, the production failure is not an error — it is a handshake
/// that never completes and a session that never forms, with nothing naming the cause.
/// </para>
/// <para>
/// Every value is derived, not chosen: the two private keys are RFC 7748 §6.1's, and
/// everything else follows from them and the fixed jti.
/// </para>
/// </remarks>
public class SealedInteropTests
{
    private const string ClientPrivate = "77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a";
    private const string ServerPrivate = "5dab087e624a8a4b79e17f8b83800ee66f3bb1292618b6fd1c2f8b27ff88e0eb";
    private const string Jti = "interop-jti-0001";
    private const string JoinSecret = "interop-join-secret";
    private const string Plaintext = "interop payload";
    private const ulong Sequence = 7;

    private const string ClientPublic = "8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a";
    private const string ServerPublic = "de9edb7d7b7dc1b4d35b61c2ece435373f8343c85b78674dadfc7e146f882b4f";
    private const string Shared = "4a5d9d5ba4ce2de1728e3bf480350f25e07e21c947d19e3376f09b3c1e161742";
    private const string KeyC2S = "9b5cb56cd4dcc26b2cd3a89c34a79feddeac1bee943566cf7bc24e80d76d221e";
    private const string KeyS2C = "54e46a0de4f5e86296813a4dc8e62829cde095d88db19195d061c4611623e31b";
    private const string Binding = "f0788e11400bd7a65954cef957c685f3ff1afebb052964e8b0dc9ad7f3bef2bc";
    private const string Frame = "c1010000000000000007f599297035b016c0f6ecef9fe5c4d1879637da13c7c39676f6f435aed71bd0";

    private static string Hex(byte[] b) => Convert.ToHexString(b).ToLowerInvariant();

    /// <summary>The whole handshake, value by value, so a divergence names the step.</summary>
    [Fact]
    public void Handshake_MatchesTheGoImplementation()
    {
        SealedKeyPair client = SealedKeyPair.FromPrivate(Convert.FromHexString(ClientPrivate));
        SealedKeyPair server = SealedKeyPair.FromPrivate(Convert.FromHexString(ServerPrivate));

        Assert.Equal(ClientPublic, Hex(client.Public));
        Assert.Equal(ServerPublic, Hex(server.Public));

        Assert.True(client.TryAgree(server.Public, out byte[] shared));
        Assert.Equal(Shared, Hex(shared));

        byte[] transcript = SealedHandshake.Transcript(Jti, client.Public, server.Public);
        (byte[] c2s, byte[] s2c) = SealedCrypto.DeriveDirectionKeys(shared, transcript);

        Assert.Equal(KeyC2S, Hex(c2s));
        Assert.Equal(KeyS2C, Hex(s2c));

        var signer = new SealedTranscriptSigner(JoinSecret, Jti);
        Assert.Equal(Binding, Hex(signer.Sign(transcript)));
        Assert.True(signer.Verify(transcript, Convert.FromHexString(Binding)));

        // The server, deriving from its own side, must reach the same secret — the
        // property the whole exchange exists for.
        Assert.True(server.TryAgree(client.Public, out byte[] fromServer));
        Assert.Equal(Shared, Hex(fromServer));
    }

    /// <summary>C# OPENS what Go sealed — the exact byte string from the Go test.</summary>
    [Fact]
    public void Frame_SealedByGo_OpensInCSharp()
    {
        byte[] c2s = InteropClientToServerKey();

        var session = new SealedSession(new SealedAead(c2s), new StrictMonotonicSequence());
        SealedOpenResult result = session.Open(Convert.FromHexString(Frame), out byte[] plaintext);

        Assert.Equal(SealedOpenResult.Ok, result);
        Assert.Equal(Plaintext, Encoding.UTF8.GetString(plaintext));
        Assert.Equal(Sequence, session.HighestReceived);
    }

    /// <summary>
    /// C# SEALS the same inputs and must produce the identical byte string — which is what
    /// makes "Go opens what C# sealed" true, since the Go test opens exactly this constant.
    /// </summary>
    [Fact]
    public void Frame_SealedByCSharp_MatchesGoByteForByte()
    {
        byte[] c2s = InteropClientToServerKey();

        // Seal at sequence 7 by advancing the session's counter the honest way.
        var session = new SealedSession(new SealedAead(c2s), new StrictMonotonicSequence());
        for (ulong i = 0; i < Sequence; i++) session.Seal("filler"u8);

        byte[] frame = session.Seal(Encoding.UTF8.GetBytes(Plaintext));

        Assert.Equal(Frame, Hex(frame));
    }

    /// <summary>
    /// A frame sealed under the wrong direction's key must not open. The two keys exist
    /// precisely so the counter nonce cannot repeat across directions, and this is what
    /// proves they are actually distinct in use rather than merely in derivation.
    /// </summary>
    [Fact]
    public void Frame_DoesNotOpenUnderTheOtherDirectionsKey()
    {
        SealedKeyPair client = SealedKeyPair.FromPrivate(Convert.FromHexString(ClientPrivate));
        SealedKeyPair server = SealedKeyPair.FromPrivate(Convert.FromHexString(ServerPrivate));
        Assert.True(client.TryAgree(server.Public, out byte[] shared));

        byte[] transcript = SealedHandshake.Transcript(Jti, client.Public, server.Public);
        (_, byte[] s2c) = SealedCrypto.DeriveDirectionKeys(shared, transcript);

        var wrongWay = new SealedSession(new SealedAead(s2c), new StrictMonotonicSequence());
        Assert.Equal(SealedOpenResult.Rejected, wrongWay.Open(Convert.FromHexString(Frame), out _));
    }

    private static byte[] InteropClientToServerKey()
    {
        SealedKeyPair client = SealedKeyPair.FromPrivate(Convert.FromHexString(ClientPrivate));
        SealedKeyPair server = SealedKeyPair.FromPrivate(Convert.FromHexString(ServerPrivate));
        Assert.True(client.TryAgree(server.Public, out byte[] shared));

        byte[] transcript = SealedHandshake.Transcript(Jti, client.Public, server.Public);
        (byte[] c2s, _) = SealedCrypto.DeriveDirectionKeys(shared, transcript);
        return c2s;
    }
}
