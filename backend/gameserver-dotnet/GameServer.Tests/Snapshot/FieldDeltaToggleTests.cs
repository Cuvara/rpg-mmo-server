using Google.Protobuf;
using System.Linq;
using GameServer.Snapshot;
using RpgMmo.Wire.V1;
using Shared.GameLogic.Components;
using Shared.GameLogic.Systems;

namespace GameServer.Tests.Snapshot;

/// <summary>
/// <c>GAMESERVER_FIELD_DELTA</c> — the kill switch that makes field-level delta measurable
/// against itself.
/// </summary>
/// <remarks>
/// <para>
/// Field-delta is otherwise enabled per connection purely by protocol version match, and the
/// server refuses a client whose version does not match exactly. So the control arm requires
/// a client that cannot connect, and every bandwidth figure for the feature was a
/// before/after across two builds (#381). This switch produces the arm that was missing.
/// </para>
/// <para>
/// <b>The safety property matters more than the saving.</b> Turning the switch off must
/// change the bytes and nothing else: the fields a partial entity does carry must agree with
/// what the full encoder would have sent for the same entity on the same tick. If they can
/// disagree, the toggle is not a control — it is a second encoder, and comparing the two
/// measures the difference between them rather than the cost of the feature.
/// </para>
/// </remarks>
public class FieldDeltaToggleTests
{
    private const int NoKeyframes = 100_000;

    private static List<EntityState> Moving(int count, float phase)
    {
        var list = new List<EntityState>(count);
        for (int i = 0; i < count; i++)
        {
            float angle = i * 0.37f;
            float r = 5f + (i % 20);
            list.Add(TestHelpers.CreatePlayer(
                $"e{i:D3}",
                (float)(r * Math.Cos(angle)) + phase,
                (float)(r * Math.Sin(angle)) + phase,
                hp: 100 - (i % 7)));
        }

        return list;
    }

    [Fact]
    public void Off_SendsEveryFieldAndCostsMore_On_SendsAMaskAndCostsLess()
    {
        var on = new SnapshotDeltaState { FieldDelta = true };
        var off = new SnapshotDeltaState { FieldDelta = false };

        long onBytes = 0, offBytes = 0;
        int maskedEntities = 0;

        // Tick 1 introduces everything (a full entity either way); the deltas are ticks 2+.
        for (int tick = 1; tick <= 40; tick++)
        {
            List<EntityState> world = Moving(30, tick * 0.5f);

            SnapshotMessage a = on.Encode((ulong)tick, 0, world, NoKeyframes, intern: true);
            SnapshotMessage b = off.Encode((ulong)tick, 0, world, NoKeyframes, intern: true);

            onBytes += a.ToByteArray().Length;
            offBytes += b.ToByteArray().Length;

            if (tick == 1) continue;

            // The OFF arm must never claim a partial update.
            Assert.All(b.Entities, e => Assert.Equal(0u, e.ChangedFields));

            foreach (EntitySnapshot e in a.Entities.Where(x => x.ChangedFields != 0))
            {
                maskedEntities++;

                EntitySnapshot full = b.Entities.Single(x => x.Handle == e.Handle);

                // Every field the mask claims is present must equal what the full encoder
                // sent. A mask that says "x changed" while carrying a stale x is the one
                // failure that would make the two arms incomparable.
                if ((e.ChangedFields & SnapshotFieldBits.X) != 0)
                    Assert.Equal(full.X, e.X);
                if ((e.ChangedFields & SnapshotFieldBits.Y) != 0)
                    Assert.Equal(full.Y, e.Y);
                if ((e.ChangedFields & SnapshotFieldBits.Hp) != 0)
                    Assert.Equal(full.Hp, e.Hp);
                if ((e.ChangedFields & SnapshotFieldBits.ActionSeq) != 0)
                    Assert.Equal(full.ActionSeq, e.ActionSeq);
            }
        }

        Assert.True(maskedEntities > 0,
            "the ON arm never emitted a partial entity, so this fixture compared two " +
            "identical encoders and the assertion below would pass on a broken toggle");

        Assert.True(onBytes < offBytes,
            $"field-delta ON wrote {onBytes} bytes against OFF's {offBytes} — the switch " +
            "changed nothing, which is what a toggle wired to the wrong flag looks like");
    }

    /// <summary>
    /// The switch is a kill switch, not a policy: with it off the encoder must behave
    /// exactly as a pre-v2 server does, which is what makes the control arm honest.
    /// </summary>
    [Fact]
    public void Off_IsByteIdenticalToAnEncoderThatNeverHeardOfFieldDelta()
    {
        // FieldDelta defaults to... whatever the property default is. Pin the comparison to
        // an explicit false rather than relying on that, so a change to the default cannot
        // turn this into a test of nothing.
        var explicitlyOff = new SnapshotDeltaState { FieldDelta = false };
        var neverSet = new SnapshotDeltaState();
        neverSet.FieldDelta = false;

        for (int tick = 1; tick <= 25; tick++)
        {
            List<EntityState> world = Moving(20, tick * 0.5f);

            byte[] a = explicitlyOff.Encode((ulong)tick, 0, world, NoKeyframes, intern: true)
                .ToByteArray();
            byte[] b = neverSet.Encode((ulong)tick, 0, world, NoKeyframes, intern: true)
                .ToByteArray();

            Assert.True(a.SequenceEqual(b), $"tick {tick}: the two OFF arms diverged");
        }
    }
}
