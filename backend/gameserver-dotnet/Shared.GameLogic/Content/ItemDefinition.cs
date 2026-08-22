using System;

namespace Shared.GameLogic.Content
{
    /// <summary>Where an item can be equipped, or <see cref="None"/> if it cannot be.</summary>
    public enum ItemSlot
    {
        None = 0,
        Weapon = 1,
        Head = 2,
        Chest = 3,
        Legs = 4,
        Trinket = 5,
    }

    /// <summary>
    /// Item rarity. Ordered, so a comparison against a tier is meaningful.
    /// </summary>
    public enum ItemRarity
    {
        Common = 0,
        Uncommon = 1,
        Rare = 2,
        Epic = 3,
        Legendary = 4,
    }

    /// <summary>
    /// One item, exactly as content authoring defines it. Immutable once built.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This type is the <b>shared schema</b>, not a parser. It deliberately holds no
    /// serialization attributes and knows nothing about JSON: the server and the client
    /// each parse with their own tooling and construct this, because the two runtimes do
    /// not share a JSON library. Unity compiles these files as source and has no
    /// <c>System.Text.Json</c>; the server runs NativeAOT and cannot use reflection. There
    /// is no single parser that satisfies both, so the schema is shared and the parsing is
    /// not — with golden vectors standing where a shared parser would have.
    /// </para>
    /// <para>
    /// Constructor-assigned rather than <c>init</c>-set: <c>init</c> accessors need
    /// <c>IsExternalInit</c>, which netstandard2.1 does not carry, so a property with
    /// <c>init</c> compiles on the server and fails in Unity. That is exactly the
    /// class of break ADR-10 pins the language version to prevent.
    /// </para>
    /// </remarks>
    public sealed class ItemDefinition
    {
        public ItemDefinition(
            string id,
            string name,
            ItemSlot slot,
            ItemRarity rarity,
            int stackMax,
            int attack,
            int defense,
            int levelRequirement)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Slot = slot;
            Rarity = rarity;
            StackMax = stackMax;
            Attack = attack;
            Defense = defense;
            LevelRequirement = levelRequirement;
        }

        /// <summary>Stable identifier. Referenced by loot tables, inventories and saves.</summary>
        /// <remarks>
        /// Once an id has been persisted against a player it can never be reused for a
        /// different item, because a saved inventory row is only a reference. Renaming an
        /// item is free; renaming its <b>id</b> silently repoints every existing copy.
        /// </remarks>
        public string Id { get; }

        /// <summary>Display name. Free to change at any time — nothing references it.</summary>
        public string Name { get; }

        public ItemSlot Slot { get; }

        public ItemRarity Rarity { get; }

        /// <summary>Maximum stack size. 1 for anything equippable.</summary>
        public int StackMax { get; }

        /// <summary>Flat attack contribution while equipped.</summary>
        public int Attack { get; }

        /// <summary>Flat defense contribution while equipped.</summary>
        public int Defense { get; }

        /// <summary>Minimum character level required to equip. 0 means no requirement.</summary>
        public int LevelRequirement { get; }

        /// <summary>True when this item occupies an equipment slot.</summary>
        public bool IsEquippable => Slot != ItemSlot.None;

        public override string ToString() => Id;
    }
}
