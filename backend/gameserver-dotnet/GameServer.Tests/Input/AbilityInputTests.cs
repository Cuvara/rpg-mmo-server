using System.Linq;
using System.Text;
using GameServer.Content;
using GameServer.Input;
using GameServer.Snapshot;
using GameServer.World;
using GameServer.World.Components;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.GameLogic.Components;
using Shared.GameLogic.Content;
using Xunit;

namespace GameServer.Tests.Input;

/// <summary>
/// Abilities and the event channel through the real input path, not through the shared
/// predicates alone. These assert on what the simulation DID — health written, cooldown
/// charged, events recorded — because the shared logic being right proves nothing about
/// whether the handler calls it.
/// </summary>
public class AbilityInputTests
{
    private const uint Bolt = 1;
    private const uint Mend = 2;
    private const uint Nova = 3;

    private const string ContentJson = """
    {
      "items": [],
      "abilities": [
        { "id": 1, "name": "Bolt", "targeting": "entity", "effect": "damage",
          "range": 10, "radius": 0, "power": 20, "cooldownTicks": 5 },
        { "id": 2, "name": "Mend", "targeting": "self", "effect": "heal",
          "range": 0, "radius": 0, "power": 30, "cooldownTicks": 5 },
        { "id": 3, "name": "Nova", "targeting": "ground", "effect": "damage",
          "range": 15, "radius": 6, "power": 40, "cooldownTicks": 5 }
      ]
    }
    """;

    private static ContentDatabase Content() =>
        ContentLoader.LoadFromBytes(Encoding.UTF8.GetBytes(ContentJson), "test.json").Database;

    private sealed class Fixture : System.IDisposable
    {
        public EcsWorld World { get; } = new();
        public TickEventBuffer Events { get; } = new();
        public InputHandler Handler { get; }

        public Fixture(int playerHp = 100, int enemyHp = 100)
        {
            World.AddEntity(TestHelpers.CreatePlayer("p1", x: 0, y: 0, atk: 10));
            ref var self = ref World.ArchInternal.Get<Health>(World.ResolveLocked("p1").Value);
            self.Hp = playerHp;

            World.Spawn(new EntityState
            {
                Id = "mob-1",
                Type = "mob",
                Position = new Vec2(2, 0),
                Hp = enemyHp, MaxHp = 100,
                Speed = 2.5f,
                Attack = 5, Defense = 5,
            }, EntityTags.EnemyAi);

            Handler = new InputHandler(
                World, NullLogger.Instance, null, GameConstants.DefaultTickRate, MapBounds.Default,
                onRejected: null, onAttackAccepted: null, events: Events, content: Content());
        }

        public EntityState State(string id) => World.ComposeLocked(World.ResolveLocked(id).Value);

        public void Dispose() => World.Dispose();
    }

    private static InputData Cast(ulong tick, uint abilityId, string? target = null, float aimX = 0, float aimY = 0) =>
        new(tick, 0f, 0f, null, abilityId, target, new Vec2(aimX, aimY));

    [Fact]
    public void ATargetedDamageAbility_AppliesDamageAndEmitsBothEvents()
    {
        using var f = new Fixture(enemyHp: 100);

        f.Handler.ProcessInput("p1", Cast(1, Bolt, "mob-1"), currentTick: 1);

        // 10 attack + 20 power - 5 defense
        Assert.Equal(75, f.State("mob-1").Hp);
        Assert.Equal(1, f.Handler.Abilities.Accepted);

        var types = f.Events.Events.Select(e => e.Data.Type).ToArray();
        Assert.Equal(new[] { GameEventType.AbilityCast, GameEventType.Damage }, types);

        var damage = f.Events.Events.Single(e => e.Data.Type == GameEventType.Damage);
        Assert.Equal(25, damage.Data.Amount);
        Assert.Equal(Bolt, damage.Data.AbilityId);
        Assert.Equal("p1", damage.Data.SourceId);
        Assert.Equal("mob-1", damage.Data.TargetId);
    }

    [Fact]
    public void AKillingAbility_EmitsDeathAndSetsTheDeadAction()
    {
        using var f = new Fixture(enemyHp: 10);

        f.Handler.ProcessInput("p1", Cast(1, Bolt, "mob-1"), currentTick: 1);

        EntityState mob = f.State("mob-1");
        Assert.True(mob.Dead);
        Assert.Equal(0, mob.Hp);
        Assert.Equal(EntityAction.Dead, mob.Action);
        Assert.Contains(f.Events.Events, e => e.Data.Type == GameEventType.Death);
    }

    [Fact]
    public void CastingChargesTheCooldown_AndASecondCastWithinItIsRefused()
    {
        using var f = new Fixture();

        f.Handler.ProcessInput("p1", Cast(1, Bolt, "mob-1"), currentTick: 1);
        Assert.Equal(6ul, f.State("p1").AbilityCooldownUntilTick);

        int hpAfterFirst = f.State("mob-1").Hp;

        f.Handler.ProcessInput("p1", Cast(2, Bolt, "mob-1"), currentTick: 2);

        Assert.Equal(hpAfterFirst, f.State("mob-1").Hp);
        Assert.Equal(1, f.Handler.Abilities.Rejected);
        Assert.Same(global::Shared.GameLogic.Systems.AbilityLogic.CooldownRejection, f.Handler.Abilities.LastRejection);
    }

    /// <summary>
    /// The cooldown is charged whatever the effect turns out to be worth. If it depended on
    /// the outcome, a heal aimed at a full-health target would be free — a rate limit a
    /// client can defeat by aiming badly on purpose.
    /// </summary>
    [Fact]
    public void AHealThatRestoresNothing_StillChargesTheCooldown_AndStillReportsItself()
    {
        using var f = new Fixture(playerHp: 100);

        f.Handler.ProcessInput("p1", Cast(1, Mend), currentTick: 1);

        Assert.Equal(6ul, f.State("p1").AbilityCooldownUntilTick);
        Assert.Equal(1, f.Handler.Abilities.Accepted);

        var heal = f.Events.Events.Single(e => e.Data.Type == GameEventType.Heal);
        Assert.Equal(0, heal.Data.Amount);
        // Reported as immune rather than omitted: a client that shows nothing for a cast
        // that visibly happened looks broken.
        Assert.Equal(GameEventFlags.Immune, heal.Data.Flags);
    }

    [Fact]
    public void ASelfHeal_IsClampedToMissingHealthAndTheEventMatchesWhatWasApplied()
    {
        using var f = new Fixture(playerHp: 90);

        f.Handler.ProcessInput("p1", Cast(1, Mend), currentTick: 1);

        Assert.Equal(100, f.State("p1").Hp);

        var heal = f.Events.Events.Single(e => e.Data.Type == GameEventType.Heal);
        // 10, not the ability's 30: the number shown and the number applied are one number.
        Assert.Equal(10, heal.Data.Amount);
    }

    [Fact]
    public void AnUnknownAbilityId_IsRefusedWithoutFaultingTheTick()
    {
        using var f = new Fixture();

        f.Handler.ProcessInput("p1", Cast(1, abilityId: 999, target: "mob-1"), currentTick: 1);

        Assert.Equal(1, f.Handler.Abilities.Rejected);
        Assert.Equal(0, f.Handler.Abilities.Accepted);
        Assert.Empty(f.Events.Events);
    }

    [Fact]
    public void AnOutOfRangeTarget_IsRefusedAndNoCooldownIsSpent()
    {
        using var f = new Fixture();

        // Move the mob far away by respawning the world state around it.
        ref var pos = ref f.World.ArchInternal.Get<Position>(f.World.ResolveLocked("mob-1").Value);
        pos.Value = new Vec2(500f, 0f);

        f.Handler.ProcessInput("p1", Cast(1, Bolt, "mob-1"), currentTick: 1);

        Assert.Equal(0ul, f.State("p1").AbilityCooldownUntilTick);
        Assert.Equal(1, f.Handler.Abilities.Rejected);
    }

    [Fact]
    public void CastingAdvancesTheRetriggerCounter_AndARepeatedCastAdvancesItAgain()
    {
        using var f = new Fixture();

        f.Handler.ProcessInput("p1", Cast(1, Bolt, "mob-1"), currentTick: 1);
        uint first = f.State("p1").ActionSeq;
        Assert.NotEqual(0u, first);

        // Past the cooldown.
        f.Handler.ProcessInput("p1", Cast(10, Bolt, "mob-1"), currentTick: 10);
        uint second = f.State("p1").ActionSeq;

        Assert.NotEqual(first, second);
        Assert.Equal(EntityAction.Attacking, f.State("p1").Action);
    }

    /// <summary>
    /// Ground abilities are accepted, animated and charged, and apply no damage — there is no
    /// area query during input processing. This pins that as a STATED gap with a counter,
    /// rather than leaving it to be rediscovered as "ground abilities are broken".
    /// </summary>
    [Fact]
    public void AGroundCast_IsAcceptedAndCounted_ButAppliesNoEffectYet()
    {
        using var f = new Fixture();

        int before = f.State("mob-1").Hp;
        f.Handler.ProcessInput("p1", Cast(1, Nova, aimX: 2f, aimY: 0f), currentTick: 1);

        Assert.Equal(1, f.Handler.Abilities.Accepted);
        Assert.Equal(1, f.Handler.Abilities.GroundCastsWithoutArea);
        Assert.Equal(before, f.State("mob-1").Hp);

        // The cast itself is still reported, so a client shows it and the cooldown truthfully.
        Assert.Contains(f.Events.Events, e => e.Data.Type == GameEventType.AbilityCast);
        Assert.DoesNotContain(f.Events.Events, e => e.Data.Type == GameEventType.Damage);
    }

    [Fact]
    public void AnInputWithNoAbility_TouchesNeitherTheCounterNorTheEventBuffer()
    {
        using var f = new Fixture();

        f.Handler.ProcessInput("p1", new InputData(1, 1f, 0f, null), currentTick: 1);

        Assert.Equal(0, f.Handler.Abilities.Received);
        Assert.Empty(f.Events.Events);
    }

    /// <summary>
    /// A plain attack emits the same two events an ability does. Without this the event
    /// channel would only work for the feature that shipped with it.
    /// </summary>
    [Fact]
    public void APlainAttack_AlsoEmitsDamage_AndAKillAlsoEmitsDeath()
    {
        // 4 hp against a basic attack of 10 attack - 5 defense = 5 damage, so one hit kills.
        // The ability numbers are larger; using them here is what made this read as a defect
        // in the event path the first time.
        using var f = new Fixture(enemyHp: 4);

        f.Handler.ProcessInput("p1", new InputData(1, 0f, 0f, "mob-1"), currentTick: 1);

        Assert.Contains(f.Events.Events, e => e.Data.Type == GameEventType.Damage);
        Assert.Contains(f.Events.Events, e => e.Data.Type == GameEventType.Death);

        var damage = f.Events.Events.First(e => e.Data.Type == GameEventType.Damage);
        // A basic attack carries no ability id.
        Assert.Equal(0u, damage.Data.AbilityId);
    }

    [Fact]
    public void EventsCarryResolvedStableKeys_SoTheEncoderNeverHashesAnIdString()
    {
        using var f = new Fixture();

        f.Handler.ProcessInput("p1", Cast(1, Bolt, "mob-1"), currentTick: 1);

        var damage = f.Events.Events.Single(e => e.Data.Type == GameEventType.Damage);
        Assert.True(damage.HasSource);
        Assert.True(damage.HasTarget);
        Assert.NotEqual(PendingGameEvent.NoKey, damage.SourceKey);
        Assert.NotEqual(damage.SourceKey, damage.TargetKey);
    }

    [Fact]
    public void TheBuffer_DropsBeyondItsCapacityAndCountsWhatItDropped()
    {
        var buffer = new TickEventBuffer();

        for (int i = 0; i < TickEventBuffer.Capacity + 10; i++)
        {
            buffer.Add(GameEventData.Damage("a", "b", 1), 1, 2);
        }

        Assert.Equal(TickEventBuffer.Capacity, buffer.Count);
        Assert.Equal(10, buffer.Dropped);
    }

    [Fact]
    public void ClearingTheBuffer_LeavesNothingForTheNextTickToDeliverTwice()
    {
        var buffer = new TickEventBuffer();
        buffer.Add(GameEventData.Damage("a", "b", 1), 1, 2);

        buffer.Clear();

        Assert.Equal(0, buffer.Count);
    }
}
