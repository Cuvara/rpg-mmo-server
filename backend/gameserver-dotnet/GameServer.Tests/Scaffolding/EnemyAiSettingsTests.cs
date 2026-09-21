using System.Globalization;
using GameServer.Scaffolding;
using Shared.GameLogic.Components;

namespace GameServer.Tests.Scaffolding;

/// <summary>
/// The enemy AI's configuration surface: what it accepts, what it refuses, and what it
/// does with nothing set.
///
/// <para><b>Why refusal is the bulk of the file.</b> The defaults are covered by the
/// characterization suite, which runs against them. What is not covered anywhere else is
/// the failure the strict parse exists to prevent — a server that reads
/// <c>GAMESERVER_ENEMY_MAX=3O</c> (letter O), silently runs at 30, and reports nothing
/// wrong anywhere. A lenient parser passes every test a strict one does; only the refusal
/// cases tell them apart.</para>
/// </summary>
public class EnemyAiSettingsTests
{
    private static bool Parse(
        IDictionary<string, string?> env, out EnemyAiSettings? settings, out string? error) =>
        EnemyAiSettings.TryCreate(
            n => env.TryGetValue(n, out string? v) ? v : null,
            MapBounds.Default, out settings, out error);

    private static EnemyAiSettings ParseOk(params (string Name, string Value)[] vars)
    {
        var env = new Dictionary<string, string?>();
        foreach ((string name, string value) in vars) env[name] = value;

        Assert.True(Parse(env, out EnemyAiSettings? s, out string? err), err);
        Assert.Null(err);
        return s!;
    }

    // ── Defaults ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Nothing configured is today's server, value for value. Asserted against literals
    /// rather than against <c>EnemyAiTuning</c>, on the same deliberate-duplication
    /// contract the characterization suite uses: a change to a constant must fail here
    /// rather than be followed silently.
    /// </summary>
    [Fact]
    public void NothingConfigured_IsThePreChangeTuning()
    {
        EnemyAiSettings s = ParseOk();

        Assert.Equal(30, s.MaxEnemies);
        Assert.Equal(2, s.WaveSize);
        Assert.Equal(1.5f, s.WaveIntervalSec);
        Assert.Equal(13.0f, s.SpawnDistance);
        Assert.Equal(16, s.Hp);
        Assert.Equal(5, s.Attack);
        Assert.Equal(2, s.Defense);
        Assert.Equal(2.5f, s.Speed);

        // And the two knobs that are new: chase on, and a population that grows with the
        // crowd. Both are inert on an empty server, which is the property that let the
        // characterization suite stay untouched.
        Assert.True(s.Chase);
        Assert.Equal(30, s.EffectiveMaxEnemies(0));
        Assert.Equal(2, s.EffectiveWaveSize(0));
    }

    /// <summary>
    /// An empty value means unset, consistently with every other knob here — because
    /// <c>Program.Env</c> maps an empty environment variable to null, and a deployment
    /// that writes <c>VAR=</c> in a manifest means "I am not setting this".
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AbsentOrEmpty_TakesTheDefault(string? raw)
    {
        var env = new Dictionary<string, string?> { [EnemyAiSettings.EnvMaxEnemies] = raw };

        Assert.True(Parse(env, out EnemyAiSettings? s, out string? err), err);
        Assert.Equal(30, s!.MaxEnemies);
    }

    // ── Accepted values ──────────────────────────────────────────────────────

    [Fact]
    public void EveryKnobIsReadable()
    {
        EnemyAiSettings s = ParseOk(
            (EnemyAiSettings.EnvMaxEnemies, "120"),
            (EnemyAiSettings.EnvMaxEnemiesPerPlayer, "10"),
            (EnemyAiSettings.EnvWaveSize, "9"),
            (EnemyAiSettings.EnvWaveSizePerPlayer, "3"),
            (EnemyAiSettings.EnvWaveIntervalSec, "0.75"),
            (EnemyAiSettings.EnvSpawnDistance, "22.5"),
            (EnemyAiSettings.EnvMinSpawnDistance, "11"),
            (EnemyAiSettings.EnvContactRange, "2"),
            (EnemyAiSettings.EnvHp, "40"),
            (EnemyAiSettings.EnvAttack, "7"),
            (EnemyAiSettings.EnvDefense, "3"),
            (EnemyAiSettings.EnvSpeed, "4.25"),
            (EnemyAiSettings.EnvChase, "off"));

        Assert.Equal(120, s.MaxEnemies);
        Assert.Equal(10, s.MaxEnemiesPerPlayer);
        Assert.Equal(9, s.WaveSize);
        Assert.Equal(3, s.WaveSizePerPlayer);
        Assert.Equal(0.75f, s.WaveIntervalSec);
        Assert.Equal(22.5f, s.SpawnDistance);
        Assert.Equal(11f, s.MinSpawnDistance);
        Assert.Equal(2f, s.ContactRange);
        Assert.Equal(40, s.Hp);
        Assert.Equal(7, s.Attack);
        Assert.Equal(3, s.Defense);
        Assert.Equal(4.25f, s.Speed);
        Assert.False(s.Chase);
    }

    /// <summary>
    /// Every spelling <c>GAMESERVER_FIELD_DELTA</c> accepts, because an operator who
    /// learned the vocabulary on one toggle will use it on the next.
    /// </summary>
    [Theory]
    [InlineData("on", true)]
    [InlineData("ON", true)]
    [InlineData("true", true)]
    [InlineData("1", true)]
    [InlineData("yes", true)]
    [InlineData(" off ", false)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("no", false)]
    public void ChaseAcceptsTheSameSpellingsAsTheOtherToggles(string raw, bool expected)
    {
        Assert.Equal(expected, ParseOk((EnemyAiSettings.EnvChase, raw)).Chase);
    }

    /// <summary>
    /// Decimals are InvariantCulture wherever they appear, for the reason
    /// <c>AoiSettings</c> gives: a container inherits its base image's locale, and a
    /// comma-decimal locale turns "12.5" into 125 on some runtimes.
    /// </summary>
    [Fact]
    public void DecimalsAreInvariantCulture()
    {
        CultureInfo original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");

            Assert.Equal(12.5f, ParseOk((EnemyAiSettings.EnvSpawnDistance, "12.5")).SpawnDistance);

            var env = new Dictionary<string, string?>
            {
                [EnemyAiSettings.EnvSpawnDistance] = "12,5",
            };
            Assert.False(Parse(env, out _, out string? err));
            Assert.Contains(EnemyAiSettings.EnvSpawnDistance, err);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }

    // ── Refusals ─────────────────────────────────────────────────────────────

    /// <summary>
    /// The whole point of the strict parse. Each row is a value a lenient parser would
    /// have swallowed, leaving a server running numbers nobody wrote.
    /// </summary>
    [Theory]
    // Not a number at all — the classic typo, letter O for zero.
    [InlineData(EnemyAiSettings.EnvMaxEnemies, "3O")]
    [InlineData(EnemyAiSettings.EnvMaxEnemies, "lots")]
    [InlineData(EnemyAiSettings.EnvMaxEnemies, "30.5")]
    [InlineData(EnemyAiSettings.EnvWaveSize, "two")]
    [InlineData(EnemyAiSettings.EnvHp, "-")]
    // Out of range, either direction.
    [InlineData(EnemyAiSettings.EnvMaxEnemies, "-1")]
    [InlineData(EnemyAiSettings.EnvMaxEnemies, "999999")]
    [InlineData(EnemyAiSettings.EnvMaxEnemiesPerPlayer, "-5")]
    [InlineData(EnemyAiSettings.EnvWaveSize, "-1")]
    [InlineData(EnemyAiSettings.EnvHp, "0")]       // an enemy that is born dead
    [InlineData(EnemyAiSettings.EnvAttack, "-1")]
    [InlineData(EnemyAiSettings.EnvDefense, "-1")]
    // Floats: unparseable, non-finite, and degenerate.
    [InlineData(EnemyAiSettings.EnvSpeed, "fast")]
    [InlineData(EnemyAiSettings.EnvSpeed, "0")]     // an enemy that never arrives
    [InlineData(EnemyAiSettings.EnvSpeed, "-2.5")]
    [InlineData(EnemyAiSettings.EnvSpeed, "NaN")]
    [InlineData(EnemyAiSettings.EnvSpeed, "Infinity")]
    [InlineData(EnemyAiSettings.EnvWaveIntervalSec, "0")]  // a wave every tick, forever
    [InlineData(EnemyAiSettings.EnvWaveIntervalSec, "-1")]
    [InlineData(EnemyAiSettings.EnvSpawnDistance, "0")]    // spawns inside the player
    [InlineData(EnemyAiSettings.EnvSpawnDistance, "1e9")]
    [InlineData(EnemyAiSettings.EnvMinSpawnDistance, "-1")]
    [InlineData(EnemyAiSettings.EnvContactRange, "-1")]
    // The toggle: anything but the recognised spellings.
    [InlineData(EnemyAiSettings.EnvChase, "of")]
    [InlineData(EnemyAiSettings.EnvChase, "enabled")]
    [InlineData(EnemyAiSettings.EnvChase, "2")]
    public void UnusableValuesAreRefused_NotDefaulted(string name, string raw)
    {
        var env = new Dictionary<string, string?> { [name] = raw };

        Assert.False(Parse(env, out EnemyAiSettings? s, out string? err),
            $"{name}={raw} was accepted");
        Assert.Null(s);
        Assert.NotNull(err);
        Assert.Contains(name, err);
    }

    /// <summary>
    /// The one refusal that needs two values to see. A minimum separation at or above the
    /// spawn distance rejects every candidate placement, including the one it was measured
    /// from — so the knob set to keep enemies off players would instead have disabled the
    /// check that keeps them off.
    /// </summary>
    [Theory]
    [InlineData("13", "13")]
    [InlineData("13", "20")]
    [InlineData("5", "5.0001")]
    public void MinimumSeparationAtOrAboveTheSpawnDistanceIsRefused(string spawn, string min)
    {
        var env = new Dictionary<string, string?>
        {
            [EnemyAiSettings.EnvSpawnDistance] = spawn,
            [EnemyAiSettings.EnvMinSpawnDistance] = min,
        };

        Assert.False(Parse(env, out _, out string? err));
        Assert.Contains(EnemyAiSettings.EnvMinSpawnDistance, err);
        Assert.Contains(EnemyAiSettings.EnvSpawnDistance, err);
    }

    [Fact]
    public void AMinimumBelowTheSpawnDistanceIsFine()
    {
        EnemyAiSettings s = ParseOk(
            (EnemyAiSettings.EnvSpawnDistance, "13"),
            (EnemyAiSettings.EnvMinSpawnDistance, "12.999"));

        Assert.Equal(12.999f, s.MinSpawnDistance);
    }

    // ── Derived populations ──────────────────────────────────────────────────

    [Theory]
    [InlineData(0, 30, 2)]
    [InlineData(1, 75, 8)]
    [InlineData(2, 120, 14)]
    [InlineData(10, 480, 62)]
    public void DefaultsScaleAdditivelyWithPlayers(int players, int cap, int wave)
    {
        EnemyAiSettings s = EnemyAiSettings.Default;

        Assert.Equal(cap, s.EffectiveMaxEnemies(players));
        Assert.Equal(wave, s.EffectiveWaveSize(players));
    }

    /// <summary>
    /// The ceilings bound the derived value however it was reached. Each input below is
    /// individually inside the accepted range, and their product is not.
    /// </summary>
    [Fact]
    public void CeilingsBoundTheDerivedPopulation()
    {
        EnemyAiSettings s = ParseOk(
            (EnemyAiSettings.EnvMaxEnemies, "20000"),
            (EnemyAiSettings.EnvMaxEnemiesPerPlayer, "20000"),
            (EnemyAiSettings.EnvWaveSize, "2000"),
            (EnemyAiSettings.EnvWaveSizePerPlayer, "2000"));

        Assert.Equal(EnemyAiSettings.PopulationCeiling, s.EffectiveMaxEnemies(1_000_000));
        Assert.Equal(EnemyAiSettings.WaveCeiling, s.EffectiveWaveSize(1_000_000));
    }

    /// <summary>
    /// A negative player count is not reachable from the systems, and is clamped anyway:
    /// the alternative is a cap below the base that reads as "spawn nothing", which is a
    /// population that silently collapses rather than a bounded number.
    /// </summary>
    [Fact]
    public void ANegativePlayerCountIsClampedRatherThanSubtracted()
    {
        Assert.Equal(30, EnemyAiSettings.Default.EffectiveMaxEnemies(-4));
        Assert.Equal(2, EnemyAiSettings.Default.EffectiveWaveSize(-4));
    }

    // ── Reporting ────────────────────────────────────────────────────────────

    /// <summary>
    /// <c>/status</c> publishes <see cref="EnemyAiSettings.ToString"/> verbatim, so every
    /// knob has to appear in it. A value that is configurable but invisible is one an
    /// operator has to read the manifest for — and an allocated Agones GameServer keeps
    /// the environment it was created with, so the manifest may no longer describe it.
    /// </summary>
    [Fact]
    public void ToStringCarriesEveryKnob()
    {
        string rendered = ParseOk(
            (EnemyAiSettings.EnvMaxEnemies, "111"),
            (EnemyAiSettings.EnvMaxEnemiesPerPlayer, "222"),
            (EnemyAiSettings.EnvWaveSize, "33"),
            (EnemyAiSettings.EnvWaveSizePerPlayer, "44"),
            (EnemyAiSettings.EnvWaveIntervalSec, "5.5"),
            (EnemyAiSettings.EnvSpawnDistance, "66.5"),
            (EnemyAiSettings.EnvMinSpawnDistance, "7.5"),
            (EnemyAiSettings.EnvContactRange, "8.5"),
            (EnemyAiSettings.EnvHp, "99"),
            (EnemyAiSettings.EnvAttack, "12"),
            (EnemyAiSettings.EnvDefense, "13"),
            (EnemyAiSettings.EnvSpeed, "14.5"),
            (EnemyAiSettings.EnvChase, "off")).ToString();

        foreach (string expected in new[]
                 {
                     "chase=off", "111", "222", "33", "44", "5.5",
                     "66.5", "7.5", "8.5", "99", "12", "13", "14.5",
                 })
        {
            Assert.Contains(expected, rendered);
        }
    }

    // ── Enemy-side combat ────────────────────────────────────────────────────

    /// <summary>The defaults of the combat knobs, against literals for the reason above.</summary>
    [Fact]
    public void NothingConfigured_HasEnemyCombatOnAndCapped()
    {
        EnemyAiSettings s = ParseOk();

        Assert.True(s.AttacksEnabled);
        Assert.Equal(3, s.AttackersPerTarget);
        Assert.Equal(0.5f, s.AttackIntervalSec);
        Assert.True(s.RespawnPlayers);
    }

    /// <summary>
    /// Every combat knob refuses a value it does not recognise, rather than falling back.
    ///
    /// <para>This is the table that tells a strict parser from a lenient one, and the
    /// consequence is not cosmetic for two of these rows in particular:
    /// <c>ATTACKERS_PER_TARGET=3O</c> silently running at 3 is survivable, but
    /// <c>ATTACKS=of</c> silently running with attacks ON is an operator who disabled the
    /// feature for a demo and did not, and <c>PLAYER_RESPAWN=no1</c> silently running with
    /// respawn on is a rule the operator believes is off deciding what happens to a dead
    /// player.</para>
    /// </summary>
    [Theory]
    [InlineData(EnemyAiSettings.EnvAttacksEnabled, "of")]
    [InlineData(EnemyAiSettings.EnvAttacksEnabled, "yes please")]
    [InlineData(EnemyAiSettings.EnvRespawnPlayers, "no1")]
    [InlineData(EnemyAiSettings.EnvRespawnPlayers, "maybe")]
    [InlineData(EnemyAiSettings.EnvAttackersPerTarget, "3O")]
    [InlineData(EnemyAiSettings.EnvAttackersPerTarget, "-1")]
    [InlineData(EnemyAiSettings.EnvAttackersPerTarget, "1001")]
    [InlineData(EnemyAiSettings.EnvAttackIntervalSec, "half")]
    [InlineData(EnemyAiSettings.EnvAttackIntervalSec, "0,5")]
    [InlineData(EnemyAiSettings.EnvAttackIntervalSec, "0")]
    [InlineData(EnemyAiSettings.EnvAttackIntervalSec, "NaN")]
    public void ACombatKnobWithAnUnrecognisedValue_IsRefused(string name, string value)
    {
        var env = new Dictionary<string, string?> { [name] = value };

        Assert.False(Parse(env, out EnemyAiSettings? s, out string? err));
        Assert.Null(s);
        Assert.NotNull(err);
        Assert.Contains(name, err);
    }

    /// <summary>
    /// The combat knobs accept the values they document, including a cap of zero — which
    /// is a meaningful setting ("enemies never land a hit") and not a mistake to refuse.
    /// </summary>
    [Fact]
    public void CombatKnobsAcceptTheirDocumentedValues()
    {
        EnemyAiSettings s = ParseOk(
            (EnemyAiSettings.EnvAttacksEnabled, "off"),
            (EnemyAiSettings.EnvAttackersPerTarget, "0"),
            (EnemyAiSettings.EnvAttackIntervalSec, "2.5"),
            (EnemyAiSettings.EnvRespawnPlayers, "no"));

        Assert.False(s.AttacksEnabled);
        Assert.Equal(0, s.AttackersPerTarget);
        Assert.Equal(2.5f, s.AttackIntervalSec);
        Assert.False(s.RespawnPlayers);
    }

    /// <summary>
    /// The survivability arithmetic, computed by the type rather than by the reader.
    ///
    /// <para>This is the number an operator has to get right before a demo and the number
    /// the feature is answerable for, so it is a function with a table rather than a
    /// paragraph in a document that cannot be run.</para>
    /// </summary>
    [Theory]
    // cap, interval, damage per hit  ->  damage per second
    [InlineData(3, 0.5f, 1, 6f)]        // the defaults against a default player: 100 HP / 6 = 16.7s
    [InlineData(3, 0.5f, 15, 90f)]      // GAMESERVER_ENEMY_ATTACK=20 against defense 5: 1.1s
    [InlineData(1, 1.0f, 1, 1f)]        // the gentlest useful setting: 100s
    [InlineData(20, 0.5f, 1, 40f)]      // a cap raised without thinking: 2.5s
    [InlineData(0, 0.5f, 1, 0f)]        // the cap off
    public void WorstCaseDamagePerSecond_IsWhatTheKnobsSay(
        int cap, float interval, int damagePerHit, float expected)
    {
        EnemyAiSettings s = ParseOk(
            (EnemyAiSettings.EnvAttackersPerTarget, cap.ToString(CultureInfo.InvariantCulture)),
            (EnemyAiSettings.EnvAttackIntervalSec, interval.ToString(CultureInfo.InvariantCulture)));

        Assert.Equal(expected, s.WorstCaseDamagePerSecond(damagePerHit), 3);
    }

    /// <summary>With attacks off the worst case is zero, whatever the cap says.</summary>
    [Fact]
    public void WorstCaseDamagePerSecond_IsZeroWithAttacksOff()
    {
        EnemyAiSettings s = ParseOk(
            (EnemyAiSettings.EnvAttacksEnabled, "off"),
            (EnemyAiSettings.EnvAttackersPerTarget, "50"));

        Assert.Equal(0f, s.WorstCaseDamagePerSecond(15));
    }

    /// <summary>
    /// The status line carries the combat tuning, both ways round. An operator reading
    /// <c>/status</c> on an already-allocated pod has no other honest source for whether
    /// enemies fight back on it — the manifest describes the fleet, not the pod.
    /// </summary>
    [Fact]
    public void ToStringCarriesTheCombatTuning()
    {
        Assert.Contains("attacks=4/target every 0.25s",
            ParseOk(
                (EnemyAiSettings.EnvAttackersPerTarget, "4"),
                (EnemyAiSettings.EnvAttackIntervalSec, "0.25")).ToString());

        Assert.Contains("attacks=off",
            ParseOk((EnemyAiSettings.EnvAttacksEnabled, "off")).ToString());

        Assert.Contains("respawn=off",
            ParseOk((EnemyAiSettings.EnvRespawnPlayers, "off")).ToString());
    }

    /// <summary>
    /// Rendered under a comma-decimal culture too: the status line is read by operators
    /// and scraped by tooling, and a server whose base image sets de-DE must not publish
    /// "spawn@13,0" where every other pod publishes "spawn@13".
    /// </summary>
    [Fact]
    public void ToStringIsCultureInvariant()
    {
        CultureInfo original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new CultureInfo("de-DE");
            Assert.Contains("spawn@13.5", ParseOk((EnemyAiSettings.EnvSpawnDistance, "13.5")).ToString());
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }
}
