namespace GameServer.Persistence;

/// <summary>
/// Persistent state of one character (ADR-31.2): the <c>character_state</c> row.
/// Position, HP, level and XP are written by the periodic save sweep
/// (<see cref="ICharacterStore.SaveCharacterAsync"/>), so the ADR-6 loss window of at
/// most 30 s applies to them. Items are NOT here: they are written through at grant time.
/// </summary>
/// <param name="CharacterId">Roster character id (the join token's <c>cid</c> claim).</param>
/// <param name="UserId">Owning account.</param>
/// <param name="MapId">Map the character was last saved on ("" = unattributable).</param>
/// <param name="X">Ground-plane X.</param>
/// <param name="Y">Ground-plane Y.</param>
/// <param name="Z">Height (ADR-28).</param>
/// <param name="Yaw">Facing, radians.</param>
/// <param name="Hp">Current HP.</param>
/// <param name="MaxHp">Maximum HP.</param>
/// <param name="Level">Character level (column default 1).</param>
/// <param name="Xp">Experience points (column default 0).</param>
public sealed record CharacterState(
    string CharacterId,
    string UserId,
    string MapId,
    float X,
    float Y,
    float Z,
    float Yaw,
    int Hp,
    int MaxHp,
    int Level,
    long Xp)
{
    /// <summary>
    /// True when this state was built from the account's legacy <c>player_states</c> row
    /// because the character had no <c>character_state</c> row yet (ADR-31.2, first load).
    /// The store has already copied it into <c>character_state</c> by the time it returns.
    /// </summary>
    public bool FromLegacyPlayerState { get; init; }
}

/// <summary>Where an item instance lives.</summary>
public enum ItemContainer
{
    /// <summary>In the bag; <see cref="CharacterItem.BagIndex"/> orders it, no slot.</summary>
    Bag,

    /// <summary>Equipped in <see cref="CharacterItem.Slot"/>; at most one item per slot.</summary>
    Equipped,
}

/// <summary>One item instance owned by a character (a <c>character_items</c> row).</summary>
/// <param name="InstanceId">Unique instance id.</param>
/// <param name="CharacterId">Owning character.</param>
/// <param name="ItemId">Content item id (meaning is content's, not the store's).</param>
/// <param name="Quantity">Stack size, always &gt; 0 (an emptied stack is deleted).</param>
/// <param name="Container">Bag or equipped.</param>
/// <param name="Slot">Equipment slot name when equipped, otherwise null.</param>
/// <param name="BagIndex">Bag position when in the bag, otherwise null.</param>
/// <param name="GrantedBy">Grant id that created the instance, if any.</param>
public sealed record CharacterItem(
    string InstanceId,
    string CharacterId,
    string ItemId,
    int Quantity,
    ItemContainer Container,
    string? Slot,
    int? BagIndex,
    string? GrantedBy);

/// <summary>Outcome of <see cref="ICharacterStore.GrantItemAsync"/>.</summary>
/// <param name="Applied">False when the grant id had already been applied (a replay; nothing changed).</param>
/// <param name="InstanceId">The instance the grant created (on a replay: the one the original created).</param>
public sealed record ItemGrantResult(bool Applied, string InstanceId);

/// <summary>Outcome of <see cref="ICharacterStore.ConsumeItemAsync"/>.</summary>
/// <param name="Applied">False when the grant id had already been applied (a replay; nothing changed).</param>
/// <param name="RemainingQuantity">
/// Quantity left in the instance after this call (0 = the stack was deleted). On a replay,
/// the instance's current quantity (0 if it no longer exists).
/// </param>
public sealed record ItemConsumeResult(bool Applied, int RemainingQuantity);

/// <summary>Why an item or character operation was refused.</summary>
public enum CharacterStoreError
{
    /// <summary>An argument was empty or out of range (empty id, quantity &lt;= 0, ...).</summary>
    InvalidArgument,

    /// <summary>The character has no <c>character_state</c> row; save or load it first.</summary>
    CharacterNotFound,

    /// <summary>The character row belongs to a different account than the caller named.</summary>
    OwnershipMismatch,

    /// <summary>No such item instance on this character.</summary>
    ItemNotFound,

    /// <summary>A consume asked for more than the instance holds.</summary>
    InsufficientQuantity,

    /// <summary>The grant id was already applied for a different character or operation.</summary>
    GrantIdConflict,
}

/// <summary>
/// A refused character-store operation. Nothing was written: every item operation is one
/// transaction, so a refusal leaves the character exactly as it was.
/// </summary>
public sealed class CharacterStoreException : Exception
{
    /// <summary>Build a refusal.</summary>
    /// <param name="error">Machine-readable reason.</param>
    /// <param name="message">Human-readable detail.</param>
    /// <param name="inner">Underlying exception, if any.</param>
    public CharacterStoreException(CharacterStoreError error, string message, Exception? inner = null)
        : base(message, inner) => Error = error;

    /// <summary>Machine-readable reason.</summary>
    public CharacterStoreError Error { get; }
}

/// <summary>Helpers for character ids.</summary>
public static class CharacterIds
{
    /// <summary>
    /// The character id a connection plays: the join token's <c>cid</c> claim, or — when
    /// the claim is absent (protocol 2 clients, "the account's default character", ADR-31.1)
    /// — the user id itself. Using the user id keeps the default character a stable,
    /// collision-free key without a roster entry: roster ids are UUIDs, user ids are
    /// Nakama account ids, and both are owned by the same account.
    /// </summary>
    /// <param name="cid">The <c>cid</c> claim, possibly empty.</param>
    /// <param name="userId">The authenticated account.</param>
    public static string Resolve(string? cid, string userId)
        => string.IsNullOrEmpty(cid) ? userId : cid;
}

/// <summary>
/// Game-state persistence of characters (ADR-31): state saved by the sweep, and bag /
/// equipment written through at grant time, one transaction per operation, idempotent by
/// grant id where an operation could be replayed.
/// </summary>
/// <remarks>
/// Single writer (ADR-1, ADR-31.3): only the game server hosting the character calls the
/// mutating methods. Implementations are thread-safe, but the store does not arbitrate
/// between two servers writing the same character.
/// </remarks>
public interface ICharacterStore
{
    /// <summary>
    /// Load a character. When it has no <c>character_state</c> row, fall back to the
    /// account's legacy <c>player_states</c> row (ADR-31.2): its map/position/HP are copied
    /// into a new <c>character_state</c> row (level 1, XP 0, z 0, yaw 0) in the same
    /// transaction, and the result has <see cref="CharacterState.FromLegacyPlayerState"/>
    /// set. Returns null when neither row exists (a brand-new character).
    /// </summary>
    /// <param name="characterId">Character to load.</param>
    /// <param name="userId">Account the caller believes owns it.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="CharacterStoreException">
    /// <see cref="CharacterStoreError.OwnershipMismatch"/> when the row belongs to another account.
    /// </exception>
    Task<CharacterState?> LoadCharacterAsync(string characterId, string userId, CancellationToken ct);

    /// <summary>
    /// Upsert the sweep-saved fields (map, position, yaw, HP, level, XP). Creates the row
    /// when absent, which is also how a brand-new character becomes eligible for items.
    /// </summary>
    /// <param name="state">State to write.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="CharacterStoreException">
    /// <see cref="CharacterStoreError.OwnershipMismatch"/> when the row belongs to another account.
    /// </exception>
    Task SaveCharacterAsync(CharacterState state, CancellationToken ct);

    /// <summary>
    /// Grant <paramref name="quantity"/> of <paramref name="itemId"/> into the bag as a new
    /// instance, in one transaction with its <c>item_grants</c> row. A grant id that was
    /// already applied is a no-op returning <c>Applied = false</c> and the original instance id.
    /// </summary>
    /// <param name="grantId">Idempotency key, unique per grant.</param>
    /// <param name="characterId">Receiving character (must have a <c>character_state</c> row).</param>
    /// <param name="itemId">Content item id.</param>
    /// <param name="quantity">Stack size, &gt; 0.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ItemGrantResult> GrantItemAsync(string grantId, string characterId, string itemId, int quantity, CancellationToken ct);

    /// <summary>
    /// Equip an instance into <paramref name="slot"/>. An item already in that slot goes back
    /// to the end of the bag in the same transaction (a swap). Equipping an item into the
    /// slot it already occupies is a no-op. Slot names are content's and are not validated
    /// beyond being non-empty.
    /// </summary>
    /// <param name="characterId">Owning character.</param>
    /// <param name="instanceId">Instance to equip.</param>
    /// <param name="slot">Equipment slot name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The instance as stored after the operation.</returns>
    Task<CharacterItem> EquipItemAsync(string characterId, string instanceId, string slot, CancellationToken ct);

    /// <summary>Move an equipped instance to the end of the bag. Already in the bag: no-op.</summary>
    /// <param name="characterId">Owning character.</param>
    /// <param name="instanceId">Instance to unequip.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The instance as stored after the operation.</returns>
    Task<CharacterItem> UnequipItemAsync(string characterId, string instanceId, CancellationToken ct);

    /// <summary>
    /// Move an instance to bag position <paramref name="bagIndex"/> (unequipping it if
    /// equipped). Bag positions are not unique and have no capacity limit here: bag rules
    /// are content's.
    /// </summary>
    /// <param name="characterId">Owning character.</param>
    /// <param name="instanceId">Instance to move.</param>
    /// <param name="bagIndex">Target bag position, &gt;= 0.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The instance as stored after the operation.</returns>
    Task<CharacterItem> MoveItemAsync(string characterId, string instanceId, int bagIndex, CancellationToken ct);

    /// <summary>
    /// Consume <paramref name="quantity"/> from an instance in one transaction with its
    /// <c>item_grants</c> row; an emptied stack is deleted. A grant id that was already
    /// applied is a no-op returning <c>Applied = false</c>.
    /// </summary>
    /// <param name="grantId">Idempotency key, unique per consume.</param>
    /// <param name="characterId">Owning character.</param>
    /// <param name="instanceId">Instance to consume from.</param>
    /// <param name="quantity">Amount, &gt; 0 and &lt;= the instance's quantity.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ItemConsumeResult> ConsumeItemAsync(string grantId, string characterId, string instanceId, int quantity, CancellationToken ct);

    /// <summary>All item instances of a character: equipped first (by slot), then bag (by index).</summary>
    /// <param name="characterId">Owning character.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<CharacterItem>> ListItemsAsync(string characterId, CancellationToken ct);
}

/// <summary>
/// In-memory <see cref="ICharacterStore"/> for development and tests, with the same
/// semantics as <see cref="PostgresCharacterStore"/>: one lock stands in for one
/// transaction, so every operation is all-or-nothing.
/// </summary>
public sealed class MemoryCharacterStore : ICharacterStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, CharacterState> _states = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CharacterItem> _items = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string CharacterId, string Kind, string? InstanceId)> _grants = new(StringComparer.Ordinal);
    private readonly IPlayerStore? _legacy;

    /// <summary>Build an empty store.</summary>
    /// <param name="legacyPlayers">
    /// Where the first-load fallback reads <c>player_states</c> rows from; null disables
    /// the fallback (every unknown character loads as null).
    /// </param>
    public MemoryCharacterStore(IPlayerStore? legacyPlayers = null) => _legacy = legacyPlayers;

    /// <inheritdoc />
    public async Task<CharacterState?> LoadCharacterAsync(string characterId, string userId, CancellationToken ct)
    {
        Require(characterId, nameof(characterId));
        Require(userId, nameof(userId));

        lock (_gate)
        {
            if (_states.TryGetValue(characterId, out var existing))
                return CheckOwner(existing, userId);
        }

        if (_legacy is null) return null;
        var legacy = await _legacy.LoadPlayerAsync(userId, ct);
        if (legacy is null) return null;

        var copied = new CharacterState(characterId, userId, legacy.MapId ?? "", legacy.X, legacy.Y, 0f, 0f,
            legacy.Hp, legacy.MaxHp, Level: 1, Xp: 0);
        lock (_gate)
        {
            // A concurrent load may have materialised it first; the stored row wins, as
            // INSERT ... ON CONFLICT DO NOTHING does in Postgres.
            if (_states.TryGetValue(characterId, out var raced))
                return CheckOwner(raced, userId);
            _states[characterId] = copied;
        }
        return copied with { FromLegacyPlayerState = true };
    }

    /// <inheritdoc />
    public Task SaveCharacterAsync(CharacterState state, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(state);
        Require(state.CharacterId, nameof(state));
        Require(state.UserId, nameof(state));
        lock (_gate)
        {
            if (_states.TryGetValue(state.CharacterId, out var existing))
                CheckOwner(existing, state.UserId);
            _states[state.CharacterId] = state with { FromLegacyPlayerState = false };
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<ItemGrantResult> GrantItemAsync(string grantId, string characterId, string itemId, int quantity, CancellationToken ct)
    {
        Require(grantId, nameof(grantId));
        Require(characterId, nameof(characterId));
        Require(itemId, nameof(itemId));
        RequirePositive(quantity, nameof(quantity));
        lock (_gate)
        {
            if (_grants.TryGetValue(grantId, out var g))
            {
                if (g.CharacterId != characterId || g.Kind != "grant")
                    throw GrantConflict(grantId);
                return Task.FromResult(new ItemGrantResult(false, g.InstanceId ?? ""));
            }
            RequireCharacter(characterId);
            string instanceId = Guid.NewGuid().ToString();
            _items[instanceId] = new CharacterItem(instanceId, characterId, itemId, quantity,
                ItemContainer.Bag, null, NextBagIndex(characterId), grantId);
            _grants[grantId] = (characterId, "grant", instanceId);
            return Task.FromResult(new ItemGrantResult(true, instanceId));
        }
    }

    /// <inheritdoc />
    public Task<CharacterItem> EquipItemAsync(string characterId, string instanceId, string slot, CancellationToken ct)
    {
        Require(characterId, nameof(characterId));
        Require(instanceId, nameof(instanceId));
        Require(slot, nameof(slot));
        lock (_gate)
        {
            var item = RequireItem(characterId, instanceId);
            if (item.Container == ItemContainer.Equipped && item.Slot == slot)
                return Task.FromResult(item);

            foreach (var other in _items.Values.Where(i => i.CharacterId == characterId
                         && i.Container == ItemContainer.Equipped && i.Slot == slot && i.InstanceId != instanceId).ToList())
            {
                _items[other.InstanceId] = other with
                {
                    Container = ItemContainer.Bag, Slot = null, BagIndex = NextBagIndex(characterId),
                };
            }
            var equipped = item with { Container = ItemContainer.Equipped, Slot = slot, BagIndex = null };
            _items[instanceId] = equipped;
            return Task.FromResult(equipped);
        }
    }

    /// <inheritdoc />
    public Task<CharacterItem> UnequipItemAsync(string characterId, string instanceId, CancellationToken ct)
    {
        Require(characterId, nameof(characterId));
        Require(instanceId, nameof(instanceId));
        lock (_gate)
        {
            var item = RequireItem(characterId, instanceId);
            if (item.Container == ItemContainer.Bag) return Task.FromResult(item);
            var moved = item with { Container = ItemContainer.Bag, Slot = null, BagIndex = NextBagIndex(characterId) };
            _items[instanceId] = moved;
            return Task.FromResult(moved);
        }
    }

    /// <inheritdoc />
    public Task<CharacterItem> MoveItemAsync(string characterId, string instanceId, int bagIndex, CancellationToken ct)
    {
        Require(characterId, nameof(characterId));
        Require(instanceId, nameof(instanceId));
        if (bagIndex < 0)
            throw new CharacterStoreException(CharacterStoreError.InvalidArgument, "bag index must be >= 0");
        lock (_gate)
        {
            var item = RequireItem(characterId, instanceId);
            var moved = item with { Container = ItemContainer.Bag, Slot = null, BagIndex = bagIndex };
            _items[instanceId] = moved;
            return Task.FromResult(moved);
        }
    }

    /// <inheritdoc />
    public Task<ItemConsumeResult> ConsumeItemAsync(string grantId, string characterId, string instanceId, int quantity, CancellationToken ct)
    {
        Require(grantId, nameof(grantId));
        Require(characterId, nameof(characterId));
        Require(instanceId, nameof(instanceId));
        RequirePositive(quantity, nameof(quantity));
        lock (_gate)
        {
            if (_grants.TryGetValue(grantId, out var g))
            {
                if (g.CharacterId != characterId || g.Kind != "consume" || g.InstanceId != instanceId)
                    throw GrantConflict(grantId);
                int current = _items.TryGetValue(instanceId, out var cur) ? cur.Quantity : 0;
                return Task.FromResult(new ItemConsumeResult(false, current));
            }
            var item = RequireItem(characterId, instanceId);
            if (item.Quantity < quantity)
                throw new CharacterStoreException(CharacterStoreError.InsufficientQuantity,
                    $"instance {instanceId} holds {item.Quantity}, asked to consume {quantity}");
            int left = item.Quantity - quantity;
            if (left == 0) _items.Remove(instanceId);
            else _items[instanceId] = item with { Quantity = left };
            _grants[grantId] = (characterId, "consume", instanceId);
            return Task.FromResult(new ItemConsumeResult(true, left));
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<CharacterItem>> ListItemsAsync(string characterId, CancellationToken ct)
    {
        Require(characterId, nameof(characterId));
        lock (_gate)
        {
            IReadOnlyList<CharacterItem> list = _items.Values
                .Where(i => i.CharacterId == characterId)
                .OrderBy(i => i.Container == ItemContainer.Equipped ? 0 : 1)
                .ThenBy(i => i.Slot, StringComparer.Ordinal)
                .ThenBy(i => i.BagIndex)
                .ThenBy(i => i.InstanceId, StringComparer.Ordinal)
                .ToList();
            return Task.FromResult(list);
        }
    }

    private static CharacterState CheckOwner(CharacterState state, string userId)
    {
        if (state.UserId != userId)
            throw new CharacterStoreException(CharacterStoreError.OwnershipMismatch,
                $"character {state.CharacterId} does not belong to user {userId}");
        return state;
    }

    private void RequireCharacter(string characterId)
    {
        if (!_states.ContainsKey(characterId))
            throw new CharacterStoreException(CharacterStoreError.CharacterNotFound,
                $"character {characterId} has no character_state row");
    }

    private CharacterItem RequireItem(string characterId, string instanceId)
    {
        if (!_items.TryGetValue(instanceId, out var item) || item.CharacterId != characterId)
            throw new CharacterStoreException(CharacterStoreError.ItemNotFound,
                $"no item {instanceId} on character {characterId}");
        return item;
    }

    private int NextBagIndex(string characterId)
    {
        int max = -1;
        foreach (var i in _items.Values)
            if (i.CharacterId == characterId && i.Container == ItemContainer.Bag && i.BagIndex is int b && b > max)
                max = b;
        return max + 1;
    }

    private static CharacterStoreException GrantConflict(string grantId)
        => new(CharacterStoreError.GrantIdConflict,
            $"grant id {grantId} was already applied to a different character or operation");

    internal static void Require(string? value, string name)
    {
        if (string.IsNullOrEmpty(value))
            throw new CharacterStoreException(CharacterStoreError.InvalidArgument, $"{name} is empty");
    }

    internal static void RequirePositive(int value, string name)
    {
        if (value <= 0)
            throw new CharacterStoreException(CharacterStoreError.InvalidArgument, $"{name} must be > 0");
    }
}
