using System;
using System.Collections.Generic;
using Shared.GameLogic.Components;

namespace GameServer.World;

/// <summary>
/// A uniform spatial hash over entity positions, rebuilt once per gather scope and
/// queried once per viewer. It narrows the AOI search; it never decides membership.
///
/// <para><b>This is the second attempt, and the first one lost.</b> BENCHMARK.md Part V
/// records a uniform grid that was implemented, proved correct, measured at <b>0.32-0.45x
/// (2.8x slower)</b> at realistic density, and reverted at <c>2e3e5db</c>. That entry
/// exists specifically so nobody rebuilds this on the strength of the Big-O argument, so
/// the difference here has to be stated rather than assumed.</para>
///
/// <para><b>Why the first attempt lost.</b> The scan's cost was never the distance tests
/// — 40 000 sequential reads over contiguous chunk arrays are worth microseconds against
/// a 66 ms budget. The cost was <i>composing</i> an entity struct per match, which is
/// proportional to matches and which no index reduces, because the matches are the
/// answer. The first index then made composition strictly worse: it held only an
/// <c>Entity</c> handle, so it composed through seven random-access component lookups per
/// match, against a scan that composed from the chunk it was already iterating.</para>
///
/// <para><b>What changed.</b> Part V named the condition under which the index's argument
/// returns: <i>"a composition path the index can use as cheaply as the scan does"</i>.
/// Issue #237 (Part VII) trimmed the gather's product to <see cref="EntityView"/> — seven
/// fields, exactly what the snapshot encoder consumes. That struct is small enough to
/// live <b>inside the index</b>. So this grid stores the composed view in the entry
/// itself, composed once per entity during the O(n) rebuild, and a query copies it. The
/// consequence is the one that matters: composition stops being per match <i>per viewer</i>
/// and becomes per entity <i>per tick</i>. At 200 viewers with 15 matches each that is
/// 3 000 composes replaced by 200. The first attempt paid more per match than the scan;
/// this one pays less, and the redundancy across viewers — which the brute-force scan also
/// pays and which no amount of tuning removes from it — is gone.</para>
///
/// <para><b>Rebuilt whole, every tick, instead of maintained incrementally.</b> The same
/// deliberate choice the first attempt made, for the same reason. Positions are written
/// from the input handler, the enemy move system and the spawn/reconnect path, and an
/// incremental index has to intercept all of them forever, including the next one someone
/// adds. A missed write does not throw — it leaves an entity in the wrong bucket, and the
/// symptom is a player who vanishes from someone else's screen. A whole rebuild is one
/// linear pass with no distance tests and cannot be stale by construction: it is built
/// from the authoritative component storage immediately before the queries that read it.
/// It is also what makes the pre-composed entry affordable, since the rebuild pass is
/// already touching every component span it needs.</para>
///
/// <para><b>Bucketing is a counting sort, not a comparison sort.</b> Two passes — count
/// per cell, then place at a prefix-sum cursor — so entries within a cell keep the order
/// the chunk scan produced. <c>Array.Sort</c> is unstable and would not.</para>
///
/// <para><b>Unbounded, and deliberately not derived from <c>MapBounds</c>.</b> Cells are
/// hashed rather than indexed into a fixed array, so an entity outside the play area
/// buckets correctly instead of clamping onto an edge cell and being found by queries it
/// is nowhere near.</para>
///
/// <para><b>Not in Shared.GameLogic, and that is correct.</b> The AOI <i>predicate</i> is
/// shared with the Unity client so both sides agree on visibility; this is only a search
/// structure for evaluating that predicate against many observers at once. A client has
/// exactly one observer and nothing to amortise, so an index there would be cost with no
/// benefit. <c>AoiLogic.GetNearbyEntities</c> remains the brute-force definition of the
/// answer and is this index's test oracle.</para>
/// </summary>
internal sealed class SpatialGrid
{
    /// <summary>
    /// One entity's contribution to the index: the composed snapshot view (which carries
    /// its position) plus where it fell in the rebuild's chunk scan.
    /// </summary>
    private struct Entry
    {
        /// <summary>
        /// The view the query hands back, composed once at rebuild rather than once per
        /// match per viewer. This field is the whole reason this index can win where the
        /// Part V one could not.
        /// </summary>
        public EntityView View;

        /// <summary>
        /// Position of this entity in the rebuild's chunk scan.
        ///
        /// <para>Carried so a query can restore the order the brute-force scan produced.
        /// Bucketing necessarily reorders entities — that is what an index is — but the
        /// delta encoder interns entity ids in AOI arrival order, so emitting them
        /// cell-major would change the bytes on the wire for an identical set. The ordinal
        /// is what lets the index change the <i>search</i> without changing the
        /// <i>answer</i>.</para>
        /// </summary>
        public int ScanOrdinal;
    }

    private readonly float _cellSize;
    private readonly float _invCellSize;

    /// <summary>
    /// Cell key -> dense ordinal. Keys are cleared each rebuild but the dictionary's
    /// buckets are retained, so a warm map stops reallocating.
    /// </summary>
    private readonly Dictionary<long, int> _cellOrdinals = new();

    private int[] _cellStart = Array.Empty<int>();
    private int[] _cellCount = Array.Empty<int>();
    private int[] _cursor = Array.Empty<int>();
    private long[] _entryKeys = Array.Empty<long>();
    private Entry[] _entries = Array.Empty<Entry>();

    /// <summary>
    /// Destination for the counting-sort placement pass, reused across rebuilds so a
    /// rebuild allocates nothing once the population has stabilised.
    /// </summary>
    private Entry[] _bucketed = Array.Empty<Entry>();

    private int _entryCount;
    private int _cellsUsed;

    public SpatialGrid(float cellSize)
    {
        if (!(cellSize > 0f) || !float.IsFinite(cellSize))
        {
            throw new ArgumentOutOfRangeException(nameof(cellSize), cellSize,
                "Cell size must be a positive finite number.");
        }

        _cellSize = cellSize;
        _invCellSize = 1f / cellSize;
    }

    /// <summary>Entities in the index as of the last rebuild.</summary>
    public int Count => _entryCount;

    /// <summary>Distinct occupied cells as of the last rebuild. Diagnostics and tests.</summary>
    public int OccupiedCells => _cellsUsed;

    /// <summary>Cell edge length in world units.</summary>
    public float CellSize => _cellSize;

    /// <summary>
    /// Minimum occupied-cell count at which querying through the index beats the
    /// brute-force scan. Below it the gather takes the scan instead — see
    /// <see cref="IsWorthQuerying"/>.
    /// </summary>
    /// <remarks>
    /// <b>Measured, not chosen.</b> A query visits at most a 3x3 neighbourhood, so it
    /// examines roughly <c>9 / OccupiedCells</c> of the population — which makes occupied
    /// cells, not entity count, the statistic that predicts the win. The A/B sweep in
    /// <c>AoiIndexBench</c> crosses 1.00x at ~90 occupied cells and does so at both 200 and
    /// 400 entities, confirming the count itself does not enter:
    /// <code>
    /// n=200  cells  16 -> 0.38x   36 -> 0.59x   59 -> 0.90x   86 -> 0.97x  123 -> 1.43x
    /// n=400  cells  16 -> 0.38x   36 -> 0.62x   64 -> 0.87x   99 -> 1.11x  167 -> 1.44x
    /// </code>
    /// 96 is the first power-of-two-ish value clear of the crossover. Entity count is
    /// deliberately <i>not</i> the gate: BENCHMARK.md Part V's dense-400 row loses while its
    /// sparse-200 row wins, so a count threshold would get both backwards.
    /// </remarks>
    public const int MinOccupiedCellsToQuery = 96;

    /// <summary>
    /// Whether querying through this index is expected to beat the full scan for the
    /// population it currently holds.
    ///
    /// <para>This is a <b>performance</b> decision and never a correctness one: both paths
    /// return the same entities in the same order, which is what
    /// <c>AoiIndexDifferentialTests</c> asserts, so a wrong answer here costs microseconds
    /// and cannot cost an entity. That is why a cheap approximate statistic is the right
    /// instrument.</para>
    /// </summary>
    public bool IsWorthQuerying => _cellsUsed >= MinOccupiedCellsToQuery;

    // ── Build ────────────────────────────────────────────────────────────────

    /// <summary>Start a rebuild. Callers add every entity, then call <see cref="Finish"/>.</summary>
    public void Begin(int expectedCount)
    {
        if (_entries.Length < expectedCount)
        {
            int capacity = Math.Max(expectedCount, _entries.Length * 2);
            _entries = new Entry[capacity];
            _entryKeys = new long[capacity];
            _bucketed = new Entry[capacity];
        }

        _entryCount = 0;
        _cellsUsed = 0;
        _cellOrdinals.Clear();
    }

    /// <summary>
    /// Add one entity with the view composed from the position it currently holds. Call
    /// order defines the scan ordinals, so callers must add in the same chunk order the
    /// brute-force scan iterates.
    /// </summary>
    public void Add(in EntityView view)
    {
        if (_entryCount == _entries.Length)
        {
            int capacity = Math.Max(4, _entries.Length * 2);
            Array.Resize(ref _entries, capacity);
            Array.Resize(ref _entryKeys, capacity);
            Array.Resize(ref _bucketed, capacity);
        }

        long key = CellKey(view.Position);

        _entries[_entryCount] = new Entry { View = view, ScanOrdinal = _entryCount };
        _entryKeys[_entryCount] = key;
        _entryCount++;

        if (!_cellOrdinals.ContainsKey(key))
        {
            _cellOrdinals[key] = _cellsUsed++;
        }
    }

    /// <summary>Finish the rebuild: bucket the entries by cell, stably.</summary>
    public void Finish()
    {
        if (_cellStart.Length < _cellsUsed + 1)
        {
            int capacity = Math.Max(_cellsUsed + 1, _cellStart.Length * 2);
            _cellStart = new int[capacity];
            _cellCount = new int[capacity];
            _cursor = new int[capacity];
        }

        Array.Clear(_cellCount, 0, _cellsUsed);

        for (int i = 0; i < _entryCount; i++)
        {
            _cellCount[_cellOrdinals[_entryKeys[i]]]++;
        }

        int running = 0;
        for (int c = 0; c < _cellsUsed; c++)
        {
            _cellStart[c] = running;
            _cursor[c] = running;
            running += _cellCount[c];
        }

        // Place in scan order, so a cell's entries keep the order the chunk iteration
        // produced. This is what makes the query order deterministic.
        for (int i = 0; i < _entryCount; i++)
        {
            int ordinal = _cellOrdinals[_entryKeys[i]];
            _bucketed[_cursor[ordinal]++] = _entries[i];
        }

        Array.Copy(_bucketed, _entries, _entryCount);
    }

    // ── Query ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Fill <paramref name="destination"/> with every entity within
    /// <paramref name="radius"/> of <paramref name="center"/>, in brute-force scan order.
    ///
    /// <para>Same <b>count-don't-saturate</b> contract as
    /// <c>AoiLogic.GetNearbyEntities</c> and every other AOI entry point in this server:
    /// the return value is the total match count and may exceed the buffer, so a caller
    /// detects truncation with <c>count &gt; destination.Length</c>, resizes and retries
    /// once. Because matches are discovered cell-major and must be emitted scan-major,
    /// <b>every</b> match is collected and sorted before the first
    /// <c>destination.Length</c> are written — a truncating query still returns the same
    /// prefix the scan would have written, not an arbitrary subset of the same size.</para>
    ///
    /// <para><paramref name="scratchOrdinals"/> and <paramref name="scratchViews"/> are
    /// caller-owned working buffers, each at least <see cref="Count"/> long. They are
    /// parameters rather than fields because the gather runs on several workers inside one
    /// <c>ReadAllParallel</c> region: shared query scratch would be a data race, and the
    /// grid itself is read-only once <see cref="Finish"/> has returned.</para>
    /// </summary>
    public int Query(
        in Vec2 center,
        float radius,
        Span<EntityView> destination,
        Span<int> scratchOrdinals,
        Span<EntityView> scratchViews)
    {
        if (_entryCount == 0) return 0;

        float radiusSq = radius * radius;
        int matches = 0;

        if (!float.IsFinite(center.X) || !float.IsFinite(center.Y) || !float.IsFinite(radius))
        {
            // A non-finite query cannot be reasoned about cell-wise. Test everything and
            // let the exact predicate reject it, exactly as the full scan did. NaN fails
            // every comparison, so this yields zero matches — which is what the scan's
            // `> radiusSq` continue also yields.
            matches = CollectAll(in center, radiusSq, scratchOrdinals, scratchViews);
            return Emit(matches, destination, scratchOrdinals, scratchViews);
        }

        // The covering cell range. Correctness rests on one property: CellCoord is
        // monotonic non-decreasing, and the *same* CellCoord assigns entities to cells and
        // derives this range.
        //
        // Any entity inside the circle satisfies |p.X - center.X| <= radius, hence
        // center.X - radius <= p.X <= center.X + radius, hence — by monotonicity —
        // CellCoord(center.X - radius) <= CellCoord(p.X) <= CellCoord(center.X + radius).
        // So no in-radius entity can fall outside this box, and the index cannot lose one.
        //
        // Float rounding does not weaken that. `center.X - radius` is rounded to the
        // nearest float; call it lo. If some in-radius p.X satisfied p.X < lo, then p.X
        // would be a float lying strictly between the exact difference and lo, which
        // contradicts lo being the *nearest* float to that difference. So lo <= p.X still
        // holds after rounding, and symmetrically for the upper bound. The one thing that
        // would break the argument is computing the range with different arithmetic than
        // the cell assignment (say, double here and float there) — hence both go through
        // CellCoord. AoiIndexDifferentialTests pins this with radius-boundary cases.
        int minX = CellCoord(center.X - radius);
        int maxX = CellCoord(center.X + radius);
        int minY = CellCoord(center.Y - radius);
        int maxY = CellCoord(center.Y + radius);

        long cellsSpanned = (long)(maxX - minX + 1) * (maxY - minY + 1);
        if (cellsSpanned >= _entryCount)
        {
            // A neighbourhood covering more cells than the index holds entities would
            // spend longer walking empty cells than testing everything. Keeps a huge
            // radius from being slower than the scan it replaced.
            matches = CollectAll(in center, radiusSq, scratchOrdinals, scratchViews);
            return Emit(matches, destination, scratchOrdinals, scratchViews);
        }

        for (int cx = minX; cx <= maxX; cx++)
        {
            for (int cy = minY; cy <= maxY; cy++)
            {
                if (!_cellOrdinals.TryGetValue(Pack(cx, cy), out int ordinal)) continue;

                int start = _cellStart[ordinal];
                int end = start + _cellCount[ordinal];
                for (int i = start; i < end; i++)
                {
                    ref Entry e = ref _entries[i];
                    // Vec2.DistanceSq is Shared.GameLogic's — the same predicate the
                    // client predicts with, and the same one the brute-force scan applies.
                    // The index chose which entities to test; it does not decide the answer.
                    if (Vec2.DistanceSq(center, e.View.Position) > radiusSq) continue;

                    scratchOrdinals[matches] = e.ScanOrdinal;
                    scratchViews[matches] = e.View;
                    matches++;
                }
            }
        }

        return Emit(matches, destination, scratchOrdinals, scratchViews);
    }

    /// <summary>Exact predicate over every entry, for the two fallback cases.</summary>
    private int CollectAll(
        in Vec2 center, float radiusSq, Span<int> scratchOrdinals, Span<EntityView> scratchViews)
    {
        int matches = 0;
        for (int i = 0; i < _entryCount; i++)
        {
            ref Entry e = ref _entries[i];
            if (Vec2.DistanceSq(center, e.View.Position) > radiusSq) continue;

            scratchOrdinals[matches] = e.ScanOrdinal;
            scratchViews[matches] = e.View;
            matches++;
        }

        return matches;
    }

    /// <summary>
    /// Restore brute-force scan order and copy out what fits. The sort is over match
    /// count, not entity count — typically a couple of dozen keys.
    /// </summary>
    private static int Emit(
        int matches, Span<EntityView> destination, Span<int> scratchOrdinals, Span<EntityView> scratchViews)
    {
        Span<int> keys = scratchOrdinals[..matches];
        Span<EntityView> values = scratchViews[..matches];
        keys.Sort(values);

        int emitted = Math.Min(matches, destination.Length);
        values[..emitted].CopyTo(destination[..emitted]);
        return matches;
    }

    // ── Cell maths ───────────────────────────────────────────────────────────

    private int CellCoord(float v)
    {
        float scaled = v * _invCellSize;
        // Floor, not truncate: truncation folds -0.4 and 0.4 into the same cell and would
        // put an entity a cell away from where a neighbouring query looks for it. This is
        // the negative-coordinate case the differential test covers explicitly.
        return (int)MathF.Floor(scaled);
    }

    private long CellKey(in Vec2 position) => Pack(CellCoord(position.X), CellCoord(position.Y));

    private static long Pack(int cx, int cy) => ((long)cx << 32) ^ (uint)cy;
}
