using System;
using Shared.GameLogic.Components;

namespace GameServer.Snapshot;

/// <summary>
/// How much one entity matters to one connection, as a scalar the replication scheduler
/// can sort on.
///
/// <para><b>What this deliberately does NOT decide.</b> Interest — whether the entity is a
/// candidate at all — is settled upstream by the AOI query, and importance cannot rescue an
/// entity interest has already excluded. A boss telegraph outside the radius is not a
/// scoring problem; it is a radius problem (<see cref="Server.AoiSettings"/>).</para>
///
/// <para><b>Why the weights default to zero.</b> Wiring a new sort key into a shipped
/// encoder is the kind of change that looks correct and reorders the wire. With every
/// weight at zero this term is always 0, every comparison ties, and the byte-identity
/// digests must pass untouched — which makes the migration provable rather than argued.
/// Turning weights on is a separate, measurable change.</para>
///
/// <para><b>Why most of the factors are zero and stay that way.</b> The proposal this
/// implements lists eleven contributors. Three have a real data source on this server today
/// (distance, visible-state change, entity type), two more are derivable from state the AOI
/// gather already fetches (combat, via <see cref="EntityAction"/>), and the rest read
/// gameplay systems that <b>do not exist</b>: there is no party on the game server (parties
/// live in Nakama and are consumed by the gateway), no PvP, no boss or elite tier, no
/// quests, no line of sight and no intra-map zones. They are named here so that adding one
/// later is a weight change rather than a redesign, and they are zero so that nobody reads
/// the type as a claim that the server knows any of it.</para>
/// </summary>
public static class ReplicationImportance
{
    /// <summary>
    /// Everything the score is allowed to look at, gathered at the one point where all of
    /// it is already in hand.
    /// </summary>
    public readonly struct Inputs
    {
        public Inputs(
            float distanceSq, float aoiRadius, bool isPlayer, EntityAction action,
            bool hpChanged, bool actionChanged)
        {
            DistanceSq = distanceSq;
            AoiRadius = aoiRadius;
            IsPlayer = isPlayer;
            Action = action;
            HpChanged = hpChanged;
            ActionChanged = actionChanged;
        }

        /// <summary>Squared distance to the observer. Squared, because the square root
        /// changes no ordering and this runs per candidate per connection per snapshot.</summary>
        public readonly float DistanceSq;

        /// <summary>The connection's AOI radius, so the distance term is scale-free: a
        /// deployment that halves its radius must not silently halve every score.</summary>
        public readonly float AoiRadius;

        /// <summary>
        /// The entity is a player rather than a mob.
        /// </summary>
        /// <remarks>
        /// A bool rather than the wire's <c>EntityType</c>, and that is the point: this type
        /// does arithmetic over primitives and knows nothing about the wire schema or about
        /// how a caller decided. It also keeps the enum's two-value poverty visible — a
        /// taxonomy with exactly one distinction is a bool, and calling it a Type would
        /// dress it up as something it is not.
        /// </remarks>
        public readonly bool IsPlayer;

        public readonly EntityAction Action;

        /// <summary>Health differs from what this connection was last sent.</summary>
        public readonly bool HpChanged;

        /// <summary>Action or its retrigger counter differ from what was last sent.</summary>
        public readonly bool ActionChanged;
    }

    /// <summary>
    /// Per-factor weights. Zero disables a factor completely.
    /// </summary>
    /// <remarks>
    /// A struct of named floats rather than an array: a weight vector indexed by position
    /// is a configuration format in which a reordering is silent.
    /// </remarks>
    public readonly struct Weights
    {
        // ── Factors with a data source on this server ────────────────────────────
        /// <summary>Nearer entities score higher. Error is most visible close to the camera.</summary>
        public readonly float Distance;
        /// <summary>Health or action changed since this connection was last told.</summary>
        public readonly float Change;
        /// <summary>A player outranks a mob at equal distance.</summary>
        public readonly float Type;
        /// <summary>The entity is mid-attack.</summary>
        public readonly float Combat;

        // ── Named, reserved, and zero until the gameplay system exists ───────────
        /// <summary>Party membership. Not on the game server: parties live in Nakama and
        /// the gateway consumes them. Inside a dungeon instance every occupant IS the
        /// party, so the factor is degenerate exactly where the data would be available.</summary>
        public readonly float Party;
        /// <summary>PvP relationship. No PvP exists: no flags, no factions, no hostility.</summary>
        public readonly float Pvp;
        /// <summary>Boss or elite. <c>EntityType</c> enumerates PLAYER and MOB only.</summary>
        public readonly float Boss;
        /// <summary>Quest relevance. No quest system in either repository.</summary>
        public readonly float Quest;
        /// <summary>Line of sight. No collision geometry on the server.</summary>
        public readonly float Visibility;
        /// <summary>Zone. <c>map_id</c> is a whole server, not a region of one.</summary>
        public readonly float Zone;
        /// <summary>Recent player interaction. No target component, no threat table.</summary>
        public readonly float Interaction;

        public Weights(
            float distance = 0f, float change = 0f, float type = 0f, float combat = 0f,
            float party = 0f, float pvp = 0f, float boss = 0f, float quest = 0f,
            float visibility = 0f, float zone = 0f, float interaction = 0f)
        {
            Distance = distance;
            Change = change;
            Type = type;
            Combat = combat;
            Party = party;
            Pvp = pvp;
            Boss = boss;
            Quest = quest;
            Visibility = visibility;
            Zone = zone;
            Interaction = interaction;
        }

        /// <summary>
        /// Every weight zero: the scheduler behaves exactly as it did before importance
        /// existed, and the wire is byte-identical.
        /// </summary>
        public static Weights Legacy => default;

        /// <summary>True when no factor can contribute, so the score need not be computed.</summary>
        public bool AllZero =>
            Distance == 0f && Change == 0f && Type == 0f && Combat == 0f &&
            Party == 0f && Pvp == 0f && Boss == 0f && Quest == 0f &&
            Visibility == 0f && Zone == 0f && Interaction == 0f;
    }

    /// <summary>
    /// The score. Higher is more important.
    /// </summary>
    /// <remarks>
    /// <b>Additive, and Self and deferral age are deliberately NOT in it.</b> Those two stay
    /// as lexicographic keys ahead of this term in the comparer, because the starvation
    /// bound depends on age being strictly dominant: an entity deferred on tick N must
    /// outrank every entity that became dirty on N+1, whatever its gameplay score. Folding
    /// age into a weighted sum makes that bound a function of the weights, and a weight
    /// change would then silently retune fairness. See <c>CandidateComparer</c>.
    /// </remarks>
    public static float Score(in Inputs i, in Weights w)
    {
        if (w.AllZero) return 0f;

        float score = 0f;

        if (w.Distance != 0f)
        {
            // 1 at the observer, falling to 0.5 at the radius. Normalised by the radius so
            // the term means the same thing at any GAMESERVER_AOI_RADIUS; an un-normalised
            // distance term would make the weights radius-dependent, which is a footgun
            // that only fires on the deployment that tunes the radius.
            float r = i.AoiRadius > 0f ? i.AoiRadius : 1f;
            float n = i.DistanceSq / (r * r);
            score += w.Distance * (1f / (1f + n));
        }

        if (w.Change != 0f && (i.HpChanged || i.ActionChanged))
        {
            // Not a magnitude: HP and action are the two fields a client cannot interpolate
            // or dead-reckon, so "it changed" is the whole signal. A 1-point HP tick and a
            // 40% hit are equally un-guessable.
            score += w.Change;
        }

        if (w.Type != 0f && i.IsPlayer) score += w.Type;

        if (w.Combat != 0f && i.Action == EntityAction.Attacking) score += w.Combat;

        // The remaining factors have no input to read. They are not omitted from the sum by
        // accident: there is nothing in Inputs that could feed them, which is the honest
        // shape of "this server does not know that yet".

        return score;
    }
}
