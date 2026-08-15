using System;

namespace GameServer.Server;

/// <summary>
/// Which rate group a system belongs to. A system declares its group; it never decides
/// its own frequency.
///
/// <para>The names are responsibilities, not frequencies, and that is deliberate: the Hz
/// behind each one is configuration (<see cref="SimulationRates"/>), so a group called
/// <c>Hz60</c> would be a lie the first time an operator changed
/// <c>SIM_CRITICAL_HZ</c>.</para>
/// </summary>
public enum SimulationGroup
{
    /// <summary>
    /// Latency-sensitive, prediction-relevant work: player input, movement integration,
    /// combat resolution. Runs on every base tick — this group <i>is</i> the base rate.
    /// </summary>
    Critical = 0,

    /// <summary>
    /// World simulation: AI, spawning, despawning, non-critical entity updates. Also the
    /// cadence the snapshot broadcast runs at, which is why demoting it changes what
    /// clients see rather than only what the server computes.
    /// </summary>
    World = 1,

    /// <summary>
    /// Background work where a delay of a whole interval is explicitly acceptable.
    /// At the default 5 Hz that delay is 200ms, which is a gameplay decision rather than
    /// a performance one — see the placement rules in <c>docs/DESIGN.md</c>.
    /// </summary>
    Background = 2,
}

/// <summary>
/// The three configured group frequencies, reduced to an integer tick timeline.
///
/// <para><b>Why integers and not accumulators.</b> A group could be scheduled by adding
/// wall-clock deltas into a float accumulator and firing when it crosses an interval. That
/// drifts, it makes "which tick did this run on" unanswerable, and it makes a replay of the
/// same inputs produce a different schedule. Instead every group is a divisor of one
/// canonical <see cref="BaseHz"/> counter: group boundaries are exact, decided by
/// <c>baseTick % every == 0</c>, and identical on every run and every machine. The tick
/// number is then a real identity, which is what input acknowledgement, reconciliation and
/// replay all need.</para>
///
/// <para><b>The base rate is derived, not configured.</b> It is the fastest configured
/// group, and every other group must divide it exactly. A configuration like
/// <c>60/25/5</c> has no integer timeline at 60Hz — 25 does not divide 60 — and its true
/// common base would be 300Hz, which would silently make the server run five times faster
/// than anyone asked for. That is rejected at startup rather than accommodated: see
/// <see cref="TryCreate"/>.</para>
/// </summary>
public sealed class SimulationRates
{
    /// <summary>Default critical-group frequency (Hz) when nothing is configured.</summary>
    public const int DefaultCriticalHz = 60;

    /// <summary>Default world-group frequency (Hz) when nothing is configured.</summary>
    public const int DefaultWorldHz = 15;

    /// <summary>Default background-group frequency (Hz) when nothing is configured.</summary>
    public const int DefaultBackgroundHz = 5;

    /// <summary>
    /// Upper bound on a configured rate. Not a hardware limit — a guard against a typo
    /// (<c>SIM_CRITICAL_HZ=6000</c>) turning into a busy loop that starves the network
    /// threads on the same box before anyone reads a metric.
    /// </summary>
    public const int MaxHz = 240;

    private SimulationRates(int criticalHz, int worldHz, int backgroundHz)
    {
        CriticalHz = criticalHz;
        WorldHz = worldHz;
        BackgroundHz = backgroundHz;
        BaseHz = criticalHz;
        CriticalEvery = 1;
        WorldEvery = criticalHz / worldHz;
        BackgroundEvery = criticalHz / backgroundHz;
    }

    /// <summary>Configured frequency of <see cref="SimulationGroup.Critical"/>, in Hz.</summary>
    public int CriticalHz { get; }

    /// <summary>Configured frequency of <see cref="SimulationGroup.World"/>, in Hz.</summary>
    public int WorldHz { get; }

    /// <summary>Configured frequency of <see cref="SimulationGroup.Background"/>, in Hz.</summary>
    public int BackgroundHz { get; }

    /// <summary>
    /// The canonical base tick frequency, in Hz. Equal to <see cref="CriticalHz"/>: the
    /// critical group runs on every base tick by definition.
    /// </summary>
    public int BaseHz { get; }

    /// <summary>Base ticks between two runs of the critical group. Always 1.</summary>
    public int CriticalEvery { get; }

    /// <summary>Base ticks between two runs of the world group.</summary>
    public int WorldEvery { get; }

    /// <summary>Base ticks between two runs of the background group.</summary>
    public int BackgroundEvery { get; }

    /// <summary>
    /// The default configuration (60/15/5), used where nothing supplies one.
    /// </summary>
    public static SimulationRates Default { get; } =
        new(DefaultCriticalHz, DefaultWorldHz, DefaultBackgroundHz);

    /// <summary>
    /// A configuration where every group runs at <paramref name="hz"/>, i.e. every group
    /// fires on every base tick.
    ///
    /// <para>This is what reproduces the single-rate server exactly: with one rate there is
    /// one timeline, the world group runs every base tick, and the snapshot cadence equals
    /// the simulation cadence — the pre-multi-rate behaviour, byte for byte. It is what the
    /// existing tick-rate-sensitive tests construct, which is why they did not have to be
    /// rewritten.</para>
    /// </summary>
    public static SimulationRates Uniform(int hz) => new(hz, hz, hz);

    /// <summary>How many base ticks separate two runs of <paramref name="group"/>.</summary>
    public int EveryFor(SimulationGroup group) => group switch
    {
        SimulationGroup.Critical => CriticalEvery,
        SimulationGroup.World => WorldEvery,
        SimulationGroup.Background => BackgroundEvery,
        _ => throw new ArgumentOutOfRangeException(nameof(group)),
    };

    /// <summary>The configured frequency of <paramref name="group"/>, in Hz.</summary>
    public int HzFor(SimulationGroup group) => group switch
    {
        SimulationGroup.Critical => CriticalHz,
        SimulationGroup.World => WorldHz,
        SimulationGroup.Background => BackgroundHz,
        _ => throw new ArgumentOutOfRangeException(nameof(group)),
    };

    /// <summary>
    /// The fixed timestep of <paramref name="group"/>, in seconds — the dt every system in
    /// that group must integrate with.
    ///
    /// <para>This is the whole point of grouping the systems rather than letting each one
    /// count ticks: a system is handed the dt of the rate it actually runs at, so
    /// <c>units per second</c> means the same thing in every group.</para>
    /// </summary>
    public float DeltaTimeFor(SimulationGroup group) => 1f / HzFor(group);

    /// <summary>Whether <paramref name="group"/> runs on the given base tick.</summary>
    /// <remarks>
    /// The base tick counter starts at 1 (the first tick the loop runs is tick 1), so the
    /// boundary test is <c>(tick - 1) % every == 0</c>: every group fires together on the
    /// first tick, which is the alignment the schedule diagram in <c>docs/DESIGN.md</c>
    /// shows and the one a test can reason about.
    /// </remarks>
    public bool RunsOn(SimulationGroup group, ulong baseTick)
    {
        int every = EveryFor(group);
        if (every <= 1) return true;
        return (baseTick - 1) % (ulong)every == 0;
    }

    /// <summary>
    /// Validate a configuration and reduce it to an integer timeline.
    /// </summary>
    /// <param name="criticalHz">Configured critical-group frequency.</param>
    /// <param name="worldHz">Configured world-group frequency.</param>
    /// <param name="backgroundHz">Configured background-group frequency.</param>
    /// <param name="rates">The reduced configuration, when this returns true.</param>
    /// <param name="error">
    /// An operator-facing explanation of why the configuration was rejected, naming the
    /// variable at fault and what would be acceptable. Null when this returns true.
    /// </param>
    /// <remarks>
    /// Every rejection here is a fail-fast at startup rather than a fallback to a default.
    /// A server that quietly substitutes 60 for an unusable 25 is a server whose measured
    /// behaviour does not match its configuration, which is the failure mode that makes a
    /// misconfiguration unfindable — the same argument the tick-rate wire gap (#93) rests
    /// on, one layer up.
    /// </remarks>
    public static bool TryCreate(
        int criticalHz,
        int worldHz,
        int backgroundHz,
        out SimulationRates? rates,
        out string? error)
    {
        rates = null;
        error = null;

        foreach ((string name, int hz) in new[]
                 {
                     ("SIM_CRITICAL_HZ", criticalHz),
                     ("SIM_WORLD_HZ", worldHz),
                     ("SIM_BACKGROUND_HZ", backgroundHz),
                 })
        {
            if (hz <= 0)
            {
                error = $"{name}={hz} is not a positive frequency. Every simulation group " +
                        "must run at least once per second.";
                return false;
            }

            if (hz > MaxHz)
            {
                error = $"{name}={hz} exceeds the {MaxHz}Hz ceiling. This bound exists to " +
                        "turn a typo into a startup failure rather than into a busy loop.";
                return false;
            }
        }

        if (worldHz > criticalHz)
        {
            error = $"SIM_WORLD_HZ={worldHz} is faster than SIM_CRITICAL_HZ={criticalHz}. " +
                    "The critical group is the base timeline and must be the fastest; a " +
                    "world group that outran it would mean the group names no longer " +
                    "describe what runs when.";
            return false;
        }

        if (backgroundHz > worldHz)
        {
            error = $"SIM_BACKGROUND_HZ={backgroundHz} is faster than SIM_WORLD_HZ={worldHz}. " +
                    "Background work must not outrun world simulation.";
            return false;
        }

        if (criticalHz % worldHz != 0)
        {
            error = $"SIM_WORLD_HZ={worldHz} does not divide SIM_CRITICAL_HZ={criticalHz} " +
                    $"exactly ({criticalHz} / {worldHz} is not a whole number). The " +
                    "scheduler runs on an integer tick timeline, so every group must be an " +
                    $"exact divisor of the base rate. Nearest usable values for " +
                    $"{criticalHz}Hz: {DivisorList(criticalHz)}.";
            return false;
        }

        if (criticalHz % backgroundHz != 0)
        {
            error = $"SIM_BACKGROUND_HZ={backgroundHz} does not divide " +
                    $"SIM_CRITICAL_HZ={criticalHz} exactly. Nearest usable values for " +
                    $"{criticalHz}Hz: {DivisorList(criticalHz)}.";
            return false;
        }

        rates = new SimulationRates(criticalHz, worldHz, backgroundHz);
        return true;
    }

    /// <summary>
    /// The divisors of <paramref name="baseHz"/>, for an error message that tells an
    /// operator what to type instead of only what was wrong.
    /// </summary>
    private static string DivisorList(int baseHz)
    {
        var parts = new List<int>();
        for (int i = 1; i <= baseHz; i++)
        {
            if (baseHz % i == 0) parts.Add(i);
        }
        return string.Join(", ", parts);
    }

    /// <summary>A one-line summary for the startup banner.</summary>
    public override string ToString() =>
        $"critical={CriticalHz}Hz (base), world={WorldHz}Hz (every {WorldEvery}), " +
        $"background={BackgroundHz}Hz (every {BackgroundEvery})";
}
