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
    /// Connections registered on this server: the set the snapshot broadcast iterates, and
    /// therefore the number snapshot bandwidth is paid per.
    ///
    /// <para><b>Read it next to <see cref="PlayersOnline"/>, and treat a disagreement as a
    /// defect.</b> They are different measurements of the same thing by different means:
    /// <c>players_online</c> is a counter balanced by hand on join and leave, this is the
    /// live size of the connection registry. In a healthy server they are equal. If this is
    /// non-zero while <c>players_online</c> reads 0, connections are outliving their
    /// players and the server is gathering, encoding and writing for sockets nobody owns;
    /// if the reverse, the join/leave balance has drifted and the number an operator uses
    /// to decide a pod is idle is wrong.</para>
    ///
    /// <para>Bots hold no connection, so they appear in neither (see
    /// <see cref="Bots"/>). Transports still inside the handshake appear in neither either
    /// — those are <see cref="HandshakesPending"/>.</para>
    ///
    /// <para>Published because its absence cost a whole investigation: #401's snapshot
    /// traffic on an empty server had to be diagnosed from three 30s samples of
    /// <c>snapshot_bytes</c> and a read of the broadcast source, when one request would
    /// have answered it.</para>
    /// </summary>
    [JsonPropertyName("connections")]
    public int Connections { get; set; }

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

    /// <summary>Player save attempts that succeeded since process start.</summary>
    [JsonPropertyName("player_saves_ok")]
    public long PlayerSavesOk { get; set; }

    /// <summary>Player save attempts that threw since process start.</summary>
    [JsonPropertyName("player_saves_error")]
    public long PlayerSavesError { get; set; }

    /// <summary>
    /// Failed share of all player save attempts since start, in <c>[0,1]</c>.
    ///
    /// <para>The save sweep is the only thing that persists position and HP, and ADR-6
    /// accepts a &lt;=30s crash-loss window <em>on the assumption that the sweep works</em>.
    /// When it does not, the real window is "since the last success", which is unbounded.
    /// This field exists because that distinction was previously visible only in a
    /// Prometheus counter nobody scrapes on a dev box (#402).</para>
    ///
    /// <para>Cumulative over process lifetime — see
    /// <see cref="GameMetrics.PlayerSaveErrorRatio"/> for why the ok/error pair must be
    /// read alongside it rather than this ratio alone.</para>
    /// </summary>
    [JsonPropertyName("player_save_error_ratio")]
    public double PlayerSaveErrorRatio { get; set; }

    /// <summary>Inputs that carried an attack target id, since process start.</summary>
    [JsonPropertyName("attacks_received")]
    public long AttacksReceived { get; set; }

    /// <summary>Attacks whose target id did not resolve to a live entity.</summary>
    [JsonPropertyName("attacks_unresolved")]
    public long AttacksUnresolved { get; set; }

    /// <summary>Attacks refused by validation (range, cooldown, dead attacker or target).</summary>
    [JsonPropertyName("attacks_rejected")]
    public long AttacksRejected { get; set; }

    /// <summary>
    /// Times an account landed more accepted attacks inside the audit window than one
    /// entity's cooldown permits. Observation only; see <c>Input/AttackRateAudit.cs</c>.
    /// </summary>
    [JsonPropertyName("attack_rate_violations")]
    public long AttackRateViolations { get; set; }

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

    /// <summary>
    /// Which <c>IEventStream</c> backs cross-server events: <c>"redis"</c> (publishing to
    /// the <c>events:game</c> stream, ADR-5) or <c>"noop"</c> (events discarded —
    /// <c>REDIS_ADDR</c> unset, or the Redis connection could not be built at startup).
    /// </summary>
    [JsonPropertyName("event_stream")]
    public string EventStream { get; set; } = "noop";

    /// <summary>
    /// Events dropped oldest-first by the bounded publish queue (or offered after
    /// shutdown), since process start. Non-zero means Redis was unreachable long enough
    /// to fill the bound. Same value as <c>gameserver_events_dropped_total</c>.
    /// </summary>
    [JsonPropertyName("events_dropped")]
    public long EventsDropped { get; set; }

    /// <summary>
    /// Events dropped after exhausting the XADD retry budget, since process start. Same
    /// value as <c>gameserver_events_publish_failures_total</c>.
    /// </summary>
    [JsonPropertyName("event_publish_failures")]
    public long EventPublishFailures { get; set; }

    /// <summary>
    /// State of the duplicate-login kick consumer on <c>events:kick</c>:
    /// <c>"redis"</c> (consuming) or <c>"disabled"</c> (<c>REDIS_ADDR</c> unset, or the
    /// consumer could not start — supersede events for this server are then never acted
    /// on, so a user can hold two live game connections here).
    /// </summary>
    [JsonPropertyName("kick_consumer")]
    public string KickConsumer { get; set; } = "disabled";

    /// <summary>
    /// Duplicate-login kicks executed since process start: connections force-closed
    /// because a <c>session_superseded</c> event named their join-token jti. Same value
    /// as <c>gameserver_players_kicked_total</c>.
    /// </summary>
    [JsonPropertyName("players_kicked")]
    public long PlayersKicked { get; set; }

    /// <summary>
    /// Accepted transports that have not completed the join handshake right now. Outside
    /// <see cref="PlayersOnline"/> and outside <see cref="Capacity"/>: bounded by
    /// <c>GAMESERVER_MAX_PENDING_HANDSHAKES</c> instead. Same value as
    /// <c>gameserver_handshakes_pending</c>.
    /// </summary>
    [JsonPropertyName("handshakes_pending")]
    public int HandshakesPending { get; set; }

    /// <summary>
    /// Handshakes refused before authentication since process start, every reason (pool
    /// full, deadline, malformed first frame). Same value as the sum over
    /// <c>gameserver_handshakes_rejected_total</c>. A capacity refusal is not one of
    /// these — that is an authenticated join, logged at Warning.
    /// </summary>
    [JsonPropertyName("handshakes_rejected")]
    public long HandshakesRejected { get; set; }

    /// <summary>
    /// Client inputs discarded at ingest since process start, every reason (per-connection
    /// budget, world-wide queue full). Same value as the sum over
    /// <c>gameserver_inputs_dropped_total</c>. Movement coalesced in place is not a drop.
    /// </summary>
    [JsonPropertyName("inputs_dropped")]
    public long InputsDropped { get; set; }

    // ── Transport confidentiality ────────────────────────────────────────────────
    //
    // Always present, never omitted, and deliberately more than one field. Encryption here
    // is off by default twice (TCP has no packet-crypt layer; TRANSPORT_KEY defaults to
    // empty), so the question an operator needs answered is not "is there a key" but "what
    // is actually happening to these bytes". Published here rather than only as metrics
    // because a never-incremented OpenTelemetry instrument is ABSENT from /metrics rather
    // than zero — see the note in docs/METRICS.md — and "the field is missing" is exactly
    // the wrong answer to a security question.

    /// <summary>Transport this server listens with: <c>tcp</c> or <c>kcp</c>.</summary>
    [JsonPropertyName("transport")]
    public string Transport { get; set; } = "tcp";

    /// <summary>
    /// <c>TRANSPORT_KEY</c> holds a value. <b>Not the same as encryption being on</b>: on
    /// TCP the key is ignored, which is the configuration most easily mistaken for working
    /// encryption. Compare with <see cref="TransportEncrypted"/>.
    /// </summary>
    [JsonPropertyName("transport_key_configured")]
    public bool TransportKeyConfigured { get; set; }

    /// <summary>Packets leave this process as ciphertext.</summary>
    [JsonPropertyName("transport_encrypted")]
    public bool TransportEncrypted { get; set; }

    /// <summary>
    /// Tampering with a packet in flight is detectable. <b>False on every configuration
    /// this server currently supports</b>: the KCP path is AES-CFB with a CRC32, and a
    /// CRC32 is a linear checksum, not a MAC. Separate from
    /// <see cref="TransportEncrypted"/> so that "encrypted" cannot be read as "safe from
    /// tampering", and published while false so that its becoming true is a visible event.
    /// </summary>
    [JsonPropertyName("transport_authenticated")]
    public bool TransportAuthenticated { get; set; }

    /// <summary>Cipher actually in force, or <c>none</c>.</summary>
    [JsonPropertyName("transport_cipher")]
    public string TransportCipher { get; set; } = "none";

    /// <summary>One line stating what is happening to the bytes, and what is not.</summary>
    [JsonPropertyName("transport_posture")]
    public string TransportPostureSummary { get; set; } = "";

    /// <summary>
    /// A sealed session is required on the gameplay hop (<c>GAMESERVER_SEALED=require</c>,
    /// the default).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Read this before concluding anything from <see cref="TransportEncrypted"/>.</b>
    /// The transport fields describe the transport only. On the default configuration —
    /// TCP with sealing required — <c>transport_encrypted</c> is <c>false</c> while every
    /// gameplay frame is in fact encrypted and authenticated a layer above it. A dashboard
    /// or a deploy check reading only the transport fields would report an encrypting
    /// server as plaintext, which is the wrong answer to a security question in the
    /// direction that causes work rather than the direction that causes a breach.
    /// </para>
    /// <para>
    /// It is also not a claim that nothing is readable. The join handshake before the
    /// sealed session exists, and the whole gateway hop, are unaffected by this field.
    /// </para>
    /// </remarks>
    [JsonPropertyName("sealed_required")]
    public bool SealedRequired { get; set; }

    /// <summary>
    /// AEAD in force on the gameplay hop once a session is sealed, or <c>none</c> when
    /// sealing is off. Published while <c>none</c> for the same reason
    /// <see cref="TransportAuthenticated"/> is published while false: a missing field is
    /// the wrong answer to a security question, and a never-incremented OpenTelemetry
    /// instrument is ABSENT from <c>/metrics</c> rather than zero.
    /// </summary>
    [JsonPropertyName("sealed_cipher")]
    public string SealedCipher { get; set; } = "none";

    /// <summary>
    /// Configured per-connection downlink budget in bytes of snapshot payload
    /// (<c>GAMESERVER_MAX_SNAPSHOT_BYTES</c>); 0 means the budget is off and a snapshot is
    /// bounded only by the AOI radius. Published next to what it produced, because reading
    /// <c>snapshot_entities_shed</c> without knowing the cap that caused it says nothing.
    /// </summary>
    [JsonPropertyName("max_snapshot_bytes")]
    public int MaxSnapshotBytes { get; set; }

    /// <summary>
    /// Effective area-of-interest radius in world units (<c>GAMESERVER_AOI_RADIUS</c>).
    /// </summary>
    /// <remarks>
    /// Published because it is the one configuration value that is now both deployment-set
    /// and impossible to infer from anything else on this endpoint: two servers with
    /// identical rates, capacity and budget will show completely different
    /// <c>snapshot_bytes</c> at the same population if their radii differ, and until this
    /// field existed the only way to learn a pod's radius was to read the manifest that was
    /// supposed to have produced it — which an already-allocated GameServer does not
    /// necessarily reflect, since its environment is fixed at pod creation.
    /// </remarks>
    [JsonPropertyName("aoi_radius")]
    public float AoiRadius { get; set; }

    /// <summary>
    /// True when <see cref="AoiRadius"/> reaches every corner of the map, so interest
    /// management filters nothing and every entity is in every snapshot.
    /// </summary>
    [JsonPropertyName("aoi_covers_whole_map")]
    public bool AoiCoversWholeMap { get; set; }

    /// <summary>
    /// Replication-importance profile in force (<c>GAMESERVER_IMPORTANCE</c>):
    /// <c>legacy</c>, <c>balanced</c>, or <c>custom</c> when a weight was overridden.
    /// </summary>
    /// <remarks>
    /// Published for the same reason as <c>aoi_radius</c>: it is deployment-set, it is not
    /// on the wire, and two servers running different profiles are indistinguishable from
    /// any client or any other field here. The weights come with it because "balanced" is a
    /// label, and a custom profile is only readable as its numbers.
    /// </remarks>
    [JsonPropertyName("importance_profile")]
    public string ImportanceProfile { get; set; } = "legacy";

    /// <summary>The four weights that have a data source, in force right now.</summary>
    [JsonPropertyName("importance_weights")]
    public string ImportanceWeights { get; set; } = "";

    /// <summary>
    /// Per-importance send intervals in force (<c>GAMESERVER_REPLICATION_SCHEDULE</c>),
    /// already converted to world ticks at this server's <c>SIM_WORLD_HZ</c>.
    /// </summary>
    /// <remarks>
    /// Rendered rather than named because the configured value is milliseconds and the
    /// behaviour is ticks: the same "266ms" is 4 ticks at 15Hz and 8 at 30Hz, and an
    /// operator comparing two pods needs the number that actually applies.
    /// </remarks>
    [JsonPropertyName("replication_schedule")]
    public string ReplicationSchedule { get; set; } = "";

    /// <summary>
    /// The enemy AI tuning in force (the <c>GAMESERVER_ENEMY_*</c> family), rendered as
    /// one line, or <c>off</c> when the spawner is disabled.
    /// </summary>
    /// <remarks>
    /// <para>Published for the same reason as <c>aoi_radius</c> and
    /// <c>importance_profile</c>: it is deployment-set, it is not on the wire, and two
    /// servers running different enemy tuning are indistinguishable from any client and
    /// from every other field here — <c>enemies_alive</c> answers "how many are there
    /// now", which is the same number on a server capped at 30 that has filled up and a
    /// server capped at 300 that has not.</para>
    ///
    /// <para>It is also the field that answers a question a manifest cannot: an
    /// already-allocated Agones GameServer keeps the environment it was created with, so
    /// a fleet update changes the manifest and not the pod, and the only honest source for
    /// what a running pod is doing is the running pod.</para>
    /// </remarks>
    [JsonPropertyName("enemy_ai")]
    public string EnemyAi { get; set; } = "off";

    /// <summary>
    /// Enemy population cap in force at this instant, i.e. the base cap plus the
    /// per-player allowance times the players online. 0 when the spawner is disabled.
    /// </summary>
    /// <remarks>
    /// Rendered as well as configured, because the configuration is "30 + 45 per player"
    /// and the behaviour is a number — and an operator comparing <c>enemies_alive</c>
    /// against a cap needs the one that actually applies right now.
    ///
    /// <para><b>Counted from the world's player entities, not from
    /// <see cref="PlayersOnline"/>.</b> Those are different numbers and the difference is
    /// not academic: synthetic players (see <see cref="Bots"/>) are entities the cap scales
    /// on and are not connections, so a server with 24 bots and nobody logged in published
    /// a cap of 30 while actually running 1110 when this field was derived from the
    /// connection count. It still counts a dead player awaiting respawn, which the spawner
    /// does not, so it can read one allowance high during a wipe — that residual is stated
    /// rather than papered over.</para>
    /// </remarks>
    [JsonPropertyName("enemy_ai_max_now")]
    public int EnemyAiMaxNow { get; set; }

    /// <summary>
    /// Enemy attacks decided and emitted as input since start. 0 when enemy combat is off
    /// or when nothing has been in range.
    /// </summary>
    /// <remarks>
    /// This counts DECISIONS, not landed hits — the pair to look at is this against
    /// <c>attacks_accepted</c>, which counts what the input handler then allowed. The gap
    /// is the target that another enemy killed between the world tick that decided and the
    /// critical tick that resolved, and it is expected to be small and non-zero during a
    /// wipe.
    /// </remarks>
    [JsonPropertyName("enemy_attacks_decided")]
    public long EnemyAttacksDecided { get; set; }

    /// <summary>
    /// Enemy attacks NOT emitted because the target had already taken
    /// <c>GAMESERVER_ENEMY_ATTACKERS_PER_TARGET</c> hits in the current window.
    /// </summary>
    /// <remarks>
    /// <para><b>The field that says whether the survivability cap is doing anything.</b>
    /// "Enemies are attacking and the cap is holding" and "enemies are not attacking" are
    /// indistinguishable from a player's HP bar, from <c>enemies_alive</c> and from every
    /// other field here; they differ in exactly this counter. A crowd standing on a player
    /// with this at zero means the cap is not what is limiting the fight, and the limit is
    /// somewhere the operator has not looked.</para>
    /// </remarks>
    [JsonPropertyName("enemy_attacks_throttled")]
    public long EnemyAttacksThrottled { get; set; }

    /// <summary>
    /// Players returned to the map after their HP reached 0 (<c>GAMESERVER_PLAYER_RESPAWN</c>).
    /// </summary>
    /// <remarks>
    /// Includes synthetic players. A demo whose crowd is bots will show this climbing
    /// while <c>bots_alive</c> stays flat, which is the intended reading: without the
    /// respawn rule that population drains permanently and silently, because a dead bot
    /// stops acting for the life of the process.
    /// </remarks>
    [JsonPropertyName("player_respawns")]
    public long PlayerRespawns { get; set; }

    /// <summary>
    /// Synthetic-player configuration in force (<c>GAMESERVER_BOTS</c> and the
    /// <c>GAMESERVER_BOT_*</c> family), or <c>off</c>, which is the default.
    /// </summary>
    /// <remarks>
    /// Published because bots are <b>indistinguishable from players in every other field
    /// on this endpoint and on the wire</b> — that is the point of them, and it is also
    /// the trap. A reader who sees a busy map and <c>players_online: 3</c> will conclude
    /// the count is broken; this field is the line that says it is not. It is also the
    /// only signal that a server has entities in it that will never be persisted.
    /// </remarks>
    [JsonPropertyName("bots")]
    public string Bots { get; set; } = "off";

    /// <summary>
    /// Synthetic players currently in the world. <b>Not</b> included in
    /// <see cref="PlayersOnline"/>, which counts connections — a bot holds none.
    /// </summary>
    [JsonPropertyName("bots_alive")]
    public int BotsAlive { get; set; }

    /// <summary>
    /// Whether field-level delta encoding is permitted on this server
    /// (<c>GAMESERVER_FIELD_DELTA</c>). False means every entity carries every field, which
    /// is the control arm for any bandwidth claim about the feature — see #381.
    /// </summary>
    /// <remarks>
    /// Says what the SERVER allows, not what a given connection got: a connection also has
    /// to have joined with an exact protocol version match and be speaking Protobuf. A
    /// client on protocol 1 sees whole entities regardless of this flag.
    /// </remarks>
    [JsonPropertyName("field_delta")]
    public bool FieldDelta { get; set; }

    /// <summary>Entity updates withheld because their importance tier was not due.</summary>
    [JsonPropertyName("snapshot_deferred_by_interval")]
    public long SnapshotDeferredByInterval { get; set; }

    /// <summary>
    /// Longest gap, in world ticks, between an entity's state going stale for a client and
    /// being re-sent.
    /// </summary>
    /// <remarks>
    /// The cost side of the schedule, and NOT the same number as
    /// <c>snapshot_max_shed_age</c>: that one counts budget deferrals, this one counts
    /// schedule deferrals, and a schedule deferral never touches the budget's bookkeeping.
    /// Reading one for the other reports a healthy zero while entities go seconds without an
    /// update.
    /// </remarks>
    [JsonPropertyName("snapshot_max_state_age")]
    public int SnapshotMaxStateAge { get; set; }

    /// <summary>
    /// Bytes of snapshot frames written to client sockets since process start, envelope
    /// and length prefix included. With <see cref="UptimeSeconds"/> and
    /// <see cref="PlayersOnline"/> this is the per-client downlink rate ADR-7's
    /// &lt; 50 KB/s mobile threshold is about. Same value as
    /// <c>gameserver_snapshots_bytes_total</c>.
    /// </summary>
    [JsonPropertyName("snapshot_bytes")]
    public long SnapshotBytes { get; set; }

    /// <summary>
    /// Entity updates deferred by the downlink budget since start. Deferred, not dropped:
    /// the entity stays dirty and is re-offered on the next snapshot. Read with
    /// <see cref="SnapshotMaxShedAge"/>.
    /// </summary>
    [JsonPropertyName("snapshot_entities_shed")]
    public long SnapshotEntitiesShed { get; set; }

    /// <summary>
    /// Viewer gathers skipped because the viewer's own entity could not be resolved.
    /// Brief non-zero around join/despawn is normal; sustained growth means a connection
    /// has outlived its entity (#385).
    /// </summary>
    [JsonPropertyName("snapshot_anchor_missing")]
    public long SnapshotAnchorMissing { get; set; }

    /// <summary>
    /// Entities found in viewers' areas of interest since start — what the server
    /// CONSIDERED in-interest, before the budget or the schedule withheld anything (#161).
    /// </summary>
    [JsonPropertyName("snapshot_entities_gathered")]
    public long SnapshotEntitiesGathered { get; set; }

    /// <summary>
    /// Largest single-viewer gather observed. Kept alongside the total because an average
    /// hides one client with an empty view among many full ones.
    /// </summary>
    [JsonPropertyName("snapshot_max_gather")]
    public int SnapshotMaxGather { get; set; }

    /// <summary>
    /// Despawn notifications deferred by the downlink budget since start. Should stay at
    /// zero: despawns outrank every non-self update, so this moves only when the budget is
    /// too small for the despawn list alone.
    /// </summary>
    [JsonPropertyName("snapshot_removals_deferred")]
    public long SnapshotRemovalsDeferred { get; set; }

    /// <summary>
    /// Longest deferral, in snapshots, any entity has reached on any live connection.
    /// High-water mark. Bounded by the number of dirty entities in one observer's AOI, not
    /// by session length — a value that keeps climbing means the budget is too small for
    /// the crowd rather than that an entity is stuck.
    /// </summary>
    [JsonPropertyName("snapshot_max_shed_age")]
    public int SnapshotMaxShedAge { get; set; }

    /// <summary>
    /// <c>MsgTransferMap</c> requests refused because a transfer was already running on
    /// that connection. Same value as <c>gameserver_transfers_rejected_total</c>.
    /// </summary>
    [JsonPropertyName("transfers_rejected")]
    public long TransfersRejected { get; set; }

    [JsonPropertyName("postgres")]
    public string Postgres { get; set; } = "disconnected";

    /// <summary>
    /// Client inputs refused by validation since start, all reasons.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Published here as a plain zero, always. The Prometheus counters are primed at zero
    /// too, but this stays the surface an operator should read: it cannot be confused with
    /// a broken scrape, and it carries the per-account detail that would be unbounded
    /// cardinality as a metric label.
    /// </para>
    /// <para>
    /// <b>Most of these are normal.</b> Attack rejections and dead-entity input rise with a
    /// player's latency; only <c>invalid_direction</c> is something the shipped client
    /// cannot produce. Read the breakdown, not the total.
    /// </para>
    /// </remarks>
    [JsonPropertyName("inputs_rejected")]
    public long InputsRejected { get; set; }

    /// <summary>Refused inputs by reason, keyed by the metric label.</summary>
    [JsonPropertyName("inputs_rejected_by_reason")]
    public Dictionary<string, long> InputsRejectedByReason { get; set; } = new();

    /// <summary>Accounts currently tracked for input anomalies.</summary>
    [JsonPropertyName("anomaly_accounts_tracked")]
    public int AnomalyAccountsTracked { get; set; }

    /// <summary>
    /// Accounts whose decaying anomaly score is at or above the alert threshold right now.
    /// </summary>
    [JsonPropertyName("anomaly_accounts_over_threshold")]
    public int AnomalyAccountsOverThreshold { get; set; }

    /// <summary>
    /// Times any account has crossed the alert threshold since start.
    /// <b>Observation only</b> — no player is ever acted on by this.
    /// </summary>
    [JsonPropertyName("anomaly_alerts")]
    public long AnomalyAlerts { get; set; }

    /// <summary>
    /// Observations discarded because the tracker hit its account cap. Non-zero means the
    /// numbers above describe only part of the population.
    /// </summary>
    [JsonPropertyName("anomaly_accounts_dropped")]
    public long AnomalyAccountsDropped { get; set; }

    /// <summary>
    /// The most anomalous accounts, ordered by score.
    /// </summary>
    /// <remarks>
    /// Ordered by SCORE, not by raw rejection count. The account with the most rejections
    /// is usually the one with the worst connection, and presenting that player at the top
    /// of a list an operator reads as "most suspicious" is how a latency problem gets
    /// mistaken for cheating.
    /// </remarks>
    [JsonPropertyName("anomaly_top_accounts")]
    public List<AnomalousAccount> AnomalyTopAccounts { get; set; } = new();

    /// <summary>
    /// Input frames whose ARRIVAL order was inspected at the decode step (ADR-22).
    /// </summary>
    [JsonPropertyName("frame_order_observed")]
    public long FrameOrderObserved { get; set; }

    /// <summary>
    /// Frames that arrived with a tick strictly below the highest already seen on their
    /// connection — genuine reordering. <b>The number ADR-22's open question turns on:</b>
    /// a nonce-as-sequence rule with no sliding window is safe only while this is zero.
    /// </summary>
    [JsonPropertyName("frame_order_inversions")]
    public long FrameOrderInversions { get; set; }

    /// <summary>Frames repeating the highest tick already seen on their connection.</summary>
    [JsonPropertyName("frame_order_duplicates")]
    public long FrameOrderDuplicates { get; set; }

    /// <summary>
    /// How far back the worst inversion reached, in ticks — the minimum width a sliding
    /// window would need. Zero means nothing observed required one.
    /// </summary>
    [JsonPropertyName("frame_order_largest_backward_jump")]
    public long FrameOrderLargestBackwardJump { get; set; }

    /// <summary>
    /// Frames arriving more than one tick above the previous highest: a gap left by a lost
    /// or unsent frame. A strict monotonic rule must accept these.
    /// </summary>
    [JsonPropertyName("frame_order_forward_gaps")]
    public long FrameOrderForwardGaps { get; set; }

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
/// One account's refused-input record, for <c>/status</c>.
/// </summary>
/// <remarks>
/// <b>This is not an accusation.</b> A high score means "worth looking at", and the most
/// common cause of a high rejection count is a bad connection, not a modified client. The
/// per-reason breakdown is included precisely so the two can be told apart: an account
/// whose rejections are all attack-path is lagging; one producing
/// <c>invalid_direction</c> is sending packets the shipped client cannot produce.
/// </remarks>
public sealed class AnomalousAccount
{
    /// <summary>Account id (the join token's <c>sub</c> claim).</summary>
    [JsonPropertyName("user_id")]
    public string UserId { get; set; } = "";

    /// <summary>Total refused inputs recorded for this account.</summary>
    [JsonPropertyName("rejections")]
    public long Rejections { get; set; }

    /// <summary>Current decaying anomaly score.</summary>
    [JsonPropertyName("score")]
    public double Score { get; set; }

    /// <summary>Times this account has crossed the alert threshold.</summary>
    [JsonPropertyName("alerts")]
    public long Alerts { get; set; }

    /// <summary>Refused inputs by reason, keyed by the metric label.</summary>
    [JsonPropertyName("by_reason")]
    public Dictionary<string, long> ByReason { get; set; } = new();
}

/// <summary>
/// AOT-safe JSON serialization context for <see cref="ServerStatus"/>.
/// </summary>
[JsonSerializable(typeof(ServerStatus))]
internal sealed partial class ServerStatusContext : JsonSerializerContext;
