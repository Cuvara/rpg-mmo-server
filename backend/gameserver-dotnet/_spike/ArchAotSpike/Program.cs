// THROWAWAY SPIKE — ADR-10 feasibility check.
//
// Question: does Arch ECS work in a NativeAOT-published .NET 10 binary, doing
// what the game server's tick loop actually needs?
//
// The workload deliberately includes the operations most likely to require
// dynamic type work in an archetype ECS:
//   * creating a world and archetypes at runtime
//   * queries over value-type components (generic instantiation over structs)
//   * component mutation by ref
//   * entity creation mid-run (may create a new archetype)
//   * entity destruction mid-run (archetype compaction / chunk moves)
//   * ADD and REMOVE of a component mid-run (archetype MOVE — the hottest
//     candidate for MakeGenericType / Array.CreateInstance under the hood)
//
// The final state checksum is order-independent (a commutative fold keyed by a
// stable per-entity id), so the ECS run and the baseline dictionary run can be
// compared directly, and the JIT run can be compared against the native run.

using System.Globalization;
using System.Runtime.CompilerServices;
using Arch.Core;
using Arch.Core.Extensions;

namespace ArchAotSpike;

// ---------------------------------------------------------------------------
// Components — shaped like the real EntityState after the ADR-10 prerequisites
// (integer handle instead of string id, enum instead of string type).
// All blittable value types, no managed references.
// ---------------------------------------------------------------------------

public enum EntityKind : byte
{
    Unknown = 0,
    Player = 1,
    Npc = 2,
    Monster = 3,
}

public struct Identity
{
    public int Id;
    public Identity(int id) => Id = id;
}

public struct Position
{
    public float X, Y;
    public Position(float x, float y) { X = x; Y = y; }
}

public struct Velocity
{
    public float X, Y;
    public Velocity(float x, float y) { X = x; Y = y; }
}

public struct Health
{
    public float Hp, MaxHp;
    public Health(float hp, float maxHp) { Hp = hp; MaxHp = maxHp; }
}

public struct Kind
{
    public EntityKind Value;
    public Kind(EntityKind v) => Value = v;
}

/// <summary>Tag component added/removed mid-run to force archetype moves.</summary>
public struct Stunned
{
    public int RemainingTicks;
    public Stunned(int t) => RemainingTicks = t;
}

// ---------------------------------------------------------------------------
// Deterministic PRNG — no Random, no time, so JIT and native runs agree exactly.
// ---------------------------------------------------------------------------

public struct Rng
{
    private uint _s;
    public Rng(uint seed) => _s = seed == 0 ? 0x9E3779B9u : seed;

    public uint NextUInt()
    {
        _s ^= _s << 13;
        _s ^= _s >> 17;
        _s ^= _s << 5;
        return _s;
    }

    /// <summary>Float in [-1, 1) with an exactly representable step.</summary>
    public float NextUnit() => (int)(NextUInt() & 0xFFFF) / 32768.0f - 1.0f;
}

public static class Checksum
{
    /// <summary>
    /// Order-independent fold: each entity contributes a hash of its own state,
    /// combined with unchecked addition and xor. Uses raw IEEE-754 bits so the
    /// result is exact, never a formatted-decimal comparison.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Accumulate(ref ulong sum, ref ulong xor, int id, float x, float y, float hp, byte kind)
    {
        ulong h = (ulong)(uint)id * 0x9E3779B97F4A7C15UL;
        h ^= (ulong)(uint)BitConverter.SingleToInt32Bits(x) * 0xBF58476D1CE4E5B9UL;
        h ^= (ulong)(uint)BitConverter.SingleToInt32Bits(y) * 0x94D049BB133111EBUL;
        h ^= (ulong)(uint)BitConverter.SingleToInt32Bits(hp) * 0xD6E8FEB86659FD93UL;
        h ^= (ulong)kind * 0xA24BAED4963EE407UL;
        h ^= h >> 31;
        unchecked { sum += h; }
        xor ^= h;
    }
}

public readonly struct RunResult
{
    public readonly string Label;
    public readonly int FinalCount;
    public readonly ulong Sum;
    public readonly ulong Xor;
    public readonly int Created;
    public readonly int Destroyed;
    public readonly int Stuns;
    public readonly int Unstuns;

    public RunResult(string label, int finalCount, ulong sum, ulong xor,
                     int created, int destroyed, int stuns, int unstuns)
    {
        Label = label; FinalCount = finalCount; Sum = sum; Xor = xor;
        Created = created; Destroyed = destroyed; Stuns = stuns; Unstuns = unstuns;
    }

    public string Digest =>
        string.Create(CultureInfo.InvariantCulture,
            $"count={FinalCount} sum=0x{Sum:X16} xor=0x{Xor:X16} created={Created} destroyed={Destroyed} stuns={Stuns} unstuns={Unstuns}");
}

// ---------------------------------------------------------------------------
// Shared simulation constants — identical for both implementations.
// ---------------------------------------------------------------------------

public static class Sim
{
    public const int InitialEntities = 200;
    public const int Ticks = 400;
    public const float Dt = 0.0625f;      // exactly representable (1/16)
    public const float Bound = 512.0f;

    /// <summary>Deterministic per-entity seed.</summary>
    public static Rng SeedFor(int id) => new Rng((uint)(id * 2654435761u + 12345u));

    /// <summary>Movement integration + bounce. Pure, exact float ops only.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Integrate(ref float px, ref float py, ref float vx, ref float vy)
    {
        px += vx * Dt;
        py += vy * Dt;
        if (px > Bound) { px = Bound; vx = -vx; }
        else if (px < -Bound) { px = -Bound; vx = -vx; }
        if (py > Bound) { py = Bound; vy = -vy; }
        else if (py < -Bound) { py = -Bound; vy = -vy; }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void Regen(ref float hp, float maxHp)
    {
        hp += 0.25f;
        if (hp > maxHp) hp = maxHp;
    }

    /// <summary>Should this entity be stunned on this tick? Pure function of id+tick.</summary>
    public static bool ShouldStun(int id, int tick) => ((id * 31 + tick) & 63) == 0;

    /// <summary>Should this entity despawn on this tick? Pure function of id+tick.</summary>
    public static bool ShouldDespawn(int id, int tick) =>
        tick >= 50 && tick % 10 == 0 && (id % 97) == (tick / 10) % 97;

    /// <summary>How many entities spawn on this tick.</summary>
    public static int SpawnCount(int tick) => (tick >= 20 && tick % 8 == 0) ? 3 : 0;

    public static EntityKind KindFor(int id) => (EntityKind)(byte)(1 + (id % 3));
}
