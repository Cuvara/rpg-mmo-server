using System.Reflection;
using GameServer.Scaffolding;

namespace GameServer.Tests.Deploy;

/// <summary>
/// The one definition of "a knob this server declares", shared by every deployment
/// passthrough gate.
/// </summary>
/// <remarks>
/// <para><b>Why this is shared rather than copied.</b> There are two gates —
/// <see cref="ComposeEnvPassthroughTests"/> for the compose services and
/// <see cref="FleetEnvPassthroughTests"/> for the Agones fleets — because compose files and
/// fleet manifests are different documents with different shapes. There must NOT be two
/// answers to "which knobs exist". Two lists drift, and the drift is invisible: the gate
/// that still knows about a knob goes on passing while the one that forgot it stops
/// checking, and nothing compares them. So the manifests are read twice and the server is
/// asked once.</para>
///
/// <para>Reflection over the assembly rather than a grep of the source, and rather than a
/// hand-kept list. A hand-kept list is the thing that failed the first three times. A grep
/// would be tied to the exact spelling of the field declaration and would quietly stop
/// matching the day somebody wrote it differently — the same silent miss these gates exist
/// to remove. Reflection asks the compiled assembly what it actually declares.</para>
///
/// <para><c>NonPublic</c> is included deliberately: a knob declared <c>internal</c> — which
/// every name in <c>ServerEnv</c> is — is still a knob an operator sets, and its visibility
/// to C# has nothing to do with whether Docker or Kubernetes forwards it.</para>
/// </remarks>
internal static class DeclaredKnobs
{
    /// <summary>
    /// Names that must be found by reflection whatever else changes.
    ///
    /// <para>The point of failing loudly on an empty result is that a gate which checks
    /// nothing still reports green. A bare non-empty assertion is weaker than it looks: a
    /// reflection change that matched one constant out of fifty-six would satisfy it. These
    /// come from five different declaring types, so losing any one type is caught rather
    /// than averaged away. They are spelled out rather than read from the constants,
    /// because reading them from the same source the assertion is checking proves
    /// nothing.</para>
    /// </summary>
    public static readonly string[] Sentinels =
    {
        "GAMESERVER_ENEMY_MAX",   // EnemyAiSettings
        "GAMESERVER_BOTS",        // BotSettings
        "GAMESERVER_AOI_RADIUS",  // AoiSettings

        // ImportanceSettings, and specifically a name written as `EnvVar + "_W_…"`. It is
        // here rather than `GAMESERVER_IMPORTANCE` because the compile-time concatenation is
        // the fragile part: rewritten as a runtime concatenation it would vanish from the
        // assembly's constants and from both gates without anything else changing, which is
        // the exact shape that hid this family for the first three incidents.
        "GAMESERVER_IMPORTANCE_W_DISTANCE",

        // ServerEnv, the home for the names Program.cs used to read from inline literals.
        // This one is the sixth passthrough instance and the first that produced a visible
        // gameplay defect (#404); if the whole type were deleted or its fields turned back
        // into literals, every gate would silently stop covering twenty-five knobs.
        "GAMESERVER_MAX_SNAPSHOT_BYTES",
    };

    /// <summary>
    /// Every <c>GAMESERVER_*</c> variable name the server declares as a constant.
    /// </summary>
    public static IReadOnlyCollection<string> Names()
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

    /// <summary>
    /// Absolute path to a file under <c>backend/deploy/</c>, resolved by walking up from the
    /// test assembly — the same shape <c>GoldenVectors.Directory</c> uses, and for the same
    /// reason: the test binary's location relative to the repository is the one thing a test
    /// can rely on without a build-time constant.
    /// </summary>
    public static string DeployPath(string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(
                dir.FullName, "backend", "deploy", Path.Combine(file.Split('/')));
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException(
            $"backend/deploy/{file} not found above {AppContext.BaseDirectory}. The " +
            "deployment files have moved and this gate is no longer checking anything — fix " +
            "the path, do not delete the test.");
    }
}
