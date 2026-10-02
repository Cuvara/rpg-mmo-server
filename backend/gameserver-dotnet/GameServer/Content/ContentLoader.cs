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
    /// Finds the content directory when none was configured, by walking up from the
    /// <b>binary's</b> location looking for <c>content/items.json</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Anchored to <see cref="AppContext.BaseDirectory"/> rather than the working
    /// directory, because the working directory is a property of whoever launched the
    /// process and not of the deployment. A relative default resolved against it works
    /// under <c>dotnet run</c> from the module directory and breaks everywhere else — it
    /// broke every integration test on first contact, since those launch the server from
    /// their own directory. The binary, by contrast, always sits at a known distance from
    /// the content in every layout that exists: the repo tree, the test output tree, and
    /// the container image.
    /// </para>
    /// <para>
    /// Returns null when nothing is found, so the caller reports "not configured" against
    /// the path it actually tried rather than against a guess.
    /// </para>
    /// </remarks>
    public static string? ResolveDefaultDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "content");
            if (File.Exists(Path.Combine(candidate, ItemsFileName)))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }

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

        var files = new List<ContentFile>(1 + OptionalFileNames.Length)
        {
            new ContentFile(itemsPath, ReadFile(itemsPath)),
        };

        // Optional per-type files (ADR-30). Absent is legal for each; present means it is
        // parsed and validated with everything else, and refusing to boot on a bad one is the
        // same rule items.json has always had.
        foreach (string name in OptionalFileNames)
        {
            string path = Path.Combine(contentDirectory, name);
            if (File.Exists(path)) files.Add(new ContentFile(path, ReadFile(path)));
        }

        return LoadFromFiles(files);
    }

    /// <summary>
    /// Optional content files read beside <see cref="ItemsFileName"/>, in this order.
    /// </summary>
    public static readonly string[] OptionalFileNames =
    {
        AbilitiesFileName, StatsFileName, StatusesFileName, LootFileName,
    };

    /// <summary>Abilities (effect lists, deliveries, projectiles).</summary>
    public const string AbilitiesFileName = "abilities.json";

    /// <summary>Content stats (the replicated stat block).</summary>
    public const string StatsFileName = "stats.json";

    /// <summary>Status effects (DoT/HoT, modifiers, crowd control).</summary>
    public const string StatusesFileName = "statuses.json";

    /// <summary>Loot tables. Server-only: never served to clients.</summary>
    public const string LootFileName = "loot.json";

    private static byte[] ReadFile(string path)
    {
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (IOException ex)
        {
            throw new ContentLoadException($"Could not read '{path}': {ex.Message}", ex);
        }
    }

    /// <summary>One content document and where it came from.</summary>
    public readonly record struct ContentFile(string Origin, byte[] Bytes);

    /// <summary>
    /// Parses and validates content from bytes already in hand. Exposed for tests and for
    /// the path where content arrives over the network rather than off disk.
    /// </summary>
    /// <param name="bytes">The canonical document.</param>
    /// <param name="origin">Where the bytes came from, used only in error messages.</param>
    public static LoadedContent LoadFromBytes(byte[] bytes, string origin)
    {
        if (bytes == null) throw new ArgumentNullException(nameof(bytes));
        return LoadFromFiles(new[] { new ContentFile(origin, bytes) });
    }

    /// <summary>
    /// Parses, merges and validates a content set split across several documents. The FIRST
    /// document is the items document and must carry the <c>items</c> key; every top-level key
    /// (<c>items</c>, <c>abilities</c>, <c>stats</c>, <c>statuses</c>, <c>loot</c>) may be
    /// defined in at most one document.
    /// </summary>
    /// <remarks>
    /// <para><b>What is served.</b> A set of one document is served verbatim, exactly as
    /// before protocol 3. A set of several is served as one composed document,
    /// <c>{"items":…,"abilities":…,"stats":…,"statuses":…}</c>, whose values are the source
    /// documents' JSON token streams copied through <see cref="JsonDocument"/> — comments and
    /// whitespace dropped, nothing passed through the server's own model — so a client reads
    /// the same schema it always has, with more keys. <c>loot</c> and <c>$comment</c> are
    /// never served.</para>
    /// <para>The hash is over the served bytes, as before.</para>
    /// </remarks>
    public static LoadedContent LoadFromFiles(IReadOnlyList<ContentFile> files)
    {
        if (files == null) throw new ArgumentNullException(nameof(files));
        if (files.Count == 0) throw new ContentLoadException("No content documents were supplied.");

        string origin = files.Count == 1 ? files[0].Origin : DescribeSet(files);
        var errors = new List<string>();
        var definitions = new List<ItemDefinition>();
        var abilities = new List<AbilityDefinition>();
        var stats = new List<StatDefinition>();
        var statuses = new List<StatusDefinition>();
        var lootDtos = new List<(LootTableDto Dto, int Index, string Prefix)>();
        var keyOwner = new Dictionary<string, string>(StringComparer.Ordinal);

        for (int f = 0; f < files.Count; f++)
        {
            ContentFile file = files[f];
            if (file.Bytes == null) throw new ArgumentNullException(nameof(files), "A content document has null bytes.");

            ItemFileDto? dto;
            try
            {
                dto = JsonSerializer.Deserialize(file.Bytes, ContentJsonContext.Default.ItemFileDto);
            }
            catch (JsonException ex)
            {
                throw new ContentLoadException(
                    $"'{file.Origin}' is not valid JSON: {ex.Message}", ex);
            }

            if (dto == null)
            {
                throw new ContentLoadException($"'{file.Origin}' parsed to null. The file is probably empty.");
            }

            // Errors are prefixed with the file name only when there is more than one file,
            // so a single-document set reports exactly what it always reported.
            string prefix = files.Count > 1 ? Path.GetFileName(file.Origin) + ": " : string.Empty;

            if (f == 0 && dto.Items == null)
            {
                // Distinguishes "items": [] from a document with no items key at all. The
                // first is a deliberate empty set; the second is almost always a typo in the
                // key name, which would otherwise load as a silently empty game.
                throw new ContentLoadException(
                    $"'{file.Origin}' has no 'items' array. If the set is intentionally empty write " +
                    "\"items\": [] — a missing key is indistinguishable from a misspelled one, and " +
                    "both load as a game with no items in it.");
            }

            ClaimKey("items", dto.Items != null, file, keyOwner, errors);
            ClaimKey("abilities", dto.Abilities != null, file, keyOwner, errors);
            ClaimKey("stats", dto.Stats != null, file, keyOwner, errors);
            ClaimKey("statuses", dto.Statuses != null, file, keyOwner, errors);
            ClaimKey("loot", dto.Loot != null, file, keyOwner, errors);

            if (dto.Items != null)
            {
                for (int i = 0; i < dto.Items.Count; i++)
                {
                    var item = dto.Items[i];
                    if (item == null)
                    {
                        errors.Add($"{prefix}items[{i}] is null.");
                        continue;
                    }

                    var definition = WithPrefix(prefix, errors, e => Convert(item, i, e));
                    if (definition != null) definitions.Add(definition);
                }
            }

            // Absent rather than empty is legal here, unlike items — see ItemFileDto.Abilities
            // for why the keys are treated differently.
            if (dto.Abilities != null)
            {
                for (int i = 0; i < dto.Abilities.Count; i++)
                {
                    var ability = dto.Abilities[i];
                    if (ability == null)
                    {
                        errors.Add($"{prefix}abilities[{i}] is null.");
                        continue;
                    }

                    var def = WithPrefix(prefix, errors, e => ConvertAbility(ability, i, e));
                    if (def != null) abilities.Add(def);
                }
            }

            if (dto.Stats != null)
            {
                for (int i = 0; i < dto.Stats.Count; i++)
                {
                    var stat = dto.Stats[i];
                    if (stat == null)
                    {
                        errors.Add($"{prefix}stats[{i}] is null.");
                        continue;
                    }

                    var def = WithPrefix(prefix, errors, e => ConvertStat(stat, i, e));
                    if (def != null) stats.Add(def);
                }
            }

            if (dto.Statuses != null)
            {
                for (int i = 0; i < dto.Statuses.Count; i++)
                {
                    var status = dto.Statuses[i];
                    if (status == null)
                    {
                        errors.Add($"{prefix}statuses[{i}] is null.");
                        continue;
                    }

                    var def = WithPrefix(prefix, errors, e => ConvertStatus(status, i, e));
                    if (def != null) statuses.Add(def);
                }
            }

            if (dto.Loot != null)
            {
                for (int i = 0; i < dto.Loot.Count; i++)
                {
                    if (dto.Loot[i] == null)
                    {
                        errors.Add($"{prefix}loot[{i}] is null.");
                        continue;
                    }

                    lootDtos.Add((dto.Loot[i], i, prefix));
                }
            }
        }

        byte[] served = files.Count == 1 ? files[0].Bytes : Compose(files);
        string hash = ComputeHash(served);

        // Duplicate ids throw out of the ContentDatabase constructor rather than being
        // collected here, so catch and fold that into the same report instead of letting
        // one class of content error escape as a different exception type.
        ContentDatabase database;
        try
        {
            database = new ContentDatabase(definitions, abilities, stats, statuses, hash);
        }
        catch (ArgumentException ex)
        {
            errors.Add(ex.Message);
            throw new ContentLoadException(Report(origin, errors), ex);
        }

        ContentValidation.Validate(database, errors);

        LootTables loot = BuildLoot(lootDtos, database, errors);

        if (errors.Count > 0)
        {
            throw new ContentLoadException(Report(origin, errors));
        }

        return new LoadedContent(database, served, hash, loot);
    }

    private static string DescribeSet(IReadOnlyList<ContentFile> files)
    {
        string? dir = Path.GetDirectoryName(files[0].Origin);
        return string.IsNullOrEmpty(dir) ? files[0].Origin : dir;
    }

    private static void ClaimKey(
        string key, bool present, ContentFile file, Dictionary<string, string> owner, List<string> errors)
    {
        if (!present) return;
        if (owner.TryGetValue(key, out string? first))
        {
            errors.Add($"'{key}' is defined in both '{Path.GetFileName(first)}' and '{Path.GetFileName(file.Origin)}'. " +
                       "Each content key lives in exactly one file; two definitions of one key would " +
                       "make which one wins depend on file order.");
            return;
        }

        owner[key] = file.Origin;
    }

    private static T? WithPrefix<T>(string prefix, List<string> errors, Func<List<string>, T?> convert)
        where T : class
    {
        if (prefix.Length == 0) return convert(errors);

        var local = new List<string>();
        T? result = convert(local);
        foreach (string e in local) errors.Add(prefix + e);
        return result;
    }

    /// <summary>
    /// The served document of a multi-file set: <c>items</c>, <c>abilities</c>, <c>stats</c>
    /// and <c>statuses</c>, each copied token-for-token from the file that defines it.
    /// </summary>
    private static byte[] Compose(IReadOnlyList<ContentFile> files)
    {
        var docOptions = new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        var docs = new List<JsonDocument>(files.Count);
        try
        {
            foreach (ContentFile file in files) docs.Add(JsonDocument.Parse(file.Bytes, docOptions));

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                foreach (string key in ServedKeys)
                {
                    foreach (JsonDocument doc in docs)
                    {
                        if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                        if (!TryGetPropertyIgnoreCase(doc.RootElement, key, out JsonElement value)) continue;
                        writer.WritePropertyName(key);
                        value.WriteTo(writer);
                        break;
                    }
                }

                writer.WriteEndObject();
            }

            return stream.ToArray();
        }
        catch (JsonException ex)
        {
            throw new ContentLoadException($"Content could not be composed for serving: {ex.Message}", ex);
        }
        finally
        {
            foreach (JsonDocument doc in docs) doc.Dispose();
        }
    }

    /// <summary>Keys served to clients, in document order. Loot is deliberately absent.</summary>
    private static readonly string[] ServedKeys = { "items", "abilities", "stats", "statuses" };

    private static bool TryGetPropertyIgnoreCase(JsonElement obj, string name, out JsonElement value)
    {
        // The DTO context is case-insensitive, so the composer must find the same property
        // the parser did.
        foreach (JsonProperty p in obj.EnumerateObject())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = p.Value;
                return true;
            }
        }

        value = default;
        return false;
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
    /// Turns one ability DTO into a definition, appending a diagnosis per bad field.
    /// Returns null when the entry cannot be built at all.
    /// </summary>
    private static AbilityDefinition? ConvertAbility(AbilityDto dto, int index, List<string> errors)
    {
        // Reported as the index when there is no id to name it by. An ability's id is a
        // number, so there is no readable name to fall back on the way an item's id string
        // provides one — the name field is display text and may be absent too.
        string where = dto.Id != null ? dto.Id.Value.ToString(CultureInfo.InvariantCulture) : $"abilities[{index}]";

        if (dto.Id == null)
        {
            errors.Add($"abilities[{index}]: 'id' is missing. Every ability needs one, and ids start at 1.");
            return null;
        }

        // Protocol 3 form (ADR-30): a delivery and an ordered effect list.
        if (dto.Delivery != null || dto.Effects != null)
        {
            if (dto.Delivery == null)
            {
                errors.Add($"ability {where}: has 'effects' but no 'delivery'. " +
                           "Valid deliveries: self, entity, ground, projectile.");
                return null;
            }

            return ConvertAbilityV3(dto, where, errors);
        }

        if (!TryParseEnum(dto.Targeting, out AbilityTargeting targeting))
        {
            errors.Add($"ability {where}: targeting '{dto.Targeting}' is not recognised. " +
                       "Valid: self, entity, ground.");
            return null;
        }

        if (!TryParseEnum(dto.Effect, out AbilityEffect effect))
        {
            errors.Add($"ability {where}: effect '{dto.Effect}' is not recognised. " +
                       "Valid: damage, heal.");
            return null;
        }

        if (dto.Power == null)
        {
            errors.Add($"ability {where}: 'power' is missing. Write 0 for an ability with no magnitude.");
            return null;
        }

        if (dto.CooldownTicks == null)
        {
            errors.Add($"ability {where}: 'cooldownTicks' is missing. Write 0 for no cooldown. " +
                       "The unit is SIMULATION TICKS, not milliseconds.");
            return null;
        }

        // Range and radius default to 0 rather than being required, because whether either
        // is meaningful depends on the targeting mode. ContentValidation is what decides
        // that a Ground ability with no radius is an error; repeating the rule here would
        // give it two homes that can disagree.
        return new AbilityDefinition(
            dto.Id.Value,
            dto.Name ?? string.Empty,
            targeting,
            effect,
            dto.Range ?? 0f,
            dto.Radius ?? 0f,
            dto.Power.Value,
            dto.CooldownTicks.Value);
    }

    /// <summary>
    /// The protocol 3 form of an ability: <c>delivery</c> + ordered <c>effects</c> (+
    /// <c>projectile</c>). Validation of values is <see cref="ContentValidation"/>'s; this
    /// only refuses what cannot be built or is ambiguous.
    /// </summary>
    private static AbilityDefinition? ConvertAbilityV3(AbilityDto dto, string where, List<string> errors)
    {
        bool ok = true;

        if (dto.Targeting != null || dto.Effect != null || dto.Power != null)
        {
            errors.Add($"ability {where}: has 'delivery' and also legacy 'targeting'/'effect'/'power'. " +
                       "Write one form: delivery + effects (protocol 3) or targeting + effect + power.");
            ok = false;
        }

        if (!TryParseEnum(dto.Delivery, out AbilityDelivery delivery))
        {
            errors.Add($"ability {where}: delivery '{dto.Delivery}' is not recognised. " +
                       "Valid: self, entity, ground, projectile.");
            return null;
        }

        if (dto.Effects == null)
        {
            errors.Add($"ability {where}: 'effects' is missing. A protocol 3 ability is a delivery plus " +
                       "an ordered effect list.");
            return null;
        }

        if (dto.CooldownTicks == null)
        {
            errors.Add($"ability {where}: 'cooldownTicks' is missing. Write 0 for no cooldown. " +
                       "The unit is SIMULATION TICKS, not milliseconds.");
            ok = false;
        }

        var effects = new List<EffectSpec>(dto.Effects.Count);
        for (int i = 0; i < dto.Effects.Count; i++)
        {
            EffectDto? e = dto.Effects[i];
            string ew = $"ability {where}: effects[{i}]: ";
            if (e == null)
            {
                errors.Add(ew + "is null.");
                ok = false;
                continue;
            }

            switch (e.Kind)
            {
                case "damage":
                case "heal":
                    if (e.Power == null)
                    {
                        errors.Add(ew + $"'{e.Kind}' needs 'power'.");
                        ok = false;
                        continue;
                    }

                    if (e.StatusId != null)
                    {
                        errors.Add(ew + $"'{e.Kind}' does not read 'statusId'; remove it.");
                        ok = false;
                    }

                    effects.Add(e.Kind == "damage" ? EffectSpec.Damage(e.Power.Value) : EffectSpec.Heal(e.Power.Value));
                    break;
                case "apply_status":
                    if (e.StatusId == null)
                    {
                        errors.Add(ew + "'apply_status' needs 'statusId'.");
                        ok = false;
                        continue;
                    }

                    if (e.Power != null)
                    {
                        errors.Add(ew + "'apply_status' does not read 'power'; remove it.");
                        ok = false;
                    }

                    effects.Add(EffectSpec.ApplyStatus(e.StatusId.Value));
                    break;
                default:
                    errors.Add(ew + $"kind '{e.Kind}' is not recognised. Valid: damage, heal, apply_status.");
                    ok = false;
                    break;
            }
        }

        ProjectileSpec projectile = ProjectileSpec.None;
        if (delivery == AbilityDelivery.Projectile)
        {
            if (dto.Projectile == null)
            {
                errors.Add($"ability {where}: delivery is projectile but there is no 'projectile' block " +
                           "(speed, radius, range).");
                return null;
            }

            if (dto.Projectile.Speed == null || dto.Projectile.Radius == null || dto.Projectile.Range == null)
            {
                errors.Add($"ability {where}: 'projectile' needs all of speed, radius and range.");
                return null;
            }

            projectile = new ProjectileSpec(dto.Projectile.Speed.Value, dto.Projectile.Radius.Value, dto.Projectile.Range.Value);
        }
        else if (dto.Projectile != null)
        {
            errors.Add($"ability {where}: has a 'projectile' block but delivery is {dto.Delivery}; " +
                       "only projectile delivery reads it.");
            ok = false;
        }

        if (!ok) return null;

        return new AbilityDefinition(
            dto.Id!.Value,
            dto.Name ?? string.Empty,
            delivery,
            effects,
            dto.Range ?? 0f,
            dto.Radius ?? 0f,
            dto.CooldownTicks!.Value,
            projectile);
    }

    private static StatDefinition? ConvertStat(StatDto dto, int index, List<string> errors)
    {
        if (dto.Id == null)
        {
            errors.Add($"stats[{index}]: 'id' is missing. Stat ids start at 1 and are permanent.");
            return null;
        }

        if (dto.Default == null)
        {
            errors.Add($"stat {dto.Id.Value.ToString(CultureInfo.InvariantCulture)}: 'default' is missing. " +
                       "Every stat needs the value an entity starts with.");
            return null;
        }

        return new StatDefinition(dto.Id.Value, dto.Key ?? string.Empty, dto.Default.Value);
    }

    private static StatusDefinition? ConvertStatus(StatusDto dto, int index, List<string> errors)
    {
        if (dto.Id == null)
        {
            errors.Add($"statuses[{index}]: 'id' is missing. Status ids start at 1 and are permanent.");
            return null;
        }

        string where = $"status {dto.Id.Value.ToString(CultureInfo.InvariantCulture)}";
        bool ok = true;

        if (dto.DurationTicks == null)
        {
            errors.Add($"{where}: 'durationTicks' is missing. Write 0 for a status that lasts until removed. " +
                       "The unit is BASE SIMULATION TICKS.");
            ok = false;
        }

        PeriodicSpec periodic = PeriodicSpec.None;
        if (dto.Periodic != null)
        {
            PeriodicKind kind;
            switch (dto.Periodic.Kind)
            {
                case "damage": kind = PeriodicKind.Damage; break;
                case "heal": kind = PeriodicKind.Heal; break;
                default:
                    errors.Add($"{where}: periodic kind '{dto.Periodic.Kind}' is not recognised. Valid: damage, heal.");
                    ok = false;
                    kind = PeriodicKind.None;
                    break;
            }

            if (dto.Periodic.IntervalTicks == null || dto.Periodic.Amount == null)
            {
                errors.Add($"{where}: 'periodic' needs both intervalTicks and amount.");
                ok = false;
            }
            else if (kind != PeriodicKind.None)
            {
                periodic = new PeriodicSpec(kind, dto.Periodic.IntervalTicks.Value, dto.Periodic.Amount.Value);
            }
        }

        var modifiers = new List<StatModifier>();
        if (dto.Modifiers != null)
        {
            for (int i = 0; i < dto.Modifiers.Count; i++)
            {
                ModifierDto? m = dto.Modifiers[i];
                if (m == null)
                {
                    errors.Add($"{where}: modifiers[{i}] is null.");
                    ok = false;
                    continue;
                }

                StatModifierTarget target;
                switch (m.Target)
                {
                    case "attack": target = StatModifierTarget.Attack; break;
                    case "defense": target = StatModifierTarget.Defense; break;
                    case "speed": target = StatModifierTarget.Speed; break;
                    case "max_hp": target = StatModifierTarget.MaxHp; break;
                    case "stat": target = StatModifierTarget.ContentStat; break;
                    default:
                        errors.Add($"{where}: modifiers[{i}]: target '{m.Target}' is not recognised. " +
                                   "Valid: attack, defense, speed, max_hp, stat.");
                        ok = false;
                        continue;
                }

                if (target == StatModifierTarget.ContentStat && m.StatId == null)
                {
                    errors.Add($"{where}: modifiers[{i}]: target 'stat' needs 'statId'.");
                    ok = false;
                    continue;
                }

                modifiers.Add(new StatModifier(target, m.StatId ?? 0u, m.Add ?? 0, m.MultiplierPermille ?? 0));
            }
        }

        CrowdControl cc = CrowdControl.None;
        if (dto.CrowdControl != null)
        {
            foreach (string? flag in dto.CrowdControl)
            {
                switch (flag)
                {
                    case "stun": cc |= CrowdControl.Stun; break;
                    case "root": cc |= CrowdControl.Root; break;
                    case "silence": cc |= CrowdControl.Silence; break;
                    case "slow": cc |= CrowdControl.Slow; break;
                    default:
                        errors.Add($"{where}: crowdControl '{flag}' is not recognised. Valid: stun, root, silence, slow.");
                        ok = false;
                        break;
                }
            }
        }

        if (!ok) return null;

        return new StatusDefinition(
            dto.Id.Value,
            dto.Key ?? string.Empty,
            dto.DurationTicks!.Value,
            dto.MaxStacks ?? 1,
            periodic,
            modifiers,
            cc,
            dto.SlowPermille ?? 0);
    }

    /// <summary>Largest permitted number of entries in one loot table (structural bound, not balance).</summary>
    public const int MaxLootEntries = 32;

    /// <summary>
    /// Builds and validates loot tables against the (already built) item set. Server-only
    /// content, so its rules live here rather than in the shared validator.
    /// </summary>
    private static LootTables BuildLoot(
        List<(LootTableDto Dto, int Index, string Prefix)> dtos, ContentDatabase database, List<string> errors)
    {
        var tables = new List<LootTable>(dtos.Count);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var types = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (dto, index, prefix) in dtos)
        {
            string where = prefix + (dto.Id != null ? $"loot '{dto.Id}'" : $"loot[{index}]");
            int before = errors.Count;

            if (string.IsNullOrEmpty(dto.Id) || !ContentValidation.IsValidId(dto.Id))
                errors.Add($"{where}: id must be lowercase letters, digits and underscores.");
            else if (!ids.Add(dto.Id))
                errors.Add($"{where}: duplicate loot table id.");

            if (string.IsNullOrWhiteSpace(dto.EntityType))
                errors.Add($"{where}: 'entityType' is missing. A table applies to one entity type, e.g. \"mob\".");
            else if (!types.Add(dto.EntityType))
                errors.Add($"{where}: a second table for entity type '{dto.EntityType}'. One table per type.");

            if (dto.DespawnTicks == null || dto.DespawnTicks.Value < 1)
                errors.Add($"{where}: 'despawnTicks' must be at least 1 (base simulation ticks a drop lies in the world).");

            if (dto.Entries == null || dto.Entries.Count == 0)
                errors.Add($"{where}: 'entries' is missing or empty. A table that can drop nothing should not exist.");
            else if (dto.Entries.Count > MaxLootEntries)
                errors.Add($"{where}: has {dto.Entries.Count} entries, limit is {MaxLootEntries}.");

            var entries = new List<LootEntry>();
            if (dto.Entries != null)
            {
                for (int i = 0; i < dto.Entries.Count; i++)
                {
                    LootEntryDto? e = dto.Entries[i];
                    string ew = $"{where}: entries[{i}]: ";
                    if (e == null)
                    {
                        errors.Add(ew + "is null.");
                        continue;
                    }

                    if (string.IsNullOrEmpty(e.ItemId) || !database.TryGetItem(e.ItemId, out ItemDefinition? item) || item == null)
                    {
                        errors.Add(ew + $"item '{e.ItemId}' does not exist in items.json.");
                        continue;
                    }

                    int chance = e.ChancePermille ?? 0;
                    int min = e.Min ?? 1;
                    int max = e.Max ?? min;
                    if (chance < 1 || chance > 1000)
                        errors.Add(ew + $"chancePermille is {chance}; it must be between 1 and 1000.");
                    if (min < 1)
                        errors.Add(ew + $"min is {min}; it must be at least 1.");
                    if (max < min)
                        errors.Add(ew + $"max {max} is below min {min}.");
                    if (max > item.StackMax)
                        errors.Add(ew + $"max {max} exceeds item '{item.Id}' stackMax {item.StackMax}; a drop is one stack.");

                    entries.Add(new LootEntry(e.ItemId, chance, min, max));
                }
            }

            if (errors.Count == before)
            {
                tables.Add(new LootTable(dto.Id!, dto.EntityType!, dto.DespawnTicks!.Value, entries));
            }
        }

        return errors.Count == 0 ? new LootTables(tables) : LootTables.Empty;
    }

    private static bool TryParseEnum(string? text, out AbilityDelivery delivery)
    {
        switch (text)
        {
            case "self": delivery = AbilityDelivery.Self; return true;
            case "entity": delivery = AbilityDelivery.Entity; return true;
            case "ground": delivery = AbilityDelivery.Ground; return true;
            case "projectile": delivery = AbilityDelivery.Projectile; return true;
            default: delivery = AbilityDelivery.Self; return false;
        }
    }

    private static bool TryParseEnum(string? text, out AbilityTargeting targeting)
    {
        switch (text)
        {
            case "self": targeting = AbilityTargeting.Self; return true;
            case "entity": targeting = AbilityTargeting.Entity; return true;
            case "ground": targeting = AbilityTargeting.Ground; return true;
            default: targeting = AbilityTargeting.Self; return false;
        }
    }

    private static bool TryParseEnum(string? text, out AbilityEffect effect)
    {
        switch (text)
        {
            case "damage": effect = AbilityEffect.Damage; return true;
            case "heal": effect = AbilityEffect.Heal; return true;
            default: effect = AbilityEffect.Damage; return false;
        }
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
        : this(database, canonicalBytes, hash, null)
    {
    }

    public LoadedContent(ContentDatabase database, byte[] canonicalBytes, string hash, LootTables? loot)
    {
        Database = database ?? throw new ArgumentNullException(nameof(database));
        CanonicalBytes = canonicalBytes ?? throw new ArgumentNullException(nameof(canonicalBytes));
        Hash = hash ?? throw new ArgumentNullException(nameof(hash));
        Loot = loot ?? LootTables.Empty;
    }

    /// <summary>Loot tables (server-only; not part of <see cref="CanonicalBytes"/>).</summary>
    public LootTables Loot { get; }

    public ContentDatabase Database { get; }

    /// <summary>The bytes as read from disk, served to clients verbatim.</summary>
    public byte[] CanonicalBytes { get; }

    public string Hash { get; }
}
