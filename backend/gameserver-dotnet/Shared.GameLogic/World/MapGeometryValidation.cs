using System;
using System.Collections.Generic;
using System.Globalization;
using Shared.GameLogic.Components;

namespace Shared.GameLogic.World
{
    /// <summary>
    /// Rules every map geometry must satisfy, shared by the server and the client so both
    /// agree on what a valid map is (ADR-28 decision 3, ADR-19 decisions 2 and 5).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The server runs these at boot and refuses to start on a map that fails, exactly as
    /// it does for items. The client runs the same rules on what it downloaded, because a
    /// truncated map is indistinguishable from a valid one until something checks it — and
    /// a client predicting against a different world than the server is a stream of
    /// corrections the player sees as rubber-banding.
    /// </para>
    /// <para>
    /// Every problem is reported, not just the first, and each line names the map and the
    /// element (box index, spawn or portal name) it is about.
    /// </para>
    /// </remarks>
    public static class MapGeometryValidation
    {
        /// <summary>
        /// Most boxes a map may have. Box queries are a linear scan (see
        /// <see cref="MapGeometry"/>), run per character per tick, so this is a cost cap,
        /// not a format limit; raise it together with adding a broadphase.
        /// </summary>
        public const int MaxBoxes = 4096;

        /// <summary>Most heightfield samples a map may have (1025 x 1025).</summary>
        public const int MaxHeightSamples = 1025 * 1025;

        /// <summary>Longest permitted spawn or portal name.</summary>
        public const int MaxNameLength = 64;

        /// <summary>
        /// Validate <paramref name="geometry"/> and append one human-readable line per
        /// problem to <paramref name="errors"/>. Returns true when the map is usable.
        /// </summary>
        /// <param name="mapId">Map id, used only to prefix messages.</param>
        /// <param name="geometry">The map to check.</param>
        /// <param name="errors">Receives one line per problem.</param>
        public static bool Validate(string mapId, MapGeometry geometry, List<string> errors)
        {
            if (geometry == null) throw new ArgumentNullException(nameof(geometry));
            if (errors == null) throw new ArgumentNullException(nameof(errors));

            int before = errors.Count;
            string p = "map " + (string.IsNullOrEmpty(mapId) ? "<unnamed>" : mapId) + ": ";

            MapBounds b = geometry.Bounds;
            if (!float.IsFinite(b.MinX) || !float.IsFinite(b.MinY) || !float.IsFinite(b.MaxX) || !float.IsFinite(b.MaxY))
                errors.Add(p + "bounds are not finite numbers.");
            else if (!(b.Width > 0f) || !(b.Height > 0f))
                errors.Add(p + "bounds have zero width or height; nothing could move in them.");

            if (geometry.HeightField != null) ValidateHeightField(p, geometry.HeightField, errors);

            ReadOnlySpan<StaticBox> boxes = geometry.Boxes;
            if (boxes.Length > MaxBoxes)
                errors.Add(p + "has " + boxes.Length.ToString(CultureInfo.InvariantCulture) +
                           " boxes; the limit is " + MaxBoxes.ToString(CultureInfo.InvariantCulture) +
                           " because collision scans every box per character per tick.");

            for (int i = 0; i < boxes.Length; i++)
            {
                StaticBox box = boxes[i];
                string bp = p + "box " + i.ToString(CultureInfo.InvariantCulture) + ": ";
                if (!box.IsFinite)
                {
                    errors.Add(bp + "has a non-finite edge.");
                    continue;
                }

                if (!(box.MaxX > box.MinX) || !(box.MaxY > box.MinY) || !(box.MaxZ > box.MinZ))
                    errors.Add(bp + "has zero extent on at least one axis; a flat box cannot be " +
                               "collided with reliably. Give it a thickness.");
            }

            ReadOnlySpan<SpawnPoint> spawns = geometry.Spawns;
            for (int i = 0; i < spawns.Length; i++)
            {
                SpawnPoint s = spawns[i];
                string sp = p + "spawn " + Describe(s.Name, i) + ": ";
                ValidateName(sp, s.Name, errors);
                for (int j = 0; j < i; j++)
                {
                    if (!string.IsNullOrEmpty(s.Name) && string.Equals(spawns[j].Name, s.Name, StringComparison.Ordinal))
                    {
                        errors.Add(sp + "duplicate name; portals and joins look spawns up by name.");
                        break;
                    }
                }

                if (!s.Position.IsFinite)
                    errors.Add(sp + "position is not finite.");
                else if (!b.Contains(s.Position.XY))
                    errors.Add(sp + "lies outside the map bounds; it would be clamped on arrival.");
            }

            ReadOnlySpan<Portal> portals = geometry.Portals;
            for (int i = 0; i < portals.Length; i++)
            {
                Portal portal = portals[i];
                string pp = p + "portal " + Describe(portal.Name, i) + ": ";
                ValidateName(pp, portal.Name, errors);
                for (int j = 0; j < i; j++)
                {
                    if (!string.IsNullOrEmpty(portal.Name) && string.Equals(portals[j].Name, portal.Name, StringComparison.Ordinal))
                    {
                        errors.Add(pp + "duplicate name.");
                        break;
                    }
                }

                if (!portal.Position.IsFinite)
                    errors.Add(pp + "position is not finite.");
                else if (!b.Contains(portal.Position.XY))
                    errors.Add(pp + "lies outside the map bounds; nothing could reach it.");

                if (!(portal.Radius > 0f) || !float.IsFinite(portal.Radius))
                    errors.Add(pp + "radius must be a positive number.");
                if (!(portal.Height > 0f) || !float.IsFinite(portal.Height))
                    errors.Add(pp + "height must be a positive number.");
                if (string.IsNullOrWhiteSpace(portal.TargetMapId))
                    errors.Add(pp + "target map id is empty.");
                if (string.IsNullOrWhiteSpace(portal.TargetSpawn))
                    errors.Add(pp + "target spawn is empty.");
            }

            return errors.Count == before;
        }

        private static void ValidateHeightField(string p, HeightField hf, List<string> errors)
        {
            string hp = p + "heightfield: ";
            if (hf.Columns < 2 || hf.Rows < 2)
                errors.Add(hp + "needs at least 2 x 2 samples to interpolate between; got " +
                           hf.Columns.ToString(CultureInfo.InvariantCulture) + " x " +
                           hf.Rows.ToString(CultureInfo.InvariantCulture) + ".");
            if (!(hf.CellSize > 0f) || !float.IsFinite(hf.CellSize))
                errors.Add(hp + "cell size must be a positive number.");
            if (!float.IsFinite(hf.OriginX) || !float.IsFinite(hf.OriginY))
                errors.Add(hp + "origin is not finite.");

            long expected = (long)hf.Columns * hf.Rows;
            if (expected > MaxHeightSamples)
                errors.Add(hp + "has " + expected.ToString(CultureInfo.InvariantCulture) +
                           " samples; the limit is " + MaxHeightSamples.ToString(CultureInfo.InvariantCulture) + ".");
            if (hf.Columns >= 0 && hf.Rows >= 0 && expected != hf.Heights.Length)
                errors.Add(hp + "has " + hf.Heights.Length.ToString(CultureInfo.InvariantCulture) +
                           " samples but columns x rows is " + expected.ToString(CultureInfo.InvariantCulture) + ".");

            ReadOnlySpan<float> h = hf.Heights;
            for (int i = 0; i < h.Length; i++)
            {
                if (!float.IsFinite(h[i]))
                {
                    // One line, not one per sample: a corrupted file would otherwise bury
                    // every other error under a million identical ones.
                    errors.Add(hp + "sample " + i.ToString(CultureInfo.InvariantCulture) + " is not finite.");
                    break;
                }
            }
        }

        private static void ValidateName(string prefix, string name, List<string> errors)
        {
            if (string.IsNullOrWhiteSpace(name))
                errors.Add(prefix + "name is empty.");
            else if (name.Length > MaxNameLength)
                errors.Add(prefix + "name is longer than " + MaxNameLength.ToString(CultureInfo.InvariantCulture) + " characters.");
        }

        private static string Describe(string name, int index) =>
            string.IsNullOrEmpty(name) ? "#" + index.ToString(CultureInfo.InvariantCulture) : "'" + name + "'";
    }
}
