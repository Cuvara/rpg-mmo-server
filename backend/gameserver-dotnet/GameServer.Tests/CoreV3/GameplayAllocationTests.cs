using GameServer.Input;
using GameServer.World;
using Shared.GameLogic.Components;
using Shared.GameLogic.Content;

namespace GameServer.Tests.CoreV3;

/// <summary>
/// The Core v3 critical scope — roster, hitbox history, motor steps (held and airborne),
/// status ticking with events, item bookkeeping — allocates nothing per tick in the steady
/// state. Spawns (projectiles, drops) are events and are deliberately not in the measured
/// window.
/// </summary>
public class GameplayAllocationTests
{
    [Fact]
    public void TheSteadyStateCriticalScope_AllocatesNothing()
    {
        using var f = new SimFixture();
        for (int i = 0; i < 20; i++) f.AddPlayer($"p{i}", i * 2f - 20f, 5f);
        for (int i = 0; i < 20; i++) f.AddMob($"m{i}", i * 2f - 20f, -5f);

        // A permanent heal-over-time on every mob: periodic results and (Immune) heal events
        // every 5 ticks, forever, with nobody dying.
        var hot = new StatusDefinition(100, "test_hot", 0, 1, PeriodicSpec.HealOverTime(5, 1),
            Array.Empty<StatModifier>(), CrowdControl.None, 0);
        f.World.UpdateComponents(w =>
        {
            for (int i = 0; i < 20; i++) w.RecordOf(w.Resolve($"m{i}"))!.Statuses!.Apply(hot, 1, null);
        });

        // Every player walks (held direction) and the first one jumps now and then.
        ulong inputTick = 0;
        for (int i = 0; i < 20; i++) f.Input($"p{i}", SimFixture.Move(++inputTick, 0.3f, 0.1f));
        f.Input("p0", SimFixture.Move(++inputTick, 0.3f, 0.1f), new InputExtras(true, 0f, 0, 0f, 0));
        f.Step();

        var state = new Driver(f.Handler);
        for (int i = 0; i < 300; i++) state.Tick(f.World); // warm-up: JIT, buffer growth

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 600; i++) state.Tick(f.World);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(allocated == 0, $"the gameplay critical scope allocated {allocated} B over 600 ticks");
        Assert.True(f.Events.Count > 0 || f.Events.Dropped > 0, "the HoT should have produced events");
    }

    /// <summary>The tick loop's critical scope without the fixture's closures.</summary>
    private sealed class Driver
    {
        private readonly InputHandler _handler;
        private readonly string[] _players = Enumerable.Range(0, 20).Select(i => $"p{i}").ToArray();
        private ulong _tick = 1000;
        private ulong _inputTick = 1000;

        public Driver(InputHandler handler) => _handler = handler;

        public void Tick(EcsWorld world)
        {
            _tick++;
            world.UpdateComponents(this, static (self, w) =>
            {
                self._handler.BeginTick(w, self._tick);
                // Half the players send a packet each tick (motor via the packet path), the
                // other half coast on their held direction; p0 jumps once a second.
                for (int i = (int)(self._tick % 2); i < self._players.Length; i += 2)
                {
                    var extras = new InputExtras(i == 0 && self._tick % 60 == 0, 0f, 0, 0f, 0);
                    self._handler.ProcessInput(
                        w, self._players[i], new InputData(++self._inputTick, 0.3f, (i % 3) * 0.1f, null), in extras, self._tick);
                }

                self._handler.ApplyHeldMovement(w, self._tick);
                self._handler.StepGameplay(w, self._tick);
            });
        }
    }
}
