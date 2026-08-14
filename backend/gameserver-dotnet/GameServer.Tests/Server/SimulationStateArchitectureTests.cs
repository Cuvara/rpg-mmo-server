using System.Reflection;
using GameServer.Server;

namespace GameServer.Tests.Server;

/// <summary>
/// Turns "gameplay state lives in the world, not in classes" from a comment into a
/// constraint.
///
/// <para><b>Why this exists.</b> ADR-12 says simulation state belongs on components. The
/// rule was honour-system, and it was already broken behind the seam on the day the seam
/// shipped: <c>EnemySpawnSystem</c> kept its wave accumulator and next-id counter as
/// private instance fields. Nothing failed, because nothing was looking. Those two fields
/// are now an <c>EnemySpawnState</c> component, and this test is what stops the next pair
/// from appearing.</para>
///
/// <para>The failure it prevents is not stylistic. State in a field is invisible to the
/// world: it cannot be snapshotted, persisted, or reset when the world is, and two
/// instances of the same system silently disagree about it. It also blocks the parallel
/// step the scheduler is being shaped for — a system with hidden mutable state cannot be
/// reasoned about from its declared component access, because its declaration does not
/// mention the state.</para>
///
/// <para>The one sanctioned exception is <see cref="SimulationScratchAttribute"/>, for
/// reusable buffers that carry nothing across ticks. Marking a field with it is a claim
/// that resetting it at any tick boundary would change nothing but allocation, and that
/// claim is on the person who applies it.</para>
/// </summary>
public class SimulationStateArchitectureTests
{
    /// <summary>
    /// Simulation phases, the systems they run, and any nested types those systems use.
    /// Nested types are included because moving state into a private nested helper is the
    /// obvious way around the rule.
    /// </summary>
    private static IEnumerable<Type> SimulationTypes()
    {
        Assembly assembly = typeof(ISimulationPhase).Assembly;

        var roots = assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => typeof(ISimulationPhase).IsAssignableFrom(t) ||
                        typeof(IEcsSystem).IsAssignableFrom(t))
            .ToList();

        foreach (Type root in roots)
        {
            yield return root;
            foreach (Type nested in root.GetNestedTypes(
                         BindingFlags.Public | BindingFlags.NonPublic))
            {
                yield return nested;
            }
        }
    }

    private static IEnumerable<FieldInfo> MutableInstanceFields(Type type) =>
        type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(f => !f.IsInitOnly)
            .Where(f => !f.IsLiteral)
            .Where(f => f.GetCustomAttribute<SimulationScratchAttribute>() == null)
            // Compiler-generated backing fields for auto-properties are covered by the
            // property rule below; excluding them here keeps the message about real fields.
            .Where(f => !f.Name.Contains('<'));

    [Fact]
    public void SimulationPhasesAndSystems_HoldNoMutableInstanceState()
    {
        var offenders = new List<string>();

        foreach (Type type in SimulationTypes())
        {
            foreach (FieldInfo field in MutableInstanceFields(type))
            {
                offenders.Add($"{type.FullName}.{field.Name} ({field.FieldType.Name})");
            }
        }

        Assert.True(offenders.Count == 0,
            "Simulation phases and systems must not hold mutable instance state — it is " +
            "invisible to the world, cannot be snapshotted, persisted or reset with it, " +
            "and defeats the declared component access the scheduler reasons about " +
            "(ADR-12). Put it on a component (see EnemySpawnState) or, if it is a " +
            "reusable buffer that carries nothing between ticks, mark it " +
            "[SimulationScratch] and be sure that claim is true:\n  " +
            string.Join("\n  ", offenders));
    }

    /// <summary>
    /// A settable auto-property is the same hole with different syntax, so it is closed
    /// too.
    /// </summary>
    [Fact]
    public void SimulationPhasesAndSystems_HaveNoSettableAutoProperties()
    {
        var offenders = new List<string>();

        foreach (Type type in SimulationTypes())
        {
            foreach (PropertyInfo property in type.GetProperties(
                         BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                MethodInfo? setter = property.GetSetMethod(nonPublic: true);
                if (setter == null || setter.IsAbstract) continue;
                if (property.DeclaringType != type) continue;

                offenders.Add($"{type.FullName}.{property.Name}");
            }
        }

        Assert.True(offenders.Count == 0,
            "Simulation phases and systems must not expose settable properties — same " +
            "reason as mutable fields:\n  " + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// The guard is only worth having if it actually fires, so this asserts it does —
    /// against a deliberately non-conforming system that exists only for this test. Two
    /// prior guards in this codebase (the AOT hints, the golden vectors) earned their keep
    /// by being demonstrated rather than assumed.
    /// </summary>
    [Fact]
    public void TheGuardFires_OnASystemThatKeepsStateInAField()
    {
        var offenders = MutableInstanceFields(typeof(OffendingSystem)).Select(f => f.Name).ToList();

        Assert.Contains("_accumulator", offenders);
        Assert.DoesNotContain("_dt", offenders);          // readonly config is fine
        Assert.DoesNotContain("_scratch", offenders);     // sanctioned scratch is fine
    }

    /// <summary>
    /// Shaped exactly like the real <c>EnemySpawnSystem</c> was before this stage. Not
    /// registered anywhere and never run; it exists so
    /// <see cref="TheGuardFires_OnASystemThatKeepsStateInAField"/> has something to catch.
    /// </summary>
    private sealed class OffendingSystem
    {
        private readonly float _dt = 1f;
        private float _accumulator;
        [SimulationScratch] private int[] _scratch = Array.Empty<int>();

        public float Step() => _accumulator += _dt;
        public int Scratch => _scratch.Length;
    }
}
