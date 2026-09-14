using GameServer.Input;
using GameServer.Net;
using GameServer.Observability;
using RpgMmo.Wire.V1;
using Xunit;

namespace GameServer.Tests.Server;

/// <summary>
/// A reconnecting client must not have its input refused because the PREVIOUS session's
/// tick counter is still on the entity.
/// </summary>
/// <remarks>
/// <para>
/// The entity survives a disconnect for the hold window, and it carries
/// <c>InputCursor.LastInputTick</c> with it. Nothing used to clear that on reattach, so a
/// client that restarts its own input tick — which the shipped client does whenever its
/// bootstrap is recreated, i.e. a process restart or scene reload — had EVERY input
/// rejected as <c>stale_tick</c> until it climbed back past the pre-disconnect value.
/// </para>
/// <para>
/// Measured before the fix: a 5s session reached tick 88, and the reconnecting session had
/// its first 88 frames refused; a 4-player run refused 596 of 596. The freeze lasts as long
/// as the previous session did, so ten minutes of play meant ten minutes of a player who
/// could not move. <c>docs/BENCHMARK.md</c> Part XII.
/// </para>
/// <para>
/// The rule the fix follows is the one ADR-22 settles for the crypto replay counter: the
/// counter's scope must follow the <b>session</b>, not the entity.
/// </para>
/// </remarks>
public class ReconnectInputCursorTests
{
    private static GameMetrics NewMetrics() => new(HardeningHarness.MapId, $"test.{Guid.NewGuid():N}");

    private static async Task SendInputAsync(System.Net.Sockets.TcpClient c, ulong tick, float x)
    {
        var env = WireProtocol.NewEnvelope(
            MsgType.Input,
            new InputMessage { Tick = tick, MoveX = x, MoveY = 0f },
            WireEncoding.Json);
        await c.GetStream().WriteAsync(WireProtocol.Encode(env));
        await c.GetStream().FlushAsync();
    }

    /// <summary>
    /// The regression: reconnect inside the hold window, restart the tick counter at 1, and
    /// the input must be ACCEPTED rather than refused as stale.
    /// </summary>
    [Fact]
    public async Task AReconnectingClientThatRestartsItsTickIsNotRefused()
    {
        using var metrics = NewMetrics();
        // A hold long enough that the entity is certainly still there when we come back.
        await using var h = await HardeningHarness.StartAsync(metrics, hold: TimeSpan.FromSeconds(30));

        const string user = "reconnector";

        // Session one: climb the tick counter well above where session two will start.
        using (var first = await h.JoinAsync(user))
        {
            for (ulong t = 1; t <= 40; t++) await SendInputAsync(first, t, 1f);
            await h.WaitForAsync(() => h.Server.EntityCount >= 1, what: "entity spawned");

            // Let the tick loop fully consume session one before disconnecting. Without
            // this the test races its own setup: inputs still queued when the socket closes
            // drain AFTER the reattach reset and re-advance the cursor, which is a real but
            // separate effect and not the one under test here.
            await Task.Delay(500);
        }

        // Session two: same account, brand-new connection, counter restarted at 1 —
        // exactly what a client does after a process restart or scene reload.
        using var second = await h.JoinAsync(user);
        long staleBefore = metrics.InputsRejected(InputRejectionReason.StaleTick);

        for (ulong t = 1; t <= 20; t++) await SendInputAsync(second, t, 1f);

        // Give the tick loop time to drain them.
        await Task.Delay(500);

        long staleAfter = metrics.InputsRejected(InputRejectionReason.StaleTick);
        Assert.True(staleAfter == staleBefore,
            $"a reconnecting client's restarted tick must not be refused as stale, " +
            $"but {staleAfter - staleBefore} input(s) were rejected");
    }

    /// <summary>
    /// The cursor is cleared on REATTACH specifically — a first join has nothing to clear,
    /// and this pins that the reset is tied to the reconnect path rather than to every join.
    /// </summary>
    [Fact]
    public async Task AFirstJoinRecordsNoStaleRejections()
    {
        using var metrics = NewMetrics();
        await using var h = await HardeningHarness.StartAsync(metrics, hold: TimeSpan.FromSeconds(30));

        using var c = await h.JoinAsync("fresh");
        for (ulong t = 1; t <= 20; t++) await SendInputAsync(c, t, 1f);
        await Task.Delay(500);

        Assert.Equal(0, metrics.InputsRejected(InputRejectionReason.StaleTick));
    }
}
