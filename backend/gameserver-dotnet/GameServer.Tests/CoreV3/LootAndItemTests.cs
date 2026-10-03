using GameServer.Gameplay;
using GameServer.World;
using Shared.GameLogic.Components;

namespace GameServer.Tests.CoreV3;

/// <summary>
/// Loot rolled on a mob's death becomes an <c>item</c> entity (id, quantity, despawn tick)
/// that the command layer takes out of the world with <see cref="EcsWorld.TryTakeItemEntity"/>.
/// </summary>
public class LootAndItemTests
{
    private static SimFixture KillAMob(out ulong deathTick, ulong seed = 42)
    {
        var f = new SimFixture(lootSeed: seed);
        f.AddPlayer("p1", 0f, 0f);
        f.AddMob("m1", 2f, 1f, hp: 6);
        f.Input("p1", SimFixture.Cast(1, SimFixture.Jab, "m1")); // 10 + 1 - 5 = 6: dead
        f.Step();
        deathTick = f.Tick;
        Assert.True(f.State("m1").Dead);
        return f;
    }

    [Fact]
    public void AMobDeath_DropsTheLootTable_AsAnItemEntityAtItsFeet()
    {
        using var f = KillAMob(out ulong deathTick);

        EntityView item = Assert.Single(f.Views(), v => v.Type == "item");
        Assert.Equal(2f, item.Position.X, 3);
        Assert.Equal(1f, item.Position.Y, 3);

        f.World.ReadAll(r =>
        {
            Assert.True(r.TryGetItemDrop(item.Key, out string itemId, out int quantity, out ulong despawn));
            Assert.Equal("wolf_pelt", itemId);
            Assert.Equal(2, quantity);
            Assert.Equal(deathTick + 100, despawn);
        });
        Assert.Equal(1, f.World.Gameplay.LootRolls);
    }

    [Fact]
    public void ADroppedItem_CanBeTakenOnce_InRange_ByALiveTaker()
    {
        using var f = KillAMob(out _);
        f.AddPlayer("far", 50f, 50f);
        string itemId = Assert.Single(f.Views(), v => v.Type == "item").Id;

        Assert.Equal(ItemTakeStatus.OutOfRange, f.World.TryTakeItemEntity(itemId, "far", 3f).Status);
        Assert.Equal(ItemTakeStatus.NotAnItem, f.World.TryTakeItemEntity("m1", "p1", 3f).Status);
        Assert.Equal(ItemTakeStatus.TakerNotFound, f.World.TryTakeItemEntity(itemId, "nobody", 3f).Status);

        ItemTakeResult taken = f.World.TryTakeItemEntity(itemId, "p1", 3f);
        Assert.True(taken.Taken);
        Assert.Equal("wolf_pelt", taken.ItemId);
        Assert.Equal(2, taken.Quantity);
        Assert.False(f.Exists(itemId));

        Assert.Equal(ItemTakeStatus.NotFound, f.World.TryTakeItemEntity(itemId, "p1", 3f).Status);
    }

    [Fact]
    public void ADeadTaker_CannotTakeAnItem()
    {
        using var f = KillAMob(out _);
        string itemId = Assert.Single(f.Views(), v => v.Type == "item").Id;
        f.World.UpdateComponents(w => w.HealthOf(w.Resolve("p1")).Dead = true);

        Assert.Equal(ItemTakeStatus.TakerDead, f.World.TryTakeItemEntity(itemId, "p1", 3f).Status);
        Assert.True(f.Exists(itemId));
    }

    [Fact]
    public void ADroppedItem_DespawnsAtItsDespawnTick()
    {
        using var f = KillAMob(out ulong deathTick);
        string itemId = Assert.Single(f.Views(), v => v.Type == "item").Id;

        f.StepUntil(deathTick + 99);
        Assert.True(f.Exists(itemId));
        f.Step();
        Assert.False(f.Exists(itemId));
    }

    [Fact]
    public void AnItemIsNotATarget()
    {
        using var f = KillAMob(out _);
        string itemId = Assert.Single(f.Views(), v => v.Type == "item").Id;

        f.Input("p1", new InputData(2, 0f, 0f, itemId));
        f.Step();

        Assert.True(f.Exists(itemId));
        Assert.Equal(GameServer.Input.InputHandler.NotTargetableRejection, f.Handler.Attacks.LastRejection);
    }

    [Fact]
    public void LootRolls_AreAPureFunctionOfSeedTickKeyAndEntry()
    {
        ulong a = CombatResolver.LootRandom(42, 7, 3, 0);
        Assert.Equal(a, CombatResolver.LootRandom(42, 7, 3, 0));
        Assert.NotEqual(a, CombatResolver.LootRandom(43, 7, 3, 0));
        Assert.NotEqual(a, CombatResolver.LootRandom(42, 8, 3, 0));
        Assert.NotEqual(a, CombatResolver.LootRandom(42, 7, 4, 0));
        Assert.NotEqual(a, CombatResolver.LootRandom(42, 7, 3, 1));

        // Coarse sanity on the distribution the permille test reads: 10k rolls of a 30% drop
        // land near 3000. Not a statistical proof; a broken mixer lands at 0 or 10000.
        int hits = 0;
        for (int i = 0; i < 10_000; i++)
        {
            if (CombatResolver.LootRandom(99, (ulong)i, i * 7, 0) % 1000 < 300) hits++;
        }

        Assert.InRange(hits, 2700, 3300);
    }

    [Fact]
    public void TheSameKillOnTheSameSeed_DropsTheSameThing()
    {
        using var a = KillAMob(out _, seed: 1234);
        using var b = KillAMob(out _, seed: 1234);

        EntityView ia = Assert.Single(a.Views(), v => v.Type == "item");
        EntityView ib = Assert.Single(b.Views(), v => v.Type == "item");
        Assert.Equal(ia.Id, ib.Id);
        Assert.Equal(ia.Position, ib.Position);
    }
}
