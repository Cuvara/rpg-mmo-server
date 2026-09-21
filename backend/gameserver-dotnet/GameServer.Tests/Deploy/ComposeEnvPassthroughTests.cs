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
/// <c>const string</c> — 27 of them today. It does not check:</para>
/// <list type="bullet">
///   <item><description><b>Names read from an inline string literal</b> rather than a
///     constant (25 today, including <c>GAMESERVER_FIELD_DELTA</c>,
///     <c>GAMESERVER_TICK_RATE</c> and <c>GAMESERVER_KEYFRAME_INTERVAL</c>). Several of
///     those are per-service values set literally in compose rather than forwarded from
///     <c>.env</c>, so sweeping them in would demand a dozen exclusions whose reasons
///     nobody had actually decided. Declaring a knob's name as a constant is what brings it
///     under this gate, which makes the right shape the rewarded one.</description></item>
///   <item><description><b>Names built by concatenation</b>, which no reflection or grep
///     can enumerate. <c>GAMESERVER_IMPORTANCE_W_{DISTANCE,CHANGE,TYPE,COMBAT}</c> are
///     assembled from a prefix and four suffixes, so they exist nowhere as a whole string —
///     <b>this gate would not have caught the first of the three incidents above.</b> It
///     catches the second and the third, and it caught a fourth nobody had noticed. That is
///     the honest scope.</description></item>
///   <item><description><b>The Kubernetes manifests.</b> <c>deploy/k8s/app/50-fleet-map.yaml</c>
///     has the same shape of gap and is not read here.</description></item>
/// </list>
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
    /// <para><b>Empty today, and that is the honest state</b> — every
    /// <c>GAMESERVER_*</c> constant the server declares is something an operator may want
    /// to set per deployment, so every one of them belongs in both blocks.</para>
    ///
    /// <para>It exists as a dictionary rather than as a name pattern because a pattern
    /// excludes knobs nobody considered. An entry here is a decision somebody made, with a
    /// reason the next reader can disagree with;
    /// <see cref="EveryExclusion_NamesALiveConstantAndCarriesAReason"/> makes sure it stays
    /// one — a stale entry for a knob that no longer exists fails rather than quietly
    /// widening the hole.</para>
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, string> Excluded =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>
    /// Names that must be found by reflection whatever else changes.
    ///
    /// <para>The point of <see cref="DeclaredEnvNames"/> failing loudly on an empty result
    /// is that a gate which checks nothing still reports green. A bare non-empty assertion
    /// is weaker than it looks: a reflection change that matched one constant out of
    /// twenty-seven would satisfy it. These three come from three different settings types,
    /// so losing any one type is caught rather than averaged away. They are spelled out
    /// rather than read from the constants, because reading them from the same source the
    /// assertion is checking proves nothing.</para>
    /// </summary>
    private static readonly string[] Sentinels =
    {
        "GAMESERVER_ENEMY_MAX",   // EnemyAiSettings
        "GAMESERVER_BOTS",        // BotSettings
        "GAMESERVER_AOI_RADIUS",  // AoiSettings
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
            "fix DeclaredEnvNames, do not delete this assertion.");

        foreach (string sentinel in Sentinels)
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
    /// Every <c>GAMESERVER_*</c> variable name the server declares as a constant.
    /// </summary>
    /// <remarks>
    /// <para>Reflection over the assembly rather than a grep of the source, and rather
    /// than a hand-kept list. A list is the thing that just failed three times. A grep
    /// would be tied to the exact spelling <c>public const string Env… = "GAMESERVER_…"</c>
    /// and would quietly stop matching the day somebody writes the field differently — the
    /// same silent-miss this gate exists to remove. Reflection asks the compiled assembly
    /// what it actually declares.</para>
    ///
    /// <para><c>NonPublic</c> is included deliberately: a knob declared <c>internal</c> is
    /// still a knob an operator sets, and its visibility to C# has nothing to do with
    /// whether Docker forwards it.</para>
    /// </remarks>
    private static IReadOnlyCollection<string> DeclaredEnvNames()
    {
        var names = new SortedSet<string>(StringComparer.Ordinal);

        foreach (Type type in typeof(EnemyAiSettings).Assembly.GetTypes())
        {
            foreach (FieldInfo field in type.GetFields(
                         BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            {
                if (!field.IsLiteral || field.IsInitOnly) continue;
                if (field.FieldType != typeof(string)) continue;
                if (field.GetRawConstantValue() is not string value) continue;
                if (!value.StartsWith("GAMESERVER_", StringComparison.Ordinal)) continue;

                names.Add(value);
            }
        }

        return names;
    }

    // ── Locating the compose files ───────────────────────────────────────────

    /// <summary>
    /// Absolute path to a compose file, resolved by walking up from the test assembly —
    /// the same shape <c>GoldenVectors.Directory</c> uses, and for the same reason: the
    /// test binary's location relative to the repository is the one thing a test can rely
    /// on without a build-time constant.
    /// </summary>
    private static string ComposePath(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "backend", "deploy", file);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            $"backend/deploy/{file} not found above {AppContext.BaseDirectory}. The compose " +
            "files have moved and this gate is no longer checking anything — fix the path, " +
            "do not delete the test.");
    }
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
