using GameServer.Server;
using GameServer.Snapshot;
using GameServer.World;
using RpgMmo.Wire.V1;
using Shared.GameLogic.Components;
using Xunit.Abstractions;

namespace GameServer.Tests.Bench;

/// <summary>
/// How many of the bytes on the wire are fields that did not change.
///
/// <para><b>Why measure before proposing.</b> BENCHMARK.md Part XIII established that a
/// client's downlink is <c>AOI population x 24.9 B x SIM_WORLD_HZ</c>, and Part XIV showed
/// that the one lever built so far buys its 47% by halving how often players are sent. The
/// obvious alternative attacks the 24.9 instead: the delta encoder suppresses an entity
/// only when EVERY visible field is unchanged, so a player who merely moved re-sends its
/// health, its maximum health, its speed and its type — none of which have changed since it
/// spawned.</para>
///
/// <para><b>What this does NOT do.</b> It changes nothing and proposes nothing. proto3
/// cannot express "this field is unchanged" — an omitted field is indistinguishable from a
/// zero one — so field-level delta needs a wire change (a presence mask, or optional
/// fields), and that is a decision to take with a number in hand rather than a hunch. This
/// bench is the number.</para>
///
/// <para>Skipped unless <c>BENCH_TICK=1</c>.</para>
/// </summary>
public sealed class UnchangedFieldBytesBench
{
    private const string EnvVar = "BENCH_TICK";
    private const float Radius = GameConstants.DefaultAoiRadius;
    private const int Ticks = 300;

    private readonly ITestOutputHelper _out;

    public UnchangedFieldBytesBench(ITestOutputHelper output) => _out = output;

    private void Line(string s) { _out.WriteLine(s); Console.WriteLine(s); }

    /// <summary>
    /// Exact encoded cost of one field on one entity, measured the way the budget measures
    /// an entity: by encoding a message that carries only it.
    /// </summary>
    private static int FieldBytes(Action<EntitySnapshot> set)
    {
        var e = new EntitySnapshot();
        int empty = e.CalculateSize();
        set(e);
        return e.CalculateSize() - empty;
    }

    [SkippableFact]
    public void MeasureWhatIsResentUnchanged()
    {
        Skip.If(Environment.GetEnvironmentVariable(EnvVar) != "1",
            "Set BENCH_TICK=1 to run the unchanged-field measurement.");

        // ── Part 1: what each field costs, on the values this game actually produces ──
        int hp = FieldBytes(e => e.Hp = 100);
        int maxHp = FieldBytes(e => e.MaxHp = 100);
        int speed = FieldBytes(e => e.Speed = 5f);
        int type = FieldBytes(e => e.Type = EntityType.Player);
        int handle = FieldBytes(e => e.Handle = 137);
        int x = FieldBytes(e => e.X = 12.5f);
        int y = FieldBytes(e => e.Y = -3.25f);
        int facing = FieldBytes(e => e.FacingBrad = 40001);
        int action = FieldBytes(e => e.Action = RpgMmo.Wire.V1.EntityAction.Moving);
        int actionSeq = FieldBytes(e => e.ActionSeq = 9);

        Line("per-field encoded cost on a handle-only mention (bytes)");
        Line($"  identity   handle {handle}");
        Line($"  MOVES      x {x}  y {y}  facing_brad {facing}");
        Line($"  EVENT      hp {hp}  action {action}  action_seq {actionSeq}");
        Line($"  CONSTANT   max_hp {maxHp}  speed {speed}  type {type}");
        Line("");

        // ── Part 2: a real snapshot stream, counting what each emission re-sent ──
        //
        // "Unchanged" is measured against what THIS connection was last told, which is the
        // same comparison the encoder makes -- not against the previous tick, because a
        // connection that was shed last snapshot has not been told anything.
        using var world = new EcsWorld();
        var ids = new List<string>();
        for (int i = 0; i < 120; i++)
        {
            string id = $"p{i:D3}";
            world.AddEntity(TestHelpers.CreatePlayer(id, (i % 12) * 3.5f, (i / 12) * 3.5f, speed: 5f));
            ids.Add(id);
        }

        var state = new SnapshotDeltaState
        {
            MaxSnapshotBytes = SnapshotDeltaState.DefaultMaxSnapshotBytes,
            SelfId = ids[0],
        };
        var buffer = new EntityView[ids.Count + 8];
        var lastSent = new Dictionary<string, (int hp, int maxHp, float speed, string type)>();

        long totalBytes = 0, emissions = 0;
        long unchangedHp = 0, unchangedMaxHp = 0, unchangedSpeed = 0, unchangedType = 0;
        var rng = new Random(7);

        for (ulong t = 1; t <= Ticks; t++)
        {
            world.UpdateComponents(w =>
            {
                foreach (string id in ids)
                {
                    EntityHandle h = w.Resolve(id);
                    if (!h.IsValid) continue;
                    ref var pos = ref w.PositionOf(h);
                    double a = rng.NextDouble() * Math.PI * 2;
                    pos.Value = new Vec2(
                        pos.Value.X + (float)Math.Cos(a) * 0.3f,
                        pos.Value.Y + (float)Math.Sin(a) * 0.3f);
                }
            });

            int n = 0;
            Vec2 anchor = default;
            world.ReadAll(r =>
            {
                r.TryGetSnapshotAnchor(ids[0], out anchor, out _);
                n = r.GetEntitiesInRange(anchor, Radius, buffer.AsSpan());
            });

            SnapshotMessage msg = state.Encode(t, t, buffer.AsSpan(0, n),
                GameConstants.DefaultKeyframeInterval, intern: true, observer: anchor);

            totalBytes += msg.CalculateSize();

            foreach (EntitySnapshot e in msg.Entities)
            {
                emissions++;
                // Match the emission back to the view it came from, so the comparison is
                // against values rather than against the handle-interned id.
                string id = e.Id;
                if (string.IsNullOrEmpty(id))
                {
                    for (int i = 0; i < n; i++)
                    {
                        if (buffer[i].Hp == e.Hp && buffer[i].Position.X == e.X &&
                            buffer[i].Position.Y == e.Y) { id = buffer[i].Id; break; }
                    }
                }
                if (string.IsNullOrEmpty(id)) continue;

                if (lastSent.TryGetValue(id, out var prev))
                {
                    if (prev.hp == e.Hp) unchangedHp++;
                    if (prev.maxHp == e.MaxHp) unchangedMaxHp++;
                    if (prev.speed == e.Speed) unchangedSpeed++;
                    if (prev.type == EntityTypes.NameOf(e)) unchangedType++;
                }
                lastSent[id] = (e.Hp, e.MaxHp, e.Speed, EntityTypes.NameOf(e));
            }
        }

        double perEmission = emissions > 0 ? (double)totalBytes / emissions : 0;
        double wasted = (unchangedHp * hp + unchangedMaxHp * maxHp
                       + unchangedSpeed * speed + unchangedType * type);
        double wastedPerEmission = emissions > 0 ? wasted / emissions : 0;

        Line($"stream: {emissions} entity emissions, {totalBytes} bytes of snapshot payload");
        Line($"  bytes per emission                {perEmission,8:F2}");
        Line($"  of which fields that did NOT change since this client was last told:");
        Line($"    hp        {100.0 * unchangedHp / Math.Max(1, emissions),5:F1}% of emissions x {hp} B");
        Line($"    max_hp    {100.0 * unchangedMaxHp / Math.Max(1, emissions),5:F1}% x {maxHp} B");
        Line($"    speed     {100.0 * unchangedSpeed / Math.Max(1, emissions),5:F1}% x {speed} B");
        Line($"    type      {100.0 * unchangedType / Math.Max(1, emissions),5:F1}% x {type} B");
        Line($"  recoverable per emission          {wastedPerEmission,8:F2}  " +
             $"({100.0 * wastedPerEmission / Math.Max(0.001, perEmission):F1}% of the payload)");
    }
}
