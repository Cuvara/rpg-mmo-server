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

        public ContentDatabase(IEnumerable<ItemDefinition> items, string hash)
        {
            if (items == null) throw new ArgumentNullException(nameof(items));
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
        }

        /// <summary>An empty database. Valid, and useful as a default before the first load.</summary>
        public static ContentDatabase Empty { get; } =
            new ContentDatabase(Array.Empty<ItemDefinition>(), "empty");

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
    }
}
