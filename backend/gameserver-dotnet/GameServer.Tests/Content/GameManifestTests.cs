using System.Text.Json;
using GameServer.Content;

namespace GameServer.Tests.Content;

public class GameManifestTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "game-manifest-" + Guid.NewGuid());

    public GameManifestTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, true);

    private void Write(string json) => File.WriteAllText(Path.Combine(_dir, GameManifest.FileName), json);

    [Theory]
    [InlineData("0.7.0")]
    [InlineData("0.8.1")]
    public void CompatibleManifestIsAccepted(string version)
    {
        Write($$"""{"$comment":"ignored","game_id":"cuvara","shared_gamelogic_version":"{{version}}"}""");
        var result = GameManifest.Check(_dir, "cuvara");
        Assert.Null(result.Refusal);
        Assert.Null(result.Warning);
        Assert.Equal("cuvara", result.GameId);
        Assert.Equal(version, result.Version);
    }

    [Theory]
    [InlineData("{", "cuvara")]
    [InlineData("null", "cuvara")]
    [InlineData("{}", "cuvara")]
    [InlineData("{\"game_id\":\"cuvara\"}", "cuvara")]
    [InlineData("{\"shared_gamelogic_version\":\"0.7.0\"}", "cuvara")]
    [InlineData("{\"game_id\":\"Bad!\",\"shared_gamelogic_version\":\"0.7.0\"}", "cuvara")]
    [InlineData("{\"game_id\":\"cuvara\",\"shared_gamelogic_version\":\"invalid\"}", "cuvara")]
    [InlineData("{\"game_id\":\"othergame\",\"shared_gamelogic_version\":\"0.7.0\"}", "cuvara")]
    [InlineData("{\"game_id\":\"cuvara\",\"shared_gamelogic_version\":\"1.0.0\"}", "cuvara")]
    public void InvalidOrIncompatibleManifestIsRefused(string json, string game)
    {
        Write(json);
        var result = GameManifest.Check(_dir, game);
        Assert.NotNull(result.Refusal);
        Assert.Contains(Path.Combine(_dir, GameManifest.FileName), result.Refusal);
    }

    [Fact]
    public void MismatchMessagesNameBothSides()
    {
        Write("{\"game_id\":\"othergame\",\"shared_gamelogic_version\":\"0.7.0\"}");
        var game = GameManifest.Check(_dir, "cuvara").Refusal!;
        Assert.Contains("othergame", game);
        Assert.Contains("cuvara", game);
        Write("{\"game_id\":\"cuvara\",\"shared_gamelogic_version\":\"1.0.0\"}");
        var version = GameManifest.Check(_dir, "cuvara").Refusal!;
        Assert.Contains("1.0.0", version);
        Assert.Contains(GameManifest.CompiledSharedGameLogicVersion, version);
    }

    [Theory]
    [InlineData("cuvara", true)]
    [InlineData("", true)]
    [InlineData("othergame", false)]
    public void AbsentManifestOnlyAllowsDefaultGame(string game, bool allowed)
    {
        var result = GameManifest.Check(_dir, game);
        Assert.Equal(allowed, result.Refusal == null);
        if (allowed)
        {
            Assert.NotNull(result.Warning);
            Assert.Contains("cuvara", result.Warning);
            Assert.Contains("old content tree", result.Warning);
        }
        else
            Assert.Contains(Path.Combine(_dir, GameManifest.FileName), result.Refusal!);
    }

    [Fact]
    public void CompiledVersionMatchesPackage()
    {
        string path = FindRepositoryFile("backend", "gameserver-dotnet", "Shared.GameLogic", "package.json");
        using var package = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(package.RootElement.GetProperty("version").GetString(), GameManifest.CompiledSharedGameLogicVersion);
    }

    [Fact]
    public void RepositoryManifestMatchesDefaultGame()
    {
        string path = FindRepositoryFile("backend", "content", GameManifest.FileName);
        var result = GameManifest.Check(Path.GetDirectoryName(path)!, "cuvara");
        Assert.Null(result.Refusal);
        Assert.Null(result.Warning);
        Assert.Equal("cuvara", result.GameId);
    }

    private static string FindRepositoryFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            string candidate = Path.Combine(new[] { directory.FullName }.Concat(parts).ToArray());
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Repository file not found above " + AppContext.BaseDirectory);
    }
}
