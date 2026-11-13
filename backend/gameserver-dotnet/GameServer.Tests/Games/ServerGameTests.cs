using GameServer.Games;

namespace GameServer.Tests.Games;

public class ServerGameTests
{
    [Theory]
    [InlineData(null, "map_01")]
    [InlineData(null, "gs-dotnet-map_01")]
    [InlineData(null, "gs-0123456789")]
    [InlineData(null, "map-servers-dotnet-k8s-abcde-xyz12")]
    [InlineData("", "map_01")]
    [InlineData("", "gs-dotnet-map_01")]
    [InlineData("", "gs-0123456789")]
    [InlineData("", "map-servers-dotnet-k8s-abcde-xyz12")]
    [InlineData("cuvara", "map_01")]
    [InlineData("cuvara", "gs-dotnet-map_01")]
    [InlineData("cuvara", "gs-0123456789")]
    [InlineData("cuvara", "map-servers-dotnet-k8s-abcde-xyz12")]
    public void DefaultGame_PreservesExistingServerIds(string? configured, string serverId)
    {
        Assert.True(ServerGame.TryResolve(configured, serverId, out string gameId, out string? error));
        Assert.Equal(GameId.DefaultGameId, gameId);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("Foo")]
    [InlineData("f")]
    [InlineData("gs")]
    [InlineData("map")]
    [InlineData("dungeon")]
    [InlineData("abcdefghijklmnopqrstuvwxy")]
    [InlineData("a-b")]
    public void InvalidGame_IsRefusedWithConfigurationRule(string configured)
    {
        Assert.False(ServerGame.TryResolve(configured, "map_01", out _, out string? error));
        Assert.NotNull(error);
        Assert.Contains("GAMESERVER_GAME_ID", error);
        Assert.Contains($"'{configured}'", error);
        Assert.Contains("^[a-z][a-z0-9]{1,23}$", error);
        Assert.Contains("gs, map or dungeon", error);
    }

    [Theory]
    [InlineData("foo", "foo-map01")]
    public void NonDefaultGame_AcceptsPrefixedServerId(string configured, string serverId)
    {
        Assert.True(ServerGame.TryResolve(configured, serverId, out string gameId, out string? error));
        Assert.Equal(configured, gameId);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("map_01")]
    [InlineData("gs-dotnet-map_01")]
    [InlineData("foo-")]
    [InlineData("foomap")]
    public void NonDefaultGame_RefusesServerIdWithoutPrefixAndSuffix(string serverId)
    {
        Assert.False(ServerGame.TryResolve("foo", serverId, out string gameId, out string? error));
        Assert.Equal("foo", gameId);
        Assert.NotNull(error);
        Assert.Contains($"'{serverId}'", error);
        Assert.Contains("'foo-'", error);
        Assert.Contains("non-empty suffix", error);
        Assert.Contains("--server-id", error);
        Assert.Contains("GAMESERVER_ID", error);
        Assert.Contains("POD_NAME", error);
        Assert.Contains("gs-<hex>", error);
        Assert.Contains("fleets must be named 'foo-...'", error);
    }
}
