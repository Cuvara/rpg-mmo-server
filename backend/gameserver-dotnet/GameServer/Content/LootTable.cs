using System;
using System.Collections.Generic;

namespace GameServer.Content;

/// <summary>
/// One possible drop of a <see cref="LootTable"/>: an item, the chance it drops, and how
/// many. Immutable.
/// </summary>
/// <remarks>
/// <b>Server-only content.</b> Loot is rolled on the server alone ("Loot: server-side roll
/// only", backend/CLAUDE.md), so unlike items, abilities, stats and statuses the loot tables
/// are not part of the document served at <c>/content</c> and do not live in
/// <c>Shared.GameLogic</c>: a client has no use for drop odds and every reason to want them.
/// </remarks>
public sealed class LootEntry
{
    /// <summary>Builds an entry.</summary>
    public LootEntry(string itemId, int chancePermille, int minQuantity, int maxQuantity)
    {
        ItemId = itemId ?? throw new ArgumentNullException(nameof(itemId));
        ChancePermille = chancePermille;
        MinQuantity = minQuantity;
        MaxQuantity = maxQuantity;
    }

    /// <summary>Item content id (<c>items.json</c>).</summary>
    public string ItemId { get; }

    /// <summary>Chance this entry drops, in permille (1..1000), rolled independently of the others.</summary>
    public int ChancePermille { get; }

    /// <summary>Smallest stack dropped, at least 1.</summary>
    public int MinQuantity { get; }

    /// <summary>Largest stack dropped, at least <see cref="MinQuantity"/> and at most the item's stackMax.</summary>
    public int MaxQuantity { get; }
}

/// <summary>
/// What an entity of one type drops when it dies. Immutable.
/// </summary>
public sealed class LootTable
{
    private readonly LootEntry[] _entries;

    /// <summary>Builds a table.</summary>
    public LootTable(string id, string entityType, int despawnTicks, IReadOnlyList<LootEntry> entries)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        EntityType = entityType ?? throw new ArgumentNullException(nameof(entityType));
        DespawnTicks = despawnTicks;
        if (entries == null) throw new ArgumentNullException(nameof(entries));
        _entries = new LootEntry[entries.Count];
        for (int i = 0; i < _entries.Length; i++) _entries[i] = entries[i];
    }

    /// <summary>Authoring id, unique across tables.</summary>
    public string Id { get; }

    /// <summary>
    /// Entity type this table applies to (<c>EntityState.Type</c>, e.g. <c>"mob"</c>). One
    /// table per type: the simulation has no finer mob identity yet.
    /// </summary>
    public string EntityType { get; }

    /// <summary>
    /// How long a dropped item lies in the world before it despawns, in BASE (critical)
    /// simulation ticks — the same timeline cooldowns and status durations count on.
    /// </summary>
    public int DespawnTicks { get; }

    /// <summary>Entries in authoring order; rolled in this order.</summary>
    public IReadOnlyList<LootEntry> Entries => _entries;

    /// <summary>Number of entries.</summary>
    public int EntryCount => _entries.Length;

    /// <summary>Entry at <paramref name="index"/>, without the interface.</summary>
    public LootEntry GetEntry(int index) => _entries[index];
}

/// <summary>
/// All loot tables of a content set, keyed by entity type. Immutable after load.
/// </summary>
public sealed class LootTables
{
    private readonly Dictionary<string, LootTable> _byType;
    private readonly LootTable[] _tables;

    /// <summary>Builds the set; duplicate entity types throw (the loader reports them first).</summary>
    public LootTables(IReadOnlyList<LootTable> tables)
    {
        if (tables == null) throw new ArgumentNullException(nameof(tables));
        _tables = new LootTable[tables.Count];
        _byType = new Dictionary<string, LootTable>(StringComparer.Ordinal);
        for (int i = 0; i < tables.Count; i++)
        {
            _tables[i] = tables[i];
            if (!_byType.TryAdd(tables[i].EntityType, tables[i]))
            {
                throw new ArgumentException(
                    $"Two loot tables for entity type '{tables[i].EntityType}'.", nameof(tables));
            }
        }
    }

    /// <summary>No loot at all.</summary>
    public static LootTables Empty { get; } = new(Array.Empty<LootTable>());

    /// <summary>Number of tables.</summary>
    public int Count => _tables.Length;

    /// <summary>Every table, in authoring order.</summary>
    public IReadOnlyList<LootTable> Tables => _tables;

    /// <summary>The table for an entity type, if any. Allocation-free.</summary>
    public bool TryGetForType(string? entityType, out LootTable? table)
    {
        if (entityType == null)
        {
            table = null;
            return false;
        }

        return _byType.TryGetValue(entityType, out table);
    }
}
