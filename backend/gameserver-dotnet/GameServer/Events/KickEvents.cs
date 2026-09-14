using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameServer.Events;

/// <summary>
/// The duplicate-login supersede event the gateway publishes on the
/// <c>events:kick</c> stream (Go: <c>gateway/server/kick.go</c>,
/// <c>SessionSupersededEvent</c> — that struct and this record MUST stay
/// field-for-field identical; the normative contract is in
/// <c>docs/API.md</c>).
///
/// <para><c>jti</c> is the whole race story: it names the join token the OLD
/// login's game-server connection authenticated with. The consumer kicks only
/// the connection whose stored jti equals it, so an event delivered late —
/// after the NEW login already joined with a freshly minted jti — matches
/// nothing and is a no-op. Newest login wins by construction, and at-least-once
/// redelivery is idempotent for free.</para>
/// </summary>
public sealed record SessionSupersededPayload(
    [property: JsonPropertyName("user_id")] string UserId,
    [property: JsonPropertyName("server_id")] string ServerId,
    [property: JsonPropertyName("jti")] string Jti,
    [property: JsonPropertyName("old_gateway")] string? OldGateway = null,
    [property: JsonPropertyName("new_gateway")] string? NewGateway = null);

/// <summary>AOT-compatible JSON context for kick-stream payloads.</summary>
[JsonSerializable(typeof(SessionSupersededPayload))]
public partial class KickJsonContext : JsonSerializerContext;

/// <summary>
/// Contract constants for the gateway → game-server kick stream. Mirrors Go's
/// <c>shared/constants/keys.go</c> — one shared stream, per-server consumer
/// groups (see <see cref="RedisKickConsumer"/> for why that shape).
/// </summary>
public static class KickEvents
{
    /// <summary>Logical stream name — Go's <c>constants.KickEventStream</c>.
    /// The concrete Redis key is <c>events:kick</c>.</summary>
    public const string Stream = "kick";

    /// <summary>Event type — Go's <c>constants.EventSessionSuperseded</c>.</summary>
    public const string SessionSuperseded = "session_superseded";

    /// <summary>The reason carried in the MsgKick/MsgDisconnect pair sent to the
    /// evicted client — the same literal the gateway's local kick uses
    /// (<c>gateway/server/server.go</c>, <c>KickReasonDuplicateLogin</c>), so a
    /// client sees one reason string no matter which hop evicted it.</summary>
    public const string ReasonDuplicateLogin = "duplicate_login";

    /// <summary>
    /// Parse a <c>session_superseded</c> payload. Returns null (never throws) on
    /// malformed or incomplete input — a poison entry must be counted and ACKed,
    /// not allowed to crash the consumer into a redelivery loop.
    /// </summary>
    public static SessionSupersededPayload? TryParse(byte[] payload)
    {
        try
        {
            var p = JsonSerializer.Deserialize(payload, KickJsonContext.Default.SessionSupersededPayload);
            if (p is null || string.IsNullOrEmpty(p.UserId) ||
                string.IsNullOrEmpty(p.ServerId) || string.IsNullOrEmpty(p.Jti))
            {
                return null;
            }
            return p;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
