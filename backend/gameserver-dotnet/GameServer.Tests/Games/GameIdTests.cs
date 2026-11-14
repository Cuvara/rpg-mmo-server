using System.Text.Json;
using GameServer.Games;

namespace GameServer.Tests.Games;

public class GameIdTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("a", false)]
    [InlineData("ab", true)]
    [InlineData("gs", false)]
    [InlineData("map", false)]
    [InlineData("dungeon", false)]
    [InlineData("other", false)]
    [InlineData("gsx", true)]
    [InlineData("maps", true)]
    [InlineData("dungeon1", true)]
    [InlineData("abcdefghijklmnopqrstuvwx", true)]
    [InlineData("abcdefghijklmnopqrstuvwxy", false)]
    [InlineData("a0", true)]
    [InlineData("0a", false)]
    [InlineData("Ab", false)]
    [InlineData("a-", false)]
    [InlineData("a_", false)]
    [InlineData("a:", false)]
    [InlineData("a/", false)]
    [InlineData("a.", false)]
    [InlineData("ab\n", false)]
    [InlineData("a b", false)]
    [InlineData("caf\u00e9", false)]
    [InlineData("ab\uff11", false)]
    public void GameIds(string? id, bool valid) => Assert.Equal(valid, GameId.IsValidGameId(id));

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("a", true)]
    [InlineData("ab", true)]
    [InlineData("abcdefghijklmnopqrstuvwx", true)]
    [InlineData("abcdefghijklmnopqrstuvwxy", true)]
    [InlineData("-", true)]
    [InlineData("_", true)]
    [InlineData(":", false)]
    [InlineData("/", false)]
    [InlineData(".", false)]
    [InlineData("map\n", false)]
    [InlineData("map name", false)]
    [InlineData("caf\u00e9", false)]
    [InlineData("ab\uff11", false)]
    public void MapIds(string? id, bool valid) => Assert.Equal(valid, GameId.IsValidMapId(id));

    [Fact]
    public void MapIdLengthBoundaries()
    {
        Assert.True(GameId.IsValidMapId(new string('a', 64)));
        Assert.False(GameId.IsValidMapId(new string('a', 65)));
    }

    [Theory]
    [InlineData(null, "legacy:/ .\n", true)]
    [InlineData("", "-", true)]
    [InlineData(null, "", false)]
    [InlineData(null, null, false)]
    [InlineData("foo", "foo-1", true)]
    [InlineData("map", "map-1", false)]
    [InlineData("gs", "gs-1", false)]
    [InlineData("foo", "foo-", false)]
    [InlineData("foo", "foobar-1", false)]
    [InlineData("foo", "Foo-1", false)]
    [InlineData("foo", "foo-:", true)]
    [InlineData("f", "f-1", false)]
    [InlineData("foo_", "foo_-1", false)]
    [InlineData("foo\n", "foo\n-1", false)]
    public void ServerIds(string? game, string? id, bool valid) =>
        Assert.Equal(valid, GameId.IsValidServerId(game, id));

    [Fact]
    public void DefaultGameHelpers()
    {
        Assert.Equal(GameId.DefaultGameId, GameId.GameOrDefault(null));
        Assert.Equal(GameId.DefaultGameId, GameId.GameOrDefault(""));
        Assert.Equal("foo", GameId.GameOrDefault("foo"));
        Assert.True(GameId.IsDefaultGame(null));
        Assert.True(GameId.IsDefaultGame(""));
        Assert.True(GameId.IsDefaultGame(GameId.DefaultGameId));
        Assert.False(GameId.IsDefaultGame("foo"));
        Assert.True(GameId.IsValidServerId(GameId.DefaultGameId, "legacy"));
    }

    [Fact]
    public void SharedFixture_MatchesConstantsAndValidators()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        string? fixture = null;
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, "backend", "shared", "testdata", "multigame", "identity.json");
            if (File.Exists(candidate)) { fixture = candidate; break; }
            directory = directory.Parent;
        }
        if (fixture == null) throw new FileNotFoundException("Shared multigame identity fixture not found above " + AppContext.BaseDirectory);

        using var document = JsonDocument.Parse(File.ReadAllText(fixture));
        var root = document.RootElement;
        Assert.Equal(root.GetProperty("default_game_id").GetString(), GameId.DefaultGameId);
        foreach (var section in new[] { "game_ids", "map_ids", "server_ids" })
        {
            foreach (var item in root.GetProperty(section).EnumerateArray())
            {
                var id = item.GetProperty("id").GetString();
                var game = section == "server_ids" ? item.GetProperty("game").GetString() : null;
                var expected = item.GetProperty("valid").GetBoolean();
                var actual = section switch
                {
                    "game_ids" => GameId.IsValidGameId(id),
                    "map_ids" => GameId.IsValidMapId(id),
                    _ => GameId.IsValidServerId(game, id),
                };
                Assert.True(expected == actual, $"{section} game={game} id={id}: expected {expected}, actual {actual}");
            }
        }
    }
}
