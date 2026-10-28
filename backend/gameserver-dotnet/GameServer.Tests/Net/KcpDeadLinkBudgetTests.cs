using System.Reflection;
using GameServer.Net.Transport;

namespace GameServer.Tests.Net;

public sealed class KcpDeadLinkBudgetTests
{
    private const int UpdateStepMs = KcpTuning.Interval;
    private static readonly FieldInfo RtoField = typeof(Kcp).GetField("_rxRto", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [Theory]
    [InlineData(30, 3180)]
    [InlineData(100, 10450)]
    [InlineData(300, 31350)]
    public void DeadLinkClose_UsesTheConfiguredRtoBudget(int rtoMs, int expectedCloseMs)
    {
        var nowMs = 0u;
        var kcp = CreateSilentPeer(rtoMs, () => nowMs);

        kcp.Update();
        for (nowMs = UpdateStepMs; nowMs <= expectedCloseMs; nowMs += UpdateStepMs)
        {
            kcp.Update();
            if (kcp.DeadLinkReached) break;
        }

        Assert.True(kcp.DeadLinkReached, $"RTO={rtoMs} ms did not close by {expectedCloseMs} ms");
        Assert.Equal((uint)expectedCloseMs, nowMs);
    }

    [Fact]
    public void ThirtyMillisecondRto_SurvivesTwoPointFiveSeconds_AndClosesByThreePointFive()
    {
        var nowMs = 0u;
        var kcp = CreateSilentPeer(30, () => nowMs);
        kcp.Update();

        for (nowMs = UpdateStepMs; nowMs <= 2500; nowMs += UpdateStepMs) kcp.Update();
        Assert.False(kcp.DeadLinkReached);

        for (; nowMs <= 3500; nowMs += UpdateStepMs)
        {
            kcp.Update();
            if (kcp.DeadLinkReached) break;
        }

        Assert.True(kcp.DeadLinkReached);
        Assert.Equal(3180u, nowMs);
    }

    private static Kcp CreateSilentPeer(int rtoMs, Func<uint> clock)
    {
        var kcp = new Kcp(1, static (_, _) => { }, clock);
        KcpTuning.Apply(kcp, headerSize: 0);
        RtoField.SetValue(kcp, (uint)rtoMs);
        Assert.Equal((uint)rtoMs, kcp.RtoMs);
        Assert.Equal(0, kcp.Send(new byte[] { 1 }));
        return kcp;
    }
}
