using System.Text;
using System.Text.Json;

namespace GameServer.Observability;

/// <summary>What a <c>POST /debug/kill</c> request did. Values are the response's <c>outcome</c>.</summary>
public enum DebugKillOutcome
{
    /// <summary>The player's HP was taken to 0 down the normal damage and death path.</summary>
    Killed = 0,

    /// <summary>No live entity has that id.</summary>
    NotFound = 1,

    /// <summary>The id names an entity that is not a player (an enemy, an item, a projectile).</summary>
    NotAPlayer = 2,

    /// <summary>The player was already dead; nothing was changed.</summary>
    AlreadyDead = 3,

    /// <summary>Too many kills already queued for the tick thread; nothing was queued.</summary>
    Busy = 4,

    /// <summary>The tick thread did not reach the request in time; it was withdrawn unapplied.</summary>
    Timeout = 5,
}

/// <summary>Result of one debug kill, produced on the tick thread.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="Tick">Base tick the request was resolved on; 0 when it never reached the tick.</param>
/// <param name="HpBefore">The player's HP just before the kill; 0 unless <see cref="DebugKillOutcome.Killed"/>.</param>
public readonly record struct DebugKillResult(DebugKillOutcome Outcome, ulong Tick, int HpBefore);

/// <summary>
/// Request parsing and response bodies for <c>POST /debug/kill</c>, the test lab's
/// deterministic-death hook. Served only when <see cref="ServerEnv.DebugEntities"/> is on.
/// </summary>
/// <remarks>
/// <para>Hand-written with <see cref="Utf8JsonReader"/> / <see cref="Utf8JsonWriter"/> for the
/// reason <see cref="DebugEntitiesDocument"/> is: the NativeAOT build needs no reflection
/// metadata for it.</para>
/// <para>A player's entity id is its user id, so one <c>id</c> field covers both; the
/// <c>user_id</c> and <c>entity_id</c> spellings are accepted as aliases.</para>
/// </remarks>
internal static class DebugKill
{
    /// <summary>Largest request body read. The only field is one id.</summary>
    public const int MaxBodyBytes = 4096;

    /// <summary>The <c>outcome</c> string for <paramref name="outcome"/>.</summary>
    public static string Label(DebugKillOutcome outcome) => outcome switch
    {
        DebugKillOutcome.Killed => "killed",
        DebugKillOutcome.NotFound => "not_found",
        DebugKillOutcome.NotAPlayer => "not_a_player",
        DebugKillOutcome.AlreadyDead => "already_dead",
        DebugKillOutcome.Busy => "busy",
        DebugKillOutcome.Timeout => "timeout",
        _ => "unknown",
    };

    /// <summary>HTTP status for <paramref name="outcome"/>.</summary>
    public static int StatusCode(DebugKillOutcome outcome) => outcome switch
    {
        DebugKillOutcome.Killed => 200,
        DebugKillOutcome.NotFound => 404,
        DebugKillOutcome.NotAPlayer => 400,
        DebugKillOutcome.AlreadyDead => 409,
        DebugKillOutcome.Busy => 503,
        DebugKillOutcome.Timeout => 504,
        _ => 500,
    };

    /// <summary>
    /// The target id from a JSON body <c>{"id":"..."}</c> (or <c>user_id</c> / <c>entity_id</c>),
    /// falling back to the <c>id</c> query parameter. Null when neither names a non-empty id.
    /// </summary>
    /// <remarks>
    /// A non-empty body must be exactly one complete JSON object (whitespace around it allowed).
    /// Anything else is refused with <paramref name="error"/> = <c>invalid_body</c> and names
    /// nobody, even if a valid id appears in it: a body such as
    /// <c>{"id":"target"}garbage</c>, or a truncated object, is not a request this endpoint
    /// understood, and a state change must not be made on a guess (review finding R1-2). With
    /// several id fields the first non-empty one wins, as before.
    /// </remarks>
    public static string? ParseId(ReadOnlySpan<byte> body, string? queryId, out string? error)
    {
        error = null;
        if (!body.IsEmpty)
        {
            string? found = null;
            try
            {
                // isFinalBlock (the default): a truncated document throws rather than stopping.
                var reader = new Utf8JsonReader(body);
                if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                {
                    error = InvalidBody;
                    return null;
                }

                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    bool wanted = reader.ValueTextEquals("id")
                        || reader.ValueTextEquals("user_id")
                        || reader.ValueTextEquals("entity_id");
                    if (!reader.Read()) break;
                    if (wanted && found == null && reader.TokenType == JsonTokenType.String)
                    {
                        string? id = reader.GetString();
                        if (!string.IsNullOrWhiteSpace(id)) found = id;
                    }
                    else
                    {
                        reader.Skip();
                    }
                }

                // The whole object, and nothing after it: a second value or trailing bytes throw
                // here (single-value reader), trailing whitespace reads as the end.
                if (reader.TokenType != JsonTokenType.EndObject || reader.CurrentDepth != 0 || reader.Read())
                {
                    error = InvalidBody;
                    return null;
                }
            }
            catch (JsonException)
            {
                error = InvalidBody;
                return null;
            }

            if (found != null) return found;
        }

        if (string.IsNullOrWhiteSpace(queryId))
        {
            error = MissingId;
            return null;
        }

        return queryId;
    }

    /// <summary>Error code: the body is not exactly one complete JSON object.</summary>
    public const string InvalidBody = "invalid_body";

    /// <summary>Error code: neither the body nor the query names an id.</summary>
    public const string MissingId = "missing_id";

    /// <summary>Error code: the body is larger than <see cref="MaxBodyBytes"/>.</summary>
    public const string BodyTooLarge = "body_too_large";

    /// <summary>Response body for a resolved (or refused) request.</summary>
    public static byte[] ResultBody(string id, in DebugKillResult result)
    {
        using var stream = new MemoryStream(128);
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteBoolean("ok", result.Outcome == DebugKillOutcome.Killed);
            json.WriteString("id", id);
            json.WriteString("outcome", Label(result.Outcome));
            json.WriteNumber("tick", result.Tick);
            json.WriteNumber("hp_before", result.HpBefore);
            json.WriteEndObject();
        }

        return stream.ToArray();
    }

    /// <summary>Response body for a request refused before it named a target.</summary>
    public static byte[] ErrorBody(string error) =>
        Encoding.UTF8.GetBytes("{\"ok\":false,\"error\":\"" + error + "\"}");
}
