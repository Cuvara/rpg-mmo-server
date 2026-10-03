using System;

namespace Shared.GameLogic.Content
{
    /// <summary>
    /// One content-defined stat (ADR-30): mana, level, cast progress, a resistance. Immutable
    /// once built.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why stats are content and not schema.</b> Through protocol 2 every visible number
    /// was an <c>EntitySnapshot</c> field, so adding one meant a coordinated server, Netcode
    /// and client release plus a protocol bump. Protocol 3 replicates a
    /// <c>repeated StatValue stats</c> block keyed by <see cref="Id"/>, so a new stat is an
    /// entry in a content file and nothing else.
    /// </para>
    /// <para>
    /// The four combat stats every entity already has — attack, defense, speed and max HP —
    /// stay first-class <see cref="Components.EntityState"/> fields and are NOT content stats;
    /// <see cref="StatModifierTarget"/> names them separately. Moving them into the stat block
    /// would put a lookup on the hottest combat path for no gain.
    /// </para>
    /// <para>
    /// <b><see cref="Id"/> is numeric</b> for the reason ability ids are: it travels on the
    /// wire in every stat entry. Ids start at 1 because a proto3 zero is elided and would read
    /// as "no stat". <see cref="Key"/> is the authoring name, unique across stats.
    /// </para>
    /// </remarks>
    public sealed class StatDefinition
    {
        /// <summary>Builds a stat definition.</summary>
        /// <param name="id">Stable numeric id, 1 or greater.</param>
        /// <param name="key">Authoring key, e.g. <c>mana</c>. Lowercase, digits and underscores.</param>
        /// <param name="defaultValue">Value an entity starts with when nothing else sets it.</param>
        public StatDefinition(uint id, string key, int defaultValue)
        {
            Id = id;
            Key = key ?? throw new ArgumentNullException(nameof(key));
            DefaultValue = defaultValue;
        }

        /// <summary>
        /// Stable numeric identifier, 1 or greater. Never reused once shipped: a replicated
        /// or persisted value is only a reference.
        /// </summary>
        public uint Id { get; }

        /// <summary>Authoring key, unique across stats. Free to rename only before it ships.</summary>
        public string Key { get; }

        /// <summary>Value an entity starts with when nothing else sets it.</summary>
        public int DefaultValue { get; }

        /// <inheritdoc />
        public override string ToString() => $"{Id}:{Key}";
    }
}
