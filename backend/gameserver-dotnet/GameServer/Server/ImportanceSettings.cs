using System;
using System.Globalization;
using GameServer.Snapshot;

namespace GameServer.Server;

/// <summary>
/// The replication-importance weights a deployment runs with, parsed and validated at
/// startup.
///
/// <para><b>Why a profile and not eleven knobs.</b> Eleven independent floats is a
/// configuration surface nobody tunes correctly and everybody can typo; a named profile is
/// what an operator actually chooses. The individual weights remain overridable for
/// experiments, and an override of a factor that has no data source is <b>refused</b>
/// rather than accepted and ignored — accepting it would let a manifest claim a policy the
/// server cannot run.</para>
/// </summary>
public sealed class ImportanceSettings
{
    public const string EnvVar = "GAMESERVER_IMPORTANCE";

    private ImportanceSettings(string profile, ReplicationImportance.Weights weights)
    {
        Profile = profile;
        Weights = weights;
    }

    /// <summary>Profile name as configured, for the banner and <c>/status</c>.</summary>
    public string Profile { get; }

    public ReplicationImportance.Weights Weights { get; }

    /// <summary>True when no factor can contribute, i.e. the pre-importance ordering.</summary>
    public bool Enabled => !Weights.AllZero;

    /// <summary>
    /// The shipped default: **off**.
    /// </summary>
    /// <remarks>
    /// Not timidity. Reordering which entity is shed first when the downlink budget bites is
    /// a real behavioural change, and BENCHMARK.md Part XIII measured the budget as a tail
    /// cap that does not engage at a load this server is known to handle — so switching it
    /// on by default would change behaviour in a case nobody has measured, to no measured
    /// benefit. It is turned on when a benchmark says what it buys, which is the same rule
    /// the AOI radius and the downlink budget both shipped under.
    /// </remarks>
    public static ImportanceSettings Default { get; } =
        new("legacy", ReplicationImportance.Weights.Legacy);

    /// <summary>
    /// The tuned profile. Only factors with a data source on this server are non-zero.
    /// </summary>
    /// <remarks>
    /// Scales are relative and the absolute numbers mean nothing on their own — the score is
    /// a sort key, never a threshold. The ordering they encode is the argument:
    /// <list type="bullet">
    /// <item><description><b>Change (10)</b> dominates. Health and action are the two fields
    /// a client can neither interpolate nor dead-reckon, so an entity whose HP or action
    /// this connection has not been told about is the one update that cannot be
    /// reconstructed from anything already on the client.</description></item>
    /// <item><description><b>Combat (6)</b> next: an entity mid-attack is about to produce a
    /// visible consequence, and it is the state this server most recently learned to
    /// deliver at all (see the action latch).</description></item>
    /// <item><description><b>Type (3)</b>: a player outranks a mob at equal distance. Small,
    /// because with exactly two entity kinds this is a coin flip dressed as a
    /// taxonomy.</description></item>
    /// <item><description><b>Distance (2)</b>: smallest, and deliberately so. Distance is
    /// already the tie-break BELOW the score, so weighting it heavily would just duplicate
    /// a key that is already there and drown the three factors that add information the
    /// old ordering did not have.</description></item>
    /// </list>
    /// </remarks>
    public static ImportanceSettings Balanced { get; } =
        new("balanced", new ReplicationImportance.Weights(
            distance: 2f, change: 10f, type: 3f, combat: 6f));

    /// <summary>
    /// Parse <c>GAMESERVER_IMPORTANCE</c> plus any <c>GAMESERVER_IMPORTANCE_W_*</c>
    /// overrides.
    /// </summary>
    public static bool TryCreate(
        string? profile,
        Func<string, string?> getOverride,
        out ImportanceSettings? settings,
        out string? error)
    {
        settings = null;
        error = null;

        string name = string.IsNullOrWhiteSpace(profile) ? "legacy" : profile.Trim().ToLowerInvariant();
        ReplicationImportance.Weights basis = name switch
        {
            "legacy" or "off" => ReplicationImportance.Weights.Legacy,
            "balanced" => Balanced.Weights,
            _ => default,
        };

        if (name is not ("legacy" or "off" or "balanced"))
        {
            error = $"{EnvVar}={profile} is not a known profile. Want \"legacy\" (every " +
                    "factor zero, the pre-importance ordering) or \"balanced\".";
            return false;
        }

        float distance = basis.Distance, change = basis.Change, type = basis.Type, combat = basis.Combat;
        bool overridden = false;

        if (!TryOverride(getOverride, "DISTANCE", ref distance, ref overridden, out error)) return false;
        if (!TryOverride(getOverride, "CHANGE", ref change, ref overridden, out error)) return false;
        if (!TryOverride(getOverride, "TYPE", ref type, ref overridden, out error)) return false;
        if (!TryOverride(getOverride, "COMBAT", ref combat, ref overridden, out error)) return false;

        // Refused rather than ignored: a manifest that sets one of these is describing a
        // policy this server cannot run, and silently dropping it would leave an operator
        // reading their own configuration as if it had taken effect.
        foreach (string dead in new[] { "PARTY", "PVP", "BOSS", "QUEST", "VISIBILITY", "ZONE", "INTERACTION" })
        {
            if (getOverride($"{EnvVar}_W_{dead}") != null)
            {
                error = $"{EnvVar}_W_{dead} is set, but this server has no data source for " +
                        $"that factor, so weighting it could only ever contribute zero. " +
                        "Remove it rather than letting a manifest claim a policy the " +
                        "server cannot run.";
                return false;
            }
        }

        var weights = new ReplicationImportance.Weights(
            distance: distance, change: change, type: type, combat: combat);

        // The label has to survive an override or /status lies about what is running: a
        // server reporting "balanced" while one of its weights was replaced is worse than
        // one reporting nothing, because it invites a reader to look up what balanced means.
        string label = weights.AllZero ? "legacy" : overridden ? "custom" : name;

        settings = new ImportanceSettings(label, weights);
        return true;
    }

    private static bool TryOverride(
        Func<string, string?> getOverride, string factor, ref float value,
        ref bool overridden, out string? error)
    {
        error = null;
        string key = $"{EnvVar}_W_{factor}";
        string? raw = getOverride(key);
        if (raw == null) return true;
        overridden = true;

        // InvariantCulture for the same reason AoiSettings uses it: a container inherits
        // whatever locale its base image carries, and "2,5" must be refused rather than
        // reinterpreted.
        if (!float.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out float v)
            || !float.IsFinite(v) || v < 0f)
        {
            error = $"{key}={raw} is not a non-negative finite number. Weights are relative " +
                    "magnitudes; a negative one would make an entity LESS important the more " +
                    "of the factor it has, which is never what a reader means.";
            return false;
        }
        value = v;
        return true;
    }

    public override string ToString() =>
        Enabled
            ? $"{Profile} (distance={F(Weights.Distance)} change={F(Weights.Change)} " +
              $"type={F(Weights.Type)} combat={F(Weights.Combat)})"
            : "legacy — every factor zero, pre-importance ordering";

    private static string F(float v) => v.ToString("0.##", CultureInfo.InvariantCulture);
}
