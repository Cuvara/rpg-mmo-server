namespace Shared.GameLogic.Gameplay
{
    /// <summary>
    /// Opcodes of the gameplay command channel (ADR-30.4): the <c>opcode</c> of a wire
    /// <c>CommandRequest</c> or <c>ServerPush</c>. Payload schemas are in
    /// <c>Gameplay/gameplay.proto</c>.
    /// </summary>
    /// <remarks>
    /// Numbers are FROZEN once shipped; append only. 0 is "not sent" and is refused.
    /// Requests use 1-99 and pushes 100 and up, so one number never names both.
    /// </remarks>
    public static class GameplayOpcodes
    {
        /// <summary>Not sent. Always refused.</summary>
        public const uint None = 0;

        /// <summary><see cref="InventoryRequest"/> -> <see cref="InventoryView"/>.</summary>
        public const uint Inventory = 1;

        /// <summary><see cref="PickupRequest"/> -> <see cref="InventoryView"/>.</summary>
        public const uint Pickup = 2;

        /// <summary><see cref="EquipRequest"/> -> empty result; the change arrives as <see cref="InventoryChanged"/>.</summary>
        public const uint Equip = 3;

        /// <summary><see cref="UnequipRequest"/> -> empty result; the change arrives as <see cref="InventoryChanged"/>.</summary>
        public const uint Unequip = 4;

        /// <summary><see cref="UseItemRequest"/> -> empty result; the change arrives as <see cref="InventoryChanged"/>.</summary>
        public const uint UseItem = 5;

        /// <summary>Push: <see cref="Gameplay.InventoryChanged"/>.</summary>
        public const uint InventoryChanged = 100;

        /// <summary>First opcode number reserved for server pushes.</summary>
        public const uint FirstPush = 100;

        /// <summary>True for a known request opcode.</summary>
        public static bool IsKnownRequest(uint opcode) => opcode >= Inventory && opcode <= UseItem;

        /// <summary>True for a known push opcode.</summary>
        public static bool IsKnownPush(uint opcode) => opcode == InventoryChanged;
    }

    /// <summary>
    /// Machine-readable error codes for a wire <c>CommandResult.error</c> when
    /// <c>ok = false</c>. Interned constants: a client switches on them, so they never change
    /// once shipped.
    /// </summary>
    public static class GameplayErrors
    {
        /// <summary>The server does not know the opcode (a newer client against an older server).</summary>
        public const string UnknownOpcode = "unknown_opcode";

        /// <summary>The payload did not decode as the opcode's message.</summary>
        public const string InvalidPayload = "invalid_payload";

        /// <summary>Too many requests for this opcode.</summary>
        public const string RateLimited = "rate_limited";

        /// <summary>A named entity, item instance or slot does not exist.</summary>
        public const string NotFound = "not_found";

        /// <summary>The target is too far away.</summary>
        public const string OutOfRange = "out_of_range";

        /// <summary>The item cannot be worn in the named slot.</summary>
        public const string SlotMismatch = "slot_mismatch";

        /// <summary>No bag space for the result.</summary>
        public const string InventoryFull = "inventory_full";

        /// <summary>The item instance belongs to someone else.</summary>
        public const string NotOwner = "not_owner";
    }

    /// <summary>Values of <see cref="ItemStack.Container"/>.</summary>
    public static class ItemContainers
    {
        /// <summary>In the bag, at <see cref="ItemStack.BagIndex"/>.</summary>
        public const string Bag = "bag";

        /// <summary>Worn, in <see cref="ItemStack.Slot"/>.</summary>
        public const string Equipped = "equipped";
    }
}
