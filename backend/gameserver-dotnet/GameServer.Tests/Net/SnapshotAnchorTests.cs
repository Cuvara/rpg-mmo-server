using System.IO;
using GameServer.Net;
using GameServer.Net.Transport;
using GameServer.World;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.GameLogic.Components;
using Xunit;

namespace GameServer.Tests.Net;

/// <summary>
/// A connection whose own entity cannot be resolved must be told nothing, rather than
/// being told about the world origin (#385).
/// </summary>
/// <remarks>
/// <para>
/// <c>TryGetSnapshotAnchor</c>'s result used to be discarded. On failure it leaves
/// <c>anchor</c> at <c>default(Vec2)</c> — (0, 0) — and the gather then centred the whole
/// area of interest there.
/// </para>
/// <para>
/// <b>Why that was worse than a wrong position.</b> On this map the origin is the single
/// most populated point: enemies spawn on a ring of radius 13 about it and walk inward.
/// A connection that failed to resolve its anchor therefore received a <i>busy, plausible</i>
/// world — mobs moving correctly, snapshots at full rate, <c>entities_shed</c> at zero —
/// where the only thing wrong was that none of it was near the player. Nothing on either
/// side reported anything.
/// </para>
/// <para>
/// So the assertion that matters is not "the anchor is correct". It is that the failure is
/// <b>observable</b>: nothing staged, and a counter that moves.
/// </para>
/// </remarks>
public class SnapshotAnchorTests
{
    private sealed class NullTransport : ITransportConnection
    {
        public Stream Stream { get; } = Stream.Null;
        public string RemoteEndPoint => "anchor-test";
        public void Close() { }
        public void Dispose() { }
    }

    private static Connection NewConnection(string userId) =>
        new(userId, new NullTransport(), NullLogger.Instance, WireEncoding.Proto);

    /// <summary>
    /// The control arm. Without it, the failing arm below is also satisfied by a fixture
    /// that stages nothing for some unrelated reason — an empty result reading as good news.
    /// </summary>
    [Fact]
    public void AResolvableAnchor_StagesASnapshot()
    {
        using var world = new EcsWorld();
        world.AddEntity(TestHelpers.CreatePlayer("p1", x: 40, y: 40));
        var conn = NewConnection("p1");

        bool gathered = false;
        world.ReadAll(reader => gathered = conn.GatherSnapshotView(
            reader, GameConstants.DefaultAoiRadius, tick: 1, keyframeInterval: 30));

        Assert.True(gathered);
        Assert.True(conn.TakePendingSnapshot(
            out _, out _, out _, out _, out _, out var anchor));
        Assert.Equal(40f, anchor.X);
        Assert.Equal(40f, anchor.Y);
    }

    [Fact]
    public void AnUnresolvableAnchor_StagesNothingAndSaysSo()
    {
        using var world = new EcsWorld();
        // Populate the ORIGIN, exactly as the enemy spawner does. If the gather fell back
        // to (0,0) these are what the connection would be sent, and the old code would
        // have produced a full, healthy-looking snapshot of them.
        world.AddEntity(TestHelpers.CreatePlayer("someone-else", x: 0, y: 0));

        // A connection with no entity of its own in the world.
        var conn = NewConnection("ghost");

        bool gathered = true;
        world.ReadAll(reader => gathered = conn.GatherSnapshotView(
            reader, GameConstants.DefaultAoiRadius, tick: 1, keyframeInterval: 30));

        Assert.False(gathered);

        // Nothing staged: no job, and therefore no marker for the write task to act on.
        Assert.False(conn.TakePendingSnapshot(
            out _, out _, out _, out _, out _, out _));
    }

    /// <summary>
    /// The connection recovers on its own once its entity exists: the skip is per tick,
    /// not a latch. A fix that disabled the viewer permanently would pass the test above.
    /// </summary>
    [Fact]
    public void TheSkipIsPerTick_AndRecoversWhenTheEntityAppears()
    {
        using var world = new EcsWorld();
        var conn = NewConnection("late");

        bool first = true;
        world.ReadAll(reader => first = conn.GatherSnapshotView(
            reader, GameConstants.DefaultAoiRadius, tick: 1, keyframeInterval: 30));
        Assert.False(first);

        world.AddEntity(TestHelpers.CreatePlayer("late", x: 7, y: 9));

        bool second = false;
        world.ReadAll(reader => second = conn.GatherSnapshotView(
            reader, GameConstants.DefaultAoiRadius, tick: 2, keyframeInterval: 30));

        Assert.True(second);
        Assert.True(conn.TakePendingSnapshot(
            out _, out _, out _, out _, out _, out var anchor));
        Assert.Equal(7f, anchor.X);
        Assert.Equal(9f, anchor.Y);
    }
}
