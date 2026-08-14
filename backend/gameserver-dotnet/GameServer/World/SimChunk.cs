using System;
using GameServer.World.Components;

namespace GameServer.World;

/// <summary>
/// One chunk's worth of component arrays, handed to a system so it can iterate the
/// contiguous storage rather than resolving entities one at a time.
///
/// <para><b>Why these three components and not an arbitrary set.</b> A general
/// N-component chunk query in C# needs either a source generator to emit an overload per
/// arity — banned by ADR-12, because the AOT hint guard reflects over component structs
/// and cannot enumerate generated query shapes — or a hand-written combinatorial API
/// nobody will keep complete. So this exposes the set the simulation actually walks
/// linearly, and the moment a system needs a different set, the honest options are to add
/// one more explicit shape here or to say why that system is not per-entity-linear and
/// should keep handle access. That trade is deliberate and is the main cost of the
/// generator ban.</para>
///
/// <para>A <c>ref struct</c>: it borrows the chunk's arrays and must not outlive the
/// visit.</para>
/// </summary>
public readonly ref struct SimChunk
{
    /// <summary>World-space positions, one per entity in this chunk.</summary>
    public readonly Span<Position> Positions;

    /// <summary>Hit points and liveness, index-aligned with <see cref="Positions"/>.</summary>
    public readonly Span<Health> Healths;

    /// <summary>Movement capability, index-aligned with <see cref="Positions"/>.</summary>
    public readonly Span<Locomotion> Locomotions;

    /// <summary>Entities in this chunk. The spans may be longer; only this many are live.</summary>
    public readonly int Count;

    internal SimChunk(Span<Position> positions, Span<Health> healths,
                      Span<Locomotion> locomotions, int count)
    {
        Positions = positions;
        Healths = healths;
        Locomotions = locomotions;
        Count = count;
    }
}

/// <summary>
/// A system body that runs once per chunk. Implemented by a <c>struct</c> so the call
/// devirtualises and nothing is allocated per chunk or per tick.
/// </summary>
public interface ISimChunkVisitor
{
    /// <summary>Process one chunk. Called with the world write lock held.</summary>
    void Visit(in SimChunk chunk);
}
