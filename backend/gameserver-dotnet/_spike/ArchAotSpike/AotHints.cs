// Candidate workaround for the NativeAOT failure this spike found.
//
// Arch.Core.Chunk's constructor allocates one backing array per component type
// with System.Array.CreateInstance(Type, int) — a runtime, Type-driven array
// creation. Under NativeAOT the array type T[] for a user-defined struct only
// exists if ILC saw it constructed statically somewhere; otherwise the runtime
// throws NotSupportedException: "'T[]' is missing native code or metadata".
//
// Statically constructing each component's array type here is what makes ILC
// emit it. Compile with -p:DefineConstants=AOT_HINTS to include this.
//
// Note what this costs: every component type must be listed here by hand, the
// list is invisible to the compiler (nothing fails to build if it is wrong), and
// a miss surfaces only when a native binary reaches an archetype containing the
// forgotten component — which may be a rare archetype, i.e. in production.

#if AOT_HINTS || AOT_HINTS_PARTIAL
using System.Runtime.CompilerServices;
using Arch.Core;

namespace ArchAotSpike;

internal static class AotHints
{
    private static volatile object? _keepAlive;

    [ModuleInitializer]
    internal static void Register()
    {
        // One statically-constructed array per component type used by any
        // archetype this process can create.
        _keepAlive = new object[]
        {
            new Identity[1],
            new Position[1],
            new Velocity[1],
            new Health[1],
            new Kind[1],
            new Entity[1],
#if !AOT_HINTS_PARTIAL
            // Deliberately omitted in the AOT_HINTS_PARTIAL configuration, to
            // show what a forgotten component type looks like: not a build
            // failure, not a startup failure, but a crash on the first tick that
            // creates the archetype containing it.
            new Stunned[1],
#endif
        };
    }
}
#endif
