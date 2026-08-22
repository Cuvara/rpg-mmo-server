using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Shared.GameLogic.Content;

namespace GameServer.Content;

/// <summary>Raised when content cannot be loaded or does not validate.</summary>
/// <remarks>
/// Fatal by design. A server running on content it could not parse would serve some
/// unknowable subset of the intended game, and every symptom downstream — a missing item,
/// a wrong stat, a loot table pointing at nothing — would be attributed to whichever
/// system noticed first rather than to the file that was wrong.
/// </remarks>
public sealed class ContentLoadException : Exception
{
    public ContentLoadException(string message) : base(message) { }
    public ContentLoadException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Reads content files from disk, validates them, and produces an immutable
/// <see cref="ContentDatabase"/> together with the exact bytes to serve to clients.
/// </summary>
/// <remarks>
/// <para>
/// The loader keeps the <b>canonical bytes</b> alongside the parsed database, and serves
/// those bytes verbatim rather than re-serialising the parsed objects. Re-serialising
/// would mean the client parses a document the server never read, so a bug in the
/// server's writer would present as a bug in the client's reader. Serving what was read
/// keeps the hash meaningful: it identifies a file on disk, not a round trip.
/// </para>
/// </remarks>
public static class ContentLoader
{
    /// <summary>File name expected inside the content directory.</summary>
    public const string ItemsFileName = "items.json";

    /// <summary>
    /// Loads and validates the content set in <paramref name="contentDirectory"/>.
    /// </summary>
    /// <exception cref="ContentLoadException">
    /// The directory or file is missing, the JSON is malformed, or validation failed. The
    /// message names the file and every problem found, so one restart reports all of them.
    /// </exception>
    public static LoadedContent Load(string contentDirectory)
    {
        if (string.IsNullOrWhiteSpace(contentDirectory))
            throw new ContentLoadException("Content directory was not configured.");

        if (!Directory.Exists(contentDirectory))
        {
            throw new ContentLoadException(
                $"Content directory '{contentDirectory}' does not exist. The server has no game " +
                "content to run on. Set --content-dir (or CONTENT_DIR) to the directory holding " +
                $"{ItemsFileName}.");
        }

        string itemsPath = Path.Combine(contentDirectory, ItemsFileName);
        if (!File.Exists(itemsPath))
        {
            throw new ContentLoadException(
                $"'{itemsPath}' does not exist. Every content set needs {ItemsFileName}, even if " +
                "its items array is empty — an absent file and an intentionally empty one are not " +
                "the same statement, and only one of them is a mistake.");
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(itemsPath);
        }
        catch (IOException ex)
        {
            throw new ContentLoadException($"Could not read '{itemsPath}': {ex.Message}", ex);
        }

        return LoadFromBytes(bytes, itemsPath);
    }

    /// <summary>
    /// Parses and validates content from bytes already in hand. Exposed for tests and for
    /// the path where content arrives over the network rather than off disk.
    /// </summary>
    /// <param name="bytes">The canonical document.</param>
    /// <param name="origin">Where the bytes came from, used only in error messages.</param>
    public static LoadedContent LoadFromBytes(byte[] bytes, string origin)
    {
        if (bytes == null) throw new ArgumentNullException(nameof(bytes));

        ItemFileDto? file;
        try
        {
            file = JsonSerializer.Deserialize(bytes, ContentJsonContext.Default.ItemFileDto);
        }
        catch (JsonException ex)
        {
            throw new ContentLoadException(
                $"'{origin}' is not valid JSON: {ex.Message}", ex);
        }

        if (file == null)
        {
            throw new ContentLoadException($"'{origin}' parsed to null. The file is probably empty.");
        }

        // Distinguishes "items": [] from a document with no items key at all. The first is
        // a deliberate empty set; the second is almost always a typo in the key name, which
        // would otherwise load as a silently empty game.
        if (file.Items == null)
        {
            throw new ContentLoadException(
                $"'{origin}' has no 'items' array. If the set is intentionally empty write " +
                "\"items\": [] — a missing key is indistinguishable from a misspelled one, and " +
                "both load as a game with no items in it.");
        }

        var errors = new List<string>();
        var definitions = new List<ItemDefinition>(file.Items.Count);

        for (int i = 0; i < file.Items.Count; i++)
        {
            var dto = file.Items[i];
            if (dto == null)
            {
                errors.Add($"items[{i}] is null.");
                continue;
            }

            var definition = Convert(dto, i, errors);
            if (definition != null)
            {
                definitions.Add(definition);
            }
        }

        // Duplicate ids throw out of the ContentDatabase constructor rather than being
        // collected here, so catch and fold that into the same report instead of letting
        // one class of content error escape as a different exception type.
        ContentDatabase database;
        string hash = ComputeHash(bytes);
        try
        {
            database = new ContentDatabase(definitions, hash);
        }
        catch (ArgumentException ex)
        {
            errors.Add(ex.Message);
            throw new ContentLoadException(Report(origin, errors), ex);
        }

        ContentValidation.Validate(database, errors);

        if (errors.Count > 0)
        {
            throw new ContentLoadException(Report(origin, errors));
        }

        return new LoadedContent(database, bytes, hash);
    }

    /// <summary>
    /// Turns one DTO into a definition, appending a diagnosis per bad field. Returns null
    /// when the entry cannot be built at all.
    /// </summary>
    private static ItemDefinition? Convert(ItemDto dto, int index, List<string> errors)
    {
        string where = dto.Id ?? $"items[{index}]";

        if (string.IsNullOrWhiteSpace(dto.Id))
        {
            errors.Add($"items[{index}]: 'id' is missing. Every item needs one.");
            return null;
        }

        if (!TryParseEnum(dto.Slot, out ItemSlot slot))
        {
            errors.Add($"item '{where}': slot '{dto.Slot}' is not recognised. " +
                       "Valid: none, weapon, head, chest, legs, trinket.");
            return null;
        }

        if (!TryParseEnum(dto.Rarity, out ItemRarity rarity))
        {
            errors.Add($"item '{where}': rarity '{dto.Rarity}' is not recognised. " +
                       "Valid: common, uncommon, rare, epic, legendary.");
            return null;
        }

        if (dto.StackMax == null)
        {
            errors.Add($"item '{where}': 'stackMax' is missing. Write 1 for equipment.");
            return null;
        }

        return new ItemDefinition(
            dto.Id!,
            dto.Name ?? string.Empty,
            slot,
            rarity,
            dto.StackMax.Value,
            dto.Attack ?? 0,
            dto.Defense ?? 0,
            dto.LevelRequirement ?? 0);
    }

    /// <summary>
    /// Parses an enum from its lowercase content spelling.
    /// </summary>
    /// <remarks>
    /// Hand-matched rather than <c>Enum.Parse</c>, which is reflective and therefore a
    /// NativeAOT hazard, and which would also accept the numeric form — letting
    /// <c>"slot": "3"</c> load as Chest and making the content file depend on enum
    /// ordering that content authors have no reason to know about.
    /// </remarks>
    private static bool TryParseEnum(string? text, out ItemSlot slot)
    {
        switch (text)
        {
            case "none": slot = ItemSlot.None; return true;
            case "weapon": slot = ItemSlot.Weapon; return true;
            case "head": slot = ItemSlot.Head; return true;
            case "chest": slot = ItemSlot.Chest; return true;
            case "legs": slot = ItemSlot.Legs; return true;
            case "trinket": slot = ItemSlot.Trinket; return true;
            default: slot = ItemSlot.None; return false;
        }
    }

    private static bool TryParseEnum(string? text, out ItemRarity rarity)
    {
        switch (text)
        {
            case "common": rarity = ItemRarity.Common; return true;
            case "uncommon": rarity = ItemRarity.Uncommon; return true;
            case "rare": rarity = ItemRarity.Rare; return true;
            case "epic": rarity = ItemRarity.Epic; return true;
            case "legendary": rarity = ItemRarity.Legendary; return true;
            default: rarity = ItemRarity.Common; return false;
        }
    }

    /// <summary>
    /// SHA-256 over the canonical bytes, lowercase hex, truncated to 16 characters.
    /// </summary>
    /// <remarks>
    /// Truncated because this is a cache key and a change detector, not a security
    /// boundary — 64 bits is far past the point where an accidental collision between two
    /// hand-edited content files is worth considering, and a short hash is one a human can
    /// compare across a log line and an HTTP header at a glance.
    /// </remarks>
    public static string ComputeHash(byte[] bytes)
    {
        using var sha = SHA256.Create();
        byte[] digest = sha.ComputeHash(bytes);

        var sb = new StringBuilder(16);
        for (int i = 0; i < 8; i++)
        {
            sb.Append(digest[i].ToString("x2", CultureInfo.InvariantCulture));
        }

        return sb.ToString();
    }

    private static string Report(string origin, List<string> errors)
    {
        var sb = new StringBuilder();
        sb.Append("Content in '").Append(origin).Append("' is invalid — ")
          .Append(errors.Count).Append(errors.Count == 1 ? " problem:" : " problems:");

        foreach (string error in errors)
        {
            sb.Append("\n  - ").Append(error);
        }

        sb.Append("\nThe server will not start on content it cannot vouch for. Fix the file and " +
                  "restart; every problem above is reported at once so one restart clears them all.");
        return sb.ToString();
    }
}

/// <summary>
/// A validated content set plus the exact bytes and hash to serve to clients.
/// </summary>
public sealed class LoadedContent
{
    public LoadedContent(ContentDatabase database, byte[] canonicalBytes, string hash)
    {
        Database = database ?? throw new ArgumentNullException(nameof(database));
        CanonicalBytes = canonicalBytes ?? throw new ArgumentNullException(nameof(canonicalBytes));
        Hash = hash ?? throw new ArgumentNullException(nameof(hash));
    }

    public ContentDatabase Database { get; }

    /// <summary>The bytes as read from disk, served to clients verbatim.</summary>
    public byte[] CanonicalBytes { get; }

    public string Hash { get; }
}
