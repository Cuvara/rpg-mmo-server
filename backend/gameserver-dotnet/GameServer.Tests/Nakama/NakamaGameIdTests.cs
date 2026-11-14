using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using GameServer.Nakama;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameServer.Tests.Nakama;

public class NakamaGameIdTests
{
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public readonly ConcurrentQueue<(string Path, string Payload)> Requests = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string body = await request.Content!.ReadAsStringAsync(ct);
            Requests.Enqueue((request.RequestUri!.AbsolutePath, JsonSerializer.Deserialize<string>(body)!));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"payload\":\"{}\"}") };
        }
    }

    [Theory]
    [InlineData(null, "cuvara")]
    [InlineData("foo", "foo")]
    public async Task KillRpcs_SendClientGameId(string? gameId, string expected)
    {
        var handler = new CapturingHandler();
        using var client = gameId is null
            ? new NakamaClient("http://nakama.test:7350", "k", NullLogger.Instance, handler)
            : new NakamaClient("http://nakama.test:7350", "k", NullLogger.Instance, handler, gameId);
        var batcher = new KillRewardBatcher(client, "map_01", NullLogger.Instance, TimeSpan.FromHours(1));
        batcher.RecordKill("alice");
        await batcher.DisposeAsync();
        await client.RewardKillAsync("alice", "victim", "map_01");
        await client.SubmitKillAsync("alice");

        var requests = handler.Requests.ToArray();
        Assert.Equal(3, requests.Length);
        Assert.Equal(new[] { "/v2/rpc/reward_kills", "/v2/rpc/reward_kill", "/v2/rpc/submit_kill" },
            requests.Select(r => r.Path));
        Assert.Equal(expected, client.GameId);
        foreach (var request in requests)
        {
            using var payload = JsonDocument.Parse(request.Payload);
            Assert.Equal(expected, payload.RootElement.GetProperty("game_id").GetString());
        }
    }
}
