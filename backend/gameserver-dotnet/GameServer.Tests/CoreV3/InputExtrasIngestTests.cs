using GameServer.World;
using Shared.GameLogic.Components;

namespace GameServer.Tests.CoreV3;

/// <summary>
/// Protocol 3 input fields ride through the ingest queue, and edge-triggered inputs (a cast,
/// a jump) are never coalesced away by a following movement packet.
/// </summary>
public class InputExtrasIngestTests
{
    [Fact]
    public void ACastFollowedByMovement_IsNotCoalescedAway()
    {
        using var world = new EcsWorld();
        world.AddEntity(TestHelpers.CreatePlayer("p1"));
        var ingress = new InputIngress();

        world.PushInput("p1", new InputData(1, 0f, 0f, null, 3, null, default), ingress);
        world.PushInput("p1", new InputData(2, 1f, 0f, null), ingress);

        var drained = world.DrainInputs();
        Assert.Equal(2, drained.Count);
        Assert.True(drained[0].Input.HasAbility);
    }

    [Fact]
    public void AJumpFollowedByMovement_IsNotCoalescedAway_AndTheExtrasSurvive()
    {
        using var world = new EcsWorld();
        world.AddEntity(TestHelpers.CreatePlayer("p1"));
        var ingress = new InputIngress();

        var extras = new InputExtras(jump: true, aimZ: 1.5f, renderTick: 9, renderAlpha: 0.25f, spawnSeq: 4);
        world.PushInput("p1", new InputData(1, 1f, 0f, null), ingress, in extras);
        world.PushInput("p1", new InputData(2, 1f, 0f, null), ingress);
        world.PushInput("p1", new InputData(3, 1f, 0f, null), ingress); // coalesces with tick 2

        var drained = world.DrainInputs();
        Assert.Equal(2, drained.Count);
        Assert.True(drained[0].Extras.Jump);
        Assert.Equal(1.5f, drained[0].Extras.AimZ);
        Assert.Equal(9ul, drained[0].Extras.RenderTick);
        Assert.Equal(0.25f, drained[0].Extras.RenderAlpha);
        Assert.Equal(4u, drained[0].Extras.SpawnSeq);
        Assert.Equal(3ul, drained[1].Input.Tick);

        // Rebinding a stale handle keeps the extras.
        PendingInput rebound = drained[0].WithHandle(default);
        Assert.True(rebound.Extras.Jump);
    }
}
