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

            foreach (var ability in database.Abilities)
            {
                ValidateAbility(ability, database, errors);
            }

            ValidateStats(database, errors);
            ValidateStatuses(database, errors);

            return errors.Count == before;
        }

        /// <summary>Largest permitted <see cref="StatusDefinition.MaxStacks"/>.</summary>
        /// <remarks>
        /// Not a balance number: a structural bound so a per-stack product (periodic amount
        /// times stacks, modifier times stacks) cannot approach the range of an int.
        /// </remarks>
        public const int MaxStatusStacks = 1000;

        /// <summary>Largest permitted ability effect-list length.</summary>
        /// <remarks>Structural bound, not a balance number: one cast resolves this many effects per target.</remarks>
        public const int MaxAbilityEffects = 16;

        private static void ValidateStats(ContentDatabase database, List<string> errors)
        {
            var keys = new Dictionary<string, uint>(StringComparer.Ordinal);
            foreach (var stat in database.Stats)
            {
                uint id = stat.Id;
                if (id == 0)
                {
                    errors.Add("stat: id is 0, which is reserved — a proto3 zero is elided on the wire " +
                               "and would read as \"no stat\". Stat ids start at 1.");
                    continue;
                }

                ValidateKey("stat", id, stat.Key, keys, errors);
            }
        }

        private static void ValidateStatuses(ContentDatabase database, List<string> errors)
        {
            var keys = new Dictionary<string, uint>(StringComparer.Ordinal);
            foreach (var status in database.Statuses)
            {
                uint id = status.Id;
                if (id == 0)
                {
                    errors.Add("status: id is 0, which is reserved for \"no status\" on the wire " +
                               "(StatusEffect.effect_id, GameEvent.effect_id). Status ids start at 1.");
                    continue;
                }

                ValidateKey("status", id, status.Key, keys, errors);

                if (status.DurationTicks < 0)
                {
                    errors.Add(StatusLine(id, $"durationTicks is {status.DurationTicks}; it cannot be negative. " +
                                              "Use 0 for a status that lasts until removed."));
                }

                if (status.MaxStacks < 1 || status.MaxStacks > MaxStatusStacks)
                {
                    errors.Add(StatusLine(id, $"maxStacks is {status.MaxStacks}; it must be between 1 and {MaxStatusStacks}."));
                }

                PeriodicSpec p = status.Periodic;
                if (!Enum.IsDefined(typeof(PeriodicKind), p.Kind))
                {
                    errors.Add(StatusLine(id, $"periodic kind '{(int)p.Kind}' is not a known kind."));
                }
                else if (p.Kind == PeriodicKind.None)
                {
                    if (p.IntervalTicks != 0 || p.Amount != 0)
                    {
                        errors.Add(StatusLine(id, $"is not periodic but has intervalTicks {p.IntervalTicks} and amount {p.Amount}. " +
                                                  "A periodic block with no kind is a half-written DoT/HoT, not a default."));
                    }
                }
                else
                {
                    if (p.IntervalTicks < 1)
                    {
                        errors.Add(StatusLine(id, $"is periodic ({p.Kind}) but intervalTicks is {p.IntervalTicks}; it must be at least 1."));
                    }

                    if (p.Amount < 1)
                    {
                        errors.Add(StatusLine(id, $"is periodic ({p.Kind}) but amount is {p.Amount}; it must be at least 1. " +
                                                  "Damage and healing are separate kinds, never a signed amount."));
                    }
                }

                for (int i = 0; i < status.ModifierCount; i++)
                {
                    StatModifier m = status.GetModifier(i);
                    string where = string.Format(CultureInfo.InvariantCulture, "modifier {0}", i);

                    if (!Enum.IsDefined(typeof(StatModifierTarget), m.Target))
                    {
                        errors.Add(StatusLine(id, $"{where}: target '{(int)m.Target}' is not a known stat target."));
                        continue;
                    }

                    if (m.Target == StatModifierTarget.ContentStat)
                    {
                        if (!database.TryGetStat(m.StatId, out _))
                        {
                            errors.Add(StatusLine(id, $"{where}: stat {m.StatId} does not exist. A modifier on a stat " +
                                                      "nobody has would apply silently and change nothing."));
                        }
                    }
                    else if (m.StatId != 0)
                    {
                        errors.Add(StatusLine(id, $"{where}: target is {m.Target} but statId is {m.StatId}; statId is only read for ContentStat."));
                    }

                    if (m.Target == StatModifierTarget.Speed && m.Add != 0)
                    {
                        errors.Add(StatusLine(id, $"{where}: speed modifiers are multiplier-only, but add is {m.Add}. " +
                                                  "Speed is a float in world units per second and an integer add has no unit."));
                    }

                    if (m.MultiplierPermille < -1000)
                    {
                        errors.Add(StatusLine(id, $"{where}: multiplierPermille is {m.MultiplierPermille}; below -1000 " +
                                                  "would make a stat negative from one stack."));
                    }

                    if (m.Add == 0 && m.MultiplierPermille == 0)
                    {
                        errors.Add(StatusLine(id, $"{where}: add and multiplierPermille are both 0, so the modifier does nothing."));
                    }
                }

                CrowdControl cc = status.CrowdControl;
                if ((cc & ~CrowdControl.All) != 0)
                {
                    errors.Add(StatusLine(id, $"crowdControl has unknown flags 0x{(int)(cc & ~CrowdControl.All):X}."));
                }

                bool slows = (cc & CrowdControl.Slow) != 0;
                if (slows && (status.SlowPermille < 1 || status.SlowPermille > 1000))
                {
                    errors.Add(StatusLine(id, $"slows but slowPermille is {status.SlowPermille}; it must be between 1 and 1000."));
                }
                else if (!slows && status.SlowPermille != 0)
                {
                    errors.Add(StatusLine(id, $"slowPermille is {status.SlowPermille} but crowdControl does not include Slow, " +
                                              "so the value would never be read."));
                }
            }
        }

        private static void ValidateKey(
            string kind, uint id, string key, Dictionary<string, uint> seen, List<string> errors)
        {
            string prefix = string.Format(CultureInfo.InvariantCulture, "{0} {1}: ", kind, id);

            if (string.IsNullOrWhiteSpace(key))
            {
                errors.Add(prefix + "key is empty.");
                return;
            }

            if (key.Length > MaxIdLength)
            {
                errors.Add(prefix + $"key is {key.Length} characters, limit is {MaxIdLength}.");
            }

            if (!IsValidId(key))
            {
                errors.Add(prefix + $"key '{key}' may contain only lowercase letters, digits and underscores.");
            }

            if (seen.TryGetValue(key, out uint other))
            {
                // Reported against both ids, in ascending order, so the line is stable no
                // matter which order the dictionary enumerated the two in.
                uint lo = other < id ? other : id;
                uint hi = other < id ? id : other;
                errors.Add(string.Format(CultureInfo.InvariantCulture,
                    "{0} {1} and {0} {2}: both use key '{3}'. Keys must be unique — authoring tools and " +
                    "logs name a {0} by its key.", kind, lo, hi, key));
            }
            else
            {
                seen.Add(key, id);
            }
        }

        private static string StatusLine(uint id, string problem) =>
            string.Format(CultureInfo.InvariantCulture, "status {0}: {1}", id, problem);

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

        private static void ValidateAbility(AbilityDefinition ability, ContentDatabase database, List<string> errors)
        {
            uint id = ability.Id;

            // Zero is "no ability" on the wire (InputMessage.ability_id, GameEvent.ability_id
            // both use it that way). An ability that claimed it would be indistinguishable
            // from a client sending no ability at all, so it is refused at content time —
            // the one place the mistake is cheap to find.
            if (id == 0)
            {
                errors.Add("ability: id is 0, which is reserved for \"no ability\" on the wire. " +
                           "Ability ids start at 1.");
                return;
            }

            if (string.IsNullOrWhiteSpace(ability.Name))
            {
                errors.Add(AbilityLine(id, "name is empty. It is what the player sees."));
            }
            else if (ability.Name.Length > MaxNameLength)
            {
                errors.Add(AbilityLine(id, $"name is {ability.Name.Length} characters, limit is {MaxNameLength}."));
            }

            bool deliveryKnown = Enum.IsDefined(typeof(AbilityDelivery), ability.Delivery);
            if (!deliveryKnown)
            {
                errors.Add(AbilityLine(id, $"targeting '{(int)ability.Delivery}' is not a known targeting mode."));
            }

            if (!Enum.IsDefined(typeof(AbilityEffect), ability.Effect))
            {
                errors.Add(AbilityLine(id, $"effect '{(int)ability.Effect}' is not a known effect."));
            }

            ValidateEffects(ability, database, errors);

            if (ability.CooldownTicks < 0)
            {
                errors.Add(AbilityLine(id, $"cooldownTicks is {ability.CooldownTicks}; it cannot be negative."));
            }

            if (ability.Delivery == AbilityDelivery.Projectile)
            {
                ValidateProjectile(ability, errors);
            }

            // Range and radius are checked against what the delivery actually reads, not in
            // the abstract. A Self ability with a range is harmless noise; a Ground ability
            // with a zero radius affects nothing and looks like a broken cast to a player,
            // which is the kind of content fault that gets reported as a bug. A projectile's
            // reach is its own ProjectileSpec.Range, checked above.
            bool readsRange = deliveryKnown
                && ability.Delivery != AbilityDelivery.Self
                && ability.Delivery != AbilityDelivery.Projectile;
            if (readsRange && !(ability.Range > 0f))
            {
                errors.Add(AbilityLine(id, $"targeting is {ability.Delivery} but range is {Fmt(ability.Range)}. " +
                                           "A non-self ability with no range can never reach anything."));
            }

            if (ability.Delivery == AbilityDelivery.Ground && !(ability.Radius > 0f))
            {
                errors.Add(AbilityLine(id, $"targeting is Ground but radius is {Fmt(ability.Radius)}. " +
                                           "A ground ability with no radius affects nothing, which presents to a " +
                                           "player as a cast that does not work rather than as bad data."));
            }

            if (ability.Range < 0f)
            {
                errors.Add(AbilityLine(id, $"range is {Fmt(ability.Range)}; it cannot be negative."));
            }

            if (ability.Radius < 0f)
            {
                errors.Add(AbilityLine(id, $"radius is {Fmt(ability.Radius)}; it cannot be negative."));
            }

            // NaN deserves its own line rather than being swallowed by the comparisons
            // above: every comparison against NaN is false, so `range > 0` already reports
            // it, but it reports it as "no range" and an author reading that goes looking
            // for a missing field instead of a malformed one.
            if (float.IsNaN(ability.Range) || float.IsNaN(ability.Radius))
            {
                errors.Add(AbilityLine(id, "range or radius is NaN. Every comparison against NaN is false, " +
                                           "so this would present as a missing value rather than a malformed one."));
            }
        }

        private static void ValidateEffects(AbilityDefinition ability, ContentDatabase database, List<string> errors)
        {
            uint id = ability.Id;

            if (ability.EffectCount == 0)
            {
                errors.Add(AbilityLine(id, "has no effects. An ability that does nothing presents to a player " +
                                           "as a cast that does not work rather than as bad data."));
                return;
            }

            if (ability.EffectCount > MaxAbilityEffects)
            {
                errors.Add(AbilityLine(id, $"has {ability.EffectCount} effects, limit is {MaxAbilityEffects}."));
            }

            for (int i = 0; i < ability.EffectCount; i++)
            {
                EffectSpec e = ability.GetEffect(i);
                // A single-effect ability keeps the original wording, which content authors
                // (and the loader's tests) already know; a list names the entry.
                string where = ability.EffectCount == 1
                    ? ""
                    : string.Format(CultureInfo.InvariantCulture, "effect {0}: ", i);

                switch (e.Kind)
                {
                    case EffectKind.Damage:
                    case EffectKind.Heal:
                        if (e.Power < 0)
                        {
                            errors.Add(AbilityLine(id, where + $"power is {e.Power}; negative power is not supported. " +
                                                       "An ability that harms its caster is a damage ability aimed at self, " +
                                                       "not a heal with a negative number."));
                        }

                        if (e.StatusId != 0)
                        {
                            errors.Add(AbilityLine(id, where + $"is {e.Kind} but names status {e.StatusId}; statusId is only read by ApplyStatus."));
                        }

                        break;

                    case EffectKind.ApplyStatus:
                        if (e.StatusId == 0)
                        {
                            errors.Add(AbilityLine(id, where + "applies status 0, which is reserved for \"no status\"."));
                        }
                        else if (!database.TryGetStatus(e.StatusId, out _))
                        {
                            errors.Add(AbilityLine(id, where + $"applies status {e.StatusId}, which does not exist. " +
                                                       "The cast would resolve and apply nothing."));
                        }

                        break;

                    default:
                        errors.Add(AbilityLine(id, where + $"effect kind '{(int)e.Kind}' is not a known effect."));
                        break;
                }
            }
        }

        private static void ValidateProjectile(AbilityDefinition ability, List<string> errors)
        {
            uint id = ability.Id;
            ProjectileSpec p = ability.Projectile;

            // !(x > 0) rather than x <= 0 so NaN is refused as well: every comparison against
            // NaN is false, and a NaN speed would integrate every projectile to NaN.
            if (!(p.Speed > 0f) || float.IsInfinity(p.Speed))
            {
                errors.Add(AbilityLine(id, $"is a projectile but projectile speed is {Fmt(p.Speed)}; it must be a positive finite number."));
            }

            if (!(p.Radius > 0f) || float.IsInfinity(p.Radius))
            {
                errors.Add(AbilityLine(id, $"is a projectile but projectile radius is {Fmt(p.Radius)}; it must be a positive finite number."));
            }

            if (!(p.Range > 0f) || float.IsInfinity(p.Range))
            {
                errors.Add(AbilityLine(id, $"is a projectile but projectile range is {Fmt(p.Range)}; it must be a positive finite number. " +
                                           "A projectile with no range despawns on the tick it is fired."));
            }
        }

        private static string Fmt(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        private static string AbilityLine(uint id, string problem) =>
            string.Format(CultureInfo.InvariantCulture, "ability {0}: {1}", id, problem);

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
