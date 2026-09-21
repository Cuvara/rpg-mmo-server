using System;
using System.Globalization;
using Shared.GameLogic.Components;

namespace GameServer.Scaffolding;

/// <summary>
/// Synthetic player ("bot") configuration, parsed and validated once at startup.
///
/// <para><b>Why bots exist.</b> A battle royale is a crowd of <i>players</i> as much as a
/// crowd of enemies, and this project can only put three real clients on a map. Three
/// players in a 210-enemy world still reads as thin, because the thing that makes a fight
/// feel populated is other people fighting beside you. Bots are player-type entities that
/// move and attack through the ordinary input path, so a real client sees them exactly as
/// it sees anybody else and no client change is needed.</para>
///
/// <para><b>Default OFF.</b> <c>GAMESERVER_BOTS</c> unset or <c>0</c> spawns nothing and
/// the phase is not even constructed, so a normal deployment is untouched. This is a
/// development and demo tool: bots are not persisted, hold no connection, count against no
/// capacity, and are not an AI anybody should ship to players.</para>
///
/// <para>Same strict parse as the <c>GAMESERVER_ENEMY_*</c> family, for the same reason —
/// see <see cref="EnemyAiSettings"/>.</para>
/// </summary>
public sealed class BotSettings
{
    public const string EnvCount = "GAMESERVER_BOTS";
    public const string EnvSpread = "GAMESERVER_BOT_SPREAD";
    public const string EnvHp = "GAMESERVER_BOT_HP";
    public const string EnvAttack = "GAMESERVER_BOT_ATTACK";
    public const string EnvDefense = "GAMESERVER_BOT_DEFENSE";
    public const string EnvSpeed = "GAMESERVER_BOT_SPEED";
    public const string EnvEngageRange = "GAMESERVER_BOT_ENGAGE_RANGE";

    /// <summary>
    /// Most bots accepted. Each one is a full player entity in every snapshot that
    /// reaches it, so this bounds a number whose cost is quadratic in the worst case
    /// (bots x viewers), not a number whose cost is linear.
    /// </summary>
    public const int MaxCount = 2_000;

    /// <summary>Default id prefix. Diagnostics only — identity is the tag, never the id.</summary>
    public const string IdPrefix = "bot-";

    private BotSettings(
        int count, float spread, int hp, int attack, int defense, float speed,
        float engageRange, MapBounds bounds)
    {
        Count = count;
        Spread = spread;
        Hp = hp;
        Attack = attack;
        Defense = defense;
        Speed = speed;
        EngageRange = engageRange;
        Bounds = bounds;
        EngageRangeSq = engageRange * engageRange;
    }

    /// <summary>How many bots to spawn. 0 is off, and is the default.</summary>
    public int Count { get; }

    /// <summary>
    /// Radius of the disc bots are scattered over at startup, about the origin.
    ///
    /// <para>Deliberately <b>not</b> a tight cluster. Bots are what the enemy spawner
    /// anchors on, so where the bots are is where the fight is: scattering them over a
    /// wide disc is what turns one busy point into a populated map, and clustering them
    /// would rebuild the conveyor belt this work removed with extra steps.</para>
    /// </summary>
    public float Spread { get; }

    public int Hp { get; }
    public int Attack { get; }
    public int Defense { get; }
    public float Speed { get; }

    /// <summary>
    /// How far a bot will travel to engage an enemy. Beyond it the bot wanders instead of
    /// crossing the map, which keeps bots spread out — a "nearest enemy anywhere" rule
    /// collapses every bot onto whichever corner is busiest.
    /// </summary>
    public float EngageRange { get; }

    public float EngageRangeSq { get; }

    /// <summary>Play area. Bots are kept inside it; they are players, and players are clamped.</summary>
    public MapBounds Bounds { get; }

    /// <summary>Off: no bots, no phase, no behaviour change of any kind.</summary>
    public bool Enabled => Count > 0;

    /// <summary>
    /// Parse and validate. Follows <see cref="EnemyAiSettings.TryCreate"/> exactly,
    /// including the rule that an unrecognised value is refused rather than defaulted.
    /// </summary>
    public static bool TryCreate(
        Func<string, string?> lookup, MapBounds bounds,
        out BotSettings? settings, out string? error)
    {
        ArgumentNullException.ThrowIfNull(lookup);

        settings = null;
        error = null;

        if (!TryInt(lookup, EnvCount, 0, 0, MaxCount, out int count, out error) ||
            !TryInt(lookup, EnvHp, 100, 1, 1_000_000, out int hp, out error) ||
            !TryInt(lookup, EnvAttack, 10, 0, 1_000_000, out int attack, out error) ||
            !TryInt(lookup, EnvDefense, 5, 0, 1_000_000, out int defense, out error))
        {
            return false;
        }

        if (!TryFloat(lookup, EnvSpread, 120f, 0f, 100_000f, out float spread, out error) ||
            !TryFloat(lookup, EnvSpeed, 4f, 0.01f, 100_000f, out float speed, out error) ||
            !TryFloat(lookup, EnvEngageRange, 40f, 0.01f, 100_000f, out float engage, out error))
        {
            return false;
        }

        settings = new BotSettings(count, spread, hp, attack, defense, speed, engage, bounds);
        return true;
    }

    /// <summary>The compiled-in defaults: <b>off</b>.</summary>
    public static BotSettings Disabled { get; } =
        new(0, 120f, 100, 10, 5, 4f, 40f, MapBounds.Default);

    public override string ToString() =>
        Enabled
            ? string.Create(CultureInfo.InvariantCulture,
                $"{Count} bots spread={Spread} engage={EngageRange} hp={Hp} atk={Attack} def={Defense} speed={Speed}")
            : "off";

    private static bool TryInt(
        Func<string, string?> lookup, string name, int fallback, int min, int max,
        out int value, out string? error)
    {
        error = null;
        string? raw = lookup(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            value = fallback;
            return true;
        }

        if (!int.TryParse(raw.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            value = fallback;
            error = $"{name}={raw} is not a whole number.";
            return false;
        }

        if (value < min || value > max)
        {
            error = $"{name}={raw} is outside the accepted range {min}..{max}.";
            value = fallback;
            return false;
        }

        return true;
    }

    private static bool TryFloat(
        Func<string, string?> lookup, string name, float fallback, float min, float max,
        out float value, out string? error)
    {
        error = null;
        string? raw = lookup(name);
        if (string.IsNullOrWhiteSpace(raw))
        {
            value = fallback;
            return true;
        }

        if (!float.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            value = fallback;
            error = $"{name}={raw} is not a number (InvariantCulture, so \".\" and never \",\").";
            return false;
        }

        if (!float.IsFinite(value) || value < min || value > max)
        {
            error = $"{name}={raw} is not a finite value in the accepted range " +
                    $"{min.ToString(CultureInfo.InvariantCulture)}..{max.ToString(CultureInfo.InvariantCulture)}.";
            value = fallback;
            return false;
        }

        return true;
    }
}
