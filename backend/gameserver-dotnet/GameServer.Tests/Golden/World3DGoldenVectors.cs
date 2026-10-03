using System.Globalization;
using System.Text;
using Shared.GameLogic.World;

namespace GameServer.Tests.Golden;

/// <summary>
/// Fixture schema and shared replay drivers for the ADR-28/ADR-29 golden vectors:
/// <c>motor3d.json</c>, <c>projectile.json</c> and <c>hitbox_rewind.json</c>.
///
/// <para>
/// Same rules as <see cref="GoldenVectors"/>: flat objects with public fields only (the
/// subset Unity's <c>JsonUtility</c> binds), floats as IEEE-754 hex bit patterns. Map
/// geometry is carried <i>inside</i> each case as flat strings — boxes as
/// <c>"minX,minY,minZ,maxX,maxY,maxZ;..."</c> and heightfield samples as
/// <c>"h0,h1,..."</c>, every number a hex float — so the Unity replay needs no knowledge of
/// named scenarios in this file to rebuild the exact same world.
/// </para>
///
/// <para>
/// The <c>Run*</c> drivers are what both the generator and the replay execute. A Unity
/// replay must implement the same loop: in particular a motor case applies the same input
/// for <c>ticks</c> steps with <c>jump</c> held only for the first <c>jumpTicks</c> of them,
/// and a projectile step case steps until the projectile dies or <c>ticks</c> run out.
/// </para>
/// </summary>
internal static class World3DGolden
{
    // ── Geometry codec ───────────────────────────────────────────────────────

    public static string EncodeBoxes(params StaticBox[] boxes)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < boxes.Length; i++)
        {
            if (i > 0) sb.Append(';');
            var b = boxes[i];
            sb.Append(GoldenVectors.Hex(b.MinX)).Append(',')
              .Append(GoldenVectors.Hex(b.MinY)).Append(',')
              .Append(GoldenVectors.Hex(b.MinZ)).Append(',')
              .Append(GoldenVectors.Hex(b.MaxX)).Append(',')
              .Append(GoldenVectors.Hex(b.MaxY)).Append(',')
              .Append(GoldenVectors.Hex(b.MaxZ));
        }

        return sb.ToString();
    }

    public static StaticBox[] DecodeBoxes(string s)
    {
        if (string.IsNullOrEmpty(s)) return Array.Empty<StaticBox>();
        string[] parts = s.Split(';');
        var boxes = new StaticBox[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            string[] v = parts[i].Split(',');
            if (v.Length != 6) throw new InvalidDataException("box needs 6 values: " + parts[i]);
            boxes[i] = new StaticBox(
                GoldenVectors.Float(v[0]), GoldenVectors.Float(v[1]), GoldenVectors.Float(v[2]),
                GoldenVectors.Float(v[3]), GoldenVectors.Float(v[4]), GoldenVectors.Float(v[5]));
        }

        return boxes;
    }

    public static string EncodeFloats(float[] values) =>
        string.Join(",", values.Select(GoldenVectors.Hex));

    public static float[] DecodeFloats(string s) =>
        string.IsNullOrEmpty(s) ? Array.Empty<float>() : s.Split(',').Select(GoldenVectors.Float).ToArray();

    /// <summary>Rebuild the map a case describes.</summary>
    public static MapGeometry Geometry(IGeometryCase c)
    {
        var bounds = new MapBounds(
            GoldenVectors.Float(c.MinX), GoldenVectors.Float(c.MinY),
            GoldenVectors.Float(c.MaxX), GoldenVectors.Float(c.MaxY));

        HeightField? hf = null;
        if (!string.IsNullOrEmpty(c.HfHeights))
        {
            hf = new HeightField(
                GoldenVectors.Float(c.HfOriginX), GoldenVectors.Float(c.HfOriginY), GoldenVectors.Float(c.HfCell),
                c.HfCols, c.HfRows, DecodeFloats(c.HfHeights));
        }

        return new MapGeometry(bounds, hf, DecodeBoxes(c.Boxes), null, null);
    }

    /// <summary>Write a map into a case's geometry fields.</summary>
    public static void SetGeometry(IGeometryCase c, MapBounds bounds, StaticBox[] boxes, HeightField? hf)
    {
        c.MinX = GoldenVectors.Hex(bounds.MinX);
        c.MinY = GoldenVectors.Hex(bounds.MinY);
        c.MaxX = GoldenVectors.Hex(bounds.MaxX);
        c.MaxY = GoldenVectors.Hex(bounds.MaxY);
        c.Boxes = EncodeBoxes(boxes);
        if (hf != null)
        {
            c.HfOriginX = GoldenVectors.Hex(hf.OriginX);
            c.HfOriginY = GoldenVectors.Hex(hf.OriginY);
            c.HfCell = GoldenVectors.Hex(hf.CellSize);
            c.HfCols = hf.Columns;
            c.HfRows = hf.Rows;
            c.HfHeights = EncodeFloats(hf.Heights.ToArray());
        }
        else
        {
            c.HfOriginX = c.HfOriginY = c.HfCell = c.HfHeights = "";
            c.HfCols = c.HfRows = 0;
        }
    }

    // ── Drivers ──────────────────────────────────────────────────────────────

    public static MotorParams Params(MotorCase c) => new MotorParams
    {
        Gravity = GoldenVectors.Float(c.gravity),
        JumpSpeed = GoldenVectors.Float(c.jumpSpeed),
        StepHeight = GoldenVectors.Float(c.stepHeight),
        MaxSlopeRise = GoldenVectors.Float(c.maxSlopeRise),
        CapsuleRadius = GoldenVectors.Float(c.capsuleRadius),
        CapsuleHeight = GoldenVectors.Float(c.capsuleHeight),
    };

    public static MoveResult RunMotor(MotorCase c, out MotorState state)
    {
        MapGeometry geo = Geometry(c);
        MotorParams p = Params(c);
        state = new MotorState(
            new Vec3(GoldenVectors.Float(c.posX), GoldenVectors.Float(c.posY), GoldenVectors.Float(c.posZ)),
            GoldenVectors.Float(c.velZ), c.grounded);
        float mx = GoldenVectors.Float(c.moveX);
        float my = GoldenVectors.Float(c.moveY);
        float speed = GoldenVectors.Float(c.speed);
        float dt = GoldenVectors.Float(c.dt);

        MoveResult result = MoveResult.None;
        for (int i = 0; i < c.ticks; i++)
        {
            result = CharacterMotor.Step(state, mx, my, i < c.jumpTicks, speed, dt, geo, p, out MotorState next);
            state = next;
        }

        return result;
    }

    /// <summary>Steps a projectile until it dies or <c>ticks</c> run out; returns ticks executed.</summary>
    public static int RunProjectile(ProjectileCase c, out ProjectileState state, out bool alive, out bool hitWorld)
    {
        MapGeometry geo = Geometry(c);
        state = new ProjectileState(
            new Vec3(GoldenVectors.Float(c.ax), GoldenVectors.Float(c.ay), GoldenVectors.Float(c.az)),
            new Vec3(GoldenVectors.Float(c.bx), GoldenVectors.Float(c.by), GoldenVectors.Float(c.bz)),
            GoldenVectors.Float(c.radius), GoldenVectors.Float(c.range));
        float dt = GoldenVectors.Float(c.dt);
        alive = true;
        hitWorld = false;
        int executed = 0;
        while (alive && executed < c.ticks)
        {
            alive = ProjectileLogic.Step(state, dt, geo, out ProjectileState next, out hitWorld);
            state = next;
            executed++;
        }

        return executed;
    }
}

/// <summary>Geometry fields every 3D case carries (see <see cref="World3DGolden"/>).</summary>
internal interface IGeometryCase
{
    string MinX { get; set; }
    string MinY { get; set; }
    string MaxX { get; set; }
    string MaxY { get; set; }
    string Boxes { get; set; }
    string HfOriginX { get; set; }
    string HfOriginY { get; set; }
    string HfCell { get; set; }
    int HfCols { get; set; }
    int HfRows { get; set; }
    string HfHeights { get; set; }
}

// ── Fixture schema ───────────────────────────────────────────────────────────
// Public fields, flat. The interface is implemented explicitly over the fields so the
// serializer (which writes typeof(T).GetFields()) sees only the fields.

/// <summary>
/// One <c>CharacterMotor.Step</c> scenario: the same input applied for <c>ticks</c>
/// steps, jump held for the first <c>jumpTicks</c>.
/// </summary>
public sealed class MotorCase : IGeometryCase
{
    public string name = "";
    // geometry
    public string minX = "", minY = "", maxX = "", maxY = "";
    public string boxes = "";
    public string hfOriginX = "", hfOriginY = "", hfCell = "";
    public int hfCols, hfRows;
    public string hfHeights = "";
    // params
    public string gravity = "", jumpSpeed = "", stepHeight = "", maxSlopeRise = "", capsuleRadius = "", capsuleHeight = "";
    // initial state
    public string posX = "", posY = "", posZ = "", velZ = "";
    public bool grounded;
    // input
    public string moveX = "", moveY = "", speed = "", dt = "";
    public int ticks;
    public int jumpTicks;
    // expected (after the last tick)
    public string expectedResult = "";
    public string expectedX = "", expectedY = "", expectedZ = "", expectedVelZ = "";
    public bool expectedGrounded;

    string IGeometryCase.MinX { get => minX; set => minX = value; }
    string IGeometryCase.MinY { get => minY; set => minY = value; }
    string IGeometryCase.MaxX { get => maxX; set => maxX = value; }
    string IGeometryCase.MaxY { get => maxY; set => maxY = value; }
    string IGeometryCase.Boxes { get => boxes; set => boxes = value; }
    string IGeometryCase.HfOriginX { get => hfOriginX; set => hfOriginX = value; }
    string IGeometryCase.HfOriginY { get => hfOriginY; set => hfOriginY = value; }
    string IGeometryCase.HfCell { get => hfCell; set => hfCell = value; }
    int IGeometryCase.HfCols { get => hfCols; set => hfCols = value; }
    int IGeometryCase.HfRows { get => hfRows; set => hfRows = value; }
    string IGeometryCase.HfHeights { get => hfHeights; set => hfHeights = value; }
}

/// <summary>
/// One projectile vector. <c>kind</c> selects the operation:
/// <list type="bullet">
/// <item><c>spawn</c> — <c>ProjectileLogic.Spawn(a, b, speed, radius, range)</c>; a = origin, b = aim point.</item>
/// <item><c>step</c> — a projectile at a with velocity b stepped up to <c>ticks</c> times.</item>
/// <item><c>segment_capsule</c> — <c>SegmentCapsuleHit(a, b, radius, c, capsuleRadius, capsuleHeight)</c>.</item>
/// <item><c>sweep</c> — <c>MapGeometry.SweepSphere(a, b, radius)</c>.</item>
/// </list>
/// Fields a kind does not use are empty / zero.
/// </summary>
public sealed class ProjectileCase : IGeometryCase
{
    public string name = "";
    public string kind = "";
    // geometry (step, sweep)
    public string minX = "", minY = "", maxX = "", maxY = "";
    public string boxes = "";
    public string hfOriginX = "", hfOriginY = "", hfCell = "";
    public int hfCols, hfRows;
    public string hfHeights = "";
    // operands
    public string ax = "", ay = "", az = "";
    public string bx = "", by = "", bz = "";
    public string cx = "", cy = "", cz = "";
    public string speed = "", radius = "", range = "", dt = "";
    public string capsuleRadius = "", capsuleHeight = "";
    public int ticks;
    // expected
    public bool expectedOk;
    public bool expectedHitWorld;
    public int expectedTicks;
    public string expectedX = "", expectedY = "", expectedZ = "";
    public string expectedVx = "", expectedVy = "", expectedVz = "";
    public string expectedRemaining = "";
    public string expectedT = "";

    string IGeometryCase.MinX { get => minX; set => minX = value; }
    string IGeometryCase.MinY { get => minY; set => minY = value; }
    string IGeometryCase.MaxX { get => maxX; set => maxX = value; }
    string IGeometryCase.MaxY { get => maxY; set => maxY = value; }
    string IGeometryCase.Boxes { get => boxes; set => boxes = value; }
    string IGeometryCase.HfOriginX { get => hfOriginX; set => hfOriginX = value; }
    string IGeometryCase.HfOriginY { get => hfOriginY; set => hfOriginY = value; }
    string IGeometryCase.HfCell { get => hfCell; set => hfCell = value; }
    int IGeometryCase.HfCols { get => hfCols; set => hfCols = value; }
    int IGeometryCase.HfRows { get => hfRows; set => hfRows = value; }
    string IGeometryCase.HfHeights { get => hfHeights; set => hfHeights = value; }
}

/// <summary>
/// One lag-compensation vector. <c>kind</c> selects the operation:
/// <list type="bullet">
/// <item><c>clamp</c> — <c>HitboxHistory.ClampRewind(currentTick, renderTick, renderAlpha, tickRate, maxRewindMs)</c>.</item>
/// <item><c>interp</c> — record a at <c>renderTick</c> and (when <c>hasNext</c>) b at
/// <c>renderTick + 1</c> in a fresh history, then <c>TryGetAt(renderTick, renderAlpha)</c>.</item>
/// </list>
/// </summary>
public sealed class HitboxCase
{
    public string name = "";
    public string kind = "";
    public long currentTick;
    public long renderTick;
    public string renderAlpha = "";
    public int tickRate;
    public int maxRewindMs;
    public string ax = "", ay = "", az = "";
    public string bx = "", by = "", bz = "";
    public bool hasNext;
    public long expectedTick;
    public string expectedAlpha = "";
    public bool expectedFound;
    public string expectedX = "", expectedY = "", expectedZ = "";
}
