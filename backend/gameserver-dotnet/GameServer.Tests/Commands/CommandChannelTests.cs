using GameServer.Commands;
using GameServer.Gameplay;
using GameServer.Net;
using GameServer.Persistence;
using GameServer.World;
using RpgMmo.Wire.V1;
using Shared.GameLogic.Components;
using Shared.GameLogic.Gameplay;
using Shared.GameLogic.Systems;
using Envelope = GameServer.Net.Envelope;
using GameplayInventoryRequest = Shared.GameLogic.Gameplay.InventoryRequest;

namespace GameServer.Tests.Commands;

/// <summary>
/// The command channel end to end (ADR-30 decision 4): version gating, rate limiting, the opcode
/// router, every handler, exactly one CommandResult per request, the InventoryChanged push after
/// a mutation, and the pick-up's at-most-once grant. All over real sockets.
/// </summary>
public class CommandChannelTests
{
    private static byte[] Empty => Array.Empty<byte>();

    private static InventoryView View(CommandResult r)
    {
        Assert.True(InventoryView.TryRead(r.Payload.Span, out InventoryView? v));
        return v!;
    }

    private static InventoryView PushedView(ServerPush push)
    {
        Assert.Equal(GameplayOpcodes.InventoryChanged, push.Opcode);
        Assert.True(InventoryChanged.TryRead(push.Payload.Span, out InventoryChanged? changed));
        return changed!.View!;
    }

    private static string SpawnItemNear(V3ServerHarness h, string userId, float dx, string itemId, int quantity, out ulong despawnTick)
    {
        EntityState player = h.Server.World.GetEntity(userId)!.Value;
        string id = "";
        ulong despawn = 1_000_000;
        h.Server.World.UpdateComponents(w =>
            id = CombatResolver.SpawnItem(w, itemId, quantity, new Vec2(player.Position.X + dx, player.Position.Y), despawn));
        despawnTick = despawn;
        return id;
    }

    [Theory]
    [InlineData(WireEncoding.Proto)]
    [InlineData(WireEncoding.Json)]
    public async Task Inventory_AnswersExactlyOneResult_WithTheView(WireEncoding encoding)
    {
        await using var h = await V3ServerHarness.StartAsync();
        using var c = await h.JoinAsync("u-inv", 3, encoding);

        var (result, pushes) = await c.CommandAsync(GameplayOpcodes.Inventory, new GameplayInventoryRequest().ToByteArray());
        Assert.True(result.Ok, result.Error);
        Assert.Empty(View(result).Items);
        Assert.Empty(pushes);

        // Exactly one: nothing else with that seq follows.
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        try
        {
            while (true)
            {
                Envelope? env = await WireProtocol.DecodeAsync(c.Stream, cts.Token);
                if (env == null) break;
                Assert.NotEqual((uint)MsgType.CommandResult, env.Type);
            }
        }
        catch (OperationCanceledException) { /* quiet: expected */ }

        Assert.Equal(1, h.Metrics.CommandsReceived);
        Assert.Equal(1, h.Metrics.CommandsAccepted);
    }

    [Fact]
    public async Task V2Peer_IsAnsweredUnknownOpcode_AndNothingRuns()
    {
        await using var h = await V3ServerHarness.StartAsync();
        using var c = await h.JoinAsync("u-v2", 2, WireEncoding.Proto);
        string item = SpawnItemNear(h, "u-v2", 0.5f, "wolf_pelt", 1, out _);

        var (result, _) = await c.CommandAsync(GameplayOpcodes.Pickup, new PickupRequest { EntityId = item }.ToByteArray());
        Assert.False(result.Ok);
        Assert.Equal(GameplayErrors.UnknownOpcode, result.Error);
        Assert.Equal(1, h.Metrics.CommandsRejectedFor(CommandRouter.ReasonProtocolVersion));
        Assert.NotNull(h.Server.World.GetEntity(item)); // never executed
    }

    [Fact]
    public async Task UnknownOpcode_AndInvalidPayload_AreNamed()
    {
        await using var h = await V3ServerHarness.StartAsync();
        using var c = await h.JoinAsync("u-bad", 3, WireEncoding.Proto);

        var (unknown, _) = await c.CommandAsync(99, Empty);
        Assert.False(unknown.Ok);
        Assert.Equal(GameplayErrors.UnknownOpcode, unknown.Error);

        var (zero, _) = await c.CommandAsync(GameplayOpcodes.None, Empty);
        Assert.Equal(GameplayErrors.UnknownOpcode, zero.Error);

        var (push, _) = await c.CommandAsync(GameplayOpcodes.InventoryChanged, Empty);
        Assert.Equal(GameplayErrors.UnknownOpcode, push.Error);

        var (garbage, _) = await c.CommandAsync(GameplayOpcodes.Pickup, new byte[] { 0x0A, 0x7F, 0x01 });
        Assert.False(garbage.Ok);
        Assert.Equal(GameplayErrors.InvalidPayload, garbage.Error);

        Assert.Equal(4, h.Metrics.CommandsRejected);
    }

    [Fact]
    public async Task ABurst_IsRateLimited_AndEveryRequestStillGetsOneResult()
    {
        await using var h = await V3ServerHarness.StartAsync();
        using var c = await h.JoinAsync("u-flood", 3, WireEncoding.Proto);

        const int Sent = 30;
        for (uint seq = 1; seq <= Sent; seq++) await c.SendCommandAsync(seq, GameplayOpcodes.Inventory, Empty);

        var answered = new Dictionary<uint, CommandResult>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (answered.Count < Sent)
        {
            Envelope env = (await WireProtocol.DecodeAsync(c.Stream, cts.Token))!;
            if (env.Type != (uint)MsgType.CommandResult) continue;
            CommandResult r = WireProtocol.GetPayload<CommandResult>(env);
            Assert.True(answered.TryAdd(r.Seq, r), $"seq {r.Seq} answered twice");
        }

        int ok = answered.Values.Count(r => r.Ok);
        int limited = answered.Values.Count(r => r.Error == GameplayErrors.RateLimited);
        Assert.Equal(Sent, ok + limited);
        Assert.InRange(ok, 1, CommandLimits.BurstCapacity + 2); // burst plus at most a refill or two
        Assert.True(limited > 0);
    }

    [Fact]
    public async Task Pickup_GrantsOnce_PushesBeforeTheResult_AndTheItemCannotBeTakenTwice()
    {
        await using var h = await V3ServerHarness.StartAsync();
        using var c = await h.JoinAsync("u-pick", 3, WireEncoding.Proto);
        string item = SpawnItemNear(h, "u-pick", 0.8f, "wolf_pelt", 2, out ulong despawnTick);

        // A brand-new character has no character_state row; the first grant saves it first.
        var (result, pushes) = await c.CommandAsync(GameplayOpcodes.Pickup, new PickupRequest { EntityId = item }.ToByteArray());
        Assert.True(result.Ok, result.Error);
        ItemStack stack = Assert.Single(View(result).Items);
        Assert.Equal("wolf_pelt", stack.ItemId);
        Assert.Equal(2u, stack.Quantity);
        Assert.Equal(ItemContainers.Bag, stack.Container);
        ServerPush push = Assert.Single(pushes);
        Assert.Equal(View(result).ToByteArray(), PushedView(push).ToByteArray());

        // Gone from the world, granted under the deterministic id, exactly once.
        Assert.Null(h.Server.World.GetEntity(item));
        IReadOnlyList<CharacterItem> items = await h.Store.ListItemsAsync("u-pick", CancellationToken.None);
        Assert.Equal(h.Server.CommandRouter.PickupGrantId(item, despawnTick), Assert.Single(items).GrantedBy);

        var (again, againPushes) = await c.CommandAsync(GameplayOpcodes.Pickup, new PickupRequest { EntityId = item }.ToByteArray());
        Assert.False(again.Ok);
        Assert.Equal(GameplayErrors.NotFound, again.Error);
        Assert.Empty(againPushes);
        Assert.Single(await h.Store.ListItemsAsync("u-pick", CancellationToken.None));
    }

    [Fact]
    public async Task Pickup_TwoPlayersRacingForOneItem_ExactlyOneGetsIt()
    {
        await using var h = await V3ServerHarness.StartAsync();
        using var a = await h.JoinAsync("u-a", 3, WireEncoding.Proto);
        using var b = await h.JoinAsync("u-b", 3, WireEncoding.Proto);
        EntityState pa = h.Server.World.GetEntity("u-a")!.Value;
        h.Server.World.UpdateComponents(w =>
        {
            EntityHandle hb = w.Resolve("u-b");
            w.PositionOf(in hb).Value = pa.Position;
        });
        string item = SpawnItemNear(h, "u-a", 0.5f, "wolf_pelt", 1, out _);

        byte[] payload = new PickupRequest { EntityId = item }.ToByteArray();
        var ra = a.CommandAsync(GameplayOpcodes.Pickup, payload);
        var rb = b.CommandAsync(GameplayOpcodes.Pickup, payload);
        CommandResult[] results = { (await ra).Result, (await rb).Result };

        Assert.Equal(1, results.Count(r => r.Ok));
        Assert.Equal(GameplayErrors.NotFound, results.Single(r => !r.Ok).Error);
        int total = (await h.Store.ListItemsAsync("u-a", CancellationToken.None)).Count
                    + (await h.Store.ListItemsAsync("u-b", CancellationToken.None)).Count;
        Assert.Equal(1, total);
    }

    [Fact]
    public async Task Pickup_OutOfRange_LeavesTheItemInTheWorld()
    {
        await using var h = await V3ServerHarness.StartAsync();
        using var c = await h.JoinAsync("u-far", 3, WireEncoding.Proto);
        string item = SpawnItemNear(h, "u-far", CommandLimits.PickupRange + 2f, "wolf_pelt", 1, out _);

        var (result, _) = await c.CommandAsync(GameplayOpcodes.Pickup, new PickupRequest { EntityId = item }.ToByteArray());
        Assert.False(result.Ok);
        Assert.Equal(GameplayErrors.OutOfRange, result.Error);
        Assert.NotNull(h.Server.World.GetEntity(item));
    }

    [Fact]
    public async Task Equip_IsValidatedAgainstContent_AndUnequipUndoesIt()
    {
        await using var h = await V3ServerHarness.StartAsync();
        using var c = await h.JoinAsync("u-eq", 3, WireEncoding.Proto);
        Assert.True(await h.Server.SavePlayerNowAsync("u-eq")); // the character row items hang off
        string sword = (await h.Store.GrantItemAsync("g-sword", "u-eq", "iron_sword", 1, CancellationToken.None)).InstanceId;
        string pelt = (await h.Store.GrantItemAsync("g-pelt", "u-eq", "wolf_pelt", 3, CancellationToken.None)).InstanceId;

        var (notWearable, _) = await c.CommandAsync(GameplayOpcodes.Equip, new EquipRequest { InstanceId = pelt, Slot = "weapon" }.ToByteArray());
        Assert.Equal(GameplayErrors.SlotMismatch, notWearable.Error);
        var (wrongSlot, _) = await c.CommandAsync(GameplayOpcodes.Equip, new EquipRequest { InstanceId = sword, Slot = "head" }.ToByteArray());
        Assert.Equal(GameplayErrors.SlotMismatch, wrongSlot.Error);
        var (bogusSlot, _) = await c.CommandAsync(GameplayOpcodes.Equip, new EquipRequest { InstanceId = sword, Slot = "Weapon" }.ToByteArray());
        Assert.Equal(GameplayErrors.SlotMismatch, bogusSlot.Error);
        var (missing, _) = await c.CommandAsync(GameplayOpcodes.Equip, new EquipRequest { InstanceId = "nope", Slot = "weapon" }.ToByteArray());
        Assert.Equal(GameplayErrors.NotFound, missing.Error);

        var (equipped, pushes) = await c.CommandAsync(GameplayOpcodes.Equip, new EquipRequest { InstanceId = sword, Slot = "weapon" }.ToByteArray());
        Assert.True(equipped.Ok, equipped.Error);
        Assert.Equal(0, equipped.Payload.Length);
        ItemStack worn = PushedView(Assert.Single(pushes)).Items.Single(i => i.InstanceId == sword);
        Assert.Equal(ItemContainers.Equipped, worn.Container);
        Assert.Equal("weapon", worn.Slot);

        var (unequipped, pushes2) = await c.CommandAsync(GameplayOpcodes.Unequip, new UnequipRequest { Slot = "weapon" }.ToByteArray());
        Assert.True(unequipped.Ok, unequipped.Error);
        Assert.Equal(ItemContainers.Bag, PushedView(Assert.Single(pushes2)).Items.Single(i => i.InstanceId == sword).Container);

        var (nothingWorn, _) = await c.CommandAsync(GameplayOpcodes.Unequip, new UnequipRequest { Slot = "weapon" }.ToByteArray());
        Assert.Equal(GameplayErrors.NotFound, nothingWorn.Error);
    }

    [Fact]
    public async Task UseItem_ConsumesExactlyOne()
    {
        await using var h = await V3ServerHarness.StartAsync();
        using var c = await h.JoinAsync("u-use", 3, WireEncoding.Proto);
        Assert.True(await h.Server.SavePlayerNowAsync("u-use"));
        string pelt = (await h.Store.GrantItemAsync("g-use", "u-use", "wolf_pelt", 2, CancellationToken.None)).InstanceId;

        var (first, pushes) = await c.CommandAsync(GameplayOpcodes.UseItem, new UseItemRequest { InstanceId = pelt }.ToByteArray());
        Assert.True(first.Ok, first.Error);
        Assert.Equal(1u, PushedView(Assert.Single(pushes)).Items.Single().Quantity);

        var (second, pushes2) = await c.CommandAsync(GameplayOpcodes.UseItem, new UseItemRequest { InstanceId = pelt }.ToByteArray());
        Assert.True(second.Ok, second.Error);
        Assert.Empty(PushedView(Assert.Single(pushes2)).Items);

        var (gone, _) = await c.CommandAsync(GameplayOpcodes.UseItem, new UseItemRequest { InstanceId = pelt }.ToByteArray());
        Assert.Equal(GameplayErrors.NotFound, gone.Error);
    }

    [Fact]
    public async Task Commands_ActOnTheJoinedCharacter_NotTheAccount()
    {
        await using var h = await V3ServerHarness.StartAsync();
        using var c = await h.JoinAsync("u-alt", 3, WireEncoding.Proto, characterId: "char-alt");
        Assert.Equal("char-alt", c.Join.CharacterId);
        string item = SpawnItemNear(h, "u-alt", 0.5f, "wolf_pelt", 1, out _);

        var (result, _) = await c.CommandAsync(GameplayOpcodes.Pickup, new PickupRequest { EntityId = item }.ToByteArray());
        Assert.True(result.Ok, result.Error);
        Assert.Single(await h.Store.ListItemsAsync("char-alt", CancellationToken.None));
    }

    [Fact]
    public void GrantIds_AreDeterministicPerDrop_AndDistinctPerProcess()
    {
        var sessions = new CharacterSessions();
        using var world = new EcsWorld();
        var a = new CommandRouter(world, null, sessions, null, "gs-1", "boot-a", null, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        var b = new CommandRouter(world, null, sessions, null, "gs-1", "boot-b", null, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        Assert.Equal(a.PickupGrantId("item-3", 900), a.PickupGrantId("item-3", 900));
        Assert.NotEqual(a.PickupGrantId("item-3", 900), a.PickupGrantId("item-3", 1800)); // recycled id, later drop
        Assert.NotEqual(a.PickupGrantId("item-3", 900), b.PickupGrantId("item-3", 900));  // restarted process
        Assert.StartsWith("gs-1:", a.PickupGrantId("item-3", 900));
    }

    [Fact]
    public void TokenBucket_AllowsTheBurst_ThenRefillsAtTheRate()
    {
        long now = 1_000;
        var bucket = new TokenBucket(CommandLimits.BurstCapacity, CommandLimits.RefillPerSecond, now);
        for (int i = 0; i < CommandLimits.BurstCapacity; i++) Assert.True(bucket.TryTake(now));
        Assert.False(bucket.TryTake(now));

        long oneToken = (long)(System.Diagnostics.Stopwatch.Frequency / CommandLimits.RefillPerSecond) + 1;
        Assert.True(bucket.TryTake(now + oneToken));
        Assert.False(bucket.TryTake(now + oneToken));
    }
}
