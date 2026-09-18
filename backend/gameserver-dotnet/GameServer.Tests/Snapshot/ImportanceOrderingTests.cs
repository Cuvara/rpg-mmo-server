using GameServer.Snapshot;
using RpgMmo.Wire.V1;
using Shared.GameLogic.Components;
using SimAction = Shared.GameLogic.Components.EntityAction;
using W = GameServer.Snapshot.ReplicationImportance.Weights;

namespace GameServer.Tests.Snapshot;

/// <summary>
/// The invariant the whole design rests on: gameplay importance decides the order
/// <b>among equally-aged candidates</b>, and never outranks deferral age.
///
/// <para><b>Why that has to be a test and not a comment.</b> The starvation bound —
/// max deferral is the size of the dirty set, independent of session length — is a
/// consequence of the comparison being strictly oldest-first. Fold age into a weighted sum,
/// or let any gameplay term jump ahead of it, and the bound silently becomes a function of
/// the weights: a high-scoring crowd can then hold the budget for ever while one unlucky
/// entity is never sent, and every existing test still passes, because they all run with
/// weights at zero.</para>
/// </summary>
public class ImportanceOrderingTests
{
    private const int NoKeyframes = int.MaxValue;

    /// <summary>Weights large enough that any sane score dwarfs the others.</summary>
    private static W Loud => new(distance: 1000f, change: 1000f, type: 1000f, combat: 1000f);

    private static EntityState Mob(string id, float x, float y, int hp = 100)
    {
        EntityState e = TestHelpers.CreateMob(id, x, y);
        e.Hp = hp;
        return e;
    }

    /// <summary>
    /// One deferred entity against a crowd of maximally important fresh ones. The deferred
    /// one must be carried, every time, or the aging round-robin is gone.
    /// </summary>
    [Fact]
    public void DeferralAge_OutranksEveryGameplayScore()
    {
        // Budget small enough that only a couple of entities fit per snapshot, so something
        // is deferred on every tick and the aging order is what decides.
        var state = new SnapshotDeltaState { MaxSnapshotBytes = 120, ImportanceWeights = Loud };

        var world = new List<EntityState>();
        // The victim: far away, a mob, never changing anything but position -- the lowest
        // score the policy can produce.
        world.Add(Mob("victim", 49f, 0f));
        // The crowd: close, players, attacking, taking damage. Everything the score loves.
        for (int i = 0; i < 12; i++)
        {
            EntityState p = TestHelpers.CreatePlayer($"star{i:D2}", 0.5f + (i * 0.01f), 0f);
            p.Action = SimAction.Attacking;
            world.Add(p);
        }

        int victimCarried = 0;
        ulong longestVictimGap = 0, lastVictimTick = 0;
        uint victimHandle = 0;

        for (ulong tick = 1; tick <= 400; tick++)
        {
            // Everything moves, so everything is dirty every tick.
            for (int i = 0; i < world.Count; i++)
            {
                EntityState e = world[i];
                e.Position = new Vec2(e.Position.X + 0.01f, e.Position.Y + 0.01f);
                if (i > 0) e.Hp = 100 - (int)(tick % 40);   // the crowd keeps "changing"
                world[i] = e;
            }

            SnapshotMessage msg = state.Encode(tick, tick, world, NoKeyframes, intern: true,
                observer: new Vec2(0f, 0f));

            // The id rides only the snapshot that introduces the handle, so track the
            // handle once it is known and match on either afterwards.
            foreach (EntitySnapshot x in msg.Entities)
            {
                if (x.Id == "victim") victimHandle = x.Handle;
            }
            bool carried = msg.Entities.Any(
                x => x.Id == "victim" || (victimHandle != 0 && x.Handle == victimHandle));
            if (carried)
            {
                victimCarried++;
                if (lastVictimTick != 0)
                {
                    ulong gap = tick - lastVictimTick;
                    if (gap > longestVictimGap) longestVictimGap = gap;
                }
                lastVictimTick = tick;
            }
        }

        Assert.True(victimCarried > 0,
            "the lowest-scoring entity was never sent in 400 ticks: gameplay score has " +
            "overtaken deferral age and the starvation bound is gone");

        // Bounded by the dirty set (13 entities), not by the session. Generous headroom:
        // the claim under test is "bounded and small", not an exact figure.
        Assert.True(longestVictimGap <= 40,
            $"longest gap was {longestVictimGap} ticks against a 13-entity dirty set; " +
            "the aging round-robin is no longer strict");
    }

    /// <summary>
    /// Guards the guard. If the budget never bit, the test above would pass against a build
    /// with no scheduler at all.
    /// </summary>
    [Fact]
    public void TheBudgetActuallyBitesInThatScenario()
    {
        var state = new SnapshotDeltaState { MaxSnapshotBytes = 120, ImportanceWeights = Loud };
        var world = new List<EntityState> { Mob("victim", 49f, 0f) };
        for (int i = 0; i < 12; i++) world.Add(TestHelpers.CreatePlayer($"star{i:D2}", 0.5f, 0f));

        for (ulong tick = 1; tick <= 20; tick++)
        {
            for (int i = 0; i < world.Count; i++)
            {
                EntityState e = world[i];
                e.Position = new Vec2(e.Position.X + 0.01f, e.Position.Y);
                world[i] = e;
            }
            state.Encode(tick, tick, world, NoKeyframes, intern: true, observer: new Vec2(0f, 0f));
        }

        Assert.True(state.EntitiesShed > 0,
            "nothing was ever deferred, so the ordering under test never ran");
    }

    /// <summary>
    /// With weights on, a higher-scoring candidate wins <b>among equals</b> — which is the
    /// only place the score is allowed to decide anything.
    /// </summary>
    [Fact]
    public void AmongEquallyAgedCandidates_TheHigherScoreIsCarriedFirst()
    {
        // Two entities at the same distance, both fresh, differing only in type.
        // The mob is listed FIRST, so emitting in candidate order would carry the mob --
        // which is what makes this test able to fail.
        var state = new SnapshotDeltaState
        {
            MaxSnapshotBytes = 34,             // room for one entity and the header
            ImportanceWeights = new W(type: 100f),
        };

        var world = new List<EntityState>
        {
            Mob("mob", 10f, 0f),
            TestHelpers.CreatePlayer("player", 10f, 0f),
        };

        SnapshotMessage msg = state.Encode(1, 1, world, NoKeyframes, intern: true,
            observer: new Vec2(0f, 0f));

        // Guards the guard: if both fitted, the sort never ran and this proves nothing.
        Assert.True(state.EntitiesShed > 0,
            $"both entities fitted in {state.LastPayloadBytes} B, so nothing was ordered");
        Assert.NotEmpty(msg.Entities);
        Assert.Equal("player", msg.Entities[0].Id);
    }

    /// <summary>With Legacy weights the same scenario falls back to nearest-first, i.e. the
    /// pre-importance tie-break, and the type no longer decides.</summary>
    [Fact]
    public void WithLegacyWeights_TypeDoesNotDecide()
    {
        var state = new SnapshotDeltaState { MaxSnapshotBytes = 34, ImportanceWeights = W.Legacy };

        // Player listed first and further away. Under Legacy the nearer mob must still win,
        // because with every weight zero only distance is left to decide.
        var world = new List<EntityState>
        {
            TestHelpers.CreatePlayer("player", 20f, 0f),           // further
            Mob("mob", 5f, 0f),                                    // nearer
        };

        SnapshotMessage msg = state.Encode(1, 1, world, NoKeyframes, intern: true,
            observer: new Vec2(0f, 0f));

        Assert.True(state.EntitiesShed > 0,
            $"both entities fitted in {state.LastPayloadBytes} B, so nothing was ordered");
        Assert.NotEmpty(msg.Entities);
        Assert.Equal("mob", msg.Entities[0].Id);
    }
}
