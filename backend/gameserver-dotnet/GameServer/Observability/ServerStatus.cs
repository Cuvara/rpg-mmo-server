using System.Text.Json.Serialization;
using GameServer.Server;

namespace GameServer.Observability;

/// <summary>
/// JSON response for the <c>/status</c> endpoint. Aggregates live server
/// state into a single object for client-side status panels and dev tools.
///
/// <para><b>Every rate on this object is named for the group it belongs to.</b> The server
/// runs three simulation groups at three different frequencies (ADR-13), so a single
/// unqualified "tick rate" is not a fact about it — it is a question with three answers,
/// and whichever one is printed, some reader is wrong by a factor. That ambiguity is not
/// hypothetical here: <c>/status</c> used to publish the legacy <c>--tick-rate</c> scalar,
/// which no deployment sets, so it reported the compiled-in default (15) on a server whose
/// prediction rate was 60 — see #144. The rule this file now follows is that a rate field
/// either names its group or states, in its own documentation, which group it is.</para>
/// </summary>
public sealed class ServerStatus
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    /// <summary>
    /// The rate, in Hz, at which player movement is integrated and the authoritative tick
    /// counter advances — i.e. <see cref="SimulationRates.MovementHz"/>, which is the
    /// critical group's rate.
    ///
    /// <para><b>This is defined to be the same number as the wire field
    /// <c>join_token_resp.tick_rate</c></b> (normative definition in <c>docs/API.md</c>),
    /// and for the same reason: it is the rate a client must predict at. The field keeps
    /// its name because a client already reads it under that name from both surfaces — the
    /// Unity DOTS sample polls <c>/status</c> and assigns <c>status.tick_rate</c>. Renaming
    /// it would have moved the defect rather than fixed it; what was wrong was the value,
    /// and that it was sourced from a variable nothing sets.</para>
    ///
    /// <para>It is <b>not</b> the snapshot rate. Snapshots are broadcast on the world
    /// group's cadence (<see cref="WorldHz"/>), which is four times slower at the default
    /// configuration. Anything sizing a jitter buffer or an interpolation delay wants
    /// <see cref="WorldHz"/>, not this.</para>
    /// </summary>
    [JsonPropertyName("tick_rate")]
    public int TickRate { get; set; }

    /// <summary>
    /// Critical-group frequency in Hz: player input, movement integration, combat. This is
    /// also the base tick timeline — <see cref="CurrentTick"/> counts these.
    /// Equal to <see cref="TickRate"/> by construction; published separately so a reader
    /// that wants "the critical rate" does not have to know that <c>tick_rate</c> happens
    /// to be it.
    /// </summary>
    [JsonPropertyName("sim_critical_hz")]
    public int CriticalHz { get; set; }

    /// <summary>
    /// World-group frequency in Hz: AI, spawning, despawning — <b>and the snapshot
    /// broadcast cadence</b>, which is what makes this the rate that governs bandwidth per
    /// client and what a client's interpolation buffer is sized against.
    /// </summary>
    [JsonPropertyName("sim_world_hz")]
    public int WorldHz { get; set; }

    /// <summary>Background-group frequency in Hz: work where a whole interval of delay is acceptable.</summary>
    [JsonPropertyName("sim_background_hz")]
    public int BackgroundHz { get; set; }

    /// <summary>
    /// The rate the base timeline is <b>actually</b> advancing at, in Hz, measured by the
    /// server over a short sliding window on the monotonic clock. Compare against
    /// <see cref="CriticalHz"/>, which is what was <i>configured</i>: a healthy server has
    /// these equal to within rounding.
    ///
    /// <para><b>This field exists so that nobody computes a rate themselves.</b> With a
    /// configured rate, a tick counter and an uptime on the same object and no measured
    /// rate, the obvious move is <c>current_tick / uptime_seconds</c> — and that is issue
    /// #147: a defect filed against a server running at exactly 60 Hz, propagated into an
    /// ADR and blamed for a client prediction defect, because the observer's clock was 10-17%
    /// fast (#153). An observer that must supply a clock will eventually supply a bad one.</para>
    ///
    /// <para><b>0 means "not measured yet"</b> — no window has completed, i.e. the process
    /// is younger than <see cref="Server.AchievedRateMeter.DefaultWindowSeconds"/>. It does
    /// not mean the loop has stopped; <c>current_tick</c> distinguishes those.</para>
    ///
    /// <para>Base timeline only. The world and background groups are exact integer divisors
    /// of it (`SimulationRates`), so publishing three measured rates would be publishing one
    /// measurement and two pieces of arithmetic — three things to drift instead of one. Per
    /// group, `rate(gameserver_sim_group_runs_total[...])` on `/metrics` is the measurement.</para>
    /// </summary>
    [JsonPropertyName("achieved_tick_hz")]
    public double AchievedTickHz { get; set; }

    /// <summary>
    /// The base tick counter — one increment per critical-group tick, i.e. per
    /// <see cref="CriticalHz"/>.
    /// </summary>
    [JsonPropertyName("current_tick")]
    public ulong CurrentTick { get; set; }

    [JsonPropertyName("players_online")]
    public int PlayersOnline { get; set; }

    /// <summary>
    /// The admission limit this server enforces (<c>GAMESERVER_CAPACITY</c>). Published
    /// because it is a limit an operator otherwise cannot observe: a client refused for
    /// capacity is refused on this number, and the same number is what the gateway reads
    /// out of the registry when it skips a full server.
    /// </summary>
    [JsonPropertyName("capacity")]
    public int Capacity { get; set; }

    [JsonPropertyName("entities")]
    public int Entities { get; set; }

    [JsonPropertyName("enemies_alive")]
    public int EnemiesAlive { get; set; }

    /// <summary>Inputs that carried an attack target id, since process start.</summary>
    [JsonPropertyName("attacks_received")]
    public long AttacksReceived { get; set; }

    /// <summary>Attacks whose target id did not resolve to a live entity.</summary>
    [JsonPropertyName("attacks_unresolved")]
    public long AttacksUnresolved { get; set; }

    /// <summary>Attacks refused by validation (range, cooldown, dead attacker or target).</summary>
    [JsonPropertyName("attacks_rejected")]
    public long AttacksRejected { get; set; }

    /// <summary>Attacks that dealt damage.</summary>
    [JsonPropertyName("attacks_accepted")]
    public long AttacksAccepted { get; set; }

    /// <summary>Accepted attacks that killed their target.</summary>
    [JsonPropertyName("attack_kills")]
    public long AttackKills { get; set; }

    /// <summary>
    /// Verbatim reason of the most recent rejection, or null if nothing has been rejected.
    /// One string, most-recent-wins: this is a diagnostic breadcrumb ("target out of
    /// range" — an interned constant since #249; the measured distance lives in the
    /// Debug-guarded rejection log), not a log.
    /// </summary>
    [JsonPropertyName("last_attack_rejection")]
    public string? LastAttackRejection { get; set; }

    [JsonPropertyName("redis")]
    public string Redis { get; set; } = "disconnected";

    [JsonPropertyName("postgres")]
    public string Postgres { get; set; } = "disconnected";

    /// <summary>
    /// Seconds since the process started, measured on a <b>monotonic</b> clock
    /// (<see cref="System.Diagnostics.Stopwatch"/>), not on wall time.
    ///
    /// <para>This matters more than it looks. <c>current_tick / uptime_seconds</c> is the
    /// obvious way for an observer to derive an achieved tick rate, and it used to divide a
    /// <c>CLOCK_MONOTONIC</c>-paced counter by a <c>CLOCK_REALTIME</c> duration. On a box
    /// whose realtime clock runs 10-17% fast — this one does, see #153 — that quotient
    /// reports a 60Hz loop as roughly 54Hz, which is exactly the non-defect filed as #147
    /// and closed. Both terms now come from the same monotonic source, so the quotient is
    /// an achieved rate rather than a measurement of the host's clock drift.</para>
    /// </summary>
    [JsonPropertyName("uptime_seconds")]
    public long UptimeSeconds { get; set; }

    /// <summary>
    /// Copy the resolved simulation rates onto this status object.
    ///
    /// <para>This exists so that the mapping from <see cref="SimulationRates"/> to the wire
    /// fields has one definition that a test can call. The previous mapping lived inline in
    /// <c>Program.cs</c>, which is a top-level statement file with no seam — which is
    /// precisely why it could source <c>tick_rate</c> from an unrelated variable for as
    /// long as it did without a test noticing.</para>
    /// </summary>
    public void ApplyRates(SimulationRates rates)
    {
        ArgumentNullException.ThrowIfNull(rates);
        TickRate = rates.MovementHz;
        CriticalHz = rates.CriticalHz;
        WorldHz = rates.WorldHz;
        BackgroundHz = rates.BackgroundHz;
    }
}

/// <summary>
/// AOT-safe JSON serialization context for <see cref="ServerStatus"/>.
/// </summary>
[JsonSerializable(typeof(ServerStatus))]
internal sealed partial class ServerStatusContext : JsonSerializerContext;
