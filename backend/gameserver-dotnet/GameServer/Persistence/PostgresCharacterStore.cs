using System.Data;
using Npgsql;
using NpgsqlTypes;

namespace GameServer.Persistence;

/// <summary>
/// PostgreSQL-backed <see cref="ICharacterStore"/> over the tables migration
/// <c>002_characters</c> creates (ADR-31).
///
/// <para>Every item operation is ONE transaction (ADR-6 grant-time persistence): the
/// <c>item_grants</c> ledger row and the <c>character_items</c> change commit together or
/// not at all, so a crash can neither lose a grant nor apply it twice. Idempotency comes
/// from <c>item_grants.grant_id</c> being the primary key: a replayed grant id hits
/// <c>ON CONFLICT DO NOTHING</c> (blocking until a concurrent first attempt commits) and
/// becomes a no-op.</para>
///
/// <para>Same NativeAOT posture as <see cref="PostgresPlayerStore"/>: raw commands, typed
/// parameters, positional readers, no ORM or reflection.</para>
/// </summary>
public sealed class PostgresCharacterStore : ICharacterStore
{
    private const string ItemColumns =
        "instance_id, character_id, item_id, quantity, container, slot, bag_index, granted_by";

    /// <summary>Next free bag index of a character (max + 1, or 0).</summary>
    private const string NextBagIndexSql =
        "(SELECT COALESCE(MAX(bag_index), -1) + 1 FROM character_items WHERE character_id = @character_id AND container = 'bag')";

    private const string SelectStateSql = """
        SELECT character_id, user_id, map_id, x, y, z, yaw, hp, max_hp, level, xp
        FROM character_state WHERE character_id = @character_id
        """;

    private const string SelectLegacySql = """
        SELECT map_id, x, y, hp, max_hp FROM player_states WHERE user_id = @user_id
        """;

    private const string InsertFromLegacySql = """
        INSERT INTO character_state (character_id, user_id, map_id, x, y, z, yaw, hp, max_hp, level, xp, updated_at)
        VALUES (@character_id, @user_id, @map_id, @x, @y, 0, 0, @hp, @max_hp, 1, 0, now())
        ON CONFLICT (character_id) DO NOTHING
        """;

    private const string UpsertStateSql = """
        INSERT INTO character_state (character_id, user_id, map_id, x, y, z, yaw, hp, max_hp, level, xp, updated_at)
        VALUES (@character_id, @user_id, @map_id, @x, @y, @z, @yaw, @hp, @max_hp, @level, @xp, now())
        ON CONFLICT (character_id) DO UPDATE SET
            map_id     = EXCLUDED.map_id,
            x          = EXCLUDED.x,
            y          = EXCLUDED.y,
            z          = EXCLUDED.z,
            yaw        = EXCLUDED.yaw,
            hp         = EXCLUDED.hp,
            max_hp     = EXCLUDED.max_hp,
            level      = EXCLUDED.level,
            xp         = EXCLUDED.xp,
            updated_at = now()
        WHERE character_state.user_id = EXCLUDED.user_id
        """;

    private readonly NpgsqlDataSource _dataSource;
    private readonly int _commandTimeoutSeconds;

    /// <summary>
    /// Build a store over an existing pool. The caller keeps ownership of
    /// <paramref name="dataSource"/> (this type is not disposable).
    /// </summary>
    /// <param name="dataSource">Pool connected to the game-state database, migrated to 002 or later.</param>
    /// <param name="commandTimeoutSeconds">Per-command timeout.</param>
    public PostgresCharacterStore(NpgsqlDataSource dataSource,
        int commandTimeoutSeconds = PostgresPlayerStore.DefaultCommandTimeoutSeconds)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        _dataSource = dataSource;
        _commandTimeoutSeconds = commandTimeoutSeconds;
    }

    /// <summary>
    /// Build a store sharing <paramref name="players"/>' pool and timeout — how the server
    /// wires it, so one <c>GAME_DB_URL</c> gives one pool. Disposing the player store
    /// disposes the pool under this one too.
    /// </summary>
    /// <param name="players">The connected (and migrated) player store.</param>
    public static PostgresCharacterStore Over(PostgresPlayerStore players)
    {
        ArgumentNullException.ThrowIfNull(players);
        return new PostgresCharacterStore(players.DataSource, players.CommandTimeoutSeconds);
    }

    /// <inheritdoc />
    public async Task<CharacterState?> LoadCharacterAsync(string characterId, string userId, CancellationToken ct)
    {
        MemoryCharacterStore.Require(characterId, nameof(characterId));
        MemoryCharacterStore.Require(userId, nameof(userId));

        return await InTransactionAsync($"load character {characterId}", async (conn, tx) =>
        {
            var existing = await ReadStateAsync(conn, tx, characterId, ct);
            if (existing is not null) return CheckOwner(existing, userId);

            // ADR-31.2 first-load fallback: copy the account's legacy row.
            float x, y; int hp, maxHp; string mapId;
            await using (var cmd = Command(conn, tx, SelectLegacySql))
            {
                cmd.Parameters.Add(Text("user_id", userId));
                await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SingleRow, ct);
                if (!await reader.ReadAsync(ct)) return null;
                mapId = reader.GetString(0);
                x = reader.GetFloat(1);
                y = reader.GetFloat(2);
                hp = reader.GetInt32(3);
                maxHp = reader.GetInt32(4);
            }

            int inserted;
            await using (var cmd = Command(conn, tx, InsertFromLegacySql))
            {
                cmd.Parameters.Add(Text("character_id", characterId));
                cmd.Parameters.Add(Text("user_id", userId));
                cmd.Parameters.Add(Text("map_id", mapId));
                cmd.Parameters.Add(Real("x", x));
                cmd.Parameters.Add(Real("y", y));
                cmd.Parameters.Add(Int("hp", hp));
                cmd.Parameters.Add(Int("max_hp", maxHp));
                inserted = await cmd.ExecuteNonQueryAsync(ct);
            }
            if (inserted == 0)
            {
                // A concurrent load materialised it between our read and insert.
                var raced = await ReadStateAsync(conn, tx, characterId, ct);
                return raced is null ? null : CheckOwner(raced, userId);
            }

            return new CharacterState(characterId, userId, mapId, x, y, 0f, 0f, hp, maxHp, 1, 0)
            {
                FromLegacyPlayerState = true,
            };
        }, ct);
    }

    /// <inheritdoc />
    public async Task SaveCharacterAsync(CharacterState state, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(state);
        MemoryCharacterStore.Require(state.CharacterId, nameof(state));
        MemoryCharacterStore.Require(state.UserId, nameof(state));

        await using var cmd = _dataSource.CreateCommand(UpsertStateSql);
        cmd.CommandTimeout = _commandTimeoutSeconds;
        cmd.Parameters.Add(Text("character_id", state.CharacterId));
        cmd.Parameters.Add(Text("user_id", state.UserId));
        cmd.Parameters.Add(Text("map_id", state.MapId ?? ""));
        cmd.Parameters.Add(Real("x", state.X));
        cmd.Parameters.Add(Real("y", state.Y));
        cmd.Parameters.Add(Real("z", state.Z));
        cmd.Parameters.Add(Real("yaw", state.Yaw));
        cmd.Parameters.Add(Int("hp", state.Hp));
        cmd.Parameters.Add(Int("max_hp", state.MaxHp));
        cmd.Parameters.Add(Int("level", state.Level));
        cmd.Parameters.Add(new NpgsqlParameter("xp", NpgsqlDbType.Bigint) { Value = state.Xp });

        int affected;
        try
        {
            affected = await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"pgstore save character {state.CharacterId}: {ex.Message}", ex);
        }
        // The DO UPDATE's WHERE filtered the row out: it belongs to another account.
        if (affected == 0)
            throw new CharacterStoreException(CharacterStoreError.OwnershipMismatch,
                $"character {state.CharacterId} does not belong to user {state.UserId}");
    }

    /// <inheritdoc />
    public async Task<ItemGrantResult> GrantItemAsync(string grantId, string characterId, string itemId, int quantity, CancellationToken ct)
    {
        MemoryCharacterStore.Require(grantId, nameof(grantId));
        MemoryCharacterStore.Require(characterId, nameof(characterId));
        MemoryCharacterStore.Require(itemId, nameof(itemId));
        MemoryCharacterStore.RequirePositive(quantity, nameof(quantity));

        return await InTransactionAsync($"grant {grantId}", async (conn, tx) =>
        {
            string instanceId = Guid.NewGuid().ToString();
            if (!await ClaimGrantAsync(conn, tx, grantId, characterId, "grant", instanceId, ct))
            {
                var (gChar, gKind, gInstance) = await ReadGrantAsync(conn, tx, grantId, ct);
                if (gChar != characterId || gKind != "grant") throw GrantConflict(grantId);
                return new ItemGrantResult(false, gInstance ?? "");
            }

            await using var cmd = Command(conn, tx, $"""
                INSERT INTO character_items ({ItemColumns}, updated_at)
                VALUES (@instance_id, @character_id, @item_id, @quantity, 'bag', NULL, {NextBagIndexSql}, @grant_id, now())
                """);
            cmd.Parameters.Add(Text("instance_id", instanceId));
            cmd.Parameters.Add(Text("character_id", characterId));
            cmd.Parameters.Add(Text("item_id", itemId));
            cmd.Parameters.Add(Int("quantity", quantity));
            cmd.Parameters.Add(Text("grant_id", grantId));
            await cmd.ExecuteNonQueryAsync(ct);
            return new ItemGrantResult(true, instanceId);
        }, ct);
    }

    /// <inheritdoc />
    public async Task<CharacterItem> EquipItemAsync(string characterId, string instanceId, string slot, CancellationToken ct)
    {
        MemoryCharacterStore.Require(characterId, nameof(characterId));
        MemoryCharacterStore.Require(instanceId, nameof(instanceId));
        MemoryCharacterStore.Require(slot, nameof(slot));

        return await InTransactionAsync($"equip {instanceId}", async (conn, tx) =>
        {
            var item = await LockItemAsync(conn, tx, characterId, instanceId, ct);
            if (item.Container == ItemContainer.Equipped && item.Slot == slot) return item;

            // Swap: whatever occupies the slot goes to the end of the bag first, so the
            // partial unique index never sees two items in one slot.
            await using (var cmd = Command(conn, tx, $"""
                UPDATE character_items
                SET container = 'bag', slot = NULL, bag_index = {NextBagIndexSql}, updated_at = now()
                WHERE character_id = @character_id AND container = 'equipped' AND slot = @slot
                  AND instance_id <> @instance_id
                """))
            {
                cmd.Parameters.Add(Text("character_id", characterId));
                cmd.Parameters.Add(Text("slot", slot));
                cmd.Parameters.Add(Text("instance_id", instanceId));
                await cmd.ExecuteNonQueryAsync(ct);
            }

            await using var upd = Command(conn, tx, $"""
                UPDATE character_items
                SET container = 'equipped', slot = @slot, bag_index = NULL, updated_at = now()
                WHERE instance_id = @instance_id
                RETURNING {ItemColumns}
                """);
            upd.Parameters.Add(Text("slot", slot));
            upd.Parameters.Add(Text("instance_id", instanceId));
            return await ReadSingleItemAsync(upd, ct);
        }, ct);
    }

    /// <inheritdoc />
    public async Task<CharacterItem> UnequipItemAsync(string characterId, string instanceId, CancellationToken ct)
    {
        MemoryCharacterStore.Require(characterId, nameof(characterId));
        MemoryCharacterStore.Require(instanceId, nameof(instanceId));

        return await InTransactionAsync($"unequip {instanceId}", async (conn, tx) =>
        {
            var item = await LockItemAsync(conn, tx, characterId, instanceId, ct);
            if (item.Container == ItemContainer.Bag) return item;

            await using var upd = Command(conn, tx, $"""
                UPDATE character_items
                SET container = 'bag', slot = NULL, bag_index = {NextBagIndexSql}, updated_at = now()
                WHERE instance_id = @instance_id
                RETURNING {ItemColumns}
                """);
            upd.Parameters.Add(Text("character_id", characterId));
            upd.Parameters.Add(Text("instance_id", instanceId));
            return await ReadSingleItemAsync(upd, ct);
        }, ct);
    }

    /// <inheritdoc />
    public async Task<CharacterItem> MoveItemAsync(string characterId, string instanceId, int bagIndex, CancellationToken ct)
    {
        MemoryCharacterStore.Require(characterId, nameof(characterId));
        MemoryCharacterStore.Require(instanceId, nameof(instanceId));
        if (bagIndex < 0)
            throw new CharacterStoreException(CharacterStoreError.InvalidArgument, "bag index must be >= 0");

        return await InTransactionAsync($"move {instanceId}", async (conn, tx) =>
        {
            await LockItemAsync(conn, tx, characterId, instanceId, ct);
            await using var upd = Command(conn, tx, $"""
                UPDATE character_items
                SET container = 'bag', slot = NULL, bag_index = @bag_index, updated_at = now()
                WHERE instance_id = @instance_id
                RETURNING {ItemColumns}
                """);
            upd.Parameters.Add(Int("bag_index", bagIndex));
            upd.Parameters.Add(Text("instance_id", instanceId));
            return await ReadSingleItemAsync(upd, ct);
        }, ct);
    }

    /// <inheritdoc />
    public async Task<ItemConsumeResult> ConsumeItemAsync(string grantId, string characterId, string instanceId, int quantity, CancellationToken ct)
    {
        MemoryCharacterStore.Require(grantId, nameof(grantId));
        MemoryCharacterStore.Require(characterId, nameof(characterId));
        MemoryCharacterStore.Require(instanceId, nameof(instanceId));
        MemoryCharacterStore.RequirePositive(quantity, nameof(quantity));

        return await InTransactionAsync($"consume {grantId}", async (conn, tx) =>
        {
            if (!await ClaimGrantAsync(conn, tx, grantId, characterId, "consume", instanceId, ct))
            {
                var (gChar, gKind, gInstance) = await ReadGrantAsync(conn, tx, grantId, ct);
                if (gChar != characterId || gKind != "consume" || gInstance != instanceId) throw GrantConflict(grantId);
                await using var q = Command(conn, tx,
                    "SELECT quantity FROM character_items WHERE instance_id = @instance_id AND character_id = @character_id");
                q.Parameters.Add(Text("instance_id", instanceId));
                q.Parameters.Add(Text("character_id", characterId));
                object? current = await q.ExecuteScalarAsync(ct);
                return new ItemConsumeResult(false, current is int n ? n : 0);
            }

            // A refusal below throws, which rolls back the ledger row claimed above too.
            var item = await LockItemAsync(conn, tx, characterId, instanceId, ct);
            if (item.Quantity < quantity)
                throw new CharacterStoreException(CharacterStoreError.InsufficientQuantity,
                    $"instance {instanceId} holds {item.Quantity}, asked to consume {quantity}");

            int left = item.Quantity - quantity;
            await using var cmd = left == 0
                ? Command(conn, tx, "DELETE FROM character_items WHERE instance_id = @instance_id")
                : Command(conn, tx, "UPDATE character_items SET quantity = @quantity, updated_at = now() WHERE instance_id = @instance_id");
            cmd.Parameters.Add(Text("instance_id", instanceId));
            if (left > 0) cmd.Parameters.Add(Int("quantity", left));
            await cmd.ExecuteNonQueryAsync(ct);
            return new ItemConsumeResult(true, left);
        }, ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<CharacterItem>> ListItemsAsync(string characterId, CancellationToken ct)
    {
        MemoryCharacterStore.Require(characterId, nameof(characterId));

        await using var cmd = _dataSource.CreateCommand($"""
            SELECT {ItemColumns} FROM character_items
            WHERE character_id = @character_id
            ORDER BY (container = 'equipped') DESC, slot, bag_index, instance_id
            """);
        cmd.CommandTimeout = _commandTimeoutSeconds;
        cmd.Parameters.Add(Text("character_id", characterId));
        try
        {
            var list = new List<CharacterItem>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) list.Add(ReadItem(reader));
            return list;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"pgstore list items {characterId}: {ex.Message}", ex);
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Run <paramref name="body"/> in one READ COMMITTED transaction: commit on return,
    /// roll back on any exception. A <see cref="CharacterStoreException"/> passes through;
    /// a foreign-key violation (no <c>character_state</c> row) becomes
    /// <see cref="CharacterStoreError.CharacterNotFound"/>; anything else is wrapped.
    /// </summary>
    private async Task<T> InTransactionAsync<T>(string what,
        Func<NpgsqlConnection, NpgsqlTransaction, Task<T>> body, CancellationToken ct)
    {
        try
        {
            await using var conn = await _dataSource.OpenConnectionAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
            T result = await body(conn, tx);
            await tx.CommitAsync(ct);
            return result;
        }
        catch (CharacterStoreException)
        {
            throw;
        }
        catch (PostgresException pg) when (pg.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            throw new CharacterStoreException(CharacterStoreError.CharacterNotFound,
                $"pgstore {what}: character has no character_state row", pg);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException($"pgstore {what}: {ex.Message}", ex);
        }
    }

    private NpgsqlCommand Command(NpgsqlConnection conn, NpgsqlTransaction tx, string sql)
        => new(sql, conn, tx) { CommandTimeout = _commandTimeoutSeconds };

    /// <summary>Insert the ledger row; false when the grant id already exists.</summary>
    private async Task<bool> ClaimGrantAsync(NpgsqlConnection conn, NpgsqlTransaction tx,
        string grantId, string characterId, string kind, string instanceId, CancellationToken ct)
    {
        await using var cmd = Command(conn, tx, """
            INSERT INTO item_grants (grant_id, character_id, kind, instance_id, applied_at)
            VALUES (@grant_id, @character_id, @kind, @instance_id, now())
            ON CONFLICT (grant_id) DO NOTHING
            """);
        cmd.Parameters.Add(Text("grant_id", grantId));
        cmd.Parameters.Add(Text("character_id", characterId));
        cmd.Parameters.Add(Text("kind", kind));
        cmd.Parameters.Add(Text("instance_id", instanceId));
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    private async Task<(string CharacterId, string Kind, string? InstanceId)> ReadGrantAsync(
        NpgsqlConnection conn, NpgsqlTransaction tx, string grantId, CancellationToken ct)
    {
        await using var cmd = Command(conn, tx,
            "SELECT character_id, kind, instance_id FROM item_grants WHERE grant_id = @grant_id");
        cmd.Parameters.Add(Text("grant_id", grantId));
        await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SingleRow, ct);
        if (!await reader.ReadAsync(ct))
            throw new InvalidOperationException($"grant {grantId} conflicted but cannot be read back");
        return (reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2));
    }

    /// <summary>Row-lock an item of this character, or refuse with ItemNotFound.</summary>
    private async Task<CharacterItem> LockItemAsync(NpgsqlConnection conn, NpgsqlTransaction tx,
        string characterId, string instanceId, CancellationToken ct)
    {
        await using var cmd = Command(conn, tx, $"""
            SELECT {ItemColumns} FROM character_items
            WHERE instance_id = @instance_id AND character_id = @character_id
            FOR UPDATE
            """);
        cmd.Parameters.Add(Text("instance_id", instanceId));
        cmd.Parameters.Add(Text("character_id", characterId));
        await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SingleRow, ct);
        if (!await reader.ReadAsync(ct))
            throw new CharacterStoreException(CharacterStoreError.ItemNotFound,
                $"no item {instanceId} on character {characterId}");
        return ReadItem(reader);
    }

    private async Task<CharacterState?> ReadStateAsync(NpgsqlConnection conn, NpgsqlTransaction tx,
        string characterId, CancellationToken ct)
    {
        await using var cmd = Command(conn, tx, SelectStateSql);
        cmd.Parameters.Add(Text("character_id", characterId));
        await using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.SingleRow, ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new CharacterState(
            CharacterId: reader.GetString(0),
            UserId: reader.GetString(1),
            MapId: reader.GetString(2),
            X: reader.GetFloat(3),
            Y: reader.GetFloat(4),
            Z: reader.GetFloat(5),
            Yaw: reader.GetFloat(6),
            Hp: reader.GetInt32(7),
            MaxHp: reader.GetInt32(8),
            Level: reader.GetInt32(9),
            Xp: reader.GetInt64(10));
    }

    private static async Task<CharacterItem> ReadSingleItemAsync(NpgsqlCommand cmd, CancellationToken ct)
    {
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            throw new InvalidOperationException("item row vanished inside its own transaction");
        return ReadItem(reader);
    }

    private static CharacterItem ReadItem(NpgsqlDataReader r) => new(
        InstanceId: r.GetString(0),
        CharacterId: r.GetString(1),
        ItemId: r.GetString(2),
        Quantity: r.GetInt32(3),
        Container: r.GetString(4) == "equipped" ? ItemContainer.Equipped : ItemContainer.Bag,
        Slot: r.IsDBNull(5) ? null : r.GetString(5),
        BagIndex: r.IsDBNull(6) ? null : r.GetInt32(6),
        GrantedBy: r.IsDBNull(7) ? null : r.GetString(7));

    private static CharacterState CheckOwner(CharacterState state, string userId)
    {
        if (state.UserId != userId)
            throw new CharacterStoreException(CharacterStoreError.OwnershipMismatch,
                $"character {state.CharacterId} does not belong to user {userId}");
        return state;
    }

    private static CharacterStoreException GrantConflict(string grantId)
        => new(CharacterStoreError.GrantIdConflict,
            $"grant id {grantId} was already applied to a different character or operation");

    private static NpgsqlParameter Text(string name, string value)
        => new(name, NpgsqlDbType.Text) { Value = value };

    private static NpgsqlParameter Real(string name, float value)
        => new(name, NpgsqlDbType.Real) { Value = value };

    private static NpgsqlParameter Int(string name, int value)
        => new(name, NpgsqlDbType.Integer) { Value = value };
}
