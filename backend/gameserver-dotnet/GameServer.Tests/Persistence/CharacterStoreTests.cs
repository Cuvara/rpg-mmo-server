namespace GameServer.Tests.Persistence;

/// <summary>
/// Behavioural contract of <see cref="ICharacterStore"/> (ADR-31), run against BOTH
/// implementations so the in-memory store used in dev/tests cannot drift from Postgres.
/// Every test uses fresh GUID-based ids because the Postgres container is shared.
/// </summary>
public abstract class CharacterStoreContract : IAsyncLifetime
{
    /// <summary>Real xUnit skip when the backing store is unavailable; no-op otherwise.</summary>
    protected abstract void SkipUnlessAvailable(string testName);

    /// <summary>A store plus the legacy player store its first-load fallback reads.</summary>
    protected abstract Task<(ICharacterStore Store, IPlayerStore Players)> CreateAsync();

    /// <inheritdoc />
    public virtual Task InitializeAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public virtual Task DisposeAsync() => Task.CompletedTask;

    private static string Id(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private static CharacterState NewState(string cid, string uid) =>
        new(cid, uid, "map_01", 1.5f, -2.25f, 3.75f, 0.5f, 80, 100, 3, 1234);

    private async Task<(ICharacterStore Store, string Cid, string Uid)> WithCharacterAsync()
    {
        var (store, _) = await CreateAsync();
        string cid = Id("char"), uid = Id("user");
        await store.SaveCharacterAsync(NewState(cid, uid), default);
        return (store, cid, uid);
    }

    private static async Task<CharacterStoreError> ErrorOf(Func<Task> act)
        => (await Assert.ThrowsAsync<CharacterStoreException>(act)).Error;

    [SkippableFact]
    public async Task Load_UnknownCharacterWithoutLegacyRow_ReturnsNull()
    {
        SkipUnlessAvailable(nameof(Load_UnknownCharacterWithoutLegacyRow_ReturnsNull));
        var (store, _) = await CreateAsync();

        Assert.Null(await store.LoadCharacterAsync(Id("char"), Id("user"), default));
    }

    /// <summary>ADR-31.2: first load copies player_states into character_state.</summary>
    [SkippableFact]
    public async Task Load_FallsBackToLegacyPlayerState_ThenMaterialisesIt()
    {
        SkipUnlessAvailable(nameof(Load_FallsBackToLegacyPlayerState_ThenMaterialisesIt));
        var (store, players) = await CreateAsync();
        string cid = Id("char"), uid = Id("user");
        await players.SavePlayerAsync(new PlayerState(uid, 12.5f, -3.25f, 77, 120, "map_07"), default);

        var first = await store.LoadCharacterAsync(cid, uid, default);

        Assert.NotNull(first);
        Assert.True(first!.FromLegacyPlayerState);
        Assert.Equal(("map_07", 12.5f, -3.25f, 77, 120), (first.MapId, first.X, first.Y, first.Hp, first.MaxHp));
        Assert.Equal((0f, 0f, 1, 0L), (first.Z, first.Yaw, first.Level, first.Xp));

        // The legacy row moving afterwards must not change the character: it now has its own row.
        await players.SavePlayerAsync(new PlayerState(uid, 99f, 99f, 1, 1, "map_99"), default);
        var second = await store.LoadCharacterAsync(cid, uid, default);
        Assert.NotNull(second);
        Assert.False(second!.FromLegacyPlayerState);
        Assert.Equal(("map_07", 12.5f, 77), (second.MapId, second.X, second.Hp));
    }

    [SkippableFact]
    public async Task SaveThenLoad_RoundTripsEveryColumn()
    {
        SkipUnlessAvailable(nameof(SaveThenLoad_RoundTripsEveryColumn));
        var (store, cid, uid) = await WithCharacterAsync();

        var updated = NewState(cid, uid) with { MapId = "map_02", X = 4f, Y = 5f, Z = 6f, Yaw = 1.25f, Hp = 10, Level = 7, Xp = 9_000_000_000 };
        await store.SaveCharacterAsync(updated, default);
        var loaded = await store.LoadCharacterAsync(cid, uid, default);

        Assert.Equal(updated, loaded);
    }

    [SkippableFact]
    public async Task LoadAndSave_ForeignAccount_AreRefused()
    {
        SkipUnlessAvailable(nameof(LoadAndSave_ForeignAccount_AreRefused));
        var (store, cid, uid) = await WithCharacterAsync();
        string intruder = Id("user");

        Assert.Equal(CharacterStoreError.OwnershipMismatch,
            await ErrorOf(() => store.LoadCharacterAsync(cid, intruder, default)));
        Assert.Equal(CharacterStoreError.OwnershipMismatch,
            await ErrorOf(() => store.SaveCharacterAsync(NewState(cid, intruder) with { Hp = 1 }, default)));

        var unchanged = await store.LoadCharacterAsync(cid, uid, default);
        Assert.Equal(80, unchanged!.Hp);
    }

    [SkippableFact]
    public async Task Grant_IsIdempotentByGrantId()
    {
        SkipUnlessAvailable(nameof(Grant_IsIdempotentByGrantId));
        var (store, cid, _) = await WithCharacterAsync();
        string grant = Id("grant");

        var first = await store.GrantItemAsync(grant, cid, "potion_small", 3, default);
        var replay = await store.GrantItemAsync(grant, cid, "potion_small", 3, default);

        Assert.True(first.Applied);
        Assert.False(replay.Applied);
        Assert.Equal(first.InstanceId, replay.InstanceId);
        var items = await store.ListItemsAsync(cid, default);
        var item = Assert.Single(items);
        Assert.Equal((first.InstanceId, "potion_small", 3, ItemContainer.Bag, (string?)null, (int?)0, grant),
            (item.InstanceId, item.ItemId, item.Quantity, item.Container, item.Slot, item.BagIndex, item.GrantedBy));
    }

    [SkippableFact]
    public async Task Grant_ConcurrentReplays_ApplyExactlyOnce()
    {
        SkipUnlessAvailable(nameof(Grant_ConcurrentReplays_ApplyExactlyOnce));
        var (store, cid, _) = await WithCharacterAsync();
        string grant = Id("grant");

        var results = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() => store.GrantItemAsync(grant, cid, "ore", 1, default))));

        Assert.Equal(1, results.Count(r => r.Applied));
        Assert.Single(results.Select(r => r.InstanceId).Distinct());
        Assert.Single(await store.ListItemsAsync(cid, default));
    }

    [SkippableFact]
    public async Task Grant_ToCharacterWithoutRow_IsRefused_AndRecordsNothing()
    {
        SkipUnlessAvailable(nameof(Grant_ToCharacterWithoutRow_IsRefused_AndRecordsNothing));
        var (store, _) = await CreateAsync();
        string cid = Id("char"), uid = Id("user"), grant = Id("grant");

        Assert.Equal(CharacterStoreError.CharacterNotFound,
            await ErrorOf(() => store.GrantItemAsync(grant, cid, "sword", 1, default)));

        // The ledger row rolled back with the failed insert: once the character exists the
        // same grant id applies for real instead of reading as an already-applied replay.
        await store.SaveCharacterAsync(NewState(cid, uid), default);
        Assert.True((await store.GrantItemAsync(grant, cid, "sword", 1, default)).Applied);
    }

    [SkippableFact]
    public async Task GrantId_ReusedForAnotherCharacterOrOperation_IsAConflict()
    {
        SkipUnlessAvailable(nameof(GrantId_ReusedForAnotherCharacterOrOperation_IsAConflict));
        var (store, cid, _) = await WithCharacterAsync();
        string other = Id("char");
        await store.SaveCharacterAsync(NewState(other, Id("user")), default);
        string grant = Id("grant");
        var g = await store.GrantItemAsync(grant, cid, "ore", 2, default);

        Assert.Equal(CharacterStoreError.GrantIdConflict,
            await ErrorOf(() => store.GrantItemAsync(grant, other, "ore", 2, default)));
        Assert.Equal(CharacterStoreError.GrantIdConflict,
            await ErrorOf(() => store.ConsumeItemAsync(grant, cid, g.InstanceId, 1, default)));
    }

    [SkippableFact]
    public async Task Equip_SwapsTheOccupantBackToTheBag()
    {
        SkipUnlessAvailable(nameof(Equip_SwapsTheOccupantBackToTheBag));
        var (store, cid, _) = await WithCharacterAsync();
        var a = await store.GrantItemAsync(Id("grant"), cid, "sword_a", 1, default);
        var b = await store.GrantItemAsync(Id("grant"), cid, "sword_b", 1, default);

        var equippedA = await store.EquipItemAsync(cid, a.InstanceId, "main_hand", default);
        Assert.Equal((ItemContainer.Equipped, "main_hand", (int?)null), (equippedA.Container, equippedA.Slot, equippedA.BagIndex));

        // Same slot again: no-op.
        Assert.Equal(equippedA, await store.EquipItemAsync(cid, a.InstanceId, "main_hand", default));

        var equippedB = await store.EquipItemAsync(cid, b.InstanceId, "main_hand", default);
        Assert.Equal(ItemContainer.Equipped, equippedB.Container);

        var items = await store.ListItemsAsync(cid, default);
        Assert.Equal(b.InstanceId, items[0].InstanceId); // equipped first
        var backInBag = items.Single(i => i.InstanceId == a.InstanceId);
        Assert.Equal((ItemContainer.Bag, (string?)null), (backInBag.Container, backInBag.Slot));
        Assert.Single(items, i => i.Container == ItemContainer.Equipped);
    }

    [SkippableFact]
    public async Task Unequip_AndMove_PlaceTheItemInTheBag()
    {
        SkipUnlessAvailable(nameof(Unequip_AndMove_PlaceTheItemInTheBag));
        var (store, cid, _) = await WithCharacterAsync();
        var bagItem = await store.GrantItemAsync(Id("grant"), cid, "ore", 5, default);   // bag index 0
        var gear = await store.GrantItemAsync(Id("grant"), cid, "helm", 1, default);      // bag index 1
        await store.EquipItemAsync(cid, gear.InstanceId, "head", default);

        var unequipped = await store.UnequipItemAsync(cid, gear.InstanceId, default);
        Assert.Equal((ItemContainer.Bag, (string?)null, (int?)1), (unequipped.Container, unequipped.Slot, unequipped.BagIndex));
        Assert.Equal(unequipped, await store.UnequipItemAsync(cid, gear.InstanceId, default)); // already in bag: no-op

        var moved = await store.MoveItemAsync(cid, bagItem.InstanceId, 7, default);
        Assert.Equal((ItemContainer.Bag, (int?)7), (moved.Container, moved.BagIndex));

        // Move also unequips.
        await store.EquipItemAsync(cid, gear.InstanceId, "head", default);
        var movedGear = await store.MoveItemAsync(cid, gear.InstanceId, 2, default);
        Assert.Equal((ItemContainer.Bag, (string?)null, (int?)2), (movedGear.Container, movedGear.Slot, movedGear.BagIndex));
    }

    [SkippableFact]
    public async Task Consume_PartialThenFull_IsIdempotentAndDeletesTheEmptyStack()
    {
        SkipUnlessAvailable(nameof(Consume_PartialThenFull_IsIdempotentAndDeletesTheEmptyStack));
        var (store, cid, _) = await WithCharacterAsync();
        var g = await store.GrantItemAsync(Id("grant"), cid, "potion", 3, default);
        string c1 = Id("consume"), c2 = Id("consume");

        Assert.Equal(new ItemConsumeResult(true, 2), await store.ConsumeItemAsync(c1, cid, g.InstanceId, 1, default));
        Assert.Equal(new ItemConsumeResult(false, 2), await store.ConsumeItemAsync(c1, cid, g.InstanceId, 1, default));

        Assert.Equal(CharacterStoreError.InsufficientQuantity,
            await ErrorOf(() => store.ConsumeItemAsync(c2, cid, g.InstanceId, 5, default)));
        Assert.Equal(2, Assert.Single(await store.ListItemsAsync(cid, default)).Quantity); // refused = unchanged

        // c2 was rolled back with its refusal, so it is still usable.
        Assert.Equal(new ItemConsumeResult(true, 0), await store.ConsumeItemAsync(c2, cid, g.InstanceId, 2, default));
        Assert.Empty(await store.ListItemsAsync(cid, default));
        Assert.Equal(new ItemConsumeResult(false, 0), await store.ConsumeItemAsync(c2, cid, g.InstanceId, 2, default));
    }

    [SkippableFact]
    public async Task ItemOperations_OnAnotherCharactersInstance_AreItemNotFound()
    {
        SkipUnlessAvailable(nameof(ItemOperations_OnAnotherCharactersInstance_AreItemNotFound));
        var (store, owner, _) = await WithCharacterAsync();
        string thief = Id("char");
        await store.SaveCharacterAsync(NewState(thief, Id("user")), default);
        var g = await store.GrantItemAsync(Id("grant"), owner, "gem", 1, default);

        var ops = new Func<Task>[]
        {
            () => store.EquipItemAsync(thief, g.InstanceId, "neck", default),
            () => store.UnequipItemAsync(thief, g.InstanceId, default),
            () => store.MoveItemAsync(thief, g.InstanceId, 0, default),
            () => store.ConsumeItemAsync(Id("consume"), thief, g.InstanceId, 1, default),
        };
        foreach (var op in ops)
            Assert.Equal(CharacterStoreError.ItemNotFound, await ErrorOf(op));

        Assert.Equal(owner, Assert.Single(await store.ListItemsAsync(owner, default)).CharacterId);
    }

    [SkippableFact]
    public async Task InvalidArguments_AreRefusedBeforeTouchingStorage()
    {
        SkipUnlessAvailable(nameof(InvalidArguments_AreRefusedBeforeTouchingStorage));
        var (store, cid, _) = await WithCharacterAsync();

        var ops = new Func<Task>[]
        {
            () => store.LoadCharacterAsync("", "u", default),
            () => store.LoadCharacterAsync("c", "", default),
            () => store.GrantItemAsync("", cid, "x", 1, default),
            () => store.GrantItemAsync(Id("g"), cid, "", 1, default),
            () => store.GrantItemAsync(Id("g"), cid, "x", 0, default),
            () => store.EquipItemAsync(cid, "i", "", default),
            () => store.MoveItemAsync(cid, "i", -1, default),
            () => store.ConsumeItemAsync(Id("g"), cid, "i", -2, default),
        };
        foreach (var op in ops)
            Assert.Equal(CharacterStoreError.InvalidArgument, await ErrorOf(op));
    }
}

/// <summary><see cref="CharacterStoreContract"/> against <see cref="MemoryCharacterStore"/>.</summary>
public sealed class MemoryCharacterStoreTests : CharacterStoreContract
{
    /// <inheritdoc />
    protected override void SkipUnlessAvailable(string testName) { }

    /// <inheritdoc />
    protected override Task<(ICharacterStore Store, IPlayerStore Players)> CreateAsync()
    {
        var players = new MemoryPlayerStore();
        return Task.FromResult<(ICharacterStore, IPlayerStore)>((new MemoryCharacterStore(players), players));
    }

    [Fact]
    public void ResolveCharacterId_DefaultsToUserIdWhenCidAbsent()
    {
        Assert.Equal("user-1", CharacterIds.Resolve("", "user-1"));
        Assert.Equal("user-1", CharacterIds.Resolve(null, "user-1"));
        Assert.Equal("char-1", CharacterIds.Resolve("char-1", "user-1"));
    }

    [Fact]
    public async Task WithoutLegacyStore_UnknownCharacterLoadsAsNull()
    {
        var store = new MemoryCharacterStore();
        Assert.Null(await store.LoadCharacterAsync("c", "u", default));
    }
}

/// <summary>
/// <see cref="CharacterStoreContract"/> against <see cref="PostgresCharacterStore"/> on the
/// shared ephemeral container, migrated through 002. Skips (really) without docker.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class PostgresCharacterStoreTests : CharacterStoreContract
{
    private readonly PostgresFixture _pg;
    private readonly List<PostgresPlayerStore> _opened = new();

    /// <summary>Bind the shared container.</summary>
    public PostgresCharacterStoreTests(PostgresFixture pg) => _pg = pg;

    /// <inheritdoc />
    protected override void SkipUnlessAvailable(string testName) => _pg.SkipUnlessAvailable(testName);

    /// <inheritdoc />
    protected override async Task<(ICharacterStore Store, IPlayerStore Players)> CreateAsync()
    {
        var players = await _pg.ConnectStoreAsync();
        _opened.Add(players);
        await players.MigrateAsync();
        return (PostgresCharacterStore.Over(players), players);
    }

    /// <inheritdoc />
    public override async Task DisposeAsync()
    {
        foreach (var s in _opened) await s.DisposeAsync();
    }

    [SkippableFact]
    public async Task Migration002_CreatesTheThreeTables_AndLeavesPlayerStatesAlone()
    {
        SkipUnlessAvailable(nameof(Migration002_CreatesTheThreeTables_AndLeavesPlayerStatesAlone));
        var (_, players) = await CreateAsync();
        var ds = ((PostgresPlayerStore)players).DataSource;

        foreach (var table in new[] { "character_state", "character_items", "item_grants", "player_states" })
        {
            await using var cmd = ds.CreateCommand(
                "SELECT count(*) FROM information_schema.tables WHERE table_name = @t");
            cmd.Parameters.AddWithValue("t", table);
            Assert.Equal(1L, (long)(await cmd.ExecuteScalarAsync())!);
        }

        await using var cols = ds.CreateCommand(
            "SELECT count(*) FROM information_schema.columns WHERE table_name = 'player_states'");
        Assert.Equal(7L, (long)(await cols.ExecuteScalarAsync())!); // exactly 001's columns
    }
}
