using GameServer.Server;

namespace GameServer.Tests.Server;

public class JtiTrackerTests
{
    [Fact]
    public void TryConsume_FirstUse_ReturnsTrue()
    {
        var tracker = new JtiTracker();
        Assert.True(tracker.TryConsume("jti-1"));
    }

    [Fact]
    public void TryConsume_Replay_ReturnsFalse()
    {
        var tracker = new JtiTracker();
        Assert.True(tracker.TryConsume("jti-1"));
        Assert.False(tracker.TryConsume("jti-1"));
    }

    [Fact]
    public void TryConsume_DifferentJtis_AllSucceed()
    {
        var tracker = new JtiTracker();
        Assert.True(tracker.TryConsume("jti-1"));
        Assert.True(tracker.TryConsume("jti-2"));
        Assert.True(tracker.TryConsume("jti-3"));
        Assert.Equal(3, tracker.Count);
    }

    [Fact]
    public void Count_ReflectsConsumedTokens()
    {
        var tracker = new JtiTracker();
        Assert.Equal(0, tracker.Count);
        tracker.TryConsume("jti-1");
        Assert.Equal(1, tracker.Count);
        tracker.TryConsume("jti-2");
        Assert.Equal(2, tracker.Count);
    }
}
