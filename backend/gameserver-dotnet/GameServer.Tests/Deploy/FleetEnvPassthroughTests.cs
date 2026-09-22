using System.Text;

namespace GameServer.Tests.Deploy;

/// <summary>
/// Every strictly-parsed <c>GAMESERVER_*</c> knob reaches every Agones fleet that runs the
/// game server, or is excluded by name with a reason.
///
/// <para><b>Why this exists.</b> <see cref="ComposeEnvPassthroughTests"/> closed this hole
/// for compose and said, in its own documentation, that the Kubernetes manifests had the
/// same shape of gap and were not read. They did, and they were not: the three fleet
/// manifests declared <b>nine</b> <c>GAMESERVER_*</c> names against compose's forty-one, so
/// the entire enemy-AI, combat and bot surface was unreachable on a cluster. A staging or
/// production fleet ran the built-in defaults — enemies attacking, respawn on, a
/// 30 + 45/player population and an 8192-byte snapshot budget — with no way to change any of
/// it short of editing the manifest (#400).</para>
///
/// <para>That hole was documented, in three places. Documented is exactly what the compose
/// blocks were, and the comment failed four times in a row; a documented hole and an
/// invisible one behave identically the day somebody needs the knob.</para>
///
/// <para><b>Three fleets, not one</b> — the same reason the compose gate reads two services.
/// Each fleet declares its own <c>env:</c> list and inherits nothing from the others, so a
/// fix applied to one leaves the rest running different configurations from the same
/// ConfigMap. That failure is harder to find than the original gap, because the knob now
/// demonstrably works and only works <i>somewhere</i>. It has already happened twice in this
/// repository: <c>GAMESERVER_IMPORTANCE_W_*</c> between the two compose services (#398), and
/// <c>GAMESERVER_FIELD_DELTA</c> between them again, found by the compose gate the moment
/// #404 declared that name as a constant.</para>
///
/// <para><b>What this gate checks, precisely.</b> That the NAME is declared in the fleet's
/// <c>env:</c> list. Not the value — a value is a deployment's business. A container
/// receives nothing its spec did not name, so the name is the passthrough, and a knob whose
/// name is absent is settable in the ConfigMap and silently ignored.</para>
///
/// <para><b>Prefix fragments are not declarations.</b> <c>GAMESERVER_IMPORTANCE_W_</c>
/// appeared in <c>50-fleet-map.yaml</c> as a fragment inside a comment, naming a family of
/// four in a form that forwards none of them — the same concealment that hid that family
/// from reflection through three incidents. This reader counts <c>- name:</c> entries and
/// therefore cannot be satisfied by a fragment, a comment, or a prose mention.</para>
/// </summary>
public class FleetEnvPassthroughTests
{
    /// <summary>
    /// The fleets under the gate: the manifest, relative to <c>backend/deploy/</c>, and
    /// whether it runs <c>GAMESERVER_MODE: dungeon</c>.
    /// </summary>
    /// <remarks>
    /// A list rather than a scan of every manifest, for the same reason the compose gate
    /// keeps one: most YAML under <c>deploy/</c> is not a game server and must NOT carry
    /// these variables. Adding a fourth fleet means adding a row here, which is the one
    /// manual step this design keeps and the smallest one available.
    /// </remarks>
    public static readonly (string File, bool Dungeon)[] GatedFleets =
    {
        // The live dev fleet in `rpg-realtime`, allocated from by the compose gateway.
        ("agones/fleet-map-dotnet-dev.yaml", false),
        // Its sibling in `rpg-k8s-realtime`, with the in-cluster gateway and data tier.
        // Applied UNCHANGED to dev and staging, so anything missing here is missing on both.
        ("k8s/app/50-fleet-map.yaml", false),
        // Instanced dungeons (ADR-26).
        ("k8s/app/60-fleet-dungeon.yaml", true),
    };

    /// <summary>
    /// Knobs deliberately NOT declared in a fleet spec, each with the reason.
    /// </summary>
    /// <remarks>
    /// <para><b>Unlike the compose gate, this dictionary is not empty and never can be.</b>
    /// A fleet is not a machine with a config file: several of these values are properties
    /// of the fleet or of the scheduler, and one of them is actively forbidden. Writing an
    /// exclusion list to make a check pass is how a gate stops gating, so each entry below
    /// says what would happen if the name WERE declared, not that declaring it is
    /// inconvenient.</para>
    ///
    /// <para><see cref="EveryExclusion_NamesALiveConstantAndCarriesAReason"/> keeps them
    /// honest: an entry naming a knob the server no longer declares fails, rather than
    /// quietly widening the hole for whatever replaced it.</para>
    /// </remarks>
    private static readonly IReadOnlyDictionary<string, string> ExcludedEverywhere =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GAMESERVER_ID"] =
                "FORBIDDEN on a fleet, not merely unnecessary. Program.cs resolves " +
                "`--server-id ?? GAMESERVER_ID ?? POD_NAME`, so GAMESERVER_ID WINS over the " +
                "pod name. Every pod from one template would then self-register under one " +
                "hardcoded id while the allocator hands the client the GameServer's real " +
                "name, and every join is rejected with `Token is for a different server` — a " +
                "message that says nothing about the cause. POD_NAME must stay the only " +
                "source; the fleet manifests carry the full server-id chain in a comment.",

            ["GAMESERVER_PUBLIC_ADDR"] =
                "Absent on purpose. It replaces the WHOLE advertised address, so it cannot " +
                "carry a port that is only known at scheduling time under portPolicy: " +
                "Dynamic — and Program.cs reads it only when Agones is OFF, which on a fleet " +
                "it never is. The fleet's knob is GAMESERVER_ADVERTISE_HOST, the host half " +
                "alone, which IS declared and is composed with the Agones-assigned port " +
                "(ADR-16 decision 2). Setting this one alongside it is the classic way to " +
                "advertise a stale port to every client.",

            ["GAMESERVER_MIGRATE_ONLY"] =
                "A one-shot invocation mode, not configuration: it applies pending " +
                "migrations and exits WITHOUT listening. A fleet pod that read it true would " +
                "exit at boot, be restarted by Agones, exit again, and present as a " +
                "CrashLoopBackOff whose logs show a successful migration. CD runs migrations " +
                "as their own invocation before the deploy step.",

            ["GAMESERVER_TICK_RATE"] =
                "Cannot take effect on a fleet, and the cause is the manifest rather than the " +
                "knob. Program.cs applies the legacy scalar only when `tickRateSet && " +
                "!anySimVar`, and every fleet sets SIM_CRITICAL_HZ, SIM_WORLD_HZ and " +
                "SIM_BACKGROUND_HZ explicitly, so anySimVar is always true. Declaring it " +
                "would add a knob that reads as settable and provably does nothing. Set the " +
                "three SIM_* rates, which are declared.",

            ["GAMESERVER_MAP_WIDTH"] =
                "A property of the map, not of the deployment, and the fleets run on the " +
                "compiled-in default. It is not cosmetic — GAMESERVER_AOI_RADIUS is " +
                "validated against it and a radius reaching the map's diagonal disables " +
                "interest filtering altogether — so it belongs next to the map's own " +
                "identity as a literal, the way GAMESERVER_MAP_ID is written, rather than " +
                "forwarded from a ConfigMap shared by every fleet in the cluster.",

            ["GAMESERVER_MAP_HEIGHT"] =
                "A property of the map, not of the deployment — see GAMESERVER_MAP_WIDTH " +
                "above; the two are set together or not at all, since MapBounds.FromSize " +
                "takes both.",

            ["GAMESERVER_TRANSPORT"] =
                "Structurally coupled to a field no environment variable can reach. Choosing " +
                "`kcp` changes the wire to UDP, but the fleet's port is declared " +
                "`protocol: TCP` in the ports block above the container; forwarding this " +
                "alone would let an operator advertise kcp through the registry while the " +
                "port still speaks TCP, and every client would fail to connect to a fleet " +
                "that is Ready and healthy. Moving a fleet to KCP is a manifest change — the " +
                "port's protocol and this value together — not a ConfigMap edit.",
        };

    private static readonly IReadOnlyDictionary<string, string> MapFleetExclusions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GAMESERVER_JOIN_DEADLINE_SECONDS"] =
                "Dungeon mode only (ADR-26). Program.cs ignores it on a map server, and this " +
                "fleet runs GAMESERVER_MODE: map. It is REQUIRED on the dungeon fleet, which " +
                "is why this exclusion is keyed per fleet rather than global: a single global " +
                "entry would have excused the one deployment where the knob does something.",
        };

    private static readonly IReadOnlyDictionary<string, string> DungeonFleetExclusions =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["GAMESERVER_MAP_ID"] =
                "Absent on purpose on a dungeon fleet. A dungeon server writes its " +
                "servers:id: hash and never joins servers:map: (ADR-26 decision 8), so it " +
                "owns no map; giving it a map id would put a dungeon pod into the map " +
                "registry under an id a real map server also claims, which is ADR-2's " +
                "invariant broken by a manifest line.",
        };

    /// <summary>
    /// The exclusions in force for one fleet: the global ones, plus the set chosen by the
    /// fleet's mode.
    /// </summary>
    /// <remarks>
    /// The mode picks the set rather than the filename, so <see cref="GatedFleets"/>'s
    /// <c>Dungeon</c> flag is load-bearing: get it wrong and the gate demands
    /// GAMESERVER_MAP_ID of a dungeon fleet, or excuses GAMESERVER_JOIN_DEADLINE_SECONDS on
    /// the one deployment where it does something.
    /// <see cref="TheDungeonFlag_MatchesWhatTheManifestActuallyRuns"/> checks the flag against
    /// the manifest for that reason — a flag nothing consults would be a claim, not a guard.
    /// </remarks>
    private static IReadOnlyDictionary<string, string> ExclusionsFor(bool dungeon)
    {
        var all = new Dictionary<string, string>(ExcludedEverywhere, StringComparer.Ordinal);
        foreach ((string name, string reason) in dungeon ? DungeonFleetExclusions : MapFleetExclusions)
        {
            all[name] = reason;
        }
        return all;
    }

    private static bool IsDungeon(string file) =>
        GatedFleets.Single(f => f.File == file).Dungeon;

    // ── The gate ─────────────────────────────────────────────────────────────

    public static TheoryData<string> Fleets()
    {
        var data = new TheoryData<string>();
        foreach ((string file, _) in GatedFleets) data.Add(file);
        return data;
    }

    [Theory]
    [MemberData(nameof(Fleets))]
    public void EveryDeclaredKnob_ReachesTheFleet(string file)
    {
        IReadOnlyCollection<string> declared = DeclaredKnobs.Names();
        IReadOnlyDictionary<string, string> excluded = ExclusionsFor(IsDungeon(file));
        string path = DeclaredKnobs.DeployPath(file);
        IReadOnlyCollection<string> present = FleetEnvironment.NamesIn(
            File.ReadAllText(path), path);

        List<string> missing = declared
            .Where(n => !excluded.ContainsKey(n))
            .Where(n => !present.Contains(n))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.True(missing.Count == 0, Explain(file, missing));
    }

    /// <summary>
    /// The failure message is the whole value of this test to the person who trips it, so it
    /// is built once and shared with the guard-fires test below — which asserts on it, so a
    /// message that stopped naming the variable and the manifest would fail.
    /// </summary>
    private static string Explain(string file, IReadOnlyCollection<string> missing)
    {
        var sb = new StringBuilder();
        sb.Append(missing.Count)
          .Append(missing.Count == 1 ? " knob is" : " knobs are")
          .Append(" declared by the server but missing from the `env:` list of the fleet in backend/deploy/")
          .Append(file).AppendLine(":");
        foreach (string name in missing) sb.Append("  ").AppendLine(name);
        sb.AppendLine();
        sb.AppendLine(
            "A container receives nothing its spec did not name, so each of these is " +
            "settable in the gameserver-config ConfigMap and silently ignored: the server " +
            "runs its compiled default and /status reports that default truthfully. Add one " +
            "entry per name to that `env:` list, as a configMapKeyRef with `optional: true` " +
            "so a cluster that sets nothing keeps the default. Every gated fleet needs it — " +
            "they inherit nothing from each other, and this manifest set is applied " +
            "unchanged to dev and staging. If the knob genuinely does not belong on this " +
            "fleet, add it to ExcludedEverywhere or the per-fleet dictionary WITH THE REASON.");
        return sb.ToString();
    }

    // ── Requirement: this gate must never check nothing ──────────────────────

    /// <summary>
    /// <b>The most important test in this file.</b> The gate compares a set of declared
    /// names against a fleet's env list; if the first set is empty the comparison is
    /// vacuously satisfied and the gate reports green while checking nothing. That failure
    /// mode has no symptom anywhere.
    /// </summary>
    [Fact]
    public void DeclaredEnvNames_IsNotEmpty_AndStillFindsEveryKnownSettingsType()
    {
        IReadOnlyCollection<string> declared = DeclaredKnobs.Names();

        Assert.True(declared.Count > 0,
            "No GAMESERVER_* constants were found by reflection at all. The gate above " +
            "would have passed vacuously. Either every settings type moved out of the " +
            "GameServer assembly, or the constants stopped being `const string` fields — " +
            "fix DeclaredKnobs.Names, do not delete this assertion.");

        foreach (string sentinel in DeclaredKnobs.Sentinels)
        {
            Assert.True(declared.Contains(sentinel),
                $"{sentinel} was not found by reflection. Its declaring type has moved, been " +
                "renamed, or stopped declaring its variable name as a const — so that whole " +
                "family is no longer gated and the gate cannot tell you which one.");
        }
    }

    /// <summary>
    /// The fleet parser refuses to return an empty or absent answer, for the same reason.
    /// </summary>
    /// <remarks>
    /// Each expected phrase is the one that DISTINGUISHES its failure from the other three,
    /// not merely a word the message happens to contain. With a looser expectation a
    /// mutation that removed one throw would still pass here, because another throw caught
    /// the same input and reported a DIFFERENT AND WRONG cause — and a diagnostic that
    /// survives by naming the wrong reason is worse than none, because it sends the reader
    /// to look at something that is not there.
    /// </remarks>
    [Theory]
    // No env: block at all — a container that declares no environment forwards nothing.
    [InlineData("spec:\n  containers:\n    - name: gameserver\n", "no `env:` list")]
    // Two of them: which container did the reader just check? Refuse rather than guess.
    [InlineData("              env:\n                - name: A\n              env:\n                - name: B\n",
        "more than one")]
    // Present but yielding nothing.
    [InlineData("              env:\n              resources: {}\n", "parsed as empty")]
    // Entries that are not `- name:` at all: a reader that skipped them would report a
    // block of variables as zero variables.
    [InlineData("              env:\n                - valueFrom: x\n", "parsed as empty")]
    public void TheFleetParser_FailsLoudlyRatherThanReturningNothing(string yaml, string expected)
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => FleetEnvironment.NamesIn(yaml, "test.yaml"));

        Assert.Contains(expected, ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("test.yaml", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// All three manifests exist where this test expects them. Separate from the gate so
    /// that a moved or renamed file reports as a moved file rather than as forty missing
    /// knobs — and so it reports at all, instead of being skipped as a missing dependency.
    /// </summary>
    [Fact]
    public void EveryGatedFleetFileIsWhereThisTestLooks()
    {
        foreach ((string file, _) in GatedFleets)
        {
            string path = DeclaredKnobs.DeployPath(file);
            Assert.True(File.Exists(path), $"fleet manifest not found: {path}");
        }
    }

    /// <summary>
    /// The reader counts <c>env:</c> entries and nothing else.
    ///
    /// <para>This is the specific trap these manifests set. A naive scan for <c>- name:</c>
    /// also matches the port named <c>game</c>, the container named <c>gameserver</c>, the
    /// <c>imagePullSecrets</c> entry and the volume and volumeMount named
    /// <c>nakama-tls-pin</c> — all of them at the SAME indentation as an env entry in these
    /// files. A gate that counted those would report names nobody declared, and would go
    /// green on a fleet whose env list was empty as long as it had ports.</para>
    /// </summary>
    [Fact]
    public void TheFleetParser_ReadsOnlyTheEnvList_NotPortsVolumesOrContainers()
    {
        const string yaml = """
            spec:
              template:
                spec:
                  ports:
                    - name: game
                      containerPort: 9000
                  template:
                    spec:
                      imagePullSecrets:
                        - name: registry-creds
                      containers:
                        - name: gameserver
                          env:
                            - name: GAMESERVER_MODE
                              value: "map"
                          volumeMounts:
                            - name: nakama-tls-pin
                              mountPath: /etc/nakama-tls
                      volumes:
                        - name: nakama-tls-pin
            """;

        IReadOnlyCollection<string> present = FleetEnvironment.NamesIn(yaml, "fixture.yaml");

        Assert.Equal(new[] { "GAMESERVER_MODE" }, present);
        foreach (string decoy in new[] { "game", "gameserver", "registry-creds", "nakama-tls-pin" })
        {
            Assert.DoesNotContain(decoy, present);
        }
    }

    // ── Requirement: prove the gate can fail ─────────────────────────────────

    /// <summary>
    /// The guard fires, demonstrated rather than assumed — on a synthetic manifest, so the
    /// proof runs on every build instead of depending on somebody having once deleted a line
    /// from a real fleet by hand.
    ///
    /// <para>It asserts on the message, not merely on the failure: a gate that goes red
    /// without naming the variable and the manifest sends the reader to read three files.</para>
    /// </summary>
    [Fact]
    public void TheGuardFires_AndNamesTheVariableAndTheFleet()
    {
        const string yaml = """
            containers:
              - name: gameserver
                env:
                  - name: GAMESERVER_ENEMY_MAX
                    value: "10"
                  # GAMESERVER_BOTS is deliberately absent from this fixture.
                  - name: REDIS_ADDR
                    value: redis:6379
            """;

        IReadOnlyCollection<string> present = FleetEnvironment.NamesIn(yaml, "fixture.yaml");

        List<string> missing = new[] { "GAMESERVER_ENEMY_MAX", "GAMESERVER_BOTS" }
            .Where(n => !present.Contains(n)).ToList();

        string message = Explain("k8s/app/50-fleet-map.yaml", missing);

        Assert.Equal(new[] { "GAMESERVER_BOTS" }, missing);
        Assert.Contains("GAMESERVER_BOTS", message, StringComparison.Ordinal);
        Assert.Contains("k8s/app/50-fleet-map.yaml", message, StringComparison.Ordinal);

        // And the control: the variable that IS present must not be reported, or the message
        // above would be satisfied by a gate that reports everything.
        Assert.DoesNotContain("GAMESERVER_ENEMY_MAX", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A prefix fragment does not satisfy this gate.
    ///
    /// <para><c>GAMESERVER_IMPORTANCE_W_</c> sat in <c>50-fleet-map.yaml</c> naming a family
    /// of four, in a form that forwards none of them, for as long as that file existed. This
    /// asserts the reader is not fooled by the fragment — including when it appears as a
    /// real env entry and not merely in a comment, which is the stronger case.</para>
    /// </summary>
    [Fact]
    public void APrefixFragment_DoesNotCountAsTheNamesItNames()
    {
        const string yaml = """
            containers:
              - name: gameserver
                env:
                  # Per-factor overrides: GAMESERVER_IMPORTANCE_W_{DISTANCE,CHANGE,TYPE,COMBAT}
                  - name: GAMESERVER_IMPORTANCE_W_
                    value: ""
            """;

        IReadOnlyCollection<string> present = FleetEnvironment.NamesIn(yaml, "fixture.yaml");

        Assert.Contains("GAMESERVER_IMPORTANCE_W_", present);
        foreach (string real in new[]
                 {
                     "GAMESERVER_IMPORTANCE_W_DISTANCE", "GAMESERVER_IMPORTANCE_W_CHANGE",
                     "GAMESERVER_IMPORTANCE_W_TYPE", "GAMESERVER_IMPORTANCE_W_COMBAT",
                 })
        {
            Assert.DoesNotContain(real, present);
        }
    }

    // ── Requirement: exclusions are explicit and stay honest ─────────────────

    /// <summary>
    /// Every exclusion names a knob that still exists and carries a reason.
    ///
    /// <para>A stale exclusion is the quiet way this gate stops gating: the knob is renamed,
    /// the old name keeps sitting in the dictionary, and the new name is never checked by
    /// anyone. An entry with an empty reason is the other way — an exclusion nobody can
    /// evaluate is indistinguishable from an oversight.</para>
    ///
    /// <para>It also fails on an exclusion for a name that IS declared in the manifest. That
    /// is not tidiness: an excused name that is present anyway means the excuse is wrong, and
    /// a reader comparing the two would have to decide which to believe.</para>
    /// </summary>
    [Fact]
    public void EveryExclusion_NamesALiveConstantAndCarriesAReason()
    {
        IReadOnlyCollection<string> declared = DeclaredKnobs.Names();

        foreach ((string file, bool dungeon) in GatedFleets)
        {
            IReadOnlyCollection<string> present =
                FleetEnvironment.NamesIn(File.ReadAllText(DeclaredKnobs.DeployPath(file)), file);

            foreach ((string name, string reason) in ExclusionsFor(dungeon))
            {
                Assert.True(declared.Contains(name),
                    $"{name} is excluded from the fleet gate for {file} but is no longer " +
                    "declared by the server. Remove the exclusion — a stale one hides " +
                    "whatever replaced it.");
                Assert.False(string.IsNullOrWhiteSpace(reason),
                    $"{name} is excluded for {file} with no reason given.");
                Assert.False(present.Contains(name),
                    $"{name} is excluded from the fleet gate for {file} — on the grounds that " +
                    $"it does not belong there — and yet {file} declares it. One of the two is " +
                    "wrong. Delete the exclusion if the knob belongs, or the env entry if it " +
                    "does not.");
            }
        }
    }

    /// <summary>
    /// The dungeon fleet is the one that runs <c>GAMESERVER_MODE: dungeon</c>, and the map
    /// fleets are not.
    /// </summary>
    /// <remarks>
    /// <see cref="GatedFleets"/> carries a <c>Dungeon</c> flag that decides which exclusion
    /// set applies, and that flag is a claim about a file. If it were wrong — or if a fleet
    /// were switched from map to dungeon without the row being updated — the gate would
    /// demand GAMESERVER_JOIN_DEADLINE_SECONDS of a map server, or excuse it on the one
    /// deployment that needs it, and in the second case it would do so silently. So the claim
    /// is checked against the manifest rather than trusted.
    /// </remarks>
    [Fact]
    public void TheDungeonFlag_MatchesWhatTheManifestActuallyRuns()
    {
        foreach ((string file, bool dungeon) in GatedFleets)
        {
            string yaml = File.ReadAllText(DeclaredKnobs.DeployPath(file));
            bool declaresDungeonMode = yaml.Contains("value: \"dungeon\"", StringComparison.Ordinal);

            Assert.True(declaresDungeonMode == dungeon,
                $"GatedFleets says {file} is " + (dungeon ? "a dungeon" : "a map") +
                " fleet, but the manifest says otherwise. The flag picks which exclusions " +
                "apply, so a wrong one either demands a dungeon-only knob of a map server or " +
                "excuses it on the only fleet where it does anything.");
        }
    }
}

/// <summary>
/// The one piece of YAML reading this gate needs: the variable names declared in a fleet
/// container's <c>env:</c> list.
/// </summary>
/// <remarks>
/// <para><b>Hand-written rather than a YAML library</b>, for the same reason the compose
/// reader is: this module's dependency rule is "System.Text.Json,
/// Microsoft.Extensions.Logging, xunit, and nothing else", and a whole YAML parser to read a
/// list of keys would be the largest dependency in the repository serving the smallest
/// need.</para>
///
/// <para>The cost is that this reader understands exactly the subset these three files are
/// written in — one <c>env:</c> key, its entries as <c>- name: X</c> at one deeper
/// indentation — and <b>throws on anything else</b>. It never returns a partial or empty
/// answer, because a lenient reader here would make the gate silently stop gating, which is
/// precisely the failure the gate exists to prevent.</para>
///
/// <para><b>It is scoped to the env list on purpose.</b> These manifests contain
/// <c>- name:</c> entries under <c>ports:</c>, <c>imagePullSecrets:</c>, <c>containers:</c>,
/// <c>volumeMounts:</c> and <c>volumes:</c>, several of them at the same indentation as an
/// env entry. Counting those would let a fleet with an empty env list pass on the strength of
/// its ports.</para>
/// </remarks>
internal static class FleetEnvironment
{
    public static IReadOnlyCollection<string> NamesIn(string yaml, string origin)
    {
        string[] lines = yaml.Replace("\r\n", "\n").Split('\n');

        List<int> starts = new();
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimEnd().EndsWith("env:", StringComparison.Ordinal)
                && lines[i].TrimStart().StartsWith("env:", StringComparison.Ordinal))
            {
                starts.Add(i);
            }
        }

        if (starts.Count == 0)
        {
            throw new InvalidOperationException(
                $"no `env:` list found in {origin}. A container that runs the game server and " +
                "declares no environment forwards nothing, so every knob is silently ignored " +
                "— which is the condition this gate exists to catch, not a reason to report " +
                "zero missing.");
        }

        if (starts.Count > 1)
        {
            throw new InvalidOperationException(
                $"{origin} contains more than one `env:` list ({starts.Count}). This reader " +
                "cannot tell which one belongs to the game-server container, and guessing " +
                "would mean checking the wrong container's variables while reporting success " +
                "for the right one. Split the manifest or teach this reader about containers.");
        }

        int start = starts[0];
        int envIndent = Indent(lines[start]);
        var names = new HashSet<string>(StringComparer.Ordinal);

        for (int i = start + 1; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.Trim().Length == 0) continue;

            // A dedent to the env key's own level or shallower ends the list.
            if (Indent(line) <= envIndent) break;

            string entry = line.Trim();
            if (entry.StartsWith('#')) continue;
            if (!entry.StartsWith("- name:", StringComparison.Ordinal)) continue;

            string name = entry["- name:".Length..].Trim().Trim('"', '\'');
            if (name.Length > 0) names.Add(name);
        }

        if (names.Count == 0)
        {
            throw new InvalidOperationException(
                $"the `env:` list in {origin} was parsed as empty. That is a reader failure, " +
                "not a valid state — the gate would otherwise report every knob as missing " +
                "for the wrong reason.");
        }

        return names;
    }

    private static int Indent(string line) => line.Length - line.TrimStart().Length;
}
