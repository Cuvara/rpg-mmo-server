using GameServer.Net;
using Microsoft.Extensions.Logging;
using RpgMmo.Wire.V1;

namespace GameServer.Net.Sealed;

/// <summary>
/// The server half of the sealed-session handshake on the gameplay hop.
/// </summary>
/// <remarks>
/// <para>
/// Runs immediately after the join reply and before the read/write loops start, so no
/// frame is ever written half-sealed. Both hellos travel in the clear — there is no key
/// yet, which is what they exist to establish.
/// </para>
/// <para>
/// <b>Gameplay hop only.</b> The anchor here is the join token's <c>jti</c> plus
/// <c>JOIN_TOKEN_SECRET</c>. The gateway hop has neither at the point it would need them
/// and requires a different anchor, which is still ADR-gated.
/// </para>
/// </remarks>
public static class SealedHandshakeServer
{
    /// <summary>Outcome of the server-side handshake.</summary>
    public enum Outcome
    {
        /// <summary>Completed; both sessions installed.</summary>
        Ok,
        /// <summary>The peer sent something other than a client hello, or nothing.</summary>
        NoHello,
        /// <summary>The peer's public key was malformed or a low-order point.</summary>
        BadPublicKey,
        /// <summary>The server could not derive its own material — a configuration fault.</summary>
        NotConfigured,
    }

    /// <summary>
    /// Perform the exchange and, on success, install the two sealed sessions on
    /// <paramref name="conn"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>There is no cleartext fallback on any path.</b> Every failure returns without
    /// installing a session, and the caller closes the connection. A peer that cannot or
    /// will not seal gets no session — never a cleartext one — because a protocol that can
    /// be talked down to cleartext will be.
    /// </para>
    /// <para>
    /// The server sends its binding, computed over a transcript containing BOTH ephemeral
    /// public keys. That is what <i>will</i> let a client detect a man in the middle: an
    /// attacker who substitutes its own key changes the transcript, so the binding it read
    /// off the wire no longer verifies.
    /// </para>
    /// <para>
    /// <b>No shipped client can check it yet, and that must not be read as if it could.</b>
    /// Verifying the binding needs material derived from <c>JOIN_TOKEN_SECRET</c>, and a
    /// client binary must not carry that secret — putting it there is exactly the
    /// pre-shared-key mistake ADR-22 supersedes, where extracting it once compromises
    /// everyone for ever. So until the pinned gateway identity key lands, a real client
    /// completes this exchange without verifying the binding: it gets confidentiality
    /// against a passive eavesdropper, which the X25519 exchange provides on its own, and
    /// NOT man-in-the-middle protection.
    /// </para>
    /// <para>
    /// The server signs the binding regardless, so the protection is already on the wire
    /// waiting for a client that can check it. The Unity side names the gap explicitly —
    /// <c>SealedClientExchange.WithoutBindingVerification</c>, a <c>BindingVerified</c>
    /// flag, and a test asserting a man in the middle succeeds against it — so it starts
    /// failing the day the identity key makes it untrue.
    /// </para>
    /// <para>
    /// <b>ADR-25: the Ed25519 identity signature is that replacement, and it ships in the
    /// same reply.</b> <paramref name="identity"/> is this pod's per-process keypair; the
    /// signature goes in <c>SealedServerHello.ServerSignature</c> and a client checks it
    /// with the public half the gateway delivered in <c>enter_world_resp</c>. It needs no
    /// secret in the binary, so unlike the binding it is reachable by a real player.
    /// <b>Both fields are sent.</b> The binding still serves the harnesses that hold
    /// <c>JOIN_TOKEN_SECRET</c>, and reusing its field number for the signature is how two
    /// versions come to disagree silently about what a byte means.
    /// </para>
    /// <para>
    /// <b>The server always signs, and never negotiates.</b> There is no capability
    /// exchange and no downgrade: a client that ignores the new field behaves exactly as it
    /// did before, and a client that requires identity and is handed no key refuses on its
    /// own side. That is what makes the server-and-gateway-first migration order the safe
    /// one here — the inverse of the sealing rollout, where the client shipped first and
    /// met a closed door.
    /// </para>
    /// <para>
    /// <b>A verified signature still does not mean the gameplay hop is authenticated</b>
    /// while the gateway hop is plaintext, which it is in every environment today. The key
    /// is exactly as trustworthy as the hop that delivered it. See
    /// <see cref="ServerIdentity"/> and ADR-25 §3.
    /// </para>
    /// </remarks>
    public static async Task<Outcome> RunAsync(
        Connection conn, string joinTokenSecret, string jti, ServerIdentity identity,
        ILogger logger, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(identity);

        SealedTranscriptSigner signer;
        try
        {
            signer = new SealedTranscriptSigner(joinTokenSecret, jti);
        }
        catch (ArgumentException)
        {
            // No secret or no jti. A configuration fault, not a peer fault — and still a
            // refusal, because the alternative is a session that believes it is protected.
            return Outcome.NotConfigured;
        }

        Envelope? env = await conn.ReadOneAsync(ct);
        if (env is null || (MsgType)env.Type != MsgType.SealedClientHello)
            return Outcome.NoHello;

        var hello = WireProtocol.GetPayload<SealedClientHello>(env);
        byte[] clientPublic = hello.PublicKey.ToByteArray();
        if (clientPublic.Length != SealedHandshake.PublicKeySize)
            return Outcome.BadPublicKey;

        SealedKeyPair server = SealedKeyPair.Generate();
        if (!server.TryAgree(clientPublic, out byte[] shared))
        {
            // Includes low-order points, which would force a shared secret the attacker
            // knows and both sides agree on — a complete break wearing the appearance of a
            // successful handshake.
            return Outcome.BadPublicKey;
        }

        byte[] transcript = SealedHandshake.Transcript(jti, clientPublic, server.Public);
        (byte[] c2s, byte[] s2c) = SealedCrypto.DeriveDirectionKeys(shared, transcript);

        var reply = new SealedServerHello
        {
            PublicKey = Google.Protobuf.ByteString.CopyFrom(server.Public),
            Binding = Google.Protobuf.ByteString.CopyFrom(signer.Sign(transcript)),
            // The SAME transcript bytes, wrapped rather than replaced (ADR-25 decision 3).
            // DeriveDirectionKeys above and the binding beside it both read that array
            // unchanged, so ADR-22's cross-implementation vectors remain valid.
            ServerSignature = Google.Protobuf.ByteString.CopyFrom(identity.Sign(transcript)),
        };
        await conn.WriteOneAsync(
            WireProtocol.NewEnvelope(MsgType.SealedServerHello, reply, conn.Encoding), ct);

        // Installed only after the reply is on the wire: the client cannot seal until it
        // has our public key, so sealing our own next frame before sending this one would
        // deadlock the handshake.
        conn.InstallSealedSession(
            inbound: new SealedSession(new SealedAead(c2s), new StrictMonotonicSequence()),
            outbound: new SealedSession(new SealedAead(s2c), new StrictMonotonicSequence()));

        logger.LogInformation(
            "Sealed session established for {UserId} (cipher=chacha20-poly1305, identity=ed25519 {IdentityKey})",
            conn.UserId, identity.PublicKeyBase64);
        return Outcome.Ok;
    }
}
