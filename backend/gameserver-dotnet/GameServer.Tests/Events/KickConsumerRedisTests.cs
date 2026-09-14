using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using GameServer.Events;
using GameServer.Tests.Registry;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Xunit;

namespace GameServer.Tests.Events;

/// <summary>
/// The kick consumer against a REAL Redis (shared <see cref="RedisFixture"/>
/// container): group creation at <c>$</c>, per-server filtering on one shared
/// stream, ACK of everything it reads (own, foreign and malformed), and group
/// destruction on graceful dispose. The Go publisher's write shape is one XADD
/// with <c>type</c>/<c>payload</c> fields — reproduced here verbatim.
/// </summary>
[Collection(RedisCollection.Name)]
public sealed class KickConsumerRedisTests(RedisFixture fixture)
{
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

    private static Task XAddSupersede(IDatabase db, string userId, string serverId, string jti)
    {
        byte[] payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            user_id = userId,
            server_id = serverId,
            jti,
            old_gateway = "gw-a",
            new_gateway = "gw-b",
        }));
        return db.StreamAddAsync(RedisKickConsumer.StreamKey,
            [
                new NameValueEntry(RedisEventStream.FieldType, KickEvents.SessionSuperseded),
                new NameValueEntry(RedisEventStream.FieldPayload, payload),
            ]);
    }

    [SkippableFact]
    public async Task Dispatches_OwnEvents_FiltersForeign_AcksEverything()
    {
        fixture.SkipUnlessAvailable(nameof(Dispatches_OwnEvents_FiltersForeign_AcksEverything));
        fixture.Flush();

        await using var mux = await ConnectRawAsync(fixture.Addr);
        var db = mux.GetDatabase();

        var handled = new ConcurrentQueue<SessionSupersededPayload>();
        var consumer = new RedisKickConsumer(mux, "srv-me",
            p => { handled.Enqueue(p); return Task.CompletedTask; },
            NullLogger.Instance, pollInterval: TimeSpan.FromMilliseconds(50));
        await consumer.StartAsync();

        // One for us, one for another server, one malformed.
        await XAddSupersede(db, "u1", "srv-me", "jti-1");
        await XAddSupersede(db, "u2", "srv-other", "jti-2");
        await db.StreamAddAsync(RedisKickConsumer.StreamKey,
            [
                new NameValueEntry(RedisEventStream.FieldType, KickEvents.SessionSuperseded),
                new NameValueEntry(RedisEventStream.FieldPayload, "not json"),
            ]);

        Assert.True(await EventuallyAsync(() => consumer.Consumed >= 3),
            $"consumer saw {consumer.Consumed}/3 entries");

        // Only our event was dispatched; the foreign one was filtered silently and
        // the malformed one was counted.
        var p = Assert.Single(handled);
        Assert.Equal("u1", p.UserId);
        Assert.Equal("jti-1", p.Jti);
        Assert.Equal(1, consumer.Malformed);

        // Everything is ACKed — no fake backlog in this group's PEL.
        Assert.True(await EventuallyAsync(() =>
        {
            var pending = db.StreamPending(RedisKickConsumer.StreamKey, "gs:srv-me");
            return pending.PendingMessageCount == 0;
        }), "entries left pending after handling");

        // Graceful dispose destroys the group (retired server ids leave nothing
        // behind in a noeviction Redis)…
        await consumer.DisposeAsync();
        var groups = await db.StreamGroupInfoAsync(RedisKickConsumer.StreamKey);
        Assert.DoesNotContain(groups, g => g.Name == "gs:srv-me");
        // …but never the stream itself, which other servers' groups still read.
        Assert.True(await db.KeyExistsAsync(RedisKickConsumer.StreamKey));
    }

    [SkippableFact]
    public async Task GroupStartsAtDollar_HistoryIsNotReplayed()
    {
        fixture.SkipUnlessAvailable(nameof(GroupStartsAtDollar_HistoryIsNotReplayed));
        fixture.Flush();

        await using var mux = await ConnectRawAsync(fixture.Addr);
        var db = mux.GetDatabase();

        // Published BEFORE the consumer exists — targets a connection that cannot
        // exist in this process, so replaying it would be pure no-op work.
        await XAddSupersede(db, "u-old", "srv-late", "jti-old");

        var handled = new ConcurrentQueue<SessionSupersededPayload>();
        var consumer = new RedisKickConsumer(mux, "srv-late",
            p => { handled.Enqueue(p); return Task.CompletedTask; },
            NullLogger.Instance, pollInterval: TimeSpan.FromMilliseconds(50));
        await consumer.StartAsync();

        await XAddSupersede(db, "u-new", "srv-late", "jti-new");
        Assert.True(await EventuallyAsync(() => !handled.IsEmpty), "post-start event never arrived");

        var p = Assert.Single(handled);
        Assert.Equal("u-new", p.UserId);
        await consumer.DisposeAsync();
    }

    [SkippableFact]
    public async Task CrashRestart_DrainsOwnPel_AndTheJtiGuardMakesItSafe()
    {
        fixture.SkipUnlessAvailable(nameof(CrashRestart_DrainsOwnPel_AndTheJtiGuardMakesItSafe));
        fixture.Flush();

        await using var mux = await ConnectRawAsync(fixture.Addr);
        var db = mux.GetDatabase();

        // Simulate a consumer that read an entry and crashed before ACKing: create
        // the group, read the entry raw under the consumer's name, ACK nothing.
        await db.StreamCreateConsumerGroupAsync(
            RedisKickConsumer.StreamKey, "gs:srv-crash", StreamPosition.NewMessages, createStream: true);
        await XAddSupersede(db, "u-stranded", "srv-crash", "jti-stranded");
        var read = await db.StreamReadGroupAsync(
            RedisKickConsumer.StreamKey, "gs:srv-crash", "srv-crash", StreamPosition.NewMessages, 16);
        Assert.Single(read);

        // "Reboot": a fresh consumer with the same server id must drain the stale
        // PEL first (handler runs — in production KickPlayerAsync no-ops because
        // the connection died with the old process) and leave nothing pending.
        var handled = new ConcurrentQueue<SessionSupersededPayload>();
        var consumer = new RedisKickConsumer(mux, "srv-crash",
            p => { handled.Enqueue(p); return Task.CompletedTask; },
            NullLogger.Instance, pollInterval: TimeSpan.FromMilliseconds(50));
        await consumer.StartAsync();

        Assert.True(await EventuallyAsync(() =>
        {
            var pending = db.StreamPending(RedisKickConsumer.StreamKey, "gs:srv-crash");
            return pending.PendingMessageCount == 0;
        }), "stale PEL entry was never drained/ACKed");
        var p = Assert.Single(handled);
        Assert.Equal("u-stranded", p.UserId);
        await consumer.DisposeAsync();
    }
}
