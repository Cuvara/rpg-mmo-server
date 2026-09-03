using System.Diagnostics;
using System.Text;
using GameServer.Events;
using GameServer.Tests.Registry;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Xunit;

namespace GameServer.Tests.Events;

/// <summary>
/// Wire-contract tests against a REAL Redis (shared <see cref="RedisFixture"/> container).
/// The Go gateway's consumer (<c>backend/shared/storage/redisstore/stream.go</c>) is the
/// contract being asserted: key <c>events:game</c>, exactly the two fields
/// <c>type</c>/<c>payload</c>, payload bytes verbatim, and <c>MAXLEN ~</c> trimming.
/// A fake would prove nothing about what XADD actually writes, so these skip (as a real
/// xUnit skip) when no docker daemon exists, and fail when docker is there but the
/// container is not — see <see cref="RedisFixture.SkipUnlessAvailable"/>.
/// </summary>
[Collection(RedisCollection.Name)]
public sealed class RedisEventStreamRedisTests(RedisFixture fixture)
{
    private const string Key = "events:game";

    private static async Task<ConnectionMultiplexer> ConnectRawAsync(string addr)
    {
        var options = ConfigurationOptions.Parse(addr);
        options.AbortOnConnectFail = false;
        return await ConnectionMultiplexer.ConnectAsync(options);
    }

    /// <summary>Poll on a monotonic budget (#153: never DateTime.UtcNow).</summary>
    private static async Task<bool> EventuallyAsync(Func<bool> done, int seconds = 10)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            if (done()) return true;
            await Task.Delay(50);
        }
        return done();
    }

    [SkippableFact]
    public async Task Publish_WritesTheGoContract_KeyFieldsAndPayloadBytes()
    {
        fixture.SkipUnlessAvailable(nameof(Publish_WritesTheGoContract_KeyFieldsAndPayloadBytes));
        fixture.Flush();

        byte[] payload = Encoding.UTF8.GetBytes(
            """{"victim_id":"mob_7","victim_type":"mob","killer_id":"u1","map_id":"map_01","server_id":"gs-1"}""");

        await using (var stream = await RedisEventStream.ConnectAsync(
            fixture.Addr, password: null, NullLogger.Instance))
        {
            await stream.PublishAsync(
                EventStreams.Game, new GameEvent("entity_killed", payload), CancellationToken.None);
            Assert.True(await EventuallyAsync(() => stream.Published == 1),
                $"event was never XADDed (failures={stream.PublishFailures})");
        }

        await using var mux = await ConnectRawAsync(fixture.Addr);
        var db = mux.GetDatabase();

        // The key the gateway relay reads — EventStreamPrefix + GameEventStream, i.e.
        // NOT `events:game_events`, the key the old literal would have produced.
        Assert.True(await db.KeyExistsAsync(Key), $"stream key {Key} does not exist");
        Assert.False(await db.KeyExistsAsync("events:game_events"),
            "the pre-fix stream name leaked back in");

        var entries = await db.StreamRangeAsync(Key);
        var entry = Assert.Single(entries);

        // Exactly two fields, named as Go's streamFieldType / streamFieldPayload.
        Assert.Equal(2, entry.Values.Length);
        Assert.Equal("type", entry.Values[0].Name);
        Assert.Equal("entity_killed", (string?)entry.Values[0].Value);
        Assert.Equal("payload", entry.Values[1].Name);
        Assert.Equal(payload, (byte[]?)entry.Values[1].Value);
    }

    [SkippableFact]
    public async Task Publish_ReachesAConsumerGroup_LikeTheGatewayRelay()
    {
        fixture.SkipUnlessAvailable(nameof(Publish_ReachesAConsumerGroup_LikeTheGatewayRelay));
        fixture.Flush();

        await using var mux = await ConnectRawAsync(fixture.Addr);
        var db = mux.GetDatabase();
        // The relay's shape: XGROUP CREATE MKSTREAM before any publish, then XREADGROUP.
        await db.StreamCreateConsumerGroupAsync(Key, "gateway", "0", createStream: true);

        await using (var stream = await RedisEventStream.ConnectAsync(
            fixture.Addr, password: null, NullLogger.Instance))
        {
            await stream.PublishAsync(
                EventStreams.Game, new GameEvent("entity_killed", [7, 8, 9]), CancellationToken.None);
            Assert.True(await EventuallyAsync(() => stream.Published == 1));
        }

        var read = await db.StreamReadGroupAsync(Key, "gateway", "consumer-1", ">", count: 16);
        var msg = Assert.Single(read);
        Assert.Equal("entity_killed", (string?)msg["type"]);
        Assert.Equal([7, 8, 9], (byte[]?)msg["payload"]);
    }

    [SkippableFact]
    public async Task Publish_TrimsWithMaxLen()
    {
        fixture.SkipUnlessAvailable(nameof(Publish_TrimsWithMaxLen));
        fixture.Flush();

        const int total = 300;
        await using (var stream = await RedisEventStream.ConnectAsync(
            fixture.Addr, password: null, NullLogger.Instance, maxLen: 10))
        {
            for (int i = 0; i < total; i++)
            {
                await stream.PublishAsync(
                    EventStreams.Game, new GameEvent("entity_killed", [(byte)i]), CancellationToken.None);
            }
            Assert.True(await EventuallyAsync(() => stream.Published == total),
                $"only {stream.Published}/{total} events were XADDed");
        }

        await using var mux = await ConnectRawAsync(fixture.Addr);
        long len = await mux.GetDatabase().StreamLengthAsync(Key);

        // `MAXLEN ~` trims whole radix-tree nodes, so the retained length may exceed the
        // bound by up to one node — but a stream that still holds all 300 entries was
        // never trimmed at all, which is the regression this guards against (#202: an
        // untrimmed stream against a noeviction Redis).
        Assert.InRange(len, 10, total - 1);
    }
}
