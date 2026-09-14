namespace GameServer.Net.Sealed;

/// <summary>How strictly a listener demands a sealed session.</summary>
public enum SealedRequirement
{
    /// <summary>
    /// Accept cleartext and do not offer sealing. What the server ships today, and the
    /// reason the transport posture reporting exists.
    /// </summary>
    Disabled = 0,

    /// <summary>
    /// Refuse any session that does not complete the sealed handshake. There is
    /// deliberately no third value — see <see cref="SealedPolicy.RefusalFor"/>.
    /// </summary>
    Required = 1,
}

/// <summary>Why a peer was refused. Stable tokens: they become metric label values.</summary>
public static class SealedRefusalReason
{
    /// <summary>Not refused.</summary>
    public const string None = "";

    /// <summary>The peer never completed the sealed handshake.</summary>
    public const string NoSealedSession = "no_sealed_session";

    /// <summary>The peer's wire encoding cannot carry a sealed session at all.</summary>
    public const string EncodingCannotSeal = "encoding_cannot_seal";
}

/// <summary>What the listener knows about a peer when it has to decide.</summary>
/// <param name="SealedHandshakeCompleted">A verified key is in force for this peer.</param>
/// <param name="EncodingCanSeal">
/// The peer's encoding can carry a sealed session. The legacy JSON encoding cannot: the
/// sealed frame is a binary layout with a marker byte JSON has no room for, and the
/// handshake fields were deliberately kept out of the JSON message set so key material
/// could never be rendered into a human-readable payload. So once encryption is required,
/// a JSON client is REFUSED, not served in the clear — which effectively deprecates the
/// JSON encoding for any deployment that requires encryption.
/// </param>
public readonly record struct SealedPeerCapabilities(
    bool SealedHandshakeCompleted,
    bool EncodingCanSeal);

/// <summary>Whether to admit a peer, and why not.</summary>
/// <param name="Refused">The session must be closed.</param>
/// <param name="Reason">A stable token; never anything peer-supplied.</param>
public readonly record struct SealedRefusal(bool Refused, string Reason);

/// <summary>The admission rule for sealed sessions.</summary>
public static class SealedPolicy
{
    /// <summary>
    /// Decide whether to admit a peer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>There is no negotiation, and that is the point.</b> A protocol that can be
    /// talked down to cleartext will be: an attacker who can modify the handshake removes
    /// the offer, both ends believe the other could do no better, and the session proceeds
    /// in the clear looking entirely healthy. That is a downgrade attack, and the only
    /// reliable defence is to have nothing to downgrade to. So the answer to "the peer did
    /// not seal" is no session — never a cleartext session, never a weaker cipher, never a
    /// retry without encryption.
    /// </para>
    /// <para>
    /// The same reasoning is why <see cref="SealedRequirement"/> has two values and not
    /// three: a "preferred" mode is a downgrade attack with a friendly name.
    /// </para>
    /// </remarks>
    public static SealedRefusal RefusalFor(SealedRequirement requirement, SealedPeerCapabilities peer)
    {
        if (requirement != SealedRequirement.Required)
            return new SealedRefusal(false, SealedRefusalReason.None);

        if (!peer.EncodingCanSeal)
            return new SealedRefusal(true, SealedRefusalReason.EncodingCannotSeal);

        if (!peer.SealedHandshakeCompleted)
            return new SealedRefusal(true, SealedRefusalReason.NoSealedSession);

        return new SealedRefusal(false, SealedRefusalReason.None);
    }
}
