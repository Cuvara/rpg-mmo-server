using System.Text.Json.Serialization;

namespace GameServer.Observability;

/// <summary>
/// JSON response for the <c>/status</c> endpoint. Aggregates live server
/// state into a single object for client-side status panels and dev tools.
/// </summary>
public sealed class ServerStatus
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("tick_rate")]
    public int TickRate { get; set; }

    [JsonPropertyName("current_tick")]
    public ulong CurrentTick { get; set; }

    [JsonPropertyName("players_online")]
    public int PlayersOnline { get; set; }

    [JsonPropertyName("entities")]
    public int Entities { get; set; }

    [JsonPropertyName("enemies_alive")]
    public int EnemiesAlive { get; set; }

    [JsonPropertyName("redis")]
    public string Redis { get; set; } = "disconnected";

    [JsonPropertyName("postgres")]
    public string Postgres { get; set; } = "disconnected";

    [JsonPropertyName("uptime_seconds")]
    public long UptimeSeconds { get; set; }
}

/// <summary>
/// AOT-safe JSON serialization context for <see cref="ServerStatus"/>.
/// </summary>
[JsonSerializable(typeof(ServerStatus))]
internal sealed partial class ServerStatusContext : JsonSerializerContext;
