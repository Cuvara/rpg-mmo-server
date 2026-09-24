using GameServer.Server;

namespace GameServer.Tests.Server;

public class ImportanceSettingsTests
{
    private static Func<string, string?> NoOverrides => _ => null;

    private static Func<string, string?> Override(params (string key, string value)[] pairs) =>
        k =>
        {
            foreach ((string key, string value) in pairs)
            {
                if (key == k) return value;
            }
            return null;
        };

    private static ImportanceSettings Ok(string? profile, Func<string, string?>? ov = null)
    {
        Assert.True(ImportanceSettings.TryCreate(profile, ov ?? NoOverrides,
            out ImportanceSettings? s, out string? err), err);
        return s!;
    }

    private static string Refused(string? profile, Func<string, string?>? ov = null)
    {
        Assert.False(ImportanceSettings.TryCreate(profile, ov ?? NoOverrides,
            out ImportanceSettings? s, out string? err));
        Assert.Null(s);
        Assert.False(string.IsNullOrWhiteSpace(err));
        return err!;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("legacy")]
    [InlineData("LEGACY")]
    [InlineData("off")]
    public void DefaultAndLegacy_AreTheePreImportanceOrdering(string? profile)
    {
        ImportanceSettings s = Ok(profile);
        Assert.False(s.Enabled);
        Assert.True(s.Weights.AllZero);
        Assert.Equal("legacy", s.Profile);
    }

    [Fact]
    public void TheShippedDefaultIsOff()
    {
        Assert.False(ImportanceSettings.Default.Enabled);
    }

    [Fact]
    public void Balanced_TurnsOnOnlyTheFactorsWithADataSource()
    {
        ImportanceSettings s = Ok("balanced");
        Assert.True(s.Enabled);
        Assert.True(s.Weights.Distance > 0f);
        Assert.True(s.Weights.Change > 0f);
        Assert.True(s.Weights.Type > 0f);
        Assert.True(s.Weights.Combat > 0f);

        // The seven with no input stay at zero even in the tuned profile.
        Assert.Equal(0f, s.Weights.Party);
        Assert.Equal(0f, s.Weights.Pvp);
        Assert.Equal(0f, s.Weights.Boss);
        Assert.Equal(0f, s.Weights.Quest);
        Assert.Equal(0f, s.Weights.Visibility);
        Assert.Equal(0f, s.Weights.Zone);
        Assert.Equal(0f, s.Weights.Interaction);
    }

    /// <summary>
    /// Change must outrank the rest: HP and action are the only two fields a client can
    /// neither interpolate nor dead-reckon, so an entity carrying an untold one is the
    /// update that cannot be reconstructed from anything already on the client.
    /// </summary>
    [Fact]
    public void Balanced_RanksChangeAboveTheOthers()
    {
        ImportanceSettings s = Ok("balanced");
        Assert.True(s.Weights.Change > s.Weights.Combat);
        Assert.True(s.Weights.Combat > s.Weights.Type);
        Assert.True(s.Weights.Type > s.Weights.Distance);
    }

    [Theory]
    [InlineData("aggressive")]
    [InlineData("balanced2")]
    [InlineData("yes")]
    public void UnknownProfile_IsRefused(string profile)
        => Assert.Contains("not a known profile", Refused(profile));

    [Fact]
    public void Overrides_ApplyOnTopOfTheProfile()
    {
        ImportanceSettings s = Ok("balanced",
            Override(("GAMESERVER_IMPORTANCE_W_DISTANCE", "12.5")));
        Assert.Equal(12.5f, s.Weights.Distance);
        Assert.Equal("custom", s.Profile);
    }

    [Fact]
    public void OverridingEverythingToZero_ReportsItselfAsLegacy()
    {
        ImportanceSettings s = Ok("balanced", Override(
            ("GAMESERVER_IMPORTANCE_W_DISTANCE", "0"),
            ("GAMESERVER_IMPORTANCE_W_CHANGE", "0"),
            ("GAMESERVER_IMPORTANCE_W_TYPE", "0"),
            ("GAMESERVER_IMPORTANCE_W_COMBAT", "0")));

        Assert.False(s.Enabled);
        Assert.Equal("legacy", s.Profile);
    }

    [Theory]
    [InlineData("2,5")]     // comma decimal must not become 25
    [InlineData("lots")]
    [InlineData("-1")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void BadOverride_IsRefused(string value)
        => Assert.Contains("non-negative finite", Refused("balanced",
            Override(("GAMESERVER_IMPORTANCE_W_CHANGE", value))));

    /// <summary>
    /// Weighting a factor this server has no data for is refused, not ignored. Accepting it
    /// would let a manifest describe a policy the server cannot run, and leave whoever wrote
    /// it reading their own configuration as if it had taken effect.
    /// </summary>
    [Theory]
    [InlineData("PARTY")]
    [InlineData("PVP")]
    [InlineData("BOSS")]
    [InlineData("QUEST")]
    [InlineData("VISIBILITY")]
    [InlineData("ZONE")]
    [InlineData("INTERACTION")]
    public void WeightingAFactorWithNoDataSource_IsRefused(string factor)
    {
        string err = Refused("balanced", Override(($"GAMESERVER_IMPORTANCE_W_{factor}", "5")));
        Assert.Contains("no data source", err);
        Assert.Contains(factor, err);
    }

    [Fact]
    public void ToString_NamesTheProfileAndItsNumbers()
    {
        Assert.Contains("pre-importance", Ok("legacy").ToString());
        string s = Ok("balanced").ToString();
        Assert.Contains("balanced", s);
        Assert.Contains("change=", s);
    }
}
