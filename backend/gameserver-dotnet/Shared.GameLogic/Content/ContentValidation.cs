using System;
using System.Collections.Generic;
using System.Globalization;

namespace Shared.GameLogic.Content
{
    /// <summary>
    /// Rules every content set must satisfy, shared by the server and the client so both
    /// agree on what "valid" means.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The server runs these at boot and refuses to start on failure. The client runs the
    /// same rules on what it downloaded — not because it distrusts the server, but because
    /// a truncated or half-written response is indistinguishable from a valid one until
    /// something checks it, and the client would otherwise discover the problem as a null
    /// reference three screens later.
    /// </para>
    /// <para>
    /// <b>Validation is not authorization.</b> These rules answer "is this content
    /// coherent", never "may this player have this item". The client validating content
    /// grants it nothing: the server still owns every gameplay decision, and a client that
    /// edited its own copy changes only what it draws.
    /// </para>
    /// <para>
    /// Every failure names the offending id and what is wrong with it. A validator that
    /// reports "3 errors" and stops has moved the work to whoever reads the log.
    /// </para>
    /// </remarks>
    public static class ContentValidation
    {
        /// <summary>Longest permitted id. Long enough for readable ids, short enough to index.</summary>
        public const int MaxIdLength = 64;

        /// <summary>Longest permitted display name.</summary>
        public const int MaxNameLength = 128;

        /// <summary>
        /// Validates a database and appends a human-readable line per problem.
        /// Returns true when the database is usable.
        /// </summary>
        /// <remarks>
        /// Collects every error rather than throwing on the first. A content author fixing
        /// one typo per server restart is the failure mode this avoids.
        /// </remarks>
        public static bool Validate(ContentDatabase database, List<string> errors)
        {
            if (database == null) throw new ArgumentNullException(nameof(database));
            if (errors == null) throw new ArgumentNullException(nameof(errors));

            int before = errors.Count;

            foreach (var item in database.Items)
            {
                ValidateItem(item, errors);
            }

            return errors.Count == before;
        }

        private static void ValidateItem(ItemDefinition item, List<string> errors)
        {
            string id = item.Id;

            if (string.IsNullOrWhiteSpace(id))
            {
                errors.Add("item: id is empty. Every item needs a stable id — it is what " +
                           "inventories and loot tables store.");
                // Everything below reports against the id, so without one there is nothing
                // useful left to say about this entry.
                return;
            }

            if (id.Length > MaxIdLength)
            {
                errors.Add(Line(id, $"id is {id.Length} characters, limit is {MaxIdLength}."));
            }

            if (!IsValidId(id))
            {
                errors.Add(Line(id, "id may contain only lowercase letters, digits and underscores. " +
                                    "Ids appear in URLs, file names and log lines, and a mixed-case or " +
                                    "punctuated id compares unequal to itself across those surfaces."));
            }

            if (string.IsNullOrWhiteSpace(item.Name))
            {
                errors.Add(Line(id, "name is empty. It is what the player sees."));
            }
            else if (item.Name.Length > MaxNameLength)
            {
                errors.Add(Line(id, $"name is {item.Name.Length} characters, limit is {MaxNameLength}."));
            }

            if (!Enum.IsDefined(typeof(ItemSlot), item.Slot))
            {
                errors.Add(Line(id, $"slot '{(int)item.Slot}' is not a known slot."));
            }

            if (!Enum.IsDefined(typeof(ItemRarity), item.Rarity))
            {
                errors.Add(Line(id, $"rarity '{(int)item.Rarity}' is not a known rarity."));
            }

            if (item.StackMax < 1)
            {
                errors.Add(Line(id, $"stackMax is {item.StackMax}; it must be at least 1. " +
                                    "An item that cannot occupy one slot cannot exist."));
            }

            // Equipment stacking is the trap this catches. A stackable sword would let a
            // player hold several in one slot and the equip path has no answer for which
            // one is worn — so it is refused at content time rather than discovered by a
            // player holding two of something they can only wear one of.
            if (item.IsEquippable && item.StackMax != 1)
            {
                errors.Add(Line(id, $"is equippable ({item.Slot}) but stackMax is {item.StackMax}. " +
                                    "Equipment must not stack: there is no rule for which copy of a " +
                                    "stack is the one being worn."));
            }

            if (item.Attack < 0)
            {
                errors.Add(Line(id, $"attack is {item.Attack}; negative stats are not supported."));
            }

            if (item.Defense < 0)
            {
                errors.Add(Line(id, $"defense is {item.Defense}; negative stats are not supported."));
            }

            if (item.LevelRequirement < 0)
            {
                errors.Add(Line(id, $"levelRequirement is {item.LevelRequirement}; it cannot be negative."));
            }

            // Not an error: a quest item or a crafting reagent legitimately has no stats
            // and no slot. Only the combination of "wearable" and "does nothing" is
            // suspicious, and even that is a design choice rather than a data fault, so it
            // is left alone deliberately.
        }

        /// <summary>
        /// Ids are lowercase ASCII, digits and underscore. Hand-rolled rather than a regex:
        /// <c>System.Text.RegularExpressions</c> is reflection-adjacent under NativeAOT and
        /// this runs over every definition at boot.
        /// </summary>
        public static bool IsValidId(string id)
        {
            if (string.IsNullOrEmpty(id)) return false;

            for (int i = 0; i < id.Length; i++)
            {
                char c = id[i];
                bool ok = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_';
                if (!ok) return false;
            }

            return true;
        }

        private static string Line(string id, string problem) =>
            string.Format(CultureInfo.InvariantCulture, "item '{0}': {1}", id, problem);
    }
}
