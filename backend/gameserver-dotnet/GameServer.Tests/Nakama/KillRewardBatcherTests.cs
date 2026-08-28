using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using GameServer.Nakama;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameServer.Tests.Nakama;

/// <summary>
/// The batcher replaced two fire-and-forget HTTP calls per mob kill with one
/// <c>reward_kills</c> call per killer per flush (#233). These tests pin the three
/// things that make that replacement safe rather than merely cheaper: kills coalesce
/// per killer, a "nothing was granted" answer re-queues them, and an unknowable
/// outcome drops them — because retrying an unknown outcome is the double-gold path.
/// </summary>
public class KillRewardBatcherTests
{
    /// <summary>
    /// Scripted Nakama: records every reward_kills request and answers from a queue
    /// (default 200 OK). A response of null simulates a timeout by cancelling.
    /// </summary>
    private sealed class ScriptedNakama : HttpMessageHandler
    {
        public readonly ConcurrentQueue<(string UserId, long Kills, string BatchId)> Requests = new();
        public readonly ConcurrentQueue<HttpStatusCode?> Script = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            string wrapped = await request.Content!.ReadAsStringAsync(ct);
            // Nakama's envelope: the body is a JSON string whose content is the payload.
            string inner = JsonSerializer.Deserialize<string>(wrapped)!;
            using var doc = JsonDocument.Parse(inner);
            Requests.Enqueue((
                doc.RootElement.GetProperty("user_id").GetString()!,
                doc.RootElement.GetProperty("kills").GetInt64(),
                doc.RootElement.GetProperty("batch_id").GetString()!));

            if (!Script.TryDequeue(out var status))
            {
                status = HttpStatusCode.OK;
            }
            if (status is null)
            {
                throw new TaskCanceledException("scripted timeout");
            }
            return new HttpResponseMessage(status.Value)
            {
                Content = new StringContent("{\"payload\":\"{}\"}")
            };
        }
    }

    private static (KillRewardBatcher batcher, ScriptedNakama nakama, NakamaClient client) NewBatcher(
        TimeSpan? interval = null)
    {
        var nakama = new ScriptedNakama();
        var client = new NakamaClient("http://nakama.test:7350", "k", NullLogger.Instance, nakama);
        // A long interval by default: tests drive flushes through DisposeAsync so
        // nothing here depends on timer scheduling.
        var batcher = new KillRewardBatcher(
            client, "map_01", NullLogger.Instance, interval ?? TimeSpan.FromMinutes(10));
        return (batcher, nakama, client);
    }

    [Fact]
    public async Task KillsCoalescePerKiller_OneRequestEach()
    {
        var (batcher, nakama, client) = NewBatcher();
        using (client)
        {
            for (int i = 0; i < 5; i++) batcher.RecordKill("alice");
            batcher.RecordKill("bob");

            await batcher.DisposeAsync(); // final flush

            Assert.Equal(2, nakama.Requests.Count);
            var byUser = nakama.Requests.ToArray().ToDictionary(r => r.UserId, r => r.Kills);
            Assert.Equal(5, byUser["alice"]);
            Assert.Equal(1, byUser["bob"]);
        }
    }

    [Fact]
    public async Task NotGrantedAnswer_RequeuesTheKills_AndTheRetryCarriesANewBatchId()
    {
        var (batcher, nakama, client) = NewBatcher(TimeSpan.FromMilliseconds(50));
        using (client)
        {
            nakama.Script.Enqueue(HttpStatusCode.InternalServerError); // first flush fails
            batcher.RecordKill("alice");
            batcher.RecordKill("alice");

            // Wait until the retry (second request) has arrived.
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (nakama.Requests.Count < 2 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }
            await batcher.DisposeAsync();

            var requests = nakama.Requests.ToArray();
            Assert.True(requests.Length >= 2, "the failed batch was never retried");
            Assert.Equal(2, requests[0].Kills);
            Assert.Equal(2, requests[1].Kills); // same kills, re-queued whole
            Assert.NotEqual(requests[0].BatchId, requests[1].BatchId);
            Assert.Equal(0, batcher.DroppedKills);
        }
    }

    [Fact]
    public async Task UnknownOutcome_DropsTheKills_InsteadOfRiskingADoubleGrant()
    {
        var (batcher, nakama, client) = NewBatcher(TimeSpan.FromMilliseconds(50));
        using (client)
        {
            nakama.Script.Enqueue(null); // scripted timeout: outcome unknown
            batcher.RecordKill("alice");
            batcher.RecordKill("alice");
            batcher.RecordKill("alice");

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (batcher.DroppedKills == 0 && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }
            await batcher.DisposeAsync();

            Assert.Equal(3, batcher.DroppedKills);
            // Exactly one request: dropped kills are never re-sent.
            Assert.Single(nakama.Requests);
        }
    }

    [Fact]
    public async Task KillsRecordedDuringAFailedSend_AreNotLostByTheRequeue()
    {
        var (batcher, nakama, client) = NewBatcher();
        using (client)
        {
            nakama.Script.Enqueue(HttpStatusCode.ServiceUnavailable);
            batcher.RecordKill("alice");

            // First flush fails and re-queues; a kill lands between the flush's
            // TryRemove and its AddOrUpdate in the worst case — the re-queue must add,
            // not overwrite. Driving the race deterministically isn't possible from
            // here, but the sum surviving both flushes is the observable contract.
            await batcher.DisposeAsync(); // flush 1: 503 → re-queue
            batcher.RecordKill("alice");  // recorded after the failed send

            // A second dispose is a no-op on the loop but still flushes pending state.
            await batcher.DisposeAsync();

            var requests = nakama.Requests.ToArray();
            Assert.Equal(2, requests.Length);
            Assert.Equal(1, requests[0].Kills);
            Assert.Equal(2, requests[1].Kills); // 1 re-queued + 1 new
        }
    }
}
