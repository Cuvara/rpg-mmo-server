using System;
using Shared.GameLogic.Components;

namespace Shared.GameLogic.World
{
    /// <summary>
    /// Static collision world of one map: bounds, an optional heightfield, axis-aligned
    /// boxes, and named spawn points and portals (ADR-28 decision 3).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Pure data plus queries.</b> No parsing lives here: <c>Shared.GameLogic</c> has
    /// no dependencies, and ADR-19 decision 4 already settled that the schema is shared and
    /// the parser is not — the server reads <c>content/maps/&lt;map_id&gt;.json</c> with
    /// <c>System.Text.Json</c>, the client with its own reader, and both hand the arrays to
    /// the constructor here and run <see cref="MapGeometryValidation"/> on the result.
    /// </para>
    /// <para>
    /// <b>Immutable.</b> The constructor copies every array. A world whose collision
    /// changed under a running simulation would make every desync unreproducible, the same
    /// reason the content database is immutable (ADR-19).
    /// </para>
    /// <para>
    /// <b>Deterministic.</b> Every query is comparisons and explicitly-rounded float
    /// arithmetic, iterates boxes in array order, and samples with fixed step counts —
    /// so the server and the predicting client get the same answer bit for bit.
    /// </para>
    /// <para>
    /// <b>No broadphase.</b> Box queries scan the whole box array. That is the right cost
    /// for the hand-authored maps ADR-28 targets (tens to a few hundred boxes) and is capped
    /// by <see cref="MapGeometryValidation.MaxBoxes"/>; a spatial index is the change to
    /// make when a measured map says so, and it must preserve array-order iteration.
    /// </para>
    /// </remarks>
    public sealed class MapGeometry
    {
        /// <summary>
        /// Fixed number of samples <see cref="SweepSphere"/> takes along a segment against
        /// the heightfield. Fixed rather than length-dependent so both sides do the same
        /// work regardless of how a float distance rounds.
        /// </summary>
        public const int SweepTerrainSamples = 16;

        /// <summary>
        /// Bisection iterations refining a terrain hit between the last clear sample and the
        /// first penetrating one. 12 halvings of a 1/16 interval resolve t to ~1.5e-5 of the
        /// segment — far below anything a player can see.
        /// </summary>
        public const int SweepTerrainRefineIterations = 12;

        private static readonly StaticBox[] NoBoxes = new StaticBox[0];
        private static readonly SpawnPoint[] NoSpawns = new SpawnPoint[0];
        private static readonly Portal[] NoPortals = new Portal[0];

        private readonly StaticBox[] _boxes;
        private readonly SpawnPoint[] _spawns;
        private readonly Portal[] _portals;

        /// <summary>Play area. Positions are clamped into it, as on the protocol 2 path.</summary>
        public MapBounds Bounds { get; }

        /// <summary>Terrain, or null for a flat ground plane at z = 0.</summary>
        public HeightField? HeightField { get; }

        /// <summary>Static box colliders, in the order the loader supplied them.</summary>
        public ReadOnlySpan<StaticBox> Boxes => _boxes;

        /// <summary>Named spawn points.</summary>
        public ReadOnlySpan<SpawnPoint> Spawns => _spawns;

        /// <summary>Portals.</summary>
        public ReadOnlySpan<Portal> Portals => _portals;

        /// <summary>
        /// Build a map from loader-supplied arrays. Every array is copied; null means empty.
        /// Nothing is validated here — run <see cref="MapGeometryValidation.Validate"/> on
        /// the result and refuse to use it when that fails, as the server does at boot.
        /// </summary>
        public MapGeometry(
            in MapBounds bounds,
            HeightField? heightField,
            StaticBox[]? boxes,
            SpawnPoint[]? spawns,
            Portal[]? portals)
        {
            Bounds = bounds;
            HeightField = heightField;
            _boxes = boxes == null || boxes.Length == 0 ? NoBoxes : (StaticBox[])boxes.Clone();
            _spawns = spawns == null || spawns.Length == 0 ? NoSpawns : (SpawnPoint[])spawns.Clone();
            _portals = portals == null || portals.Length == 0 ? NoPortals : (Portal[])portals.Clone();
        }

        /// <summary>
        /// The protocol 2 world: a flat plane at z = 0 inside <paramref name="bounds"/>, no
        /// colliders, no spawns. A map with no map file gets this, which is what lets the
        /// existing tests and the dev stack run unchanged before any map is authored.
        /// </summary>
        public static MapGeometry Flat(in MapBounds bounds) => new MapGeometry(bounds, null, null, null, null);

        /// <summary>True when the map has no terrain and no boxes.</summary>
        public bool IsFlat => HeightField == null && _boxes.Length == 0;

        // ── Ground ───────────────────────────────────────────────────────────

        /// <summary>
        /// Terrain height at <c>(x, y)</c>: the bilinear heightfield sample, or 0 where
        /// there is no heightfield or the point is outside it. Boxes are not included; see
        /// <see cref="SupportHeight"/>.
        /// </summary>
        public float GroundHeight(float x, float y) => HeightField == null ? 0f : HeightField.Sample(x, y);

        /// <summary>
        /// Highest surface a character standing at <c>(x, y)</c> can rest on: the terrain
        /// under its centre, or the top of any box its footprint overlaps whose top is at
        /// or below <paramref name="maxTopZ"/>.
        /// </summary>
        /// <remarks>
        /// The cap is what separates "a box I can stand on" from "a box I am beside": a wall
        /// next to a character overlaps its footprint too, and without the cap its top would
        /// be reported as the floor. Terrain is always support — a character is never
        /// allowed below the ground.
        /// </remarks>
        public float SupportHeight(float x, float y, float radius, float maxTopZ)
        {
            float support = GroundHeight(x, y);
            for (int i = 0; i < _boxes.Length; i++)
            {
                ref readonly StaticBox b = ref _boxes[i];
                if (b.MaxZ > support && b.MaxZ <= maxTopZ && b.FootprintOverlaps(x, y, radius))
                    support = b.MaxZ;
            }

            return support;
        }

        /// <summary>
        /// Index of the first box (in array order) that an upright capsule with feet at
        /// <paramref name="basePos"/> overlaps, or -1. See
        /// <see cref="StaticBox.CapsuleOverlaps"/> for the shape tested.
        /// </summary>
        public int FirstOverlappingBox(in Vec3 basePos, float radius, float height)
        {
            for (int i = 0; i < _boxes.Length; i++)
            {
                if (_boxes[i].CapsuleOverlaps(basePos, radius, height)) return i;
            }

            return -1;
        }

        // ── Sweep ────────────────────────────────────────────────────────────

        /// <summary>
        /// Sweep a sphere of <paramref name="radius"/> from <paramref name="from"/> to
        /// <paramref name="to"/> against the boxes and the ground, and report the first
        /// contact.
        /// </summary>
        /// <param name="from">Sphere centre at the start of the sweep (t = 0).</param>
        /// <param name="to">Sphere centre at the end of the sweep (t = 1).</param>
        /// <param name="radius">Sphere radius; negative is treated as 0.</param>
        /// <param name="hitPoint">Sphere centre at first contact; <paramref name="to"/> when nothing is hit.</param>
        /// <param name="hitT">Segment parameter of first contact in [0, 1]; 1 when nothing is hit.</param>
        /// <returns>True when the sweep hits geometry.</returns>
        /// <remarks>
        /// <para>
        /// <b>Boxes</b> are tested analytically as the box grown by the radius on every axis
        /// (slab test). That over-reports by at most <c>radius * (sqrt(3) - 1)</c> at a box's
        /// corners and edges, the conservative direction for "geometry stops a projectile"
        /// (ADR-29 decision 3), and costs six divisions instead of a rounded-box solve.
        /// Touching is not a hit; a sphere that starts inside is a hit at t = 0.
        /// </para>
        /// <para>
        /// <b>Ground</b> is a hit when the sphere's lowest point goes below the terrain
        /// directly under its centre. With no heightfield that is the plane z = 0, solved
        /// exactly. With one it is <see cref="SweepTerrainSamples"/> fixed samples plus
        /// <see cref="SweepTerrainRefineIterations"/> bisection steps — fixed counts, so both
        /// runtimes do identical work. A ridge narrower than one sample interval can be
        /// missed; terrain is smooth bilinear, so that only happens to grazing shots.
        /// </para>
        /// </remarks>
        public bool SweepSphere(in Vec3 from, in Vec3 to, float radius, out Vec3 hitPoint, out float hitT)
        {
            if (!(radius > 0f)) radius = 0f;
            float bestT = 2f;

            for (int i = 0; i < _boxes.Length; i++)
            {
                if (SweepBox(from, to, radius, _boxes[i], out float t) && t < bestT) bestT = t;
            }

            if (SweepGround(from, to, radius, out float groundT) && groundT < bestT) bestT = groundT;

            if (bestT > 1f)
            {
                hitPoint = to;
                hitT = 1f;
                return false;
            }

            hitT = bestT;
            hitPoint = Vec3.Lerp(from, to, bestT);
            return true;
        }

        private static bool SweepBox(in Vec3 from, in Vec3 to, float r, in StaticBox box, out float tHit)
        {
            float tMin = 0f;
            float tMax = 1f;
            tHit = 0f;

            if (!Slab(from.X, to.X, (float)(box.MinX - r), (float)(box.MaxX + r), ref tMin, ref tMax)) return false;
            if (!Slab(from.Y, to.Y, (float)(box.MinY - r), (float)(box.MaxY + r), ref tMin, ref tMax)) return false;
            if (!Slab(from.Z, to.Z, (float)(box.MinZ - r), (float)(box.MaxZ + r), ref tMin, ref tMax)) return false;

            tHit = tMin;
            return true;
        }

        /// <summary>
        /// Clip [tMin, tMax] to where the segment's coordinate lies strictly inside
        /// (min, max). False when the clipped interval is empty.
        /// </summary>
        private static bool Slab(float a, float b, float min, float max, ref float tMin, ref float tMax)
        {
            float d = (float)(b - a);
            if (d == 0f)
            {
                // Parallel to this slab: either always inside it or never.
                return a > min && a < max;
            }

            float t1 = (float)((float)(min - a) / d);
            float t2 = (float)((float)(max - a) / d);
            if (t1 > t2)
            {
                float tmp = t1;
                t1 = t2;
                t2 = tmp;
            }

            if (t1 > tMin) tMin = t1;
            if (t2 < tMax) tMax = t2;
            // Strict: an interval that collapsed to a point is a graze, not a hit.
            return tMin < tMax;
        }

        private bool SweepGround(in Vec3 from, in Vec3 to, float r, out float tHit)
        {
            tHit = 0f;
            HeightField? hf = HeightField;

            if (hf == null || !hf.IsUsable)
            {
                // Flat plane at z = 0: solve exactly.
                float b0 = (float)(from.Z - r);
                if (b0 < 0f) return true;
                float b1 = (float)(to.Z - r);
                if (!(b1 < 0f)) return false;
                tHit = (float)(b0 / (float)(b0 - b1));
                return true;
            }

            if (Penetration(from, to, r, 0f) < 0f) return true;

            float prev = 0f;
            for (int i = 1; i <= SweepTerrainSamples; i++)
            {
                float t = (float)((float)i / SweepTerrainSamples);
                if (Penetration(from, to, r, t) < 0f)
                {
                    float lo = prev;
                    float hi = t;
                    for (int k = 0; k < SweepTerrainRefineIterations; k++)
                    {
                        float mid = (float)((float)(lo + hi) * 0.5f);
                        if (Penetration(from, to, r, mid) < 0f) hi = mid;
                        else lo = mid;
                    }

                    tHit = hi;
                    return true;
                }

                prev = t;
            }

            return false;
        }

        /// <summary>Sphere bottom minus terrain height at parameter t; negative means below ground.</summary>
        private float Penetration(in Vec3 from, in Vec3 to, float r, float t)
        {
            Vec3 p = Vec3.Lerp(from, to, t);
            return (float)((float)(p.Z - r) - GroundHeight(p.X, p.Y));
        }

        // ── Spawns and portals ───────────────────────────────────────────────

        /// <summary>
        /// Look up a spawn point by exact (ordinal) name. Linear scan, allocation-free —
        /// spawns are looked up on join and transfer, not per tick.
        /// </summary>
        public bool FindSpawn(string name, out SpawnPoint spawn)
        {
            if (name != null)
            {
                for (int i = 0; i < _spawns.Length; i++)
                {
                    if (string.Equals(_spawns[i].Name, name, StringComparison.Ordinal))
                    {
                        spawn = _spawns[i];
                        return true;
                    }
                }
            }

            spawn = default;
            return false;
        }

        /// <summary>
        /// Index into <see cref="Portals"/> of the first portal whose trigger contains a
        /// character's feet, or -1.
        /// </summary>
        public int FindPortal(in Vec3 feet)
        {
            for (int i = 0; i < _portals.Length; i++)
            {
                if (_portals[i].Contains(feet)) return i;
            }

            return -1;
        }
    }
}
