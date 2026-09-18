using System;
using System.Globalization;
using Shared.GameLogic.Components;

namespace GameServer.Server;

/// <summary>
/// The area-of-interest radius, parsed and validated at startup.
///
/// <para><b>Why a type for one float.</b> The radius is the single largest lever on
/// downstream bandwidth — the population inside a circle grows with the square of it, and
/// BENCHMARK.md Part IX puts bandwidth, not tick time, as the binding constraint — and it
/// was previously a compile-time constant with no way to change it per deployment. A value
/// that important must not be reachable through the "parse, and fall back to the default on
/// failure" idiom the cheaper knobs use: <c>GAMESERVER_AOI_RADIUS=5o</c> would then run a
/// fleet at 50 while its manifest said 5, and nothing would report it.</para>
/// </summary>
public sealed class AoiSettings
{
    /// <summary>Environment variable and flag this is configured by.</summary>
    public const string EnvVar = "GAMESERVER_AOI_RADIUS";

    /// <summary>
    /// Largest radius accepted. Not a gameplay limit — a bound that turns a fat-fingered
    /// exponent into a startup failure rather than into a spatial grid whose cell
    /// coordinates saturate and whose every query degenerates to the full scan.
    /// </summary>
    public const float MaxRadius = 100_000f;

    private AoiSettings(float radius, bool coversWholeMap)
    {
        Radius = radius;
        CoversWholeMap = coversWholeMap;
    }

    /// <summary>Effective radius, in world units.</summary>
    public float Radius { get; }

    /// <summary>
    /// True when the radius reaches every corner of the map from anywhere in it, so AOI
    /// filters nothing and every entity appears in every snapshot.
    /// </summary>
    /// <remarks>
    /// Legitimate for a small dungeon instance and a mistake on an open map, and the server
    /// cannot tell which it is looking at — so this is reported at startup rather than
    /// refused. An operator who meant it sees a line confirming what they asked for; one who
    /// did not sees the reason their bandwidth is what it is, at the moment it becomes true
    /// rather than after a load test.
    /// </remarks>
    public bool CoversWholeMap { get; }

    /// <summary>The compiled-in default, for callers with nothing configured.</summary>
    public static AoiSettings Default { get; } =
        new(GameConstants.DefaultAoiRadius, coversWholeMap: false);

    /// <summary>
    /// Parse and validate. <paramref name="raw"/> null or empty yields the default.
    /// </summary>
    /// <param name="raw">Flag or environment value, or null when unset.</param>
    /// <param name="mapWidth">Play area width, for the whole-map report.</param>
    /// <param name="mapHeight">Play area height, for the whole-map report.</param>
    /// <param name="settings">Validated settings on success.</param>
    /// <param name="error">Why the value was refused, on failure.</param>
    public static bool TryCreate(
        string? raw, float mapWidth, float mapHeight,
        out AoiSettings? settings, out string? error)
    {
        settings = null;
        error = null;

        if (string.IsNullOrWhiteSpace(raw))
        {
            settings = Build(GameConstants.DefaultAoiRadius, mapWidth, mapHeight);
            return true;
        }

        // InvariantCulture, deliberately: a container inherits whatever locale its base
        // image carries, and under a comma-decimal locale "12.5" parses as 125 on some
        // runtimes and fails on others. Neither is a thing a deployment should discover
        // from its bandwidth graph.
        if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float radius))
        {
            error = $"{EnvVar}={raw} is not a number. The radius is in world units and " +
                    "accepts a decimal point (InvariantCulture, so \".\" and never \",\").";
            return false;
        }

        if (!float.IsFinite(radius) || radius <= 0f)
        {
            error = $"{EnvVar}={raw} is not a positive finite radius. A zero or negative " +
                    "radius means no entity is ever in anyone's interest set, which is a " +
                    "server that replicates nothing rather than a server with a small AOI.";
            return false;
        }

        if (radius > MaxRadius)
        {
            error = $"{EnvVar}={raw} exceeds the {MaxRadius} unit ceiling. This bound exists " +
                    "to turn a misplaced exponent into a startup failure rather than into a " +
                    "spatial index that silently stops narrowing anything.";
            return false;
        }

        settings = Build(radius, mapWidth, mapHeight);
        return true;
    }

    private static AoiSettings Build(float radius, float mapWidth, float mapHeight)
    {
        // The worst case is opposite corners: if the radius spans the diagonal, an observer
        // anywhere sees everything.
        double diagonal = Math.Sqrt(((double)mapWidth * mapWidth) + ((double)mapHeight * mapHeight));
        return new AoiSettings(radius, radius >= diagonal);
    }

    public override string ToString() =>
        $"{Radius.ToString(CultureInfo.InvariantCulture)} units" +
        (CoversWholeMap ? " (covers the whole map: AOI filters nothing)" : string.Empty);
}
