namespace GameServer.Input;

/// <summary>
/// Why one client input was refused. A BOUNDED enum, deliberately, and server-side only.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why an enum and not the reason strings.</b> The rejection sites already carry
/// human-readable reasons, and it would be less code to label a metric with them. Two
/// reasons not to:
/// </para>
/// <para>
/// 1. <b>Some of those strings embed attacker-controlled values.</b>
/// <c>ValidationLogic.ValidateInput</c> formats the rejected vector into
/// <c>"invalid move direction ({x}, {y})"</c> and the target id into
/// <c>"attack target '{id}' not found"</c>. As a metric label that is unbounded
/// cardinality a client can mint on demand — a cheater probing the rules would blow up
/// the metrics backend, turning a detection signal into a denial of service against the
/// thing detecting them. The enum has one value per CLASS of rejection and no payload.
/// </para>
/// <para>
/// 2. <b>A bounded set can be primed to zero.</b> The OTel Prometheus exporter emits an
/// instrument only once it has recorded a value, so a counter that has never fired is
/// ABSENT rather than zero — indistinguishable from a broken scrape or a build without
/// the feature (<c>docs/METRICS.md</c>). These are alarm counters, where that ambiguity
/// is at its worst: absence is the healthy reading. Because this enum is finite and known
/// at startup, every series can be created at zero, so "no rejections" is visibly zero
/// rather than missing.
/// </para>
/// <para>
/// This lives in <c>GameServer</c> and NOT in <c>Shared.GameLogic</c>. Counting is a
/// server concern; the shared library is compiled into the Unity client and has no
/// business carrying the server's detection vocabulary. Nothing here needs to cross that
/// boundary — the rejection sites are all in <see cref="InputHandler"/> already.
/// </para>
/// </remarks>
public enum InputRejectionReason
{
    /// <summary>
    /// The entity was destroyed between the input being accepted at ingest and the tick
    /// running. <b>Benign.</b> A race the tick loop is designed to lose safely.
    /// </summary>
    EntityGone = 0,

    /// <summary>
    /// Input from a dead player. <b>Benign and expected:</b> a client keeps sending until
    /// the snapshot telling it that it died arrives, so every death produces a short burst
    /// of these, and the burst is exactly one round trip long.
    /// </summary>
    DeadEntity = 1,

    /// <summary>
    /// <c>input.Tick &lt;= cursor.LastInputTick</c> — a duplicate, reordered or replayed
    /// input tick.
    /// </summary>
    /// <remarks>
    /// <b>Ambiguous, and the most easily misread value here.</b> On the ordered TCP
    /// transport an honest client should not produce these at all, which makes a sustained
    /// rate interesting. But two honest causes exist and both must be ruled out before
    /// anyone treats it as evidence: an out-of-order UDP/KCP datagram, and a RECONNECT —
    /// the entity is held for the reconnect window with its <c>LastInputTick</c> intact,
    /// so a client that restarts its own tick counter on reconnect trips this on every
    /// input until it catches up.
    /// </remarks>
    StaleTick = 2,

    /// <summary>
    /// <c>MoveResult.Rejected</c> — the movement vector was NaN, infinite, or beyond
    /// <c>GameConstants.MaxInputMagnitude</c>.
    /// </summary>
    /// <remarks>
    /// <b>The strongest single signal in this enum.</b> An honest client cannot produce
    /// it: the shipped client normalises its input vector before sending, so a value that
    /// fails <c>ResolveDirection</c> outright did not come from that code path. It is a
    /// crafted or corrupted packet. Note what it is NOT evidence of: the server already
    /// ignores the value, and movement is integrated from the server's own speed stat, so
    /// this is a client that TRIED something rather than one that achieved anything.
    /// </remarks>
    InvalidDirection = 3,

    /// <summary>
    /// The attack target id did not resolve to a live entity.
    /// <b>Latency-explicable:</b> the target despawned between the client choosing it and
    /// the server processing it. Also what a client attacking a made-up id produces, which
    /// is why the two cannot be told apart from this counter alone.
    /// </summary>
    AttackTargetUnresolved = 4,

    /// <summary>
    /// <c>CombatLogic.ValidateAttack</c>: the target was already dead.
    /// <b>Latency-explicable</b> — someone else's killing blow landed first.
    /// </summary>
    AttackTargetDead = 5,

    /// <summary>
    /// <c>CombatLogic.ValidateAttack</c>: the target was outside
    /// <c>GameConstants.AttackRange</c>.
    /// <b>Latency-explicable</b> — the target moved between the client's decision and the
    /// server's evaluation. Rises with round-trip time on an honest client, which is
    /// exactly why it is a poor threshold candidate on its own.
    /// </summary>
    AttackOutOfRange = 6,

    /// <summary>
    /// <c>CombatLogic.ValidateAttack</c>: the attacker's cooldown had not expired.
    /// <b>Mostly latency-explicable</b> — a client predicting its own cooldown against a
    /// drifting tick estimate fires a little early. A client sending attacks far faster
    /// than the cooldown permits also lands here, so rate matters more than presence; that
    /// distinction is roadmap item A4, not this one.
    /// </summary>
    AttackOnCooldown = 7,

    /// <summary>
    /// <c>CombatLogic.ValidateAttack</c> refused the attack for a reason this build does
    /// not classify.
    /// </summary>
    /// <remarks>
    /// Exists so that adding a reason to the validator without classifying it here is
    /// VISIBLE rather than silently folded into a neighbouring bucket. A counter reading
    /// non-zero on this value means the classifier is out of date, not that a player did
    /// anything in particular — which is a bug report about this file, and the only
    /// honest thing for it to say. Weighted as benign for exactly that reason: an
    /// unclassified reason must never manufacture suspicion.
    /// </remarks>
    AttackOther = 8,
}

/// <summary>
/// How much a rejection reason should count towards suspicion. Not a verdict — an input
/// to one, and the thing that stops a laggy player looking like a cheater.
/// </summary>
/// <remarks>
/// <para>
/// The central fact about this data: <b>most rejection reasons are produced by honest
/// clients on bad connections</b>, because the client decides on a world state that is
/// one round trip old. Out-of-range attacks, dead targets and unresolved targets all rise
/// with latency and none of them is evidence of anything on its own. Counting all
/// rejections equally would rank the worst-connected honest players as the most
/// suspicious, which is precisely backwards.
/// </para>
/// <para>
/// Only <see cref="InputRejectionReason.InvalidDirection"/> is something the shipped
/// client cannot emit at all. That asymmetry is the whole reason this classification
/// exists.
/// </para>
/// </remarks>
public enum RejectionWeight
{
    /// <summary>
    /// Expected during normal play. Counted for visibility; contributes nothing to an
    /// anomaly score.
    /// </summary>
    Benign = 0,

    /// <summary>
    /// Rises with latency on honest clients.
    /// </summary>
    /// <remarks>
    /// <b>Contributes zero to the anomaly score today</b>, because no honest baseline has
    /// ever been measured and a weight cannot be chosen without one. Counted and published
    /// per account regardless — visible, but not treated as evidence. See
    /// <c>InputAnomalyTracker.Contribution</c>.
    /// </remarks>
    LatencyExplicable = 1,

    /// <summary>
    /// The shipped client cannot produce it. A non-trivial count means a client that is
    /// not the shipped one.
    /// </summary>
    Forged = 2,
}

/// <summary>Reason metadata: stable metric labels and the suspicion classification.</summary>
public static class InputRejection
{
    /// <summary>Every reason, in declaration order. Used to prime metrics at zero.</summary>
    public static readonly InputRejectionReason[] All =
    {
        InputRejectionReason.EntityGone,
        InputRejectionReason.DeadEntity,
        InputRejectionReason.StaleTick,
        InputRejectionReason.InvalidDirection,
        InputRejectionReason.AttackTargetUnresolved,
        InputRejectionReason.AttackTargetDead,
        InputRejectionReason.AttackOutOfRange,
        InputRejectionReason.AttackOnCooldown,
        InputRejectionReason.AttackOther,
    };

    /// <summary>
    /// The metric label for a reason. <c>snake_case</c>, stable, and part of the
    /// monitoring contract — renaming one breaks every dashboard and alert built on it,
    /// so treat these like wire values.
    /// </summary>
    public static string Label(InputRejectionReason reason) => reason switch
    {
        InputRejectionReason.EntityGone => "entity_gone",
        InputRejectionReason.DeadEntity => "dead_entity",
        InputRejectionReason.StaleTick => "stale_tick",
        InputRejectionReason.InvalidDirection => "invalid_direction",
        InputRejectionReason.AttackTargetUnresolved => "attack_target_unresolved",
        InputRejectionReason.AttackTargetDead => "attack_target_dead",
        InputRejectionReason.AttackOutOfRange => "attack_out_of_range",
        InputRejectionReason.AttackOnCooldown => "attack_on_cooldown",
        InputRejectionReason.AttackOther => "attack_other",
        _ => "unknown",
    };

    /// <summary>How much this reason should count towards suspicion.</summary>
    public static RejectionWeight Weight(InputRejectionReason reason) => reason switch
    {
        InputRejectionReason.EntityGone => RejectionWeight.Benign,
        InputRejectionReason.DeadEntity => RejectionWeight.Benign,

        // Ambiguous rather than forged: a reconnecting client whose tick counter restarts
        // trips this honestly against a held entity. Treated as latency-explicable so a
        // reconnect storm cannot manufacture suspicion.
        InputRejectionReason.StaleTick => RejectionWeight.LatencyExplicable,

        InputRejectionReason.AttackTargetUnresolved => RejectionWeight.LatencyExplicable,
        InputRejectionReason.AttackTargetDead => RejectionWeight.LatencyExplicable,
        InputRejectionReason.AttackOutOfRange => RejectionWeight.LatencyExplicable,
        InputRejectionReason.AttackOnCooldown => RejectionWeight.LatencyExplicable,

        // Unclassified: it says this classifier is stale, not that a player misbehaved.
        InputRejectionReason.AttackOther => RejectionWeight.Benign,

        InputRejectionReason.InvalidDirection => RejectionWeight.Forged,

        _ => RejectionWeight.Benign,
    };
}
