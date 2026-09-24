namespace GameServer;

/// <summary>
/// The <c>GAMESERVER_*</c> environment variable names <c>Program.cs</c> reads directly,
/// declared as constants so that the deployment passthrough gates can see them.
/// </summary>
/// <remarks>
/// <para><b>Why this type exists.</b> A knob is two things: something the server reads,
/// and a line in each deployment manifest that lets a value reach the process. Docker and
/// Kubernetes both pass nothing a manifest did not declare, so the second half can be
/// missing while everything else looks right — the knob is documented, parsed, settable in
/// <c>deploy/.env</c>, and silently ignored. Six such instances have been found in this
/// repository. Five were inert; the sixth ran the server on a compiled-in
/// <c>GAMESERVER_MAX_SNAPSHOT_BYTES</c> of 8192, which truncated every keyframe at the
/// shipped enemy population and made the whole crowd blink out and back every two seconds
/// (#404).</para>
///
/// <para><b>Why constants rather than the inline string literals these replace.</b>
/// <c>ComposeEnvPassthroughTests</c> and <c>FleetEnvPassthroughTests</c> enumerate the
/// knobs by <b>reflecting over the assembly's <c>const string</c> fields</b>. A name
/// written as an inline literal inside a <c>Program.cs</c> expression exists nowhere
/// reflection can reach, so it is invisible to both gates — which is precisely where the
/// sixth instance lived, and where a seventh was found while closing it:
/// <c>GAMESERVER_FIELD_DELTA</c> had been added to <c>gameserver-dotnet</c> and never to
/// <c>gameserver-dotnet-map02</c>, so field-level delta encoding was on for the map anyone
/// would test on and off for the other. Declaring the name here is what brings a knob under
/// the gates; that is the whole mechanism, and it is the same one that exposed the fifth
/// instance when <c>GAMESERVER_IMPORTANCE_W_*</c> became constants in #398.</para>
///
/// <para><b>Being listed here does not mean "must appear in every manifest."</b> It means
/// "a gate has an opinion about this name." Several of these are deliberately NOT forwarded
/// — an Agones-only knob under compose, a one-shot mode, a per-map dimension — and each
/// such decision is recorded, with its reason, in the <c>Excluded</c> dictionary of the
/// gate it applies to. The point is that every name is now either passed through or
/// explicitly excused by somebody, and neither state can be reached by accident.</para>
///
/// <para>Values are the variable names themselves and must never be changed without
/// changing every manifest in <c>backend/deploy/</c> in the same commit: renaming one here
/// alone turns a working knob into a silently ignored one, which is the exact defect this
/// type exists to prevent.</para>
/// </remarks>
internal static class ServerEnv
{
    // ── Identity and placement: what this process is and where it listens ────

    /// <summary>Listen address, e.g. <c>:9000</c>. Per service/fleet, never shared.</summary>
    public const string Addr = "GAMESERVER_ADDR";

    /// <summary><c>map</c> or <c>dungeon</c>. Per service/fleet.</summary>
    public const string Mode = "GAMESERVER_MODE";

    /// <summary>
    /// Server id. On a fleet this must stay unset so <c>POD_NAME</c> wins — see the
    /// server-id chain comment in the fleet manifests.
    /// </summary>
    public const string Id = "GAMESERVER_ID";

    /// <summary>Map this server owns. Absent on a dungeon fleet on purpose (ADR-26).</summary>
    public const string MapId = "GAMESERVER_MAP_ID";

    /// <summary>Full <c>host:port</c> advertised to clients. Read only when Agones is OFF.</summary>
    public const string PublicAddr = "GAMESERVER_PUBLIC_ADDR";

    /// <summary>Host half of the advertised address. Read only when Agones is ON.</summary>
    public const string AdvertiseHost = "GAMESERVER_ADVERTISE_HOST";

    /// <summary>Hold registration back until Agones reports Allocated. Agones only.</summary>
    public const string RegisterOnAllocated = "GAMESERVER_REGISTER_ON_ALLOCATED";

    // ── Admission and pre-join bounds ────────────────────────────────────────

    /// <summary>Authenticated-player admission limit, published to the registry.</summary>
    public const string Capacity = "GAMESERVER_CAPACITY";

    /// <summary>Accepted sockets that may sit in the handshake at once.</summary>
    public const string MaxPendingHandshakes = "GAMESERVER_MAX_PENDING_HANDSHAKES";

    /// <summary>How long one handshake may take to deliver a complete join frame.</summary>
    public const string HandshakeTimeoutMs = "GAMESERVER_HANDSHAKE_TIMEOUT_MS";

    /// <summary>Wire protocol version floor. 0 also admits a client advertising none.</summary>
    public const string MinProtocolVersion = "GAMESERVER_MIN_PROTOCOL_VERSION";

    /// <summary>Bounded join deadline for an instanced dungeon (ADR-26). Dungeon mode only.</summary>
    public const string JoinDeadlineSeconds = "GAMESERVER_JOIN_DEADLINE_SECONDS";

    // ── Ingestion and downlink bounds ────────────────────────────────────────

    /// <summary>Inputs one connection may queue between two tick drains.</summary>
    public const string MaxInputsPerTick = "GAMESERVER_MAX_INPUTS_PER_TICK";

    /// <summary>World-wide input queue cap. 0 = capacity x per-connection budget.</summary>
    public const string MaxPendingInputs = "GAMESERVER_MAX_PENDING_INPUTS";

    /// <summary>
    /// Snapshot payload bytes one connection may be sent per snapshot. The knob whose
    /// absence from compose was the sixth passthrough instance and the first visible one.
    /// </summary>
    public const string MaxSnapshotBytes = "GAMESERVER_MAX_SNAPSHOT_BYTES";

    /// <summary>Delta snapshots between full keyframes. 0 or less = full snapshot per tick.</summary>
    public const string KeyframeInterval = "GAMESERVER_KEYFRAME_INTERVAL";

    /// <summary>Field-level delta encoding. Found missing from map02 while closing #404.</summary>
    public const string FieldDelta = "GAMESERVER_FIELD_DELTA";

    // ── Simulation shape ─────────────────────────────────────────────────────

    /// <summary>
    /// Legacy single-rate scalar. Superseded by <c>SIM_CRITICAL_HZ</c>/<c>SIM_WORLD_HZ</c>/
    /// <c>SIM_BACKGROUND_HZ</c>, and inert wherever any of those is set.
    /// </summary>
    public const string TickRate = "GAMESERVER_TICK_RATE";

    /// <summary>AOI-gather worker threads. 1 = serial, the default.</summary>
    public const string GatherWorkers = "GAMESERVER_GATHER_WORKERS";

    /// <summary>Map width in world units. A property of the map, not of a deployment.</summary>
    public const string MapWidth = "GAMESERVER_MAP_WIDTH";

    /// <summary>Map height in world units. A property of the map, not of a deployment.</summary>
    public const string MapHeight = "GAMESERVER_MAP_HEIGHT";

    /// <summary>Spawn the scaffolding enemy population.</summary>
    public const string Enemies = "GAMESERVER_ENEMIES";

    // ── Transport and lifecycle ──────────────────────────────────────────────

    /// <summary>
    /// Realtime transport for the gameplay hop: <c>tcp</c> or <c>kcp</c>. On a fleet it is
    /// coupled to the port's <c>protocol:</c>, which no environment variable can change.
    /// </summary>
    public const string Transport = "GAMESERVER_TRANSPORT";

    /// <summary>Sealed transport posture on the gameplay hop: <c>require</c> or <c>off</c>.</summary>
    public const string Sealed = "GAMESERVER_SEALED";

    /// <summary>
    /// Apply pending migrations and exit without listening. A ONE-SHOT INVOCATION MODE, not
    /// a deployment setting: a long-running service that reads it true never serves.
    /// </summary>
    public const string MigrateOnly = "GAMESERVER_MIGRATE_ONLY";
}
