using System.Collections.Generic;
using System.Text;
using GameServer.Content;
using Shared.GameLogic.Content;
using Xunit;

namespace GameServer.Tests.Content;

/// <summary>
/// Abilities through the content pipeline: what loads, what is refused, and that every
/// refusal names the ability and the field.
/// </summary>
public class AbilityContentTests
{
    private static LoadedContent Load(string json) =>
        ContentLoader.LoadFromBytes(Encoding.UTF8.GetBytes(json), "test.json");

    private static ContentLoadException Refuse(string json) =>
        Assert.Throws<ContentLoadException>(() => Load(json));

    private const string OneAbility = """
    {
      "items": [],
      "abilities": [
        { "id": 1, "name": "Fire Bolt", "targeting": "entity", "effect": "damage",
          "range": 12.5, "radius": 0, "power": 25, "cooldownTicks": 8 }
      ]
    }
    """;

    [Fact]
    public void AnAbility_LoadsWithEveryFieldIntact()
    {
        var content = Load(OneAbility);

        Assert.Equal(1, content.Database.AbilityCount);
        Assert.True(content.Database.TryGetAbility(1, out var ability));
        Assert.NotNull(ability);
        Assert.Equal("Fire Bolt", ability!.Name);
        Assert.Equal(AbilityTargeting.Entity, ability.Targeting);
        Assert.Equal(AbilityEffect.Damage, ability.Effect);
        Assert.Equal(12.5f, ability.Range);
        Assert.Equal(25, ability.Power);
        Assert.Equal(8, ability.CooldownTicks);
    }

    /// <summary>
    /// The asymmetry with <c>items</c> is deliberate — see ItemFileDto.Abilities. Every
    /// content document written before abilities existed has no such key, and refusing them
    /// would turn this field into a migration of every content set in every environment.
    /// </summary>
    [Fact]
    public void ADocumentWithNoAbilitiesKey_LoadsWithNone()
    {
        var content = Load("""{ "items": [] }""");

        Assert.Equal(0, content.Database.AbilityCount);
    }

    [Fact]
    public void ZeroIsRefusedAsAnAbilityId_BecauseTheWireReservesIt()
    {
        var ex = Refuse("""
        {
          "items": [],
          "abilities": [
            { "id": 0, "name": "x", "targeting": "self", "effect": "heal",
              "range": 0, "radius": 0, "power": 1, "cooldownTicks": 0 }
          ]
        }
        """);

        Assert.Contains("reserved", ex.Message);
        Assert.Contains("no ability", ex.Message);
    }

    [Fact]
    public void DuplicateAbilityIds_AreRefused_AndTheMessageExplainsWhyItMatters()
    {
        var ex = Refuse("""
        {
          "items": [],
          "abilities": [
            { "id": 3, "name": "a", "targeting": "self", "effect": "heal", "range": 0, "radius": 0, "power": 1, "cooldownTicks": 0 },
            { "id": 3, "name": "b", "targeting": "self", "effect": "heal", "range": 0, "radius": 0, "power": 1, "cooldownTicks": 0 }
          ]
        }
        """);

        Assert.Contains("Duplicate ability id 3", ex.Message);
    }

    [Fact]
    public void ANonSelfAbilityWithNoRange_IsRefused()
    {
        var ex = Refuse("""
        {
          "items": [],
          "abilities": [
            { "id": 1, "name": "a", "targeting": "entity", "effect": "damage",
              "range": 0, "radius": 0, "power": 5, "cooldownTicks": 0 }
          ]
        }
        """);

        Assert.Contains("ability 1", ex.Message);
        Assert.Contains("range", ex.Message);
    }

    /// <summary>
    /// A ground ability with no radius affects nothing, which presents to a player as a cast
    /// that does not work rather than as bad data — so it is refused where it is cheap.
    /// </summary>
    [Fact]
    public void AGroundAbilityWithNoRadius_IsRefused()
    {
        var ex = Refuse("""
        {
          "items": [],
          "abilities": [
            { "id": 1, "name": "a", "targeting": "ground", "effect": "damage",
              "range": 10, "radius": 0, "power": 5, "cooldownTicks": 0 }
          ]
        }
        """);

        Assert.Contains("radius", ex.Message);
    }

    [Fact]
    public void AnUnknownTargetingSpelling_IsRefusedAndListsTheValidOnes()
    {
        var ex = Refuse("""
        {
          "items": [],
          "abilities": [
            { "id": 1, "name": "a", "targeting": "cone", "effect": "damage",
              "range": 10, "radius": 0, "power": 5, "cooldownTicks": 0 }
          ]
        }
        """);

        Assert.Contains("cone", ex.Message);
        Assert.Contains("self, entity, ground", ex.Message);
    }

    /// <summary>
    /// Missing and zero must be different messages: an author who wrote nothing did not
    /// write 0, and telling them their 0 is invalid sends them looking in the wrong place.
    /// </summary>
    [Fact]
    public void AMissingCooldown_SaysItIsMissing_AndNamesTheUnit()
    {
        var ex = Refuse("""
        {
          "items": [],
          "abilities": [
            { "id": 1, "name": "a", "targeting": "self", "effect": "heal", "power": 5 }
          ]
        }
        """);

        Assert.Contains("'cooldownTicks' is missing", ex.Message);
        Assert.Contains("TICKS", ex.Message);
    }

    [Fact]
    public void AZeroCooldown_IsLegalAndIsNotAMissingField()
    {
        var content = Load("""
        {
          "items": [],
          "abilities": [
            { "id": 1, "name": "a", "targeting": "self", "effect": "heal",
              "range": 0, "radius": 0, "power": 5, "cooldownTicks": 0 }
          ]
        }
        """);

        Assert.Equal(0, content.Database.GetAbility(1).CooldownTicks);
    }

    [Fact]
    public void EveryErrorInOneDocument_IsReportedTogether()
    {
        var ex = Refuse("""
        {
          "items": [],
          "abilities": [
            { "id": 1, "name": "a", "targeting": "entity", "effect": "damage", "range": 0, "radius": 0, "power": 5, "cooldownTicks": 0 },
            { "id": 2, "name": "b", "targeting": "ground", "effect": "damage", "range": 10, "radius": 0, "power": 5, "cooldownTicks": 0 }
          ]
        }
        """);

        // A validator that stops at the first problem moves the work to whoever reads the log.
        Assert.Contains("ability 1", ex.Message);
        Assert.Contains("ability 2", ex.Message);
    }

    [Fact]
    public void AbilityIdZero_NeverResolves_EvenThroughTryGet()
    {
        var content = Load(OneAbility);

        Assert.False(content.Database.TryGetAbility(0, out var none));
        Assert.Null(none);
    }

    [Fact]
    public void TheHashCoversAbilities_SoAContentChangeIsVisibleToAClient()
    {
        string withAbility = OneAbility;
        string without = """{ "items": [], "abilities": [] }""";

        Assert.NotEqual(Load(withAbility).Hash, Load(without).Hash);
    }
}
