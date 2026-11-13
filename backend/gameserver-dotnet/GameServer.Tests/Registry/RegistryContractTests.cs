using System.Text.Json;
using GameServer.Games;
using GameServer.Registry;
using StackExchange.Redis;

namespace GameServer.Tests.Registry;

/// <summary>Pure cross-language contract checks; no Redis connection is needed.</summary>
public class RegistryContractTests
{
    private static string FixturePath(string name)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(directory.FullName, "backend", "shared", "testdata", "multigame", name);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new FileNotFoundException($"Shared multigame fixture {name} not found above {AppContext.BaseDirectory}");
    }

    // Read the shared fixture's explicit default game rather than duplicating its id here.
    private static string FixtureDefaultGame()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FixturePath("registry.json")));
        return document.RootElement.GetProperty("map_index_keys").EnumerateArray()
            .Single(item => item.GetProperty("key").GetString() == "servers:map:map_01"
                && item.GetProperty("game").GetString() != "")
            .GetProperty("game").GetString()!;
    }

    [Fact]
    public void HashFields_MatchTheSharedFixtureInGoOrder()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FixturePath("registry.json")));
        var expected = document.RootElement.GetProperty("hash_fields").EnumerateArray()
            .Select(field => field.GetString()!).ToArray();
        Assert.Equal(expected, RedisServerRegistry.HashFields);
        Assert.Equal(8, RedisServerRegistry.HashFields.Length);
    }

    [Fact]
    public void MapIndexKeys_MatchTheSharedFixture()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(FixturePath("registry.json")));
        foreach (var item in document.RootElement.GetProperty("map_index_keys").EnumerateArray())
        {
            Assert.Equal(item.GetProperty("key").GetString(), RedisServerRegistry.MapIndexKey(
                item.GetProperty("game").GetString(), item.GetProperty("map_id").GetString()!));
        }
        Assert.Equal("servers:map:map_01", RedisServerRegistry.MapIndexKey(null, "map_01"));
        Assert.Equal("servers:map:map_01", RedisServerRegistry.MapKey("map_01"));
    }

    [Fact]
    public void RegisterLua_MatchesTheSharedScriptIncludingTabs()
    {
        string expected = File.ReadAllText(FixturePath("register.lua")).Replace("\r\n", "\n").Trim();
        string actual = RedisServerRegistry.RegisterLua.Replace("\r\n", "\n").Trim();
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void ServerInfo_ExistingPositionalConstructorsDefaultToTheSharedGame()
    {
        var withoutIdentity = new ServerInfo("gs-1", "map_01", "localhost:9200", "kcp", 100, 0);
        var withIdentity = new ServerInfo("gs-1", "map_01", "localhost:9200", "kcp", 100, 0, "public-key");
        Assert.Equal(FixtureDefaultGame(), withoutIdentity.GameId);
        Assert.Equal(FixtureDefaultGame(), withIdentity.GameId);
        Assert.Equal(GameId.DefaultGameId, FixtureDefaultGame());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(GameId.DefaultGameId)]
    public void BuildRegisterArgs_WritesTheExplicitDefaultGame(string? gameId)
    {
        var info = new ServerInfo("gs-1", "map_01", "localhost:9200", "kcp", 100, 7, "public-key");
        if (gameId != null) info = info with { GameId = gameId };
        var args = RedisServerRegistry.BuildRegisterArgs(info, TimeSpan.FromMilliseconds(15001));

        Assert.Equal(10, args.Length);
        Assert.Equal(FixtureDefaultGame(), args[0].ToString());
        Assert.Equal(15001L, (long)args[1]);
        Assert.Equal(FixtureDefaultGame(), args[9].ToString());
    }

    [Fact]
    public void BuildRegisterArgs_KeepsTheGoFieldOrderForAnotherGame()
    {
        var info = new ServerInfo("foo-1", "map_01", "localhost:9200", "kcp", 100, 7, "public-key", "foo");
        var args = RedisServerRegistry.BuildRegisterArgs(info, TimeSpan.FromMilliseconds(15001));

        Assert.Equal(new RedisValue[]
        {
            GameId.DefaultGameId, 15001L, "foo-1", "map_01", "localhost:9200", "kcp", 100, 7, "public-key", "foo",
        }, args);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void FromHashEntries_MissingOrEmptyGameMeansTheDefault(string? gameId)
    {
        var entries = new List<HashEntry>
        {
            new("server_id", "gs-legacy"), new("map_id", "map_01"), new("addr", "localhost:9200"),
            new("transport", "kcp"), new("capacity", "100"), new("player_count", "7"),
        };
        if (gameId != null) entries.Add(new HashEntry("game_id", gameId));

        var info = RedisServerRegistry.FromHashEntries(entries.ToArray());
        Assert.Equal(FixtureDefaultGame(), info.GameId);
        Assert.Equal("", info.IdentityKey);
        Assert.Equal("gs-legacy", info.ServerId);
        Assert.Equal(100, info.Capacity);
        Assert.Equal(7, info.PlayerCount);
    }

    [Fact]
    public void FromHashEntries_ReadsAllEightFields()
    {
        var info = RedisServerRegistry.FromHashEntries(
        [
            new("server_id", "foo-1"), new("map_id", "map_01"), new("addr", "localhost:9200"),
            new("transport", "kcp"), new("capacity", "100"), new("player_count", "7"),
            new("identity_key", "public-key"), new("game_id", "foo"),
        ]);
        Assert.Equal(new ServerInfo("foo-1", "map_01", "localhost:9200", "kcp", 100, 7, "public-key", "foo"), info);
    }
}
