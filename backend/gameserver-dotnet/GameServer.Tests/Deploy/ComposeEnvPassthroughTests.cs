using System.Reflection;
using System.Text;
using GameServer.Scaffolding;

namespace GameServer.Tests.Deploy;

/// <summary>
/// Every strictly-parsed <c>GAMESERVER_*</c> knob reaches every compose service that runs
/// the game server.
///
/// <para><b>Why this exists, and why a comment was not enough.</b> A knob is two things: a
/// constant the server parses, and a line in each service's <c>environment:</c> block that
/// lets the value reach the process. Adding the first without the second produces a knob
/// that is documented, strictly parsed, settable in <c>deploy/.env</c> — and silently
/// ignored, because Docker passes nothing a service did not declare. Nothing fails: the
/// server runs its compiled default, <c>/status</c> reports that default truthfully, and
/// the operator reads their own manifest to find out what should be happening. The strict
/// parser cannot help, because it never sees a value to refuse.</para>
///
/// <para>This has now happened three times in this repository. Twice in consecutive PRs
/// (<c>GAMESERVER_IMPORTANCE</c>'s family, then the four
/// <c>GAMESERVER_ENEMY_ATTACK*</c>/<c>GAMESERVER_PLAYER_RESPAWN</c> knobs), each time
/// caught by hand after the fact; and a third time found by this very test on its first
/// run — <c>GAMESERVER_BOT_HP</c>, <c>_ATTACK</c>, <c>_DEFENSE</c> and <c>_SPEED</c>
/// reached <b>neither</b> service and had not reached either since they were added. Each
/// of those files carries a comment telling the next person to keep the list in step. A
/// comment has failed three times; this is the mechanical link.</para>
///
/// <para><b>What this gate does NOT cover, stated because a partial gate that reads as a
/// total one is worse than none.</b> It checks variable names the assembly declares as
/// <c>const string</c> — 56 of them today, up from 31 before #404. It does not check:</para>
/// <list type="bullet">
///   <item><description><b>Names built by concatenation at runtime</b>, which neither
///     reflection nor a grep can enumerate — they exist nowhere as a whole string. That was
///     true of <c>GAMESERVER_IMPORTANCE_W_{DISTANCE,CHANGE,TYPE,COMBAT}</c>, the *first* of
///     the incidents above and the one this gate therefore could not see. They are now
///     declared as <c>const string</c> in <c>ImportanceSettings</c> and are covered like any
///     other knob — which found a fifth instance immediately: the four had been added to
///     <c>gameserver-dotnet</c> when the gap was first fixed and never to
///     <c>gameserver-dotnet-map02</c>, so setting a weight changed map_01's replication
///     policy and silently left map_02 on the profile's own. The seven refused <c>_W_</c>
///     factors in <c>ImportanceSettings</c> stay assembled from suffixes on purpose: a knob
///     the server refuses to start on must NOT be demanded in compose.</description></item>
///   <item><description><b>Values.</b> Only the presence of the NAME in the
///     <c>environment:</c> block, because that is what decides whether a value can reach the
///     process at all. What the value should be is a deployment's business.</description></item>
/// </list>
///
/// <para><b>Two holes this list used to name are now closed, and the mechanism is the same
/// one both times.</b> <i>Names read from an inline string literal</i> — 25 of them, which
/// is where the sixth passthrough instance lived — are declared in <c>ServerEnv</c> as of
/// #404 and are gated like any other. That change immediately exposed a seventh:
/// <c>GAMESERVER_FIELD_DELTA</c> had been added to <c>gameserver-dotnet</c> and never to
/// <c>gameserver-dotnet-map02</c>. Declaring a knob's name as a constant is what brings it
/// under this gate, which makes the right shape the rewarded one; it has now found a fresh
/// instance on each of the two occasions it was applied. <i>The Kubernetes manifests</i> had
/// the same shape of gap and were not read here; they are read by
/// <see cref="FleetEnvPassthroughTests"/> as of #400, and both gates take their list of
/// knobs from <see cref="DeclaredKnobs"/> so that neither can drift from the other.</para>
///
/// <para><b>Both services, not one.</b> <c>gameserver-dotnet-map02</c> declares its own
/// <c>environment:</c> block and inherits nothing from <c>gameserver-dotnet</c>, so a fix
/// that covers one leaves the two maps running different configurations from the same
/// <c>.env</c> — which is harder to find than the original gap, because the knob now
/// demonstrably works and only works *somewhere*.</para>
/// </summary>
public class ComposeEnvPassthroughTests
{
    /// <summary>
    /// The services under the gate: the compose file, relative to <c>backend/deploy/</c>,
    /// and the service key inside it.
    /// </summary>
    /// <remarks>
    /// A list rather than a scan of every service in every file, because most services
    /// here are not the game server and must NOT carry these variables — Redis and Nakama
    /// declaring <c>GAMESERVER_ENEMY_HP</c> would be noise that this test then demanded
    /// forever. Adding a third game-server service means adding a row here, which is the
    /// one manual step this design keeps and the smallest one available.
    /// </remarks>
    public static readonly (string File, string Service)[] GatedServices =
    {
        ("docker-compose.yml", "gameserver-dotnet"),
        ("docker-compose.override.yml", "gameserver-dotnet-map02"),
    };

    /// <summary>
    /// Knobs deliberately NOT passed through compose, each with the reason.
    /// </summary>
    /// <remarks>
    /// <para><b>Empty until #404, and no longer.</b> While that issue was open this gate
    /// only saw names declared as <c>const string</c>, and every such name was one an
    /// operator may want to set, so the honest exclusion list was the empty one. #404
    /// widened the input: the 25 names <c>Program.cs</c> used to read from inline literals
    /// are now declared in <c>ServerEnv</c> and are gated like any other. Seven of them do
    /// NOT belong in a compose <c>environment:</c> block, and the reason is never "it is
    /// awkward to add" — it is that forwarding them from a shared <c>.env</c> would be
    /// inert or actively wrong. Each entry below says which.</para>
    ///
    /// <para>The seven divide into three kinds. <b>Agones-only</b>: compose sets no
    /// <c>AGONES_ENABLED</c>, so the sidecar-dependent knobs have nothing to act on.
    /// <b>Structurally inert under this file</b>: compose pins values that make the knob
    /// unreachable, and the entry names the pin rather than the knob. <b>Per-map, not
    /// per-deployment</b>: one shared value would configure both map servers identically,
    /// which for a map's own dimensions is the bug and not the feature — compose already
    /// writes that class of value as a literal in each service block (<c>GAMESERVER_MAP_ID</c>
    /// is <c>map_01</c> here and <c>map_02</c> there), and that is how these are set too.</para>
    ///
    /// <para>It exists as a dictionary rather than as a name pattern because a pattern
    /// excludes knobs nobody considered. An entry here is a decision somebody made, with a
    /// reason the next reader can disagree with;
    /// <see cref="EveryExclusion_NamesALiveConstantAndCarriesAReason"/> makes sure it stays
    /// one — a stale entry for a knob that no longer exists fails rather than quietly
    /// widening the hole.</para>
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, string> Excluded =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // ── Agones-only: compose declares no AGONES_ENABLED ──────────────
            ["GAMESERVER_ADVERTISE_HOST"] =
                "Agones only, and compose runs no Agones sidecar (no AGONES_ENABLED in " +
                "either file). Program.cs reads it ONLY when the Agones status read " +
                "succeeded, because it replaces the host half of an address whose port only " +
                "Agones knows; with the sidecar off the server logs a start-up warning and " +
                "ignores it. The compose equivalent is GAMESERVER_PUBLIC_ADDR, which is the " +
                "full host:port and IS forwarded. Exactly one of the two applies to any " +
                "deployment, so passing both here would guarantee one is always inert.",

            ["GAMESERVER_REGISTER_ON_ALLOCATED"] =
                "Agones only, same reason. It holds the registry entry back until Agones " +
                "reports the GameServer Allocated; with no sidecar there is no allocation to " +
                "wait for, and Program.cs logs and ignores it rather than never registering. " +
                "It IS gated on the three fleets, where it means something — see " +
                "FleetEnvPassthroughTests.",

            // ── Structurally inert under THIS file's own pinned values ───────
            ["GAMESERVER_TICK_RATE"] =
                "Cannot take effect under compose, and the cause is this file rather than the " +
                "knob. Program.cs applies the legacy scalar only when `tickRateSet && " +
                "!anySimVar`, and both compose services set SIM_CRITICAL_HZ, SIM_WORLD_HZ and " +
                "SIM_BACKGROUND_HZ unconditionally (`${SIM_WORLD_HZ:-15}` is never unset), so " +
                "anySimVar is always true. The resolved SimulationRates is also always " +
                "non-null, so ServerOptions.TickRate never reaches " +
                "SimulationRates.Uniform either. Forwarding it would add a knob that reads as " +
                "settable and provably does nothing — worse than its absence. Set the three " +
                "SIM_* rates, which are already forwarded.",

            ["GAMESERVER_JOIN_DEADLINE_SECONDS"] =
                "Dungeon mode only (ADR-26: it reclaims a dungeon pod that is allocated and " +
                "then never joined), and compose has no dungeon service — both game-server " +
                "services pin `GAMESERVER_MODE: map` as a literal. Program.cs ignores it on a " +
                "map server. It IS gated on the dungeon fleet, which is the deployment that " +
                "runs `GAMESERVER_MODE: dungeon` and where it had never been passed.",

            // ── One-shot invocation mode, not a deployment setting ───────────
            ["GAMESERVER_MIGRATE_ONLY"] =
                "A one-shot invocation mode, not configuration: it applies pending migrations " +
                "and exits WITHOUT listening. CD runs it as its own `--migrate-only` " +
                "invocation at a deterministic point before the deploy step. Forwarding it " +
                "from a shared .env is the one entry here that would be actively harmful " +
                "rather than merely inert — setting it once would make every long-running " +
                "game server in the stack exit at boot instead of serving, and the symptom " +
                "(containers that start, log a migration, and stop) names nothing.",

            // ── Per-map, not per-deployment ──────────────────────────────────
            ["GAMESERVER_MAP_WIDTH"] =
                "A property of the map, not of the deployment. The two compose services are " +
                "two DIFFERENT maps, so a single ${GAMESERVER_MAP_WIDTH} from a shared .env " +
                "would resize both to the same dimensions — and map size is not cosmetic: " +
                "GAMESERVER_AOI_RADIUS is validated against it, and a radius reaching the " +
                "map's diagonal disables interest filtering altogether. Set it the way this " +
                "file already sets per-map values, as a literal in the service's own block " +
                "next to GAMESERVER_MAP_ID.",

            ["GAMESERVER_MAP_HEIGHT"] =
                "A property of the map, not of the deployment — see GAMESERVER_MAP_WIDTH " +
                "above; the two are set together or not at all, since MapBounds.FromSize " +
                "takes both.",
        };

    // ── The gate ─────────────────────────────────────────────────────────────

    public static TheoryData<string, string> Services()
    {
        var data = new TheoryData<string, string>();
        foreach ((string file, string service) in GatedServices) data.Add(file, service);
        return data;
    }

    [Theory]
    [MemberData(nameof(Services))]
    public void EveryDeclaredKnob_ReachesTheService(string file, string service)
    {
        IReadOnlyCollection<string> declared = DeclaredEnvNames();
        string path = ComposePath(file);
        IReadOnlyCollection<string> present = ComposeEnvironment.NamesIn(
            File.ReadAllText(path), service, path);

        List<string> missing = declared
            .Where(n => !Excluded.ContainsKey(n))
            .Where(n => !present.Contains(n))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0, Explain(file, service, missing));
    }

    /// <summary>
    /// The failure message is the whole value of this test to the person who trips it, so
    /// it is built once and shared with the guard-fires test below — which asserts on it,
    /// so a message that stopped naming the variable would fail.
    /// </summary>
    private static string Explain(string file, string service, IReadOnlyCollection<string> missing)
    {
        var sb = new StringBuilder();
        sb.Append(missing.Count)
          .Append(missing.Count == 1 ? " knob is" : " knobs are")
          .Append(" declared by the server but missing from the `environment:` block of service `")
          .Append(service).Append("` in backend/deploy/").Append(file).AppendLine(":");
        foreach (string name in missing) sb.Append("  ").AppendLine(name);
        sb.AppendLine();
        sb.AppendLine(
            "Docker passes nothing a service did not declare, so each of these is settable in " +
            "deploy/.env and silently ignored: the server runs its compiled default and reports " +
            "that default truthfully. Add one line per name to that block, in the form " +
            "`NAME: ${NAME:-}` so an unset variable stays unset rather than becoming the empty " +
            "string the parsers read as \"not configured\". Every gated service needs it; " +
            "gameserver-dotnet-map02 inherits nothing.");
        return sb.ToString();
    }

    // ── Requirement: this gate must never check nothing ──────────────────────

    /// <summary>
    /// <b>The most important test in this file.</b> Everything above compares a set of
    /// declared names against a compose block; if the first set is empty the comparison is
    /// vacuously satisfied and the gate reports green while checking nothing. That failure
    /// mode has no symptom anywhere — it is the same shape as the counters that could only
    /// report zero, and as a dependency-gated test that returns early instead of skipping.
    /// </summary>
    [Fact]
    public void DeclaredEnvNames_IsNotEmpty_AndStillFindsEveryKnownSettingsType()
    {
        IReadOnlyCollection<string> declared = DeclaredEnvNames();

        Assert.True(declared.Count > 0,
            "No GAMESERVER_* constants were found by reflection at all. The gate below " +
            "would have passed vacuously. Either every settings type moved out of the " +
            "GameServer assembly, or the constants stopped being `const string` fields — " +
            "fix DeclaredKnobs.Names, do not delete this assertion.");

        foreach (string sentinel in DeclaredKnobs.Sentinels)
        {
            Assert.True(declared.Contains(sentinel),
                $"{sentinel} was not found by reflection. Its settings type has moved, been " +
                "renamed, or stopped declaring its variable name as a const — so that whole " +
                "family is no longer gated and the gate cannot tell you which one.");
        }
    }

    /// <summary>
    /// The compose parser refuses to return an empty or absent answer, for the same reason.
    /// A parser that returned nothing for a service whose block it could not find would
    /// make the gate report every knob as missing — loud and wrong — or, if the comparison
    /// were the other way round, report nothing as missing and be silent and wrong.
    /// </summary>
    /// <remarks>
    /// The expected phrase is the one that DISTINGUISHES this failure from the other
    /// three, not merely a word the message happens to contain. That is not pedantry: with
    /// a looser expectation ("environment") a mutation removing the missing-block throw
    /// still passed this test, because the empty-block throw caught the same input and
    /// reported a *different and wrong* cause — "has an `environment:` block this reader
    /// parsed as empty" for a service that has no such block at all. A diagnostic that
    /// survives by naming the wrong reason is worse than none, because it sends the reader
    /// to look at a block that is not there.
    /// </remarks>
    [Theory]
    [InlineData("services:\n  other:\n    image: x\n", "gameserver-dotnet", "not found")]
    [InlineData("services:\n  gs:\n    image: x\n", "gs", "no `environment:` block")]
    [InlineData("services:\n  gs:\n    environment:\n    image: x\n", "gs", "parsed as empty")]
    [InlineData("services:\n  gs:\n    environment:\n      - A=1\n", "gs", "list form")]
    public void TheComposeParser_FailsLoudlyRatherThanReturningNothing(
        string yaml, string service, string expected)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => ComposeEnvironment.NamesIn(yaml, service, "test.yml"));

        Assert.Contains(expected, ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("test.yml", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Both compose files exist where this test expects them. Separate from the gate so
    /// that a moved or renamed file reports as a moved file rather than as twenty-seven
    /// missing knobs — and so it reports at all, instead of being skipped as a "missing
    /// dependency". These files are committed to this repository: their absence is this
    /// test being broken, never an environment that cannot run it.
    /// </summary>
    [Fact]
    public void BothComposeFilesAreWhereThisTestLooks()
    {
        foreach ((string file, _) in GatedServices)
        {
            string path = ComposePath(file);
            Assert.True(File.Exists(path), $"compose file not found: {path}");
        }
    }

    // ── Requirement: prove the gate can fail ─────────────────────────────────

    /// <summary>
    /// The guard fires, demonstrated rather than assumed — on a synthetic compose document
    /// so the proof runs on every build instead of depending on somebody having once
    /// deleted a line from the real file by hand.
    ///
    /// <para>It asserts on the message, not merely on the failure: a gate that goes red
    /// without naming the variable and the service sends the reader to read two files, and
    /// the whole point of catching this at <c>dotnet test</c> is that the person who added
    /// the knob is told exactly what to add and where.</para>
    /// </summary>
    [Fact]
    public void TheGuardFires_AndNamesTheVariableAndTheService()
    {
        const string yaml = """
            services:
              gameserver-dotnet:
                image: x
                environment:
                  GAMESERVER_ENEMY_MAX: ${GAMESERVER_ENEMY_MAX:-}
                  # GAMESERVER_BOTS is deliberately absent from this fixture.
                  REDIS_ADDR: redis:6379
            """;

        IReadOnlyCollection<string> present =
            ComposeEnvironment.NamesIn(yaml, "gameserver-dotnet", "fixture.yml");

        List<string> missing = new[] { "GAMESERVER_ENEMY_MAX", "GAMESERVER_BOTS" }
            .Where(n => !present.Contains(n)).ToList();

        string message = Explain("fixture.yml", "gameserver-dotnet", missing);

        Assert.Equal(new[] { "GAMESERVER_BOTS" }, missing);
        Assert.Contains("GAMESERVER_BOTS", message, StringComparison.Ordinal);
        Assert.Contains("gameserver-dotnet", message, StringComparison.Ordinal);
        Assert.Contains("fixture.yml", message, StringComparison.Ordinal);

        // And the control: the variable that IS present must not be reported, or the
        // message above would be satisfied by a gate that reports everything.
        Assert.DoesNotContain("GAMESERVER_ENEMY_MAX", message, StringComparison.Ordinal);
    }

    // ── Requirement: exclusions are explicit and stay honest ─────────────────

    /// <summary>
    /// Every exclusion names a knob that still exists and carries a reason.
    ///
    /// <para>A stale exclusion is the quiet way this gate stops gating: the knob is
    /// renamed, the old name keeps sitting in the dictionary, and the new name is never
    /// checked by anyone. An entry with an empty reason is the other way — an exclusion
    /// nobody can evaluate is indistinguishable from an oversight.</para>
    /// </summary>
    [Fact]
    public void EveryExclusion_NamesALiveConstantAndCarriesAReason()
    {
        IReadOnlyCollection<string> declared = DeclaredEnvNames();

        foreach ((string name, string reason) in Excluded)
        {
            Assert.True(declared.Contains(name),
                $"{name} is excluded from the compose gate but is no longer declared by the " +
                "server. Remove the exclusion — a stale one hides whatever replaced it.");
            Assert.False(string.IsNullOrWhiteSpace(reason),
                $"{name} is excluded with no reason given.");
        }
    }

    // ── Reflection over the declared knobs ───────────────────────────────────

    /// <summary>
    /// Delegates to <see cref="DeclaredKnobs.Names"/> — deliberately NOT a second copy of
    /// the reflection. There are two gates and there must be one answer to "which knobs
    /// exist": two lists drift, and the drift is invisible, because the gate that still
    /// knows about a knob goes on passing while the one that forgot it stops checking.
    /// </summary>
    private static IReadOnlyCollection<string> DeclaredEnvNames() => DeclaredKnobs.Names();

    /// <summary>Absolute path to a compose file. See <see cref="DeclaredKnobs.DeployPath"/>.</summary>
    private static string ComposePath(string file) => DeclaredKnobs.DeployPath(file);
}

/// <summary>
/// The one piece of YAML reading this gate needs: the variable names declared in one
/// service's <c>environment:</c> block.
/// </summary>
/// <remarks>
/// <para><b>Hand-written rather than a YAML library</b>, because this module's dependency
/// rule is "System.Text.Json, Microsoft.Extensions.Logging, xunit, and nothing else", and
/// a whole YAML parser to read a list of keys would be the largest dependency in the
/// repository serving the smallest need.</para>
///
/// <para>The cost of that choice is that this reader understands exactly the subset these
/// two files are written in — two-space indentation, services at indent 2, keys at indent
/// 4, environment entries at indent 6 in map form — and <b>throws on anything else</b>. It
/// never returns a partial or empty answer, because a lenient reader here would make the
/// gate silently stop gating, which is precisely the failure the gate exists to prevent.
/// The list form (<c>- NAME=value</c>) is legal compose and is refused with a message
/// saying so rather than read as zero names.</para>
/// </remarks>
internal static class ComposeEnvironment
{
    public static IReadOnlyCollection<string> NamesIn(string yaml, string service, string origin)
    {
        string[] lines = yaml.Replace("\r\n", "\n").Split('\n');

        int start = Array.FindIndex(lines, l => l.TrimEnd() == $"  {service}:");
        if (start < 0)
        {
            throw new InvalidOperationException(
                $"service `{service}` not found in {origin}. Either it was renamed — in which " +
                "case update ComposeEnvPassthroughTests.GatedServices — or this reader no " +
                "longer understands the file's indentation. It is not an empty result.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        bool sawBlock = false;
        bool inBlock = false;

        for (int i = start + 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Length == 0 || line.Trim().Length == 0) continue;

            // Dedent to indent<=2 ends this service.
            if (!line.StartsWith("   ", StringComparison.Ordinal)) break;

            if (line.TrimEnd() == "    environment:")
            {
                sawBlock = true;
                inBlock = true;
                continue;
            }

            // Any other key at the service's own key indent closes the block.
            if (inBlock && line.StartsWith("    ", StringComparison.Ordinal)
                        && !line.StartsWith("     ", StringComparison.Ordinal))
            {
                inBlock = false;
                continue;
            }

            if (!inBlock) continue;

            string entry = line.Trim();
            if (entry.StartsWith('#')) continue;

            if (entry.StartsWith("- ", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"service `{service}` in {origin} writes its environment in LIST form " +
                    "(`- NAME=value`). This reader only understands the map form " +
                    "(`NAME: value`) that both files use, and refuses rather than reporting " +
                    "zero variables for a block that has some.");
            }

            int colon = entry.IndexOf(':');
            if (colon <= 0) continue;

            string name = entry[..colon].Trim();
            if (name.Length > 0) names.Add(name);
        }

        if (!sawBlock)
        {
            throw new InvalidOperationException(
                $"service `{service}` in {origin} has no `environment:` block. A service that " +
                "runs the game server and declares no environment forwards nothing, so every " +
                "knob is silently ignored — which is the condition this gate exists to catch, " +
                "not a reason to report zero missing.");
        }

        if (names.Count == 0)
        {
            throw new InvalidOperationException(
                $"service `{service}` in {origin} has an `environment:` block this reader " +
                "parsed as empty. That is a reader failure, not a valid state — the gate " +
                "would otherwise report every knob as missing for the wrong reason.");
        }

        return names;
    }
}
