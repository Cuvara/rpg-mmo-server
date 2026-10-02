using System;
using System.Collections.Generic;

namespace Shared.GameLogic.Content
{
    /// <summary>
    /// An immutable, validated set of content definitions, plus the hash of the bytes it
    /// was built from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Built once at server boot and once per client after a fetch, then only read.
    /// Nothing mutates it at runtime: content changes take effect when a server restarts
    /// with new files and clients pull the new hash, never mid-session. That is a
    /// deliberate limit — a world whose rules changed underneath a running simulation
    /// would make every desync unreproducible.
    /// </para>
    /// <para>
    /// <see cref="Hash"/> identifies the content set exactly. The client sends the hash it
    /// already has; a server holding the same one answers "unchanged" instead of resending,
    /// which is what keeps the fetch off the join critical path after the first time.
    /// </para>
    /// </remarks>
    public sealed class ContentDatabase
    {
        private readonly Dictionary<string, ItemDefinition> _items;
        private readonly Dictionary<uint, AbilityDefinition> _abilities;
        private readonly Dictionary<uint, StatDefinition> _stats;
        private readonly Dictionary<uint, StatusDefinition> _statuses;

        /// <summary>
        /// Builds a database with items only. Kept because most call sites and every
        /// pre-ability content file have no abilities to pass, and an overload is cheaper
        /// than making each of them write <c>Array.Empty&lt;AbilityDefinition&gt;()</c>.
        /// </summary>
        public ContentDatabase(IEnumerable<ItemDefinition> items, string hash)
            : this(items, Array.Empty<AbilityDefinition>(), hash)
        {
        }

        /// <summary>
        /// Builds a database with items and abilities but no stats or statuses: every
        /// content set written before protocol 3. Kept for source compatibility.
        /// </summary>
        public ContentDatabase(
            IEnumerable<ItemDefinition> items,
            IEnumerable<AbilityDefinition> abilities,
            string hash)
            : this(items, abilities, Array.Empty<StatDefinition>(), Array.Empty<StatusDefinition>(), hash)
        {
        }

        /// <summary>
        /// Builds a database with every content type (protocol 3, ADR-30).
        /// </summary>
        /// <exception cref="ArgumentException">
        /// A definition is null, or two definitions of one type share an id. Duplicates are
        /// refused here rather than reported by <see cref="ContentValidation"/> because a
        /// dictionary cannot hold both, and the validator would be handed a database that had
        /// already lost one of the two definitions it was meant to complain about.
        /// </exception>
        public ContentDatabase(
            IEnumerable<ItemDefinition> items,
            IEnumerable<AbilityDefinition> abilities,
            IEnumerable<StatDefinition> stats,
            IEnumerable<StatusDefinition> statuses,
            string hash)
        {
            if (items == null) throw new ArgumentNullException(nameof(items));
            if (abilities == null) throw new ArgumentNullException(nameof(abilities));
            if (stats == null) throw new ArgumentNullException(nameof(stats));
            if (statuses == null) throw new ArgumentNullException(nameof(statuses));
            Hash = hash ?? throw new ArgumentNullException(nameof(hash));

            _items = new Dictionary<string, ItemDefinition>(StringComparer.Ordinal);
            foreach (var item in items)
            {
                if (item == null) throw new ArgumentException("Null item definition.", nameof(items));

                // Rejected here rather than in validation because a dictionary cannot
                // represent the conflict: the second write would silently win, and the
                // validator would then be handed a database that had already lost one of
                // the two definitions it was supposed to complain about.
                if (_items.ContainsKey(item.Id))
                {
                    throw new ArgumentException(
                        $"Duplicate item id '{item.Id}'. Ids must be unique across all content files — " +
                        "a saved inventory row is only a reference, so two items answering to one id " +
                        "means every stored copy is ambiguous.",
                        nameof(items));
                }

                _items.Add(item.Id, item);
            }

            _abilities = new Dictionary<uint, AbilityDefinition>();
            foreach (var ability in abilities)
            {
                if (ability == null) throw new ArgumentException("Null ability definition.", nameof(abilities));

                // Rejected here for the same reason a duplicate item id is: a dictionary
                // cannot represent the conflict, so the second write would silently win and
                // the validator would be handed a database that had already lost one of the
                // two definitions it was meant to complain about.
                if (_abilities.ContainsKey(ability.Id))
                {
                    throw new ArgumentException(
                        $"Duplicate ability id {ability.Id}. Ids must be unique across all content files — " +
                        "a hotbar slot is only a reference, so two abilities answering to one id " +
                        "means every stored copy is ambiguous.",
                        nameof(abilities));
                }

                _abilities.Add(ability.Id, ability);
            }

            _stats = new Dictionary<uint, StatDefinition>();
            foreach (var stat in stats)
            {
                if (stat == null) throw new ArgumentException("Null stat definition.", nameof(stats));

                if (_stats.ContainsKey(stat.Id))
                {
                    throw new ArgumentException(
                        $"Duplicate stat id {stat.Id}. Ids must be unique across all content files — " +
                        "a replicated stat value is only a reference, so two stats answering to one id " +
                        "means every client shows one of them under the other's name.",
                        nameof(stats));
                }

                _stats.Add(stat.Id, stat);
            }

            _statuses = new Dictionary<uint, StatusDefinition>();
            foreach (var status in statuses)
            {
                if (status == null) throw new ArgumentException("Null status definition.", nameof(statuses));

                if (_statuses.ContainsKey(status.Id))
                {
                    throw new ArgumentException(
                        $"Duplicate status id {status.Id}. Ids must be unique across all content files — " +
                        "an active status on the wire is only a reference, so two statuses answering to " +
                        "one id means a client draws one and the server applies the other.",
                        nameof(statuses));
                }

                _statuses.Add(status.Id, status);
            }
        }

        /// <summary>An empty database. Valid, and useful as a default before the first load.</summary>
        public static ContentDatabase Empty { get; } =
            new ContentDatabase(
                Array.Empty<ItemDefinition>(),
                Array.Empty<AbilityDefinition>(),
                Array.Empty<StatDefinition>(),
                Array.Empty<StatusDefinition>(),
                "empty");

        /// <summary>Hash of the canonical bytes this was built from.</summary>
        public string Hash { get; }

        public int ItemCount => _items.Count;

        /// <summary>Every item, in no guaranteed order.</summary>
        public IEnumerable<ItemDefinition> Items => _items.Values;

        /// <summary>
        /// Looks up an item by id. Returns false rather than throwing: a reference to
        /// content that no longer exists is a data problem to report, not a crash.
        /// </summary>
        public bool TryGetItem(string id, out ItemDefinition? item)
        {
            if (id == null)
            {
                item = null;
                return false;
            }

            return _items.TryGetValue(id, out item);
        }

        /// <summary>
        /// Looks up an item by id, throwing when it is absent. For call sites that have
        /// already validated the reference and would only be able to rethrow.
        /// </summary>
        public ItemDefinition GetItem(string id)
        {
            if (!TryGetItem(id, out var item) || item == null)
            {
                throw new KeyNotFoundException(
                    $"No item with id '{id}' in content set {Hash}.");
            }

            return item;
        }

        public int AbilityCount => _abilities.Count;

        /// <summary>Every ability, in no guaranteed order.</summary>
        public IEnumerable<AbilityDefinition> Abilities => _abilities.Values;

        /// <summary>
        /// Looks up an ability by id. Returns false rather than throwing: an input naming
        /// an ability this content set does not have is an ordinary thing for a client to
        /// send — a stale hotbar, a downgraded server — and the server refuses the input
        /// rather than faulting the tick.
        /// </summary>
        public bool TryGetAbility(uint id, out AbilityDefinition? ability)
        {
            if (id == 0)
            {
                // Zero is "no ability" on the wire, never a real id. Answering false here
                // rather than missing the dictionary keeps that meaning in one place.
                ability = null;
                return false;
            }

            return _abilities.TryGetValue(id, out ability);
        }

        /// <summary>
        /// Looks up an ability by id, throwing when it is absent. For call sites that have
        /// already validated the reference and would only be able to rethrow.
        /// </summary>
        public AbilityDefinition GetAbility(uint id)
        {
            if (!TryGetAbility(id, out var ability) || ability == null)
            {
                throw new KeyNotFoundException(
                    $"No ability with id {id} in content set {Hash}.");
            }

            return ability;
        }

        /// <summary>Number of stat definitions.</summary>
        public int StatCount => _stats.Count;

        /// <summary>Every stat, in no guaranteed order.</summary>
        public IEnumerable<StatDefinition> Stats => _stats.Values;

        /// <summary>
        /// Looks up a stat by id. Returns false for 0 ("no stat") and for unknown ids rather
        /// than throwing: a stale reference is a data problem to report, not a crash.
        /// </summary>
        public bool TryGetStat(uint id, out StatDefinition? stat)
        {
            if (id == 0)
            {
                stat = null;
                return false;
            }

            return _stats.TryGetValue(id, out stat);
        }

        /// <summary>Looks up a stat by id, throwing when it is absent.</summary>
        public StatDefinition GetStat(uint id)
        {
            if (!TryGetStat(id, out var stat) || stat == null)
            {
                throw new KeyNotFoundException($"No stat with id {id} in content set {Hash}.");
            }

            return stat;
        }

        /// <summary>Number of status definitions.</summary>
        public int StatusCount => _statuses.Count;

        /// <summary>Every status, in no guaranteed order.</summary>
        public IEnumerable<StatusDefinition> Statuses => _statuses.Values;

        /// <summary>
        /// Looks up a status by id. Returns false for 0 ("no status") and for unknown ids.
        /// Allocation-free; safe on the tick path.
        /// </summary>
        public bool TryGetStatus(uint id, out StatusDefinition? status)
        {
            if (id == 0)
            {
                status = null;
                return false;
            }

            return _statuses.TryGetValue(id, out status);
        }

        /// <summary>Looks up a status by id, throwing when it is absent.</summary>
        public StatusDefinition GetStatus(uint id)
        {
            if (!TryGetStatus(id, out var status) || status == null)
            {
                throw new KeyNotFoundException($"No status with id {id} in content set {Hash}.");
            }

            return status;
        }
    }
}
