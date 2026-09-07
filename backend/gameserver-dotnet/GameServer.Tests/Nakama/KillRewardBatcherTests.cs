using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using GameServer.Nakama;
using Microsoft.Extensions.Logging.Abstractions;

namespace GameServer.Tests.Nakama;

/// <summary>
/// The batcher replaced two fire-and-forget HTTP calls per mob kill with one
/// <c>reward_kills</c> call per batch (#233). After audit F06/F07 the things that
/// make it safe are: a batch keeps ONE id across every retry (Nakama's receipt makes
/// the resend exactly-once), every non-granted answer — including a timeout — is
/// re-sent rather than dropped, and a backlog larger than Nakama's cap is split so it
/// can always be sent.
/// </summary>
public class KillRewardBatcherTests
{
    /// <summary>
    /// Scripted Nakama: records every reward_kills request and answers from a queue
    /// (default 200 OK, status granted). A null status simulates a timeout.
    /// </summary>
    private sealed class ScriptedNakama : HttpMessageHandler
    {
        public readonly ConcurrentQueue<(string UserId, long Kills, string BatchId)> Requests = new();
        public readonly ConcurrentQueue<(HttpStatusCode? Status, string Body)> Script = new();

        public static (HttpStatusCode?, string) Ok = (HttpStatusCode.OK, Envelope("{\"success\":true,\"status\":\"granted\"}"));
        public static (HttpStatusCode?, string) Partial = (HttpStatusCode.OK, Envelope("{\"success\":true,\"status\":\"partial\",\"leaderboard_error\":\"down\"}"));
        public static (HttpStatusCode?, string) Replayed = (HttpStatusCode.OK, Envelope("{\"success\":true,\"status\":\"granted\",\"replayed\":true}"));
        public static (HttpStatusCode?, string) Timeout = (null, "");
        public static (HttpStatusCode?, string) Internal = (HttpStatusCode.InternalServerError, "{\"error\":\"wallet update failed\",\"message\":\"wallet update failed\",\"code\":13}");
        public static (HttpStatusCode?, string) TooLarge = (HttpStatusCode.BadRequest, "{\"error\":\"kills must be in 1..1000\",\"message\":\"kills must be in 1..1000\",\"code\":11}");

        private static string Envelope(string inner) => "{\"payload\":" + JsonSerializer.Serialize(inner) + "}";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string wrapped = await request.Content!.ReadAsStringAsync(ct);
            string inner = JsonSerializer.Deserialize<string>(wrapped)!;
            using var doc = JsonDocument.Parse(inner);
            Requests.Enqueue((
                doc.RootElement.GetProperty("user_id").GetString()!,
                doc.RootElement.GetProperty("kills").GetInt64(),
                doc.RootElement.GetProperty("batch_id").GetString()!));

            if (!Script.TryDequeue(out var answer))
            {
                answer = Ok;
            }
            if (answer.Status is null)
            {
                throw new TaskCanceledException("scripted timeout");
            }
            return new HttpResponseMessage(answer.Status.Value) { Content = new StringContent(answer.Body) };
        }
    }

    /// <summary>Manual clock so backoff is deterministic.</summary>
    private sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 9, 7, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
        public void Advance(TimeSpan by) => Now += by;
    }

    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);

    private static (KillRewardBatcher batcher, ScriptedNakama nakama, NakamaClient client, FakeClock clock) NewBatcher(
        int maxPerBatch = KillRewardBatcher.DefaultMaxKillsPerBatch)
    {
        var nakama = new ScriptedNakama();
        var clock = new FakeClock();
        var client = new NakamaClient("http://nakama.test:7350", "k", NullLogger.Instance, nakama);
        // Timer interval is huge: tests drive flushes explicitly. Backoff uses
        // DefaultFlushInterval-sized steps through `Interval` via the fake clock.
        var batcher = new KillRewardBatcher(client, "map_01", NullLogger.Instance, Interval, maxPerBatch, clock);
        return (batcher, nakama, client, clock);
    }

    /// <summary>Flush with the clock advanced past any first-attempt backoff.</summary>
    private static async Task FlushLater(KillRewardBatcher b, FakeClock clock, TimeSpan? by = null)
    {
        clock.Advance(by ?? KillRewardBatcher.MaxRetryBackoff);
        await b.FlushAsync();
    }

    [Fact]
    public async Task KillsCoalescePerKiller_OneRequestEach()
    {
        var (batcher, nakama, client, _) = NewBatcher();
        using (client)
        {
            for (int i = 0; i < 5; i++) batcher.RecordKill("alice");
            batcher.RecordKill("bob");

            await batcher.DisposeAsync(); // final flush

            Assert.Equal(2, nakama.Requests.Count);
            var byUser = nakama.Requests.ToArray().ToDictionary(r => r.UserId, r => r.Kills);
            Assert.Equal(5, byUser["alice"]);
            Assert.Equal(1, byUser["bob"]);
            Assert.Equal(0, batcher.PendingKills);
        }
    }

    [Theory]
    [InlineData("not granted")]
    [InlineData("timeout")]
    [InlineData("partial")]
    public async Task NonGrantedAnswer_ResendsTheSameBatchId(string kind)
    {
        var (batcher, nakama, client, clock) = NewBatcher();
        using (client)
        {
            nakama.Script.Enqueue(kind switch
            {
                "timeout" => ScriptedNakama.Timeout,
                "partial" => ScriptedNakama.Partial,
                _ => ScriptedNakama.Internal,
            });
            batcher.RecordKill("alice");
            batcher.RecordKill("alice");

            await batcher.FlushAsync();            // attempt 1 fails
            Assert.Equal(2, batcher.PendingKills); // nothing dropped
            Assert.Equal(1, batcher.RequeuedBatches);

            await FlushLater(batcher, clock);      // attempt 2 succeeds
            await batcher.DisposeAsync();

            var requests = nakama.Requests.ToArray();
            Assert.Equal(2, requests.Length);
            Assert.Equal(2, requests[0].Kills);
            Assert.Equal(2, requests[1].Kills);
            Assert.Equal(requests[0].BatchId, requests[1].BatchId); // THE exactly-once property
            Assert.Equal(0, batcher.PendingKills);
        }
    }

    [Fact]
    public async Task Timeout_IsNeverDropped_EvenAcrossManyRetries()
    {
        var (batcher, nakama, client, clock) = NewBatcher();
        using (client)
        {
            for (int i = 0; i < 4; i++) nakama.Script.Enqueue(ScriptedNakama.Timeout);
            batcher.RecordKill("alice");

            for (int i = 0; i < 4; i++) await FlushLater(batcher, clock);
            Assert.Equal(1, batcher.PendingKills);
            Assert.Equal(4, nakama.Requests.Count);

            nakama.Script.Enqueue(ScriptedNakama.Replayed); // Nakama had it all along
            await FlushLater(batcher, clock);
            Assert.Equal(0, batcher.PendingKills);
            Assert.Single(nakama.Requests.Select(r => r.BatchId).Distinct());
            await batcher.DisposeAsync();
        }
    }

    [Fact]
    public async Task Backoff_HoldsAFailedBatchUntilItsSlot()
    {
        var (batcher, nakama, client, clock) = NewBatcher();
        using (client)
        {
            nakama.Script.Enqueue(ScriptedNakama.Internal);
            nakama.Script.Enqueue(ScriptedNakama.Internal);
            batcher.RecordKill("alice");

            await batcher.FlushAsync();                    // attempt 1 → wait 1×interval
            await batcher.FlushAsync();                    // same instant: held
            Assert.Single(nakama.Requests);
            await FlushLater(batcher, clock, Interval);    // attempt 2 → wait 2×interval
            Assert.Equal(2, nakama.Requests.Count);
            await FlushLater(batcher, clock, Interval);    // only 1×interval later: held
            Assert.Equal(2, nakama.Requests.Count);
            await FlushLater(batcher, clock, Interval);    // now due → granted
            Assert.Equal(3, nakama.Requests.Count);
            Assert.Equal(0, batcher.PendingKills);
            await batcher.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(999, 1)]
    [InlineData(1000, 1)]
    [InlineData(1001, 2)]
    [InlineData(2500, 3)]
    public async Task BacklogAboveTheCap_IsSplitIntoBatchesUnderIt(int kills, int expectedBatches)
    {
        var (batcher, nakama, client, _) = NewBatcher();
        using (client)
        {
            for (int i = 0; i < kills; i++) batcher.RecordKill("alice");
            await batcher.DisposeAsync();

            var requests = nakama.Requests.ToArray();
            Assert.Equal(expectedBatches, requests.Length);
            Assert.Equal(kills, requests.Sum(r => r.Kills));
            Assert.All(requests, r => Assert.InRange(r.Kills, 1, KillRewardBatcher.DefaultMaxKillsPerBatch));
            Assert.Equal(expectedBatches, requests.Select(r => r.BatchId).Distinct().Count());
        }
    }

    [Fact]
    public async Task SplitBatches_KeepTheirOwnIdsAcrossRetries()
    {
        var (batcher, nakama, client, clock) = NewBatcher(maxPerBatch: 10);
        using (client)
        {
            for (int i = 0; i < 25; i++) batcher.RecordKill("alice");
            nakama.Script.Enqueue(ScriptedNakama.Ok);       // batch 1 (10) granted
            nakama.Script.Enqueue(ScriptedNakama.Timeout);  // batch 2 (10) unknown → killer backs off
            await batcher.FlushAsync();
            Assert.Equal(2, nakama.Requests.Count);
            Assert.Equal(15, batcher.PendingKills);

            await FlushLater(batcher, clock);               // batch 2 resent with same id, then batch 3 (5)
            var reqs = nakama.Requests.ToArray();
            Assert.Equal(4, reqs.Length);
            Assert.Equal(reqs[1].BatchId, reqs[2].BatchId);
            Assert.Equal(10, reqs[2].Kills);
            Assert.Equal(5, reqs[3].Kills);
            Assert.Equal(0, batcher.PendingKills);
            await batcher.DisposeAsync();
        }
    }

    [Fact]
    public async Task KillsArrivingDuringAFlush_FormANewBatch_NeverMergeIntoAnInflightId()
    {
        var (batcher, nakama, client, clock) = NewBatcher();
        using (client)
        {
            nakama.Script.Enqueue(ScriptedNakama.Internal);
            batcher.RecordKill("alice");
            await batcher.FlushAsync();     // batch A (1) fails, keeps its id
            batcher.RecordKill("alice");    // arrives while A is outstanding
            batcher.RecordKill("alice");

            await FlushLater(batcher, clock);
            await batcher.DisposeAsync();

            var reqs = nakama.Requests.ToArray();
            Assert.Equal(3, reqs.Length);
            Assert.Equal(reqs[0].BatchId, reqs[1].BatchId);   // A retried as A
            Assert.Equal(1, reqs[1].Kills);                   // with its ORIGINAL count
            Assert.NotEqual(reqs[0].BatchId, reqs[2].BatchId);
            Assert.Equal(2, reqs[2].Kills);                   // the new kills, separately
            Assert.Equal(0, batcher.PendingKills);
        }
    }

    [Fact]
    public async Task TooLargeAnswer_SplitsWithNewIds()
    {
        // Batcher believes the cap is 50, Nakama's is lower: the refusal (code 11,
        // nothing granted) splits the batch under fresh ids.
        var (batcher, nakama, client, clock) = NewBatcher(maxPerBatch: 50);
        using (client)
        {
            for (int i = 0; i < 50; i++) batcher.RecordKill("alice");
            nakama.Script.Enqueue(ScriptedNakama.TooLarge);
            await batcher.FlushAsync();
            await batcher.DisposeAsync();

            var reqs = nakama.Requests.ToArray();
            Assert.Equal(3, reqs.Length);
            Assert.Equal(50, reqs[0].Kills);
            Assert.Equal(25, reqs[1].Kills);
            Assert.Equal(25, reqs[2].Kills);
            Assert.Equal(3, reqs.Select(r => r.BatchId).Distinct().Count());
            Assert.Equal(0, batcher.PendingKills);
            Assert.Equal(0, batcher.RequeuedBatches);
        }
    }

    [Fact]
    public async Task OneKillersFailure_DoesNotHoldBackAnother()
    {
        var (batcher, nakama, client, _) = NewBatcher();
        using (client)
        {
            batcher.RecordKill("alice");
            batcher.RecordKill("bob");
            // The first request (whichever killer) fails; the other must still go out.
            nakama.Script.Enqueue(ScriptedNakama.Internal);
            await batcher.FlushAsync();
            Assert.Equal(2, nakama.Requests.Count);
            Assert.Equal(1, batcher.PendingKills);
            await batcher.DisposeAsync();
        }
    }

    [Fact]
    public async Task LegacyPluginWithoutStatusField_IsTreatedAsGranted()
    {
        var (batcher, nakama, client, _) = NewBatcher();
        using (client)
        {
            nakama.Script.Enqueue((HttpStatusCode.OK, "{\"payload\":\"{}\"}"));
            batcher.RecordKill("alice");
            await batcher.DisposeAsync();
            Assert.Single(nakama.Requests);
            Assert.Equal(0, batcher.PendingKills);
        }
    }
}
