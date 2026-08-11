using System.Text;
using System.Text.Json;

namespace GameServer.Tests.Golden;

/// <summary>
/// Shared plumbing for the ADR-10 golden vectors: the fixture schema, the
/// IEEE-754 hex encoding, and locating the fixture directory.
///
/// <para>
/// The fixtures live inside <c>Shared.GameLogic/GoldenVectors/</c> — i.e. inside
/// the folder Unity consumes as a UPM package — because the Unity Test Runner
/// reads the same files from the package path. Anything the server-side reader
/// does that Unity's <c>JsonUtility</c> cannot do would silently break that, so
/// the schema is deliberately dull: a top-level object with a single
/// <c>cases</c> array, public fields only, no dictionaries, no properties, no
/// nesting.
/// </para>
/// </summary>
internal static class GoldenVectors
{
    /// <summary>Fixture directory, resolved from the test assembly location.</summary>
    public static string Directory
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                string candidate = Path.Combine(dir.FullName, "Shared.GameLogic", "GoldenVectors");
                if (System.IO.Directory.Exists(candidate)) return candidate;
                dir = dir.Parent;
            }

            throw new DirectoryNotFoundException(
                "Shared.GameLogic/GoldenVectors not found above " + AppContext.BaseDirectory);
        }
    }

    public static string PathTo(string file) => Path.Combine(Directory, file);

    // ── IEEE-754 hex encoding ────────────────────────────────────────────────
    //
    // Floats are stored as their exact bit pattern, not as decimal text. Decimal
    // text does not round-trip identically through two different serializers, and
    // a tolerance-based comparison would not test the property these vectors
    // exist to protect (ADR-10 decision 4).

    public static string Hex(float value) =>
        "0x" + unchecked((uint)BitConverter.SingleToInt32Bits(value)).ToString("X8");

    public static float Float(string hex)
    {
        if (hex == null) throw new ArgumentNullException(nameof(hex));
        string digits = hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex.Substring(2) : hex;
        return BitConverter.Int32BitsToSingle(unchecked((int)Convert.ToUInt32(digits, 16)));
    }

    /// <summary>
    /// Bit-exact float comparison. <c>==</c> would call NaN != NaN and 0f == -0f,
    /// both of which are divergences worth failing on.
    /// </summary>
    public static void AssertBitEqual(string expectedHex, float actual, string because)
    {
        int expectedBits = unchecked((int)Convert.ToUInt32(expectedHex.Substring(2), 16));
        int actualBits = BitConverter.SingleToInt32Bits(actual);
        if (expectedBits != actualBits)
        {
            throw new Xunit.Sdk.XunitException(
                $"{because}: expected {expectedHex} ({Float(expectedHex)}), got {Hex(actual)} ({actual})");
        }
    }

    // ── Reading ──────────────────────────────────────────────────────────────

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        IncludeFields = true,
        ReadCommentHandling = JsonCommentHandling.Disallow,
    };

    public static T[] Load<T>(string file)
    {
        string json = File.ReadAllText(PathTo(file));
        var doc = JsonSerializer.Deserialize<CaseFile<T>>(json, ReadOptions)
                  ?? throw new InvalidDataException(file + " deserialized to null");
        if (doc.cases == null || doc.cases.Length == 0)
            throw new InvalidDataException(file + " has no cases");
        return doc.cases;
    }

    private sealed class CaseFile<T>
    {
        // Assigned by the deserializer only.
        public T[]? cases = null;
    }

    // ── Writing (regeneration only) ──────────────────────────────────────────

    /// <summary>
    /// Serialize to the exact subset Unity's JsonUtility reads: one object with a
    /// <c>cases</c> array of flat objects. Written by hand rather than by a
    /// serializer so the committed file's shape cannot drift with a library
    /// upgrade.
    /// </summary>
    public static string Serialize<T>(IEnumerable<T> cases)
    {
        var sb = new StringBuilder();
        sb.Append("{\n  \"cases\": [\n");
        var fields = typeof(T).GetFields();
        bool firstCase = true;
        foreach (T c in cases)
        {
            if (!firstCase) sb.Append(",\n");
            firstCase = false;
            sb.Append("    {");
            bool firstField = true;
            foreach (var f in fields)
            {
                if (!firstField) sb.Append(',');
                firstField = false;
                sb.Append("\n      \"").Append(f.Name).Append("\": ");
                object? v = f.GetValue(c);
                switch (v)
                {
                    case null: sb.Append("\"\""); break;
                    case string s: sb.Append('"').Append(s.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"'); break;
                    case bool b: sb.Append(b ? "true" : "false"); break;
                    default: sb.Append(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)); break;
                }
            }
            sb.Append("\n    }");
        }
        sb.Append("\n  ]\n}\n");
        return sb.ToString();
    }
}

// ── Fixture schema ───────────────────────────────────────────────────────────
// Public fields, flat, no properties: the subset Unity's JsonUtility binds.

/// <summary>One <c>MovementSystem.TryMove</c> vector.</summary>
public sealed class MovementCase
{
    public string name = "";
    public string posX = "";
    public string posY = "";
    public string moveX = "";
    public string moveY = "";
    public string speed = "";
    public string dt = "";
    public string minX = "";
    public string minY = "";
    public string maxX = "";
    public string maxY = "";
    public bool dead;
    public string expectedResult = "";
    public string expectedX = "";
    public string expectedY = "";
}

/// <summary>
/// One combat vector. <c>kind</c> selects the operation:
/// <c>damage</c> (CalculateDamage), <c>death</c> (HandleDeath) or
/// <c>validate_attack</c> (ValidateAttack). Fields the kind does not use are zero.
/// </summary>
public sealed class CombatCase
{
    public string name = "";
    public string kind = "";

    // damage
    public int attackerAttack;
    public int defenderDefense;
    public int expectedDamage;

    // death
    public int hp;
    public bool alreadyDead;
    public bool expectedDied;
    public int expectedHp;
    public bool expectedDead;

    // validate_attack
    public string attackerX = "";
    public string attackerY = "";
    public string targetX = "";
    public string targetY = "";
    public bool targetDead;
    public long currentTick;
    public long cooldownUntilTick;
    public bool expectedValid;

    /// <summary>
    /// Empty when the operation is valid. Otherwise the leading, float-free part of
    /// the error message — the range error embeds a formatted distance, and a
    /// number formatted by two different runtimes is not the thing under test.
    /// </summary>
    public string expectedErrorPrefix = "";
}

/// <summary>
/// One <c>Vec2</c> vector, aimed squarely at the three <c>MathF.Sqrt</c> call sites
/// (<c>Vec2.Magnitude</c>, <c>Vec2.Distance</c>, <c>MovementSystem.ResolveDirection</c>).
///
/// <para>
/// Sqrt is where a NativeAOT-x64 / IL2CPP-ARM64 divergence shows up first, and two
/// of those sites are not otherwise reachable from a behaviour vector:
/// <c>Vec2.Magnitude</c> and <c>Vec2.Normalized</c> have no caller inside the
/// library, and <c>Vec2.Distance</c> is only used to format the out-of-range error
/// message — whose float the combat vectors deliberately truncate away, because
/// number formatting is not the thing under test. So they are pinned directly here.
/// </para>
/// </summary>
public sealed class Vec2Case
{
    public string name = "";
    public string ax = "";
    public string ay = "";
    public string bx = "";
    public string by = "";
    public string expectedSqrMagnitudeA = "";
    public string expectedMagnitudeA = "";
    public string expectedNormalizedX = "";
    public string expectedNormalizedY = "";
    public string expectedDistanceSq = "";
    public string expectedDistance = "";
}

/// <summary>One <c>ValidationLogic.ValidateInput</c> vector.</summary>
public sealed class ValidationCase
{
    public string name = "";
    public bool dead;
    public string moveX = "";
    public string moveY = "";
    public string attackTargetId = "";
    public bool targetPresent;
    public string attackerX = "";
    public string attackerY = "";
    public string targetX = "";
    public string targetY = "";
    public bool targetDead;
    public long currentTick;
    public long cooldownUntilTick;
    public bool expectedValid;
    public string expectedErrorPrefix = "";
}
