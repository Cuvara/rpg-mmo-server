using System.Diagnostics;
using System.Threading.Channels;
using Google.Protobuf;
using GameServer.Gameplay;
using GameServer.Net;
using GameServer.Observability;
using GameServer.Persistence;
using GameServer.World;
using Microsoft.Extensions.Logging;
using RpgMmo.Wire.V1;
using Shared.GameLogic.Content;
using Shared.GameLogic.Gameplay;
using Envelope = GameServer.Net.Envelope;

namespace GameServer.Commands;

/// <summary>
/// The gameplay command channel (ADR-30 decision 4): <c>MSG_TYPE_COMMAND</c> in, exactly one
/// <c>MSG_TYPE_COMMAND_RESULT</c> out per request, <c>MSG_TYPE_SERVER_PUSH</c> for unsolicited
/// changes. Payloads are <c>Shared.GameLogic</c>'s gameplay.proto messages.
/// </summary>
/// <remarks>
/// <para>
/// <b>Threads.</b> <see cref="OnRequest"/> runs on the connection's read loop and does only
/// cheap, non-blocking work: decode, version gate, rate limit, opcode check, enqueue. The
/// handler runs on that connection's worker task (thread pool), one request at a time and in
/// arrival order, so one character's inventory is never mutated by two requests at once.
/// Store I/O happens there and only there - never on the tick thread, which this class never
/// touches. World access is the world's own locked API (<see cref="EcsWorld.UpdateComponents"/>),
/// the same way the join path reaches it from a network thread. Replies are posted to the
/// connection's control lane (<see cref="Connection.SendControl"/>), which the write task drains
/// ahead of snapshots and never drops from.
/// </para>
/// <para>
/// <b>Character binding.</b> Every handler acts on the character the connection joined as
/// (<see cref="CharacterSessions"/>, ADR-31), never on an id the client names.
/// </para>
/// </remarks>
public sealed class CommandRouter
{
    /// <summary>Metrics reason for a request from a peer below protocol 3 (answered <c>unknown_opcode</c>).</summary>
    public const string ReasonProtocolVersion = "protocol_version";

    private readonly EcsWorld _world;
    private readonly ICharacterStore? _store;
    private readonly CharacterSessions _sessions;
    private readonly ContentDatabase? _content;
    private readonly GameMetrics? _metrics;
    private readonly ILogger _logger;
    private readonly Func<string, Task<bool>>? _saveCharacter;
    private readonly Func<long> _clock;
    private readonly string _grantPrefix;

    /// <summary>Builds the router.</summary>
    /// <param name="world">The world (item pick-up and its rollback).</param>
    /// <param name="store">Character store; null answers every inventory command <c>unavailable</c>.</param>
    /// <param name="sessions">Which character each user plays.</param>
    /// <param name="content">Item definitions (equip slot validation).</param>
    /// <param name="serverId">This server's id, the first part of every grant id.</param>
    /// <param name="bootNonce">
    /// Distinguishes this process from an earlier one with the same server id: item entity ids
    /// and ticks restart with the process, so without it a restarted server could mint a grant id
    /// that an earlier run already used, and the store would answer the new pick-up as a replay.
    /// </param>
    /// <param name="metrics">Counters, optional.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="saveCharacter">
    /// Saves a user's character row (the save sweep's single-player path). Used once when a
    /// brand-new character, which has no row until its first save, receives its first item.
    /// </param>
    /// <param name="clock">Monotonic timestamp source (<see cref="Stopwatch.GetTimestamp"/>); tests inject one.</param>
    public CommandRouter(
        EcsWorld world, ICharacterStore? store, CharacterSessions sessions, ContentDatabase? content,
        string serverId, string bootNonce, GameMetrics? metrics, ILogger logger,
        Func<string, Task<bool>>? saveCharacter = null, Func<long>? clock = null)
    {
        _world = world;
        _store = store;
        _sessions = sessions;
        _content = content;
        _metrics = metrics;
        _logger = logger;
        _saveCharacter = saveCharacter;
        _clock = clock ?? Stopwatch.GetTimestamp;
        _grantPrefix = $"{serverId}:{bootNonce}";
    }

    /// <summary>
    /// The deterministic grant id of picking up item entity <paramref name="itemEntityId"/>
    /// that was due to despawn at <paramref name="despawnTick"/>.
    /// </summary>
    /// <remarks>
    /// Entity ids are recycled (<c>item-N</c>, after a quarantine), so the id alone does not
    /// name one drop; the despawn tick (spawn tick + the table's lifetime) does, within a
    /// process. The same drop always yields the same id, which is what makes a retried grant a
    /// replay instead of a duplicate.
    /// </remarks>
    public string PickupGrantId(string itemEntityId, ulong despawnTick) =>
        $"{_grantPrefix}:{itemEntityId}:{despawnTick}";

    /// <summary>Grant id of consuming one item for request <paramref name="seq"/> of session <paramref name="jti"/>.</summary>
    public string UseGrantId(string jti, uint seq) => $"{_grantPrefix}:use:{jti}:{seq}";

    /// <summary>
    /// Handle one <c>MSG_TYPE_COMMAND</c> envelope. Read loop only; never blocks. Every path
    /// ends in exactly one <c>CommandResult</c>, now or from the worker.
    /// </summary>
    public void OnRequest(Connection conn, Envelope env)
    {
        _metrics?.RecordCommandReceived();

        CommandRequest request;
        try
        {
            request = WireProtocol.GetPayload<CommandRequest>(env);
        }
        catch (Exception ex)
        {
            // No seq to echo: answer seq 0, which no real request uses (seq starts at 1).
            _logger.LogDebug(ex, "Undecodable CommandRequest from {UserId}", conn.UserId);
            Reject(conn, 0, GameplayErrors.InvalidPayload);
            return;
        }

        // ADR-30 decision 5: the channel exists only for peers that advertised protocol 3.
        // A version 2 peer does not know it, so every opcode is unknown to it; the request is
        // answered (never silently dropped) and never executed.
        if (conn.PeerProtocolVersion < 3)
        {
            Reject(conn, request.Seq, GameplayErrors.UnknownOpcode, ReasonProtocolVersion);
            return;
        }

        if (!conn.Commands.TryAdmit(_clock()))
        {
            Reject(conn, request.Seq, GameplayErrors.RateLimited);
            return;
        }

        if (!GameplayOpcodes.IsKnownRequest(request.Opcode))
        {
            Reject(conn, request.Seq, GameplayErrors.UnknownOpcode);
            return;
        }

        if (!conn.Commands.TryEnqueue(request, reader => RunWorkerAsync(conn, reader)))
        {
            Reject(conn, request.Seq, GameplayErrors.RateLimited);
        }
    }

    private async Task RunWorkerAsync(Connection conn, ChannelReader<CommandRequest> reader)
    {
        try
        {
            while (await reader.WaitToReadAsync(conn.Closing))
            {
                while (reader.TryRead(out CommandRequest? request))
                {
                    if (conn.Closing.IsCancellationRequested) return;
                    try
                    {
                        await ExecuteAsync(conn, request);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Command opcode {Opcode} seq {Seq} from {UserId} failed",
                            request.Opcode, request.Seq, conn.UserId);
                        Reject(conn, request.Seq, GameplayErrors.Unavailable);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Connection closed: nothing left to answer to.
        }
    }

    /// <summary>Run one admitted request. Exposed for tests; production goes through <see cref="OnRequest"/>.</summary>
    internal async Task ExecuteAsync(Connection conn, CommandRequest request)
    {
        ReadOnlySpan<byte> payload = request.Payload.Span;
        switch (request.Opcode)
        {
            case GameplayOpcodes.Inventory:
                if (!InventoryRequest.TryRead(payload, out _))
                {
                    Reject(conn, request.Seq, GameplayErrors.InvalidPayload);
                    return;
                }
                await InventoryAsync(conn, request.Seq);
                return;

            case GameplayOpcodes.Pickup:
                if (!PickupRequest.TryRead(payload, out PickupRequest? pickup) || pickup.EntityId.Length == 0)
                {
                    Reject(conn, request.Seq, GameplayErrors.InvalidPayload);
                    return;
                }
                await PickupAsync(conn, request.Seq, pickup.EntityId);
                return;

            case GameplayOpcodes.Equip:
                if (!EquipRequest.TryRead(payload, out EquipRequest? equip) || equip.InstanceId.Length == 0)
                {
                    Reject(conn, request.Seq, GameplayErrors.InvalidPayload);
                    return;
                }
                await EquipAsync(conn, request.Seq, equip.InstanceId, equip.Slot);
                return;

            case GameplayOpcodes.Unequip:
                if (!UnequipRequest.TryRead(payload, out UnequipRequest? unequip) || unequip.Slot.Length == 0)
                {
                    Reject(conn, request.Seq, GameplayErrors.InvalidPayload);
                    return;
                }
                await UnequipAsync(conn, request.Seq, unequip.Slot);
                return;

            case GameplayOpcodes.UseItem:
                if (!UseItemRequest.TryRead(payload, out UseItemRequest? use) || use.InstanceId.Length == 0)
                {
                    Reject(conn, request.Seq, GameplayErrors.InvalidPayload);
                    return;
                }
                await UseAsync(conn, request.Seq, use.InstanceId);
                return;

            default:
                Reject(conn, request.Seq, GameplayErrors.UnknownOpcode);
                return;
        }
    }

    // ───────────────────────────── handlers ─────────────────────────────

    private async Task InventoryAsync(Connection conn, uint seq)
    {
        if (!TryCharacter(conn, out string characterId, out ICharacterStore store))
        {
            Reject(conn, seq, GameplayErrors.Unavailable);
            return;
        }

        IReadOnlyList<CharacterItem> items = await store.ListItemsAsync(characterId, conn.Closing);
        Accept(conn, seq, ToView(items).ToByteArray());
    }

    private sealed class TakeState
    {
        public required string ItemEntityId;
        public required string TakerId;
        public ItemTakeResult Result;
        public Shared.GameLogic.Components.Vec2 Position;
    }

    private async Task PickupAsync(Connection conn, uint seq, string itemEntityId)
    {
        // Resolve the character BEFORE taking anything: an item must never leave the world for
        // a connection that has nowhere to put it.
        if (!TryCharacter(conn, out string characterId, out ICharacterStore store))
        {
            Reject(conn, seq, GameplayErrors.Unavailable);
            return;
        }

        // Take it out of the world first. TryTakeItemEntity removes the entity in the same
        // locked step that checks it, so a second request (this player's or anyone's) for the
        // same entity finds nothing: an item cannot be taken twice.
        var take = new TakeState { ItemEntityId = itemEntityId, TakerId = conn.UserId };
        _world.UpdateComponents(take, static (s, w) =>
        {
            EntityHandle h = w.Resolve(s.ItemEntityId);
            if (h.IsValid) s.Position = w.PositionOf(in h).Value;
            s.Result = w.TryTakeItemEntity(s.ItemEntityId, s.TakerId, CommandLimits.PickupRange);
        });

        switch (take.Result.Status)
        {
            case ItemTakeStatus.Taken:
                break;
            case ItemTakeStatus.OutOfRange:
                Reject(conn, seq, GameplayErrors.OutOfRange);
                return;
            case ItemTakeStatus.TakerDead:
                Reject(conn, seq, GameplayErrors.Unavailable);
                return;
            default:
                Reject(conn, seq, GameplayErrors.NotFound);
                return;
        }

        string itemId = take.Result.ItemId!;
        int quantity = take.Result.Quantity;
        string grantId = PickupGrantId(itemEntityId, take.Result.DespawnTick);

        GrantOutcome outcome = await GrantAsync(conn, store, grantId, characterId, itemId, quantity);
        if (outcome == GrantOutcome.Refused)
        {
            // The store refused and wrote nothing (every item operation is one transaction), so
            // putting the item back cannot duplicate it.
            RestoreItem(itemId, quantity, take.Position, take.Result.DespawnTick);
            Reject(conn, seq, GameplayErrors.Unavailable);
            return;
        }

        if (outcome == GrantOutcome.Unknown)
        {
            // The grant may or may not have committed. Not restoring is the side that cannot
            // duplicate an item; the grant id names it for reconciliation.
            _logger.LogError(
                "Pick-up grant {GrantId} ({ItemId} x{Quantity}) for character {CharacterId} has an UNKNOWN outcome " +
                "after {Attempts} attempts; the world item was not restored. Reconcile against item_grants.",
                grantId, itemId, quantity, characterId, CommandLimits.GrantAttempts);
            Reject(conn, seq, GameplayErrors.Unavailable);
            return;
        }

        InventoryView view = ToView(await store.ListItemsAsync(characterId, conn.Closing));
        byte[] viewBytes = view.ToByteArray();
        Push(conn, viewBytes);
        Accept(conn, seq, viewBytes);
    }

    private enum GrantOutcome
    {
        Applied,
        Refused,
        Unknown,
    }

    private async Task<GrantOutcome> GrantAsync(
        Connection conn, ICharacterStore store, string grantId, string characterId, string itemId, int quantity)
    {
        bool savedRow = false;
        for (int attempt = 1; attempt <= CommandLimits.GrantAttempts; attempt++)
        {
            try
            {
                // Not the connection's token: a client that disconnects mid-grant must not
                // abort a write whose item has already left the world.
                await store.GrantItemAsync(grantId, characterId, itemId, quantity, CancellationToken.None);
                return GrantOutcome.Applied;
            }
            catch (CharacterStoreException ex) when (ex.Error == CharacterStoreError.CharacterNotFound
                                                     && !savedRow && _saveCharacter != null)
            {
                // A brand-new character has no character_state row until its first save, and
                // item rows need one (FK). Save it now, once, and retry.
                savedRow = true;
                if (!await _saveCharacter(conn.UserId)) return GrantOutcome.Refused;
                attempt--;
            }
            catch (CharacterStoreException ex)
            {
                _logger.LogWarning("Pick-up grant {GrantId} refused by the store: {Error} {Message}",
                    grantId, ex.Error, ex.Message);
                return GrantOutcome.Refused;
            }
            catch (Exception ex) when (attempt < CommandLimits.GrantAttempts)
            {
                _logger.LogWarning(ex, "Pick-up grant {GrantId} attempt {Attempt} failed; retrying with the same grant id",
                    grantId, attempt);
                await Task.Delay(100 * attempt * attempt);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Pick-up grant {GrantId} failed on the last attempt", grantId);
            }
        }

        return GrantOutcome.Unknown;
    }

    private void RestoreItem(string itemId, int quantity, Shared.GameLogic.Components.Vec2 position, ulong despawnTick)
    {
        try
        {
            _world.UpdateComponents((itemId, quantity, position, despawnTick), static (s, w) =>
                CombatResolver.SpawnItem(w, s.itemId, s.quantity, s.position, s.despawnTick));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not put refused pick-up {ItemId} x{Quantity} back into the world", itemId, quantity);
        }
    }

    private async Task EquipAsync(Connection conn, uint seq, string instanceId, string slot)
    {
        if (!TryCharacter(conn, out string characterId, out ICharacterStore store))
        {
            Reject(conn, seq, GameplayErrors.Unavailable);
            return;
        }

        if (!TryParseSlot(slot, out ItemSlot wanted))
        {
            Reject(conn, seq, GameplayErrors.SlotMismatch);
            return;
        }

        IReadOnlyList<CharacterItem> items = await store.ListItemsAsync(characterId, conn.Closing);
        CharacterItem? item = Find(items, instanceId);
        if (item == null)
        {
            Reject(conn, seq, GameplayErrors.NotFound);
            return;
        }

        // The slot is content's to decide (ADR-19): an item content does not define, or defines
        // for another slot, cannot be worn there.
        if (_content == null || !_content.TryGetItem(item.ItemId, out ItemDefinition? def) || def == null
            || def.Slot != wanted)
        {
            Reject(conn, seq, GameplayErrors.SlotMismatch);
            return;
        }

        if (!await MutateAsync(conn, seq, () => store.EquipItemAsync(characterId, instanceId, SlotName(wanted), CancellationToken.None)))
            return;

        await PushAndAcceptAsync(conn, seq, store, characterId);
    }

    private async Task UnequipAsync(Connection conn, uint seq, string slot)
    {
        if (!TryCharacter(conn, out string characterId, out ICharacterStore store))
        {
            Reject(conn, seq, GameplayErrors.Unavailable);
            return;
        }

        IReadOnlyList<CharacterItem> items = await store.ListItemsAsync(characterId, conn.Closing);
        CharacterItem? worn = null;
        for (int i = 0; i < items.Count; i++)
        {
            if (items[i].Container == ItemContainer.Equipped && string.Equals(items[i].Slot, slot, StringComparison.Ordinal))
            {
                worn = items[i];
                break;
            }
        }

        if (worn == null)
        {
            Reject(conn, seq, GameplayErrors.NotFound);
            return;
        }

        if (!await MutateAsync(conn, seq, () => store.UnequipItemAsync(characterId, worn.InstanceId, CancellationToken.None)))
            return;

        await PushAndAcceptAsync(conn, seq, store, characterId);
    }

    private async Task UseAsync(Connection conn, uint seq, string instanceId)
    {
        if (!TryCharacter(conn, out string characterId, out ICharacterStore store))
        {
            Reject(conn, seq, GameplayErrors.Unavailable);
            return;
        }

        IReadOnlyList<CharacterItem> items = await store.ListItemsAsync(characterId, conn.Closing);
        CharacterItem? item = Find(items, instanceId);
        if (item == null || item.Container != ItemContainer.Bag)
        {
            Reject(conn, seq, GameplayErrors.NotFound);
            return;
        }

        // Consume exactly one. The EFFECT of using an item is a placeholder no-op: content
        // (ItemDefinition) defines no use effect yet, so using an item only spends it. When
        // content grows one, it is applied here, after the consume committed.
        string grantId = UseGrantId(conn.JoinJti, seq);
        if (!await MutateAsync(conn, seq, () => store.ConsumeItemAsync(grantId, characterId, instanceId, 1, CancellationToken.None)))
            return;

        await PushAndAcceptAsync(conn, seq, store, characterId);
    }

    /// <summary>Run one store mutation, answering the refusal itself when there is one.</summary>
    private async Task<bool> MutateAsync<T>(Connection conn, uint seq, Func<Task<T>> mutation)
    {
        try
        {
            await mutation();
            return true;
        }
        catch (CharacterStoreException ex)
        {
            Reject(conn, seq, ex.Error switch
            {
                CharacterStoreError.ItemNotFound => GameplayErrors.NotFound,
                CharacterStoreError.InsufficientQuantity => GameplayErrors.NotFound,
                CharacterStoreError.OwnershipMismatch => GameplayErrors.NotOwner,
                CharacterStoreError.InvalidArgument => GameplayErrors.InvalidPayload,
                CharacterStoreError.GrantIdConflict => GameplayErrors.InvalidPayload,
                _ => GameplayErrors.Unavailable,
            });
            return false;
        }
    }

    /// <summary>
    /// After a successful mutation: the InventoryChanged push FIRST, then the empty result, both
    /// on the ordered control lane - so a client that refreshes when its command completes
    /// already holds the new inventory.
    /// </summary>
    private async Task PushAndAcceptAsync(Connection conn, uint seq, ICharacterStore store, string characterId)
    {
        try
        {
            Push(conn, ToView(await store.ListItemsAsync(characterId, conn.Closing)).ToByteArray());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The mutation committed; failing to describe it afterwards must not turn it into a
            // reported failure. The client can ask for the inventory (opcode 1).
            _logger.LogWarning(ex, "Inventory push for {CharacterId} failed after a committed change", characterId);
        }

        Accept(conn, seq, Array.Empty<byte>());
    }

    // ───────────────────────────── helpers ─────────────────────────────

    private bool TryCharacter(Connection conn, out string characterId, out ICharacterStore store)
    {
        characterId = "";
        store = null!;
        if (_store == null || !_sessions.TryGet(conn.UserId, out CharacterBinding binding)) return false;
        characterId = binding.CharacterId;
        store = _store;
        return true;
    }

    private static CharacterItem? Find(IReadOnlyList<CharacterItem> items, string instanceId)
    {
        for (int i = 0; i < items.Count; i++)
        {
            if (string.Equals(items[i].InstanceId, instanceId, StringComparison.Ordinal)) return items[i];
        }
        return null;
    }

    /// <summary>The wire slot names of gameplay.proto (lowercase <see cref="ItemSlot"/>).</summary>
    public static bool TryParseSlot(string name, out ItemSlot slot)
    {
        switch (name)
        {
            case "weapon": slot = ItemSlot.Weapon; return true;
            case "head": slot = ItemSlot.Head; return true;
            case "chest": slot = ItemSlot.Chest; return true;
            case "legs": slot = ItemSlot.Legs; return true;
            case "trinket": slot = ItemSlot.Trinket; return true;
            default: slot = ItemSlot.None; return false;
        }
    }

    /// <summary>Wire / store name of a slot.</summary>
    public static string SlotName(ItemSlot slot) => slot switch
    {
        ItemSlot.Weapon => "weapon",
        ItemSlot.Head => "head",
        ItemSlot.Chest => "chest",
        ItemSlot.Legs => "legs",
        ItemSlot.Trinket => "trinket",
        _ => "",
    };

    /// <summary>The complete inventory as gameplay.proto's <see cref="InventoryView"/>.</summary>
    public static InventoryView ToView(IReadOnlyList<CharacterItem> items)
    {
        var view = new InventoryView();
        for (int i = 0; i < items.Count; i++)
        {
            CharacterItem it = items[i];
            bool equipped = it.Container == ItemContainer.Equipped;
            view.Items.Add(new ItemStack
            {
                InstanceId = it.InstanceId,
                ItemId = it.ItemId,
                Quantity = (uint)Math.Max(0, it.Quantity),
                Container = equipped ? ItemContainers.Equipped : ItemContainers.Bag,
                Slot = equipped ? it.Slot ?? "" : "",
                BagIndex = equipped ? 0u : (uint)Math.Max(0, it.BagIndex ?? 0),
            });
        }
        return view;
    }

    private void Accept(Connection conn, uint seq, byte[] payload)
    {
        _metrics?.RecordCommandAccepted();
        conn.SendControl(WireProtocol.NewEnvelope(MsgType.CommandResult,
            new CommandResult { Seq = seq, Ok = true, Payload = UnsafeByteOperations.UnsafeWrap(payload) },
            conn.Encoding));
    }

    private void Reject(Connection conn, uint seq, string error, string? metricReason = null)
    {
        _metrics?.RecordCommandRejected(metricReason ?? error);
        conn.SendControl(WireProtocol.NewEnvelope(MsgType.CommandResult,
            new CommandResult { Seq = seq, Ok = false, Error = error },
            conn.Encoding));
    }

    private static void Push(Connection conn, byte[] viewBytes)
    {
        // InventoryChanged { view = 1 }: the view's bytes as a length-delimited field 1.
        InventoryView.TryRead(viewBytes, out InventoryView? view);
        var changed = new InventoryChanged { View = view ?? new InventoryView() };
        conn.SendControl(WireProtocol.NewEnvelope(MsgType.ServerPush,
            new ServerPush
            {
                Opcode = GameplayOpcodes.InventoryChanged,
                Payload = UnsafeByteOperations.UnsafeWrap(changed.ToByteArray()),
            },
            conn.Encoding));
    }
}
