using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Shared.GameLogic.Components;
using Shared.GameLogic.World;

namespace GameServer.Gameplay;

/// <summary>Raised when a map file exists but cannot be loaded or does not validate. Fatal at boot.</summary>
public sealed class MapLoadException : Exception
{
    public MapLoadException(string message) : base(message) { }
    public MapLoadException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>Result of <see cref="MapLoader.Load"/>.</summary>
/// <param name="Geometry">The map, or the flat protocol 2 world when there was no file.</param>
/// <param name="Path">The file that was read, or null when none existed.</param>
public sealed record MapLoadResult(MapGeometry Geometry, string? Path)
{
    /// <summary>True when the geometry came from a map file.</summary>
    public bool FromFile => Path != null;
}

/// <summary>
/// Loads <c>content/maps/&lt;map_id&gt;.json</c> (ADR-28 decision 3) into a
/// <see cref="MapGeometry"/> and validates it with the shared
/// <see cref="MapGeometryValidation"/>. The format is documented in
/// <c>backend/content/README.md</c>.
/// </summary>
/// <remarks>
/// <para><b>Absent is legal, invalid is fatal</b> — the same rule content has. A map with no
/// file is the flat protocol 2 world inside the configured bounds, which keeps every existing
/// deployment and test running before any map is authored. A file that exists but does not
/// parse or validate refuses the boot: a server running on half a map would put players
/// inside walls the client draws somewhere else.</para>
/// <para>Parsed with a source-generated <c>System.Text.Json</c> context (NativeAOT, ADR-11);
/// the shared library has no parser by design (ADR-19 decision 4).</para>
/// </remarks>
public static class MapLoader
{
    /// <summary>Subdirectory of the content directory that holds map files.</summary>
    public const string MapsDirectoryName = "maps";

    /// <summary>
    /// Load the map for <paramref name="mapId"/> from <paramref name="contentDirectory"/>.
    /// </summary>
    /// <param name="contentDirectory">Content directory (the one holding items.json); null means "no maps".</param>
    /// <param name="mapId">Map id; also the file name.</param>
    /// <param name="fallbackBounds">Bounds of the flat world used when there is no file.</param>
    /// <exception cref="MapLoadException">The file exists and is invalid.</exception>
    public static MapLoadResult Load(string? contentDirectory, string mapId, in MapBounds fallbackBounds)
    {
        if (string.IsNullOrEmpty(contentDirectory) || !IsSafeMapId(mapId))
        {
            return new MapLoadResult(MapGeometry.Flat(fallbackBounds), null);
        }

        string path = System.IO.Path.Combine(contentDirectory, MapsDirectoryName, mapId + ".json");
        if (!File.Exists(path))
        {
            return new MapLoadResult(MapGeometry.Flat(fallbackBounds), null);
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (IOException ex)
        {
            throw new MapLoadException($"Could not read map file '{path}': {ex.Message}", ex);
        }

        return new MapLoadResult(LoadFromBytes(bytes, mapId, path), path);
    }

    /// <summary>
    /// A map id usable as a file name: letters, digits, <c>_</c>, <c>-</c> and <c>.</c>, no
    /// <c>..</c>, at most 128 characters. Anything else is treated as "has no map file"
    /// rather than being joined into a path.
    /// </summary>
    public static bool IsSafeMapId(string? mapId)
    {
        if (string.IsNullOrEmpty(mapId) || mapId.Length > 128 || mapId.Contains("..", StringComparison.Ordinal))
            return false;
        foreach (char c in mapId)
        {
            bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')
                      || c == '_' || c == '-' || c == '.';
            if (!ok) return false;
        }

        return true;
    }

    /// <summary>Parse and validate a map document already in hand.</summary>
    /// <exception cref="MapLoadException">Malformed JSON, missing required fields, or failed validation.</exception>
    public static MapGeometry LoadFromBytes(byte[] bytes, string mapId, string origin)
    {
        if (bytes == null) throw new ArgumentNullException(nameof(bytes));

        MapFileDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize(bytes, MapJsonContext.Default.MapFileDto);
        }
        catch (JsonException ex)
        {
            throw new MapLoadException($"Map file '{origin}' is not valid JSON: {ex.Message}", ex);
        }

        if (dto == null) throw new MapLoadException($"Map file '{origin}' parsed to null. The file is probably empty.");

        var errors = new List<string>();
        MapGeometry? geometry = Build(dto, errors);
        if (geometry != null) MapGeometryValidation.Validate(mapId, geometry, errors);

        if (errors.Count > 0)
        {
            var sb = new StringBuilder();
            sb.Append("Map file '").Append(origin).Append("' is invalid — ")
              .Append(errors.Count).Append(errors.Count == 1 ? " problem:" : " problems:");
            foreach (string e in errors) sb.Append("\n  - ").Append(e);
            sb.Append("\nThe server will not start on a map it cannot vouch for: the client predicts against " +
                      "the same geometry, and a server running on a different one is a stream of corrections.");
            throw new MapLoadException(sb.ToString());
        }

        return geometry!;
    }

    private static MapGeometry? Build(MapFileDto dto, List<string> errors)
    {
        if (dto.Bounds == null || dto.Bounds.MinX == null || dto.Bounds.MinY == null ||
            dto.Bounds.MaxX == null || dto.Bounds.MaxY == null)
        {
            errors.Add("'bounds' is missing or incomplete; it needs minX, minY, maxX and maxY.");
            return null;
        }

        var bounds = new MapBounds(dto.Bounds.MinX.Value, dto.Bounds.MinY.Value, dto.Bounds.MaxX.Value, dto.Bounds.MaxY.Value);

        HeightField? heightField = null;
        if (dto.Heightfield != null)
        {
            HeightfieldDto h = dto.Heightfield;
            if (h.OriginX == null || h.OriginY == null || h.CellSize == null || h.Columns == null ||
                h.Rows == null || h.Heights == null)
            {
                errors.Add("'heightfield' needs originX, originY, cellSize, columns, rows and heights.");
            }
            else
            {
                heightField = new HeightField(
                    h.OriginX.Value, h.OriginY.Value, h.CellSize.Value, h.Columns.Value, h.Rows.Value, h.Heights);
            }
        }

        var boxes = new List<StaticBox>();
        if (dto.Boxes != null)
        {
            for (int i = 0; i < dto.Boxes.Count; i++)
            {
                BoxDto? b = dto.Boxes[i];
                if (b == null || b.MinX == null || b.MinY == null || b.MinZ == null ||
                    b.MaxX == null || b.MaxY == null || b.MaxZ == null)
                {
                    errors.Add($"boxes[{i}] needs minX, minY, minZ, maxX, maxY and maxZ.");
                    continue;
                }

                boxes.Add(new StaticBox(b.MinX.Value, b.MinY.Value, b.MinZ.Value, b.MaxX.Value, b.MaxY.Value, b.MaxZ.Value));
            }
        }

        var spawns = new List<SpawnPoint>();
        if (dto.Spawns != null)
        {
            for (int i = 0; i < dto.Spawns.Count; i++)
            {
                SpawnDto? s = dto.Spawns[i];
                if (s == null || s.X == null || s.Y == null)
                {
                    errors.Add($"spawns[{i}] needs name, x and y (z defaults to 0).");
                    continue;
                }

                spawns.Add(new SpawnPoint(s.Name ?? string.Empty, new Vec3(s.X.Value, s.Y.Value, s.Z ?? 0f)));
            }
        }

        var portals = new List<Portal>();
        if (dto.Portals != null)
        {
            for (int i = 0; i < dto.Portals.Count; i++)
            {
                PortalDto? p = dto.Portals[i];
                if (p == null || p.X == null || p.Y == null || p.Radius == null || p.Height == null)
                {
                    errors.Add($"portals[{i}] needs name, x, y, radius, height, targetMapId and targetSpawn (z defaults to 0).");
                    continue;
                }

                portals.Add(new Portal(
                    p.Name ?? string.Empty,
                    new Vec3(p.X.Value, p.Y.Value, p.Z ?? 0f),
                    p.Radius.Value,
                    p.Height.Value,
                    p.TargetMapId ?? string.Empty,
                    p.TargetSpawn ?? string.Empty));
            }
        }

        if (errors.Count > 0) return null;
        return new MapGeometry(bounds, heightField, boxes.ToArray(), spawns.ToArray(), portals.ToArray());
    }
}

// ── On-disk shape. Separate from the shared types for the reason ContentJson gives: the disk
// form has to tolerate and diagnose a missing field; the shared form is built only from
// values that passed. ──

internal sealed class MapFileDto
{
    [JsonPropertyName("bounds")]
    public BoundsDto? Bounds { get; set; }

    [JsonPropertyName("heightfield")]
    public HeightfieldDto? Heightfield { get; set; }

    [JsonPropertyName("boxes")]
    public List<BoxDto>? Boxes { get; set; }

    [JsonPropertyName("spawns")]
    public List<SpawnDto>? Spawns { get; set; }

    [JsonPropertyName("portals")]
    public List<PortalDto>? Portals { get; set; }
}

internal sealed class BoundsDto
{
    [JsonPropertyName("minX")] public float? MinX { get; set; }
    [JsonPropertyName("minY")] public float? MinY { get; set; }
    [JsonPropertyName("maxX")] public float? MaxX { get; set; }
    [JsonPropertyName("maxY")] public float? MaxY { get; set; }
}

internal sealed class HeightfieldDto
{
    [JsonPropertyName("originX")] public float? OriginX { get; set; }
    [JsonPropertyName("originY")] public float? OriginY { get; set; }
    [JsonPropertyName("cellSize")] public float? CellSize { get; set; }
    [JsonPropertyName("columns")] public int? Columns { get; set; }
    [JsonPropertyName("rows")] public int? Rows { get; set; }

    /// <summary>Row-major samples: index = row * columns + column; (0,0) at the origin.</summary>
    [JsonPropertyName("heights")] public float[]? Heights { get; set; }
}

internal sealed class BoxDto
{
    [JsonPropertyName("minX")] public float? MinX { get; set; }
    [JsonPropertyName("minY")] public float? MinY { get; set; }
    [JsonPropertyName("minZ")] public float? MinZ { get; set; }
    [JsonPropertyName("maxX")] public float? MaxX { get; set; }
    [JsonPropertyName("maxY")] public float? MaxY { get; set; }
    [JsonPropertyName("maxZ")] public float? MaxZ { get; set; }
}

internal sealed class SpawnDto
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("x")] public float? X { get; set; }
    [JsonPropertyName("y")] public float? Y { get; set; }
    [JsonPropertyName("z")] public float? Z { get; set; }
}

internal sealed class PortalDto
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("x")] public float? X { get; set; }
    [JsonPropertyName("y")] public float? Y { get; set; }
    [JsonPropertyName("z")] public float? Z { get; set; }
    [JsonPropertyName("radius")] public float? Radius { get; set; }
    [JsonPropertyName("height")] public float? Height { get; set; }
    [JsonPropertyName("targetMapId")] public string? TargetMapId { get; set; }
    [JsonPropertyName("targetSpawn")] public string? TargetSpawn { get; set; }
}

/// <summary>
/// Source-generated context for map files. Required under NativeAOT (ADR-11); see
/// <c>ContentJsonContext</c>.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    NumberHandling = JsonNumberHandling.Strict)]
[JsonSerializable(typeof(MapFileDto))]
internal partial class MapJsonContext : JsonSerializerContext
{
}
