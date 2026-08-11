using System;
using System.Runtime.CompilerServices;
using Arch.Core;
using GameServer.World.Components;

namespace GameServer.World;

/// <summary>
/// NativeAOT hints for Arch's chunk backing arrays (ADR-11).
///
/// <para><b>Why this file exists.</b> <c>Arch.Core.Chunk</c>'s constructor allocates
/// one backing array per component type through
/// <c>System.Array.CreateInstance(Type, int)</c> — runtime, <see cref="Type"/>-driven
/// array creation. Under NativeAOT the array type <c>T[]</c> for a user-defined
/// struct exists only if ILC saw it constructed statically somewhere. Without that,
/// <c>dotnet publish</c> succeeds with no warning and the binary then throws
/// <c>NotSupportedException: 'T[]' is missing native code or metadata</c> on the
/// first archetype creation — i.e. on the first tick that spawns an entity.</para>
///
/// <para><b>Why the list is guarded rather than trusted.</b> Omitting a type here is
/// invisible: no build error, no startup error, just a crash whenever the archetype
/// containing it is first created, which for a rare archetype means in production.
/// <c>GameServer.Tests.World.ArchAotHintTests</c> enumerates every component type in
/// this assembly and fails if one is not in <see cref="HintedComponentTypes"/>.
/// That set is derived from the arrays actually constructed below, via
/// <c>GetElementType()</c>, so the guard cannot drift from the hints it checks —
/// there is no second list to keep in sync.</para>
/// </summary>
public static class ArchAotHints
{
    /// <summary>
    /// Keeps the statically constructed arrays reachable so neither ILC nor the JIT
    /// can treat the allocations as dead code.
    /// </summary>
    private static readonly object[] KeepAlive =
    {
        // One statically constructed array per component type any archetype this
        // process can create. ADD A COMPONENT => ADD A LINE HERE (the test enforces it).
        new EntityIdRef[1],
        new EntityKind[1],
        new Position[1],
        new Health[1],
        new Combat[1],
        new Locomotion[1],
        new InputCursor[1],
        new PlayerTag[1],

        // Arch stores the chunk's entity handles in an Entity[] alongside the
        // component arrays, allocated the same Type-driven way.
        new Entity[1],
    };

    /// <summary>
    /// Component types whose array type this class statically constructed.
    /// Derived from <see cref="KeepAlive"/> itself, so it is the hint list rather
    /// than a description of it.
    /// </summary>
    public static Type[] HintedComponentTypes { get; } = BuildHintedTypes();

    /// <summary>
    /// Forces the hints to be emitted before any Arch world is created. A module
    /// initializer rather than a call from <c>Main</c> so a test host, a benchmark
    /// or a future entry point cannot skip it.
    /// </summary>
    [ModuleInitializer]
    internal static void Initialize()
    {
        // Touching the field is what makes the static constructor run, which is what
        // performs the array allocations ILC needs to have seen.
        if (KeepAlive.Length == 0)
        {
            throw new InvalidOperationException("Arch AOT hints are empty");
        }
    }

    private static Type[] BuildHintedTypes()
    {
        var types = new Type[KeepAlive.Length];
        for (int i = 0; i < KeepAlive.Length; i++)
        {
            Type? element = KeepAlive[i].GetType().GetElementType();
            types[i] = element ?? throw new InvalidOperationException(
                $"ArchAotHints.KeepAlive[{i}] is not an array");
        }
        return types;
    }
}
