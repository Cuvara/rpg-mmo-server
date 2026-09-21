using System;
using System.Globalization;
using System.Text;
using Shared.GameLogic.Components;

namespace GameServer.Scaffolding;

/// <summary>
/// Every knob the enemy AI has, parsed and validated once at startup.
///
/// <para><b>Why a type and not the constants it replaced.</b> <c>EnemyAiTuning</c> was a
/// block of <c>const</c>s, so the only way to change what the fight feels like was to
/// recompile. The owner's complaint about the DOTS sample — "a player hitting a few
/// enemies that trickle into the middle" — is a complaint about four of those numbers, and
/// a number that can only be changed by a release cannot be tuned by playing.</para>
///
/// <para><b>Why every value parses strictly.</b> Same reason as
/// <c>GAMESERVER_FIELD_DELTA</c> and <see cref="GameServer.Server.AoiSettings"/>: the
/// "TryParse ? value : default" idiom turns a typo into a server that silently runs the
/// default while its manifest says something else. For a population cap that is the
/// difference between the battle the operator configured and the trickle they were trying
/// to leave behind — and nothing on the wire would tell them which one they got. An
/// unrecognised value is a configuration error and exits non-zero.</para>
///
/// <para><b>Why the defaults are what they are.</b> They are exactly today's numbers at
/// <i>zero players</i>, and an obvious improvement at one or more. The population and wave
/// size are <c>base + perPlayer x players</c>, so an empty server is bit-for-bit the
/// server that shipped (cap 30, wave 2) and the numbers only grow once there is somebody
/// to fight. See <see cref="EffectiveMaxEnemies"/>.</para>
/// </summary>
public sealed class EnemyAiSettings
{
    // ── Environment variable names ───────────────────────────────────────────

    public const string EnvMaxEnemies = "GAMESERVER_ENEMY_MAX";
    public const string EnvMaxEnemiesPerPlayer = "GAMESERVER_ENEMY_MAX_PER_PLAYER";
    public const string EnvWaveSize = "GAMESERVER_ENEMY_WAVE_SIZE";
    public const string EnvWaveSizePerPlayer = "GAMESERVER_ENEMY_WAVE_SIZE_PER_PLAYER";
    public const string EnvWaveIntervalSec = "GAMESERVER_ENEMY_WAVE_INTERVAL";
    public const string EnvSpawnDistance = "GAMESERVER_ENEMY_SPAWN_DISTANCE";
    public const string EnvMinSpawnDistance = "GAMESERVER_ENEMY_MIN_SPAWN_DISTANCE";
    public const string EnvContactRange = "GAMESERVER_ENEMY_CONTACT_RANGE";
    public const string EnvHp = "GAMESERVER_ENEMY_HP";
    public const string EnvAttack = "GAMESERVER_ENEMY_ATTACK";
    public const string EnvDefense = "GAMESERVER_ENEMY_DEFENSE";
    public const string EnvSpeed = "GAMESERVER_ENEMY_SPEED";
    public const string EnvChase = "GAMESERVER_ENEMY_CHASE";

    /// <summary>
    /// Ceiling on the <i>effective</i> population, i.e. on
    /// <see cref="EffectiveMaxEnemies"/> however it was reached. Not a gameplay limit — a
    /// bound that turns a misplaced zero into a startup failure rather than into a world
    /// whose every snapshot is enemies and whose AOI scan never terminates in a tick.
    /// </summary>
    public const int PopulationCeiling = 20_000;

    /// <summary>Largest wave accepted, for the same reason.</summary>
    public const int WaveCeiling = 2_000;

    /// <summary>
    /// Largest distance accepted for any of the radial knobs. Bounds the same class of
    /// typo that <see cref="GameServer.Server.AoiSettings.MaxRadius"/> bounds.
    /// </summary>
    public const float MaxDistance = 100_000f;

    private EnemyAiSettings(
        int maxEnemies, int maxEnemiesPerPlayer,
        int waveSize, int waveSizePerPlayer,
        float waveIntervalSec,
        float spawnDistance, float minSpawnDistance, float contactRange,
        int hp, int attack, int defense, float speed,
        bool chase, MapBounds bounds)
    {
        MaxEnemies = maxEnemies;
        MaxEnemiesPerPlayer = maxEnemiesPerPlayer;
        WaveSize = waveSize;
        WaveSizePerPlayer = waveSizePerPlayer;
        WaveIntervalSec = waveIntervalSec;
        SpawnDistance = spawnDistance;
        MinSpawnDistance = minSpawnDistance;
        ContactRange = contactRange;
        Hp = hp;
        Attack = attack;
        Defense = defense;
        Speed = speed;
        Chase = chase;
        Bounds = bounds;
        ContactRangeSq = contactRange * contactRange;
        MinSpawnDistanceSq = minSpawnDistance * minSpawnDistance;
        DespawnRadiusSq = EnemyAiTuning.DespawnRadius * EnemyAiTuning.DespawnRadius;
    }

    // ── Population ───────────────────────────────────────────────────────────

    /// <summary>Enemies allowed with nobody online. The pre-change cap.</summary>
    public int MaxEnemies { get; }

    /// <summary>Extra enemies allowed per live player, added to <see cref="MaxEnemies"/>.</summary>
    public int MaxEnemiesPerPlayer { get; }

    /// <summary>Enemies per wave with nobody online. The pre-change wave.</summary>
    public int WaveSize { get; }

    /// <summary>Extra enemies per wave per live player.</summary>
    public int WaveSizePerPlayer { get; }

    /// <summary>Seconds between waves.</summary>
    public float WaveIntervalSec { get; }

    // ── Placement ────────────────────────────────────────────────────────────

    /// <summary>
    /// World units from the anchor an enemy spawns at: from a randomly chosen live player
    /// when there is one, and from the origin when there is not (which is the ring the
    /// pre-change spawner always used).
    /// </summary>
    public float SpawnDistance { get; }

    /// <summary>
    /// No enemy is placed within this distance of <i>any</i> live player if it can be
    /// avoided. <see cref="SpawnDistance"/> only guarantees separation from the player the
    /// spawn was anchored to; in a crowd the placement can still land in somebody else's
    /// lap, and being hit by something that materialised on top of you is the one spawn
    /// outcome a player reads as a bug.
    /// </summary>
    public float MinSpawnDistance { get; }

    /// <summary>
    /// How close an enemy closes to its target before it stops advancing. Inside
    /// <c>GameConstants.AttackRange</c> so the player can hit what is standing on them,
    /// and non-zero so a ring of chasers does not jitter across the target's exact
    /// position.
    /// </summary>
    public float ContactRange { get; }

    // ── Stats ────────────────────────────────────────────────────────────────

    public int Hp { get; }
    public int Attack { get; }
    public int Defense { get; }
    public float Speed { get; }

    /// <summary>
    /// Whether enemies chase the nearest live player. Off restores the pre-change
    /// behaviour in full: walk to the origin and despawn on arrival.
    /// </summary>
    public bool Chase { get; }

    /// <summary>Play area, used to keep a player-anchored spawn on the map.</summary>
    public MapBounds Bounds { get; }

    public float ContactRangeSq { get; }
    public float MinSpawnDistanceSq { get; }
    public float DespawnRadiusSq { get; }

    /// <summary>The compiled-in defaults, for callers with nothing configured.</summary>
    public static EnemyAiSettings Default { get; } = new(
        maxEnemies: EnemyAiTuning.MaxEnemies,
        maxEnemiesPerPlayer: EnemyAiTuning.MaxEnemiesPerPlayer,
        waveSize: EnemyAiTuning.EnemiesPerWave,
        waveSizePerPlayer: EnemyAiTuning.EnemiesPerWavePerPlayer,
        waveIntervalSec: EnemyAiTuning.WaveIntervalSec,
        spawnDistance: EnemyAiTuning.SpawnRadius,
        minSpawnDistance: EnemyAiTuning.MinSpawnDistance,
        contactRange: EnemyAiTuning.ContactRange,
        hp: EnemyAiTuning.EnemyHp,
        attack: EnemyAiTuning.EnemyAttack,
        defense: EnemyAiTuning.EnemyDefense,
        speed: EnemyAiTuning.EnemySpeed,
        chase: EnemyAiTuning.ChaseByDefault,
        bounds: MapBounds.Default);

    /// <summary>
    /// Population cap in force for <paramref name="livePlayers"/> players.
    ///
    /// <para>Additive rather than multiplicative on purpose. A pure per-player budget
    /// (<c>perPlayer x players</c>) is zero on an empty server, which is a different server
    /// from the one that shipped and would silently delete the only thing a solo tester
    /// sees; a pure flat cap is what made the fight threadbare as soon as a second player
    /// joined, because two players then shared thirty enemies. Additive is the only shape
    /// that is unchanged at zero and scales with the crowd.</para>
    /// </summary>
    public int EffectiveMaxEnemies(int livePlayers)
    {
        if (livePlayers < 0) livePlayers = 0;

        // long, then clamp: MaxEnemiesPerPlayer x players overflows int well inside the
        // values the parser accepts, and a negative cap reads as "spawn nothing" — a
        // population that silently collapses is exactly the failure the ceiling exists to
        // turn into a bounded number.
        long cap = (long)MaxEnemies + ((long)MaxEnemiesPerPlayer * livePlayers);
        return cap > PopulationCeiling ? PopulationCeiling : (int)cap;
    }

    /// <summary>Wave size in force for <paramref name="livePlayers"/> players.</summary>
    /// <remarks>
    /// Scales with the population for a reason that is arithmetic, not taste: filling a
    /// cap of 75 two at a time at 1.5s per wave takes 56 seconds, so a bigger cap alone
    /// buys a fight that only exists after the first minute. The wave has to grow with the
    /// cap or the cap is decoration.
    /// </remarks>
    public int EffectiveWaveSize(int livePlayers)
    {
        if (livePlayers < 0) livePlayers = 0;

        long wave = (long)WaveSize + ((long)WaveSizePerPlayer * livePlayers);
        return wave > WaveCeiling ? WaveCeiling : (int)wave;
    }

    /// <summary>
    /// Parse and validate from a name-to-value lookup (the process environment, in
    /// production). Any unrecognised value fails the whole call rather than falling back.
    /// </summary>
    /// <param name="lookup">
    /// Returns the configured value for a variable name, or null when it is unset. An
    /// empty string must be reported as null (which <c>Program.Env</c> does), so that
    /// <c>VAR=</c> means "unset" consistently with every other knob here.
    /// </param>
    /// <param name="bounds">Play area, for keeping player-anchored spawns on the map.</param>
    /// <param name="settings">Validated settings on success.</param>
    /// <param name="error">Why the value was refused, on failure.</param>
    public static bool TryCreate(
        Func<string, string?> lookup, MapBounds bounds,
        out EnemyAiSettings? settings, out string? error)
    {
        ArgumentNullException.ThrowIfNull(lookup);

        settings = null;
        error = null;

        if (!TryInt(lookup, EnvMaxEnemies, EnemyAiTuning.MaxEnemies, 0, PopulationCeiling, out int maxEnemies, out error) ||
            !TryInt(lookup, EnvMaxEnemiesPerPlayer, EnemyAiTuning.MaxEnemiesPerPlayer, 0, PopulationCeiling, out int maxPerPlayer, out error) ||
            !TryInt(lookup, EnvWaveSize, EnemyAiTuning.EnemiesPerWave, 0, WaveCeiling, out int waveSize, out error) ||
            !TryInt(lookup, EnvWaveSizePerPlayer, EnemyAiTuning.EnemiesPerWavePerPlayer, 0, WaveCeiling, out int wavePerPlayer, out error) ||
            !TryInt(lookup, EnvHp, EnemyAiTuning.EnemyHp, 1, 1_000_000, out int hp, out error) ||
            !TryInt(lookup, EnvAttack, EnemyAiTuning.EnemyAttack, 0, 1_000_000, out int attack, out error) ||
            !TryInt(lookup, EnvDefense, EnemyAiTuning.EnemyDefense, 0, 1_000_000, out int defense, out error))
        {
            return false;
        }

        if (!TryFloat(lookup, EnvWaveIntervalSec, EnemyAiTuning.WaveIntervalSec, 0.01f, 3_600f, out float interval, out error) ||
            !TryFloat(lookup, EnvSpawnDistance, EnemyAiTuning.SpawnRadius, 0.01f, MaxDistance, out float spawnDistance, out error) ||
            !TryFloat(lookup, EnvMinSpawnDistance, EnemyAiTuning.MinSpawnDistance, 0f, MaxDistance, out float minSpawn, out error) ||
            !TryFloat(lookup, EnvContactRange, EnemyAiTuning.ContactRange, 0f, MaxDistance, out float contact, out error) ||
            !TryFloat(lookup, EnvSpeed, EnemyAiTuning.EnemySpeed, 0.01f, MaxDistance, out float speed, out error))
        {
            return false;
        }

        if (!TryBool(lookup, EnvChase, EnemyAiTuning.ChaseByDefault, out bool chase, out error))
        {
            return false;
        }

        // Cross-value check. A minimum that meets or exceeds the spawn distance cannot be
        // satisfied by any player-anchored placement at all — every candidate is rejected,
        // the spawner falls through to its unavoidable-crowding path on every single
        // enemy, and the knob that was set to stop enemies landing on players has instead
        // disabled the check that stops them. Refused rather than clamped: clamping would
        // mean the server ran numbers the operator never wrote.
        if (minSpawn >= spawnDistance)
        {
            error = $"{EnvMinSpawnDistance}={minSpawn.ToString(CultureInfo.InvariantCulture)} is not " +
                    $"less than {EnvSpawnDistance}={spawnDistance.ToString(CultureInfo.InvariantCulture)}. " +
                    "An enemy is placed at the spawn distance from one player and then rejected if it " +
                    "landed within the minimum of any player, so a minimum at or above the spawn " +
                    "distance rejects every placement including the one it was measured from.";
            return false;
        }

        settings = new EnemyAiSettings(
            maxEnemies, maxPerPlayer, waveSize, wavePerPlayer, interval,
            spawnDistance, minSpawn, contact,
            hp, attack, defense, speed, chase, bounds);
        return true;
    }

    /// <summary>
    /// One line for the start-up banner and for <c>/status</c>. Renders the values that
    /// are in force, including the two derived populations an operator would otherwise
    /// have to compute, because "base 30 + 45 per player" is the configuration and
    /// "75 with one player online" is the behaviour.
    /// </summary>
    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append(CultureInfo.InvariantCulture, $"chase={(Chase ? "on" : "off")}");
        sb.Append(CultureInfo.InvariantCulture, $" max={MaxEnemies}+{MaxEnemiesPerPlayer}/player");
        sb.Append(CultureInfo.InvariantCulture, $" wave={WaveSize}+{WaveSizePerPlayer}/player");
        sb.Append(CultureInfo.InvariantCulture, $" every {WaveIntervalSec}s");
        sb.Append(CultureInfo.InvariantCulture, $" spawn@{SpawnDistance}");
        sb.Append(CultureInfo.InvariantCulture, $" min={MinSpawnDistance}");
        sb.Append(CultureInfo.InvariantCulture, $" contact={ContactRange}");
        sb.Append(CultureInfo.InvariantCulture, $" hp={Hp} atk={Attack} def={Defense} speed={Speed}");
        return sb.ToString();
    }

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

        // InvariantCulture, deliberately, and for the reason AoiSettings gives: a container
        // inherits whatever locale its base image carries, and under a comma-decimal locale
        // "12.5" parses as 125 on some runtimes and fails on others.
        if (!float.TryParse(raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            value = fallback;
            error = $"{name}={raw} is not a number. Values are in world units or seconds and " +
                    "accept a decimal point (InvariantCulture, so \".\" and never \",\").";
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

    /// <summary>
    /// The <c>GAMESERVER_FIELD_DELTA</c> parse, reused verbatim in spirit: the recognised
    /// spellings and nothing else. A toggle whose unrecognised values default to off gives
    /// the operator who wrote <c>=of</c> the feature they were turning off.
    /// </summary>
    private static bool TryBool(
        Func<string, string?> lookup, string name, bool fallback,
        out bool value, out string? error)
    {
        error = null;
        string? raw = lookup(name);

        switch (raw?.Trim().ToLowerInvariant())
        {
            case null or "":
                value = fallback;
                return true;
            case "1" or "true" or "on" or "yes":
                value = true;
                return true;
            case "0" or "false" or "off" or "no":
                value = false;
                return true;
            default:
                value = fallback;
                error = $"invalid {name}={raw}: expected one of on/true/1/yes or off/false/0/no";
                return false;
        }
    }
}
