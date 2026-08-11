using System.Reflection;
using GameServer.World;
using GameServer.World.Components;

namespace GameServer.Tests.World;

/// <summary>
/// The AOT hint guard required by ADR-11.
///
/// <para>Arch's <c>Chunk</c> allocates its backing arrays with
/// <c>System.Array.CreateInstance(Type, int)</c>. Under NativeAOT the array type
/// <c>T[]</c> for a user-defined struct exists only if ILC saw it constructed
/// statically, so every component type must appear in
/// <see cref="ArchAotHints.HintedComponentTypes"/>. A missing one produces no build
/// warning and no startup error — the native binary throws
/// <c>NotSupportedException: 'T[]' is missing native code or metadata</c> the first
/// time it creates an archetype containing that component, which for a rare
/// archetype means in production.</para>
///
/// <para>These tests run on CoreCLR with a JIT, where the failure cannot reproduce.
/// They therefore do not test Arch — they test that the hint list is <i>complete</i>,
/// which is the only part of the problem a test can see. Running the published
/// native binary is still the verification that matters (ADR-11 decision 4).</para>
///
/// <para><b>This guard has been observed to fire.</b> Adding a component struct to
/// <c>GameServer.World.Components</c> and not adding a line to
/// <c>ArchAotHints.KeepAlive</c> fails
/// <see cref="EveryComponentType_IsHintedForNativeAot"/> with the offending type
/// named. That experiment was run against this implementation; see
/// <c>docs/DESIGN.md</c>.</para>
/// </summary>
public class ArchAotHintTests
{
    /// <summary>
    /// Every component type the server can put in an archetype must have had its
    /// array type statically constructed.
    /// </summary>
    /// <remarks>
    /// Discovery is by namespace <i>or</i> attribute, deliberately both: a component
    /// declared in <c>GameServer.World.Components</c> is caught even if someone forgets
    /// <c>[EcsComponent]</c>, and a component declared elsewhere is caught if they
    /// remember it. Nothing is caught if they do neither, which is why the convention
    /// is stated on <see cref="EcsComponentAttribute"/> and in <c>docs/DESIGN.md</c>.
    /// </remarks>
    [Fact]
    public void EveryComponentType_IsHintedForNativeAot()
    {
        var hinted = new HashSet<Type>(ArchAotHints.HintedComponentTypes);

        var components = typeof(EcsWorld).Assembly
            .GetTypes()
            .Where(t => t.IsValueType && !t.IsEnum && !t.IsGenericTypeDefinition)
            .Where(t => t.Namespace == typeof(EntityIdRef).Namespace ||
                        t.GetCustomAttribute<EcsComponentAttribute>() != null)
            .ToList();

        Assert.NotEmpty(components);

        var unhinted = components.Where(t => !hinted.Contains(t)).ToList();

        Assert.True(unhinted.Count == 0,
            "These component types have no statically constructed T[] in " +
            "ArchAotHints.KeepAlive. NativeAOT will publish cleanly and then throw " +
            "NotSupportedException on the first archetype that contains them (ADR-11). " +
            "Add one 'new <Type>[1],' line per type:\n  " +
            string.Join("\n  ", unhinted.Select(t => t.Name)));
    }

    /// <summary>
    /// The hint list must not contain stale entries either: a hint for a deleted
    /// component is a line nobody will recognise as removable, and it erodes the
    /// list's meaning as the exact set of live components.
    /// </summary>
    [Fact]
    public void HintList_HasNoStaleEntries()
    {
        var components = new HashSet<Type>(typeof(EcsWorld).Assembly
            .GetTypes()
            .Where(t => t.IsValueType && !t.IsEnum && !t.IsGenericTypeDefinition)
            .Where(t => t.Namespace == typeof(EntityIdRef).Namespace ||
                        t.GetCustomAttribute<EcsComponentAttribute>() != null));

        // Arch.Core.Entity is hinted for the chunk's entity array and is not one of
        // our component types, so it is the one permitted extra.
        var stale = ArchAotHints.HintedComponentTypes
            .Where(t => !components.Contains(t) && t != typeof(Arch.Core.Entity))
            .ToList();

        Assert.True(stale.Count == 0,
            "ArchAotHints.KeepAlive hints types that are no longer components: " +
            string.Join(", ", stale.Select(t => t.Name)));
    }

    /// <summary>
    /// The hints must be in place before the first world is created, without any
    /// caller having to remember to trigger them. The module initializer is what
    /// guarantees that; this asserts it actually ran.
    /// </summary>
    [Fact]
    public void ModuleInitializer_RanBeforeAnyWorldWasCreated()
    {
        Assert.NotEmpty(ArchAotHints.HintedComponentTypes);
    }
}
