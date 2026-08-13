using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace GameServer.Nakama;

/// <summary>
/// Lightweight HTTP client for server-to-server Nakama RPC calls.
/// Uses <c>runtime.http_key</c> authentication to invoke RPCs registered
/// by the Nakama Go plugin (reward_kill, submit_kill).
/// </summary>
public sealed class NakamaClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly ILogger _logger;
    private readonly string _baseUrl;
    private readonly string _httpKey;

    /// <summary>Gold awarded per enemy kill.</summary>
    public const int GoldPerKill = 10;

    public NakamaClient(string baseUrl, string httpKey, ILogger logger)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _httpKey = httpKey;
        _logger = logger;
        _http = new HttpClient();
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>
    /// Award gold to a player who killed an enemy. Fire-and-forget safe.
    /// </summary>
    public async Task RewardKillAsync(string userId, string victimId, string mapId)
    {
        try
        {
            var req = new RewardKillRequest { UserId = userId, VictimId = victimId, MapId = mapId };
            string payload = JsonSerializer.Serialize(req, NakamaJsonContext.Default.RewardKillRequest);
            string response = await CallRpcAsync("reward_kill", payload);
            _logger.LogDebug("Reward kill: user={UserId} victim={VictimId} response={Response}",
                userId, victimId, response);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to reward kill for {UserId} (victim={VictimId})", userId, victimId);
        }
    }

    /// <summary>
    /// Increment the player's kill count on the leaderboard. Fire-and-forget safe.
    /// </summary>
    public async Task SubmitKillAsync(string userId)
    {
        try
        {
            var req = new SubmitKillRequest { UserId = userId };
            string payload = JsonSerializer.Serialize(req, NakamaJsonContext.Default.SubmitKillRequest);
            string response = await CallRpcAsync("submit_kill", payload);
            _logger.LogDebug("Submit kill: user={UserId} response={Response}", userId, response);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to submit kill for {UserId}", userId);
        }
    }

    /// <summary>
    /// Call a Nakama RPC with server-to-server auth (runtime.http_key).
    /// </summary>
    private async Task<string> CallRpcAsync(string rpcId, string jsonPayload)
    {
        // Nakama RPC endpoint expects: {"payload": "<escaped-json-string>"}
        string wrappedPayload = JsonSerializer.Serialize(jsonPayload, NakamaJsonContext.Default.String);
        var content = new StringContent(wrappedPayload, Encoding.UTF8, "application/json");

        string url = $"{_baseUrl}/v2/rpc/{rpcId}?http_key={_httpKey}";
        var response = await _http.PostAsync(url, content);

        string body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Nakama RPC {RpcId} failed: {Status} {Body}", rpcId, response.StatusCode, body);
        }

        return body;
    }

    public void Dispose() => _http.Dispose();
}

// ── RPC request DTOs ──

internal sealed class RewardKillRequest
{
    [JsonPropertyName("user_id")] public string UserId { get; set; } = "";
    [JsonPropertyName("victim_id")] public string VictimId { get; set; } = "";
    [JsonPropertyName("map_id")] public string MapId { get; set; } = "";
}

internal sealed class SubmitKillRequest
{
    [JsonPropertyName("user_id")] public string UserId { get; set; } = "";
}

/// <summary>AOT-safe JSON serialization context for Nakama RPC payloads.</summary>
[JsonSerializable(typeof(RewardKillRequest))]
[JsonSerializable(typeof(SubmitKillRequest))]
[JsonSerializable(typeof(string))]
internal sealed partial class NakamaJsonContext : JsonSerializerContext;
