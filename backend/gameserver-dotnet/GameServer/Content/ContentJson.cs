using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace GameServer.Content;

/// <summary>
/// The on-disk shape of <c>items.json</c>. Separate from
/// <see cref="Shared.GameLogic.Content.ItemDefinition"/> on purpose.
/// </summary>
/// <remarks>
/// <para>
/// The shared type is the schema the simulation runs against; this is the wire/disk form,
/// and they answer to different pressures. This one has to tolerate a missing field, an
/// unknown enum spelling and a null — because a human typed it — and turn each into a
/// diagnosable error. The shared type is constructed only from values that already passed
/// that gauntlet, which is why it can be non-nullable throughout and needs no
/// serialization attributes at all.
/// </para>
/// <para>
/// Collapsing the two would push <c>System.Text.Json</c> attributes into
/// <c>Shared.GameLogic</c>, which Unity compiles as source and cannot resolve.
/// </para>
/// </remarks>
internal sealed class ItemFileDto
{
    [JsonPropertyName("items")]
    public List<ItemDto>? Items { get; set; }

    /// <summary>
    /// Abilities, or null when the document has no <c>abilities</c> key.
    /// </summary>
    /// <remarks>
    /// <b>Optional, unlike <see cref="Items"/>, and the asymmetry is deliberate.</b> A
    /// missing <c>items</c> key is refused because it is indistinguishable from a misspelled
    /// one and would load as a silently empty game. The same argument applies here, and is
    /// outweighed by a concrete fact: every content document written before abilities existed
    /// has no such key, and requiring it would turn adding this field into a migration of
    /// every content set in every environment — for a field most sets will legitimately not
    /// use. The cost is that a document spelling it <c>abilties</c> loads with no abilities
    /// rather than being refused; the mitigation is that the first cast of any ability then
    /// fails with "unknown ability", which names the content set in its message.
    /// </remarks>
    [JsonPropertyName("abilities")]
    public List<AbilityDto>? Abilities { get; set; }
}

/// <summary>
/// The on-disk shape of an ability. Separate from
/// <see cref="Shared.GameLogic.Content.AbilityDefinition"/> for the reason
/// <see cref="ItemDto"/> gives.
/// </summary>
internal sealed class AbilityDto
{
    /// <summary>
    /// Nullable so an omitted id is distinguishable from an explicit 0 — and 0 is a value
    /// the validator specifically refuses, so the two need different messages.
    /// </summary>
    [JsonPropertyName("id")]
    public uint? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("targeting")]
    public string? Targeting { get; set; }

    [JsonPropertyName("effect")]
    public string? Effect { get; set; }

    [JsonPropertyName("range")]
    public float? Range { get; set; }

    [JsonPropertyName("radius")]
    public float? Radius { get; set; }

    [JsonPropertyName("power")]
    public int? Power { get; set; }

    /// <summary>
    /// Cooldown in SIMULATION TICKS, not milliseconds — see
    /// <see cref="Shared.GameLogic.Content.AbilityDefinition.CooldownTicks"/>. Named with
    /// the unit in the key so a content author cannot write a millisecond figure into it by
    /// assumption.
    /// </summary>
    [JsonPropertyName("cooldownTicks")]
    public int? CooldownTicks { get; set; }
}

internal sealed class ItemDto
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("slot")]
    public string? Slot { get; set; }

    [JsonPropertyName("rarity")]
    public string? Rarity { get; set; }

    /// <summary>
    /// Nullable so an omitted field is distinguishable from an explicit 0.
    /// </summary>
    /// <remarks>
    /// A non-nullable int would default a missing <c>stackMax</c> to 0, which validation
    /// then rejects as "must be at least 1" — a correct refusal with a misleading message,
    /// since the author did not write 0, they wrote nothing. Nullable lets the loader say
    /// "field is missing" instead.
    /// </remarks>
    [JsonPropertyName("stackMax")]
    public int? StackMax { get; set; }

    [JsonPropertyName("attack")]
    public int? Attack { get; set; }

    [JsonPropertyName("defense")]
    public int? Defense { get; set; }

    [JsonPropertyName("levelRequirement")]
    public int? LevelRequirement { get; set; }
}

/// <summary>
/// Source-generated serialization context. Required, not an optimisation: the server
/// publishes with NativeAOT (ADR-11), where reflection-based
/// <c>JsonSerializer.Deserialize&lt;T&gt;</c> trims away and fails at runtime on a build
/// that compiled clean.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(ItemFileDto))]
internal partial class ContentJsonContext : JsonSerializerContext
{
}
