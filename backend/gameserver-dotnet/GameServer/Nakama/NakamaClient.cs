using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace GameServer.Nakama;

/// <summary>
/// Lightweight HTTP client for server-to-server Nakama RPC calls.
/// Uses <c>runtime.http_key</c> authentication to invoke RPCs registered
/// by the Nakama Go plugin — primarily the batched <c>reward_kills</c>; the
/// legacy per-kill pair (reward_kill, submit_kill) remains for compatibility.
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
        : this(baseUrl, httpKey, logger, handler: null) { }

    /// <param name="handler">
    /// Test seam: a fake <see cref="HttpMessageHandler"/> lets the batcher tests script
    /// Nakama's answers without a network. Null (production) uses the default handler.
    /// </param>
    public NakamaClient(string baseUrl, string httpKey, ILogger logger, HttpMessageHandler? handler)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _httpKey = httpKey;
        _logger = logger;
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        // 5s, not the 100s default. Every kill used to spawn fire-and-forget calls, so a
        // stalled Nakama accumulated ~4,000 in-flight tasks and sockets before the FIRST
        // default timeout fired (#233). Batched or not, no reward call is worth waiting
        // 100 seconds for.
        _http.Timeout = TimeSpan.FromSeconds(5);
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
    /// Grant gold and leaderboard score for a batch of kills in ONE Nakama call
    /// (the <c>reward_kills</c> RPC). Replaces the per-kill reward_kill + submit_kill
    /// pair, which cost 2 HTTP requests and 2 meta-DB transactions per mob kill (#233).
    /// </summary>
    /// <returns>
    /// The outcome the batcher's retry policy is built on. <see cref="KillRewardOutcome.Granted"/>:
    /// Nakama answered 2xx — done. <see cref="KillRewardOutcome.NotGranted"/>: Nakama answered
    /// non-2xx, and the RPC's contract is that an error response means NOTHING was granted,
    /// so re-queueing the kills cannot double-grant. <see cref="KillRewardOutcome.Unknown"/>:
    /// no answer arrived (timeout / transport failure after send) — whether the grant
    /// happened is unknowable, so the batch must be DROPPED rather than retried; a retry
    /// here is the double-gold path.
    /// </returns>
    public async Task<KillRewardOutcome> RewardKillsAsync(string userId, long kills, string mapId, string batchId)
    {
        var req = new RewardKillsRequest { UserId = userId, Kills = kills, MapId = mapId, BatchId = batchId };
        string payload = JsonSerializer.Serialize(req, NakamaJsonContext.Default.RewardKillsRequest);
        string wrappedPayload = JsonSerializer.Serialize(payload, NakamaJsonContext.Default.String);
        var content = new StringContent(wrappedPayload, Encoding.UTF8, "application/json");
        string url = $"{_baseUrl}/v2/rpc/reward_kills?http_key={_httpKey}";

        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsync(url, content);
        }
        catch (HttpRequestException ex)
        {
            // Connect refused / DNS / reset before a response: with no request delivered
            // there is nothing granted, so this is safely retryable. A reset AFTER
            // delivery is indistinguishable, but HttpRequestException on POST here is
            // overwhelmingly connection establishment — the ambiguous shape is the
            // timeout below, which is classified Unknown.
            _logger.LogWarning(ex, "Nakama reward_kills transport failure for {UserId} ({Kills} kills)", userId, kills);
            return KillRewardOutcome.NotGranted;
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning(
                "Nakama reward_kills timed out for {UserId} ({Kills} kills, batch {BatchId}) — " +
                "outcome unknown, batch dropped to avoid a double grant", userId, kills, batchId);
            return KillRewardOutcome.Unknown;
        }

        using (response)
        {
            if (response.IsSuccessStatusCode)
            {
                return KillRewardOutcome.Granted;
            }
            string body = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("Nakama reward_kills failed for {UserId}: {Status} {Body}",
                userId, response.StatusCode, body);
            return KillRewardOutcome.NotGranted;
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

/// <summary>Outcome of a batched kill-reward call. See <see cref="NakamaClient.RewardKillsAsync"/>.</summary>
public enum KillRewardOutcome
{
    /// <summary>Nakama confirmed the grant.</summary>
    Granted,

    /// <summary>Nakama answered with an error, which its contract defines as "nothing granted" — safe to re-queue.</summary>
    NotGranted,

    /// <summary>No answer arrived; the grant may or may not have happened. Drop, never retry.</summary>
    Unknown,
}

internal sealed class RewardKillsRequest
{
    [JsonPropertyName("user_id")] public string UserId { get; set; } = "";
    [JsonPropertyName("kills")] public long Kills { get; set; }
    [JsonPropertyName("map_id")] public string MapId { get; set; } = "";
    [JsonPropertyName("batch_id")] public string BatchId { get; set; } = "";
}

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
[JsonSerializable(typeof(RewardKillsRequest))]
[JsonSerializable(typeof(SubmitKillRequest))]
[JsonSerializable(typeof(string))]
internal sealed partial class NakamaJsonContext : JsonSerializerContext;
