using GameServer.Observability.Logging;
using Microsoft.Extensions.Logging;

namespace GameServer.Tests.Observability.Logging;

/// <summary>
/// <see cref="LogSettings.TryParse"/>: what <c>GAMESERVER_LOG_LEVEL</c> and
/// <c>GAMESERVER_LOG_FORMAT</c> accept, what they default to, and what the server refuses.
/// A refusal is fatal at startup like every other strict <c>GAMESERVER_*</c> knob.
/// </summary>
public class LogSettingsTests
{
    [Fact]
    public void Default_IsInformationAndJson()
    {
        LogSettings d = LogSettings.Default;
        Assert.Equal(LogLevel.Information, d.Level);
        Assert.Equal(LogFormat.Json, d.Format);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void UnsetOrBlank_SelectsTheDefaultForBothKnobs(string? raw)
    {
        Assert.True(LogSettings.TryParse(raw, raw, out LogSettings s, out string? error));
        Assert.Null(error);
        Assert.Equal(LogSettings.Default, s);
    }

    [Theory]
    [InlineData("Trace", LogLevel.Trace)]
    [InlineData("Debug", LogLevel.Debug)]
    [InlineData("Information", LogLevel.Information)]
    [InlineData("Warning", LogLevel.Warning)]
    [InlineData("Error", LogLevel.Error)]
    [InlineData("Critical", LogLevel.Critical)]
    [InlineData("trace", LogLevel.Trace)]
    [InlineData("DEBUG", LogLevel.Debug)]
    [InlineData("information", LogLevel.Information)]
    [InlineData("wArNiNg", LogLevel.Warning)]
    [InlineData("ERROR", LogLevel.Error)]
    [InlineData("critical", LogLevel.Critical)]
    [InlineData("  Warning  ", LogLevel.Warning)]
    public void EveryAcceptedLevelSpelling_Parses(string raw, LogLevel expected)
    {
        Assert.True(LogSettings.TryParse(raw, null, out LogSettings s, out string? error));
        Assert.Null(error);
        Assert.Equal(expected, s.Level);
        Assert.Equal(LogFormat.Json, s.Format);
    }

    [Theory]
    [InlineData("json", "Json")]
    [InlineData("JSON", "Json")]
    [InlineData("Json", "Json")]
    [InlineData("text", "Text")]
    [InlineData("TEXT", "Text")]
    [InlineData("  Text ", "Text")]
    public void EveryAcceptedFormatSpelling_Parses(string raw, string expected)
    {
        Assert.True(LogSettings.TryParse(null, raw, out LogSettings s, out string? error));
        Assert.Null(error);
        Assert.Equal(expected, s.Format.ToString());
        Assert.Equal(LogLevel.Information, s.Level);
    }

    [Theory]
    [InlineData("verbose")]
    [InlineData("Infos")]
    [InlineData("3")]
    [InlineData("0")]
    [InlineData("99")]
    [InlineData("-1")]
    [InlineData("None")]
    [InlineData("none")]
    [InlineData("Debug,Error")]
    public void RefusedLevel_ReturnsFalseAndNamesTheVariableAndTheValue(string raw)
    {
        bool ok = LogSettings.TryParse(raw, null, out LogSettings s, out string? error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("GAMESERVER_LOG_LEVEL", error, StringComparison.Ordinal);
        Assert.Contains(raw, error, StringComparison.Ordinal);
        Assert.Equal(LogSettings.Default, s);
    }

    [Theory]
    [InlineData("yaml")]
    [InlineData("json5")]
    [InlineData("1")]
    [InlineData("none")]
    [InlineData("json,text")]
    public void RefusedFormat_ReturnsFalseAndNamesTheVariableAndTheValue(string raw)
    {
        bool ok = LogSettings.TryParse(null, raw, out LogSettings s, out string? error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("GAMESERVER_LOG_FORMAT", error, StringComparison.Ordinal);
        Assert.Contains(raw, error, StringComparison.Ordinal);
        Assert.Equal(LogSettings.Default, s);
    }

    [Fact]
    public void ABadFormat_IsRefusedEvenWhenTheLevelIsValid()
    {
        Assert.False(LogSettings.TryParse("Debug", "xml", out LogSettings s, out string? error));
        Assert.Contains("GAMESERVER_LOG_FORMAT", error, StringComparison.Ordinal);
        Assert.Equal(LogSettings.Default, s);
    }

    [Fact]
    public void TheErrorTextListsTheAcceptedLevels()
    {
        Assert.False(LogSettings.TryParse("loud", null, out _, out string? error));
        foreach (string name in new[] { "Trace", "Debug", "Information", "Warning", "Error", "Critical" })
            Assert.Contains(name, error, StringComparison.Ordinal);
    }
}
