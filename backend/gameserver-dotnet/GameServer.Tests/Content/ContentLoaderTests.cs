using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using GameServer.Content;
using Shared.GameLogic.Content;
using Xunit;

namespace GameServer.Tests.Content;

/// <summary>
/// The content pipeline's contract: what loads, what is refused, and — the part that
/// matters most — that every refusal names the file and the field.
/// </summary>
/// <remarks>
/// <para>
/// Content is edited by hand, so malformed content is the normal case rather than the
/// exceptional one. A loader that rejects it correctly but says only "invalid content"
/// moves the debugging onto whoever reads the log, which is why several tests here assert
/// on the <i>text</i> of the error and not just that one was thrown.
/// </para>
/// <para>
/// The other half is fail-fast. Every rejection below is a server that refuses to boot.
/// That is deliberate: a server running on half-parsed content serves an unknowable
/// subset of the intended game, and each downstream symptom gets blamed on whichever
/// system noticed it first.
/// </para>
/// </remarks>
public sealed class ContentLoaderTests
{
    private static byte[] Bytes(string json) => Encoding.UTF8.GetBytes(json);

    private const string OneValidItem = """
    {
      "items": [
        { "id": "iron_sword", "name": "Iron Sword", "slot": "weapon", "rarity": "uncommon",
          "stackMax": 1, "attack": 7, "defense": 0, "levelRequirement": 5 }
      ]
    }
    """;

    [Fact]
    public void LoadsAValidDocument()
    {
        var loaded = ContentLoader.LoadFromBytes(Bytes(OneValidItem), "test");

        Assert.Equal(1, loaded.Database.ItemCount);
        Assert.True(loaded.Database.TryGetItem("iron_sword", out var item));
        Assert.NotNull(item);
        Assert.Equal("Iron Sword", item!.Name);
        Assert.Equal(ItemSlot.Weapon, item.Slot);
        Assert.Equal(ItemRarity.Uncommon, item.Rarity);
        Assert.Equal(7, item.Attack);
        Assert.Equal(5, item.LevelRequirement);
        Assert.True(item.IsEquippable);
    }

    /// <summary>
    /// The repository's own content file must load. This is the test that fails when
    /// someone edits <c>items.json</c> and breaks it, which is the whole point of having
    /// the pipeline — the break is caught in CI rather than by a server that will not boot.
    /// </summary>
    [Fact]
    public void TheRepositoryContentFileLoadsAndValidates()
    {
        string dir = FindContentDirectory();
        var loaded = ContentLoader.Load(dir);

        Assert.True(loaded.Database.ItemCount > 0,
            "backend/content/items.json defines no items. If that is intentional the " +
            "assertion should change; if it is not, the file lost its contents.");

        // Hash is over the file bytes, so it must be stable across two reads of an
        // unchanged file. A hash that moved on its own would defeat client caching
        // silently — every join would re-download and nothing would look wrong.
        var again = ContentLoader.Load(dir);
        Assert.Equal(loaded.Hash, again.Hash);
        Assert.Equal(16, loaded.Hash.Length);
    }

    /// <summary>
    /// The default directory must be found from the <b>binary's</b> location, not the
    /// working directory.
    /// </summary>
    /// <remarks>
    /// This is a regression test with a specific failure behind it. The default started as
    /// the relative path <c>../../content</c>, which resolves against the working directory
    /// — a property of whoever launched the process, not of the deployment. It worked under
    /// <c>dotnet run</c> from the module directory and broke every integration test on the
    /// first CI run, because those launch the server from their own directory.
    ///
    /// <para>This test runs from the test binary's own output directory, several levels
    /// away from the repository's content, so it fails if the resolution ever goes back to
    /// being working-directory relative.</para>
    /// </remarks>
    [Fact]
    public void DefaultDirectoryIsFoundFromTheBinaryNotTheWorkingDirectory()
    {
        string? resolved = ContentLoader.ResolveDefaultDirectory();

        Assert.NotNull(resolved);
        Assert.True(File.Exists(Path.Combine(resolved!, ContentLoader.ItemsFileName)),
            $"ResolveDefaultDirectory returned '{resolved}', which holds no {ContentLoader.ItemsFileName}.");

        // And it must actually load, not merely exist.
        var loaded = ContentLoader.Load(resolved!);
        Assert.True(loaded.Database.ItemCount > 0);
    }

    [Fact]
    public void RejectsMalformedJsonNamingTheOrigin()
    {
        var ex = Assert.Throws<ContentLoadException>(
            () => ContentLoader.LoadFromBytes(Bytes("{ \"items\": [ }"), "items.json"));

        Assert.Contains("items.json", ex.Message);
        Assert.Contains("not valid JSON", ex.Message);
    }

    /// <summary>
    /// A missing <c>items</c> key and an empty one are different statements, and only one
    /// is a mistake. Conflating them loads a misspelled key as a game with no items in it.
    /// </summary>
    [Fact]
    public void DistinguishesAMissingItemsKeyFromAnEmptyOne()
    {
        var ex = Assert.Throws<ContentLoadException>(
            () => ContentLoader.LoadFromBytes(Bytes("""{ "itemz": [] }"""), "typo.json"));
        Assert.Contains("no 'items' array", ex.Message);

        var empty = ContentLoader.LoadFromBytes(Bytes("""{ "items": [] }"""), "empty.json");
        Assert.Equal(0, empty.Database.ItemCount);
    }

    [Theory]
    [InlineData("\"slot\": \"gauntlets\"", "slot", "gauntlets")]
    [InlineData("\"rarity\": \"mythic\"", "rarity", "mythic")]
    public void RejectsUnknownEnumSpellingsAndSaysWhatIsValid(string field, string kind, string bad)
    {
        string json = $$"""
        { "items": [ { "id": "x", "name": "X", "slot": "none", "rarity": "common",
                       "stackMax": 1, {{field}} } ] }
        """;

        var ex = Assert.Throws<ContentLoadException>(() => ContentLoader.LoadFromBytes(Bytes(json), "t"));

        Assert.Contains(bad, ex.Message);
        Assert.Contains(kind, ex.Message);
    }

    /// <summary>
    /// A numeric slot must not be accepted. <c>Enum.Parse</c> would take <c>"3"</c> as
    /// Chest, which makes the content file depend on declaration order that content
    /// authors have no reason to know and that a reordering would silently change.
    /// </summary>
    [Fact]
    public void RejectsNumericEnumValues()
    {
        string json = """
        { "items": [ { "id": "x", "name": "X", "slot": "3", "rarity": "common", "stackMax": 1 } ] }
        """;

        var ex = Assert.Throws<ContentLoadException>(() => ContentLoader.LoadFromBytes(Bytes(json), "t"));
        Assert.Contains("slot '3' is not recognised", ex.Message);
    }

    /// <summary>
    /// An omitted <c>stackMax</c> must not read as 0. It would be rejected either way, but
    /// with the wrong diagnosis — "must be at least 1" describes a value the author never
    /// wrote.
    /// </summary>
    [Fact]
    public void AMissingStackMaxReportsAsMissingNotAsZero()
    {
        string json = """
        { "items": [ { "id": "x", "name": "X", "slot": "none", "rarity": "common" } ] }
        """;

        var ex = Assert.Throws<ContentLoadException>(() => ContentLoader.LoadFromBytes(Bytes(json), "t"));
        Assert.Contains("'stackMax' is missing", ex.Message);
    }

    [Fact]
    public void RejectsDuplicateIds()
    {
        string json = """
        { "items": [
          { "id": "dup", "name": "A", "slot": "none", "rarity": "common", "stackMax": 1 },
          { "id": "dup", "name": "B", "slot": "none", "rarity": "common", "stackMax": 1 }
        ] }
        """;

        var ex = Assert.Throws<ContentLoadException>(() => ContentLoader.LoadFromBytes(Bytes(json), "t"));
        Assert.Contains("Duplicate item id 'dup'", ex.Message);
    }

    /// <summary>
    /// Equipment that stacks has no rule for which copy is worn. Refused at content time
    /// rather than discovered by a player holding two of something they can wear one of.
    /// </summary>
    [Fact]
    public void RejectsStackableEquipment()
    {
        string json = """
        { "items": [ { "id": "sword", "name": "S", "slot": "weapon", "rarity": "common",
                       "stackMax": 5 } ] }
        """;

        var ex = Assert.Throws<ContentLoadException>(() => ContentLoader.LoadFromBytes(Bytes(json), "t"));
        Assert.Contains("Equipment must not stack", ex.Message);
    }

    [Theory]
    [InlineData("Iron_Sword", "lowercase")]
    [InlineData("iron-sword", "lowercase")]
    [InlineData("iron sword", "lowercase")]
    public void RejectsIdsThatAreNotLowercaseSnakeCase(string id, string expected)
    {
        string json = $$"""
        { "items": [ { "id": "{{id}}", "name": "X", "slot": "none", "rarity": "common",
                       "stackMax": 1 } ] }
        """;

        var ex = Assert.Throws<ContentLoadException>(() => ContentLoader.LoadFromBytes(Bytes(json), "t"));
        Assert.Contains(expected, ex.Message);
    }

    /// <summary>
    /// Every problem is reported from one load. A validator that stops at the first fault
    /// costs one server restart per typo.
    /// </summary>
    [Fact]
    public void ReportsEveryProblemAtOnce()
    {
        string json = """
        { "items": [
          { "id": "BAD_ID", "name": "", "slot": "none", "rarity": "common", "stackMax": 0 },
          { "id": "also_bad", "name": "Fine", "slot": "weapon", "rarity": "common",
            "stackMax": 3, "attack": -1 }
        ] }
        """;

        var ex = Assert.Throws<ContentLoadException>(() => ContentLoader.LoadFromBytes(Bytes(json), "t"));

        Assert.Contains("lowercase", ex.Message);          // BAD_ID
        Assert.Contains("name is empty", ex.Message);       // ""
        Assert.Contains("stackMax is 0", ex.Message);       // 0
        Assert.Contains("Equipment must not stack", ex.Message);
        Assert.Contains("attack is -1", ex.Message);
        Assert.Contains("5 problems", ex.Message);
    }

    [Fact]
    public void MissingDirectoryAndMissingFileEachSayWhichIsWrong()
    {
        string absent = Path.Combine(Path.GetTempPath(), "no_such_content_" + Guid.NewGuid().ToString("N"));
        var dirEx = Assert.Throws<ContentLoadException>(() => ContentLoader.Load(absent));
        Assert.Contains("does not exist", dirEx.Message);
        Assert.Contains("--content-dir", dirEx.Message);

        string empty = Path.Combine(Path.GetTempPath(), "empty_content_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(empty);
        try
        {
            var fileEx = Assert.Throws<ContentLoadException>(() => ContentLoader.Load(empty));
            Assert.Contains(ContentLoader.ItemsFileName, fileEx.Message);
        }
        finally
        {
            Directory.Delete(empty, recursive: true);
        }
    }

    /// <summary>
    /// The hash identifies the bytes. Two documents that differ only in whitespace are
    /// different files and must hash differently — the hash is a change detector for a
    /// file on disk, not a semantic fingerprint of the parsed content.
    /// </summary>
    [Fact]
    public void HashTracksBytesNotMeaning()
    {
        var a = ContentLoader.LoadFromBytes(Bytes(OneValidItem), "a");
        var b = ContentLoader.LoadFromBytes(Bytes(OneValidItem.Replace("\n", "\n ")), "b");

        Assert.NotEqual(a.Hash, b.Hash);
        Assert.Equal(a.Database.ItemCount, b.Database.ItemCount);
    }

    [Fact]
    public void CanonicalBytesAreServedUnchanged()
    {
        byte[] input = Bytes(OneValidItem);
        var loaded = ContentLoader.LoadFromBytes(input, "t");

        // Reference equality is not required, but the content must be byte-identical:
        // clients parse exactly what the server read, so a defect in a server-side writer
        // can never present as a client-side reader bug.
        Assert.Equal(input, loaded.CanonicalBytes);
    }

    /// <summary>
    /// Walks up from the test binary to the repo's content directory. The test binary
    /// lives under bin/, so a relative path from the working directory is fragile across
    /// `dotnet test`, an IDE runner, and CI.
    /// </summary>
    private static string FindContentDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "content", ContentLoader.ItemsFileName);
            if (File.Exists(candidate))
            {
                return Path.Combine(dir.FullName, "content");
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not find backend/content/ by walking up from " + AppContext.BaseDirectory);
    }
}

/// <summary>Behaviour of the immutable database itself, independent of parsing.</summary>
public sealed class ContentDatabaseTests
{
    private static ItemDefinition Item(string id) =>
        new ItemDefinition(id, id, ItemSlot.None, ItemRarity.Common, 1, 0, 0, 0);

    [Fact]
    public void LookupMissesReturnFalseRatherThanThrowing()
    {
        var db = new ContentDatabase(new[] { Item("a") }, "h");

        Assert.False(db.TryGetItem("absent", out var missing));
        Assert.Null(missing);
        Assert.False(db.TryGetItem(null!, out _));
        Assert.Throws<KeyNotFoundException>(() => db.GetItem("absent"));
    }

    [Fact]
    public void IdComparisonIsOrdinalAndCaseSensitive()
    {
        var db = new ContentDatabase(new[] { Item("iron_sword") }, "h");

        Assert.True(db.TryGetItem("iron_sword", out _));
        // Ids are lowercase by validation, so a case-insensitive lookup would accept an id
        // the content file could not have declared — a reference that works in code and
        // fails validation is worse than one that fails consistently.
        Assert.False(db.TryGetItem("Iron_Sword", out _));
    }

    [Fact]
    public void EmptyIsUsableAsADefault()
    {
        Assert.Equal(0, ContentDatabase.Empty.ItemCount);
        Assert.False(ContentDatabase.Empty.TryGetItem("anything", out _));

        var errors = new List<string>();
        Assert.True(ContentValidation.Validate(ContentDatabase.Empty, errors));
        Assert.Empty(errors);
    }
}
