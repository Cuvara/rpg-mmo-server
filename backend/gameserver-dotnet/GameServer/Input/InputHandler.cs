using Microsoft.Extensions.Logging;
using Shared.GameLogic.Components;
using Shared.GameLogic.Systems;
using GameServer.World;
using GameServer.World.Components;

namespace GameServer.Input;

/// <summary>
/// Processes player input: movement validation + apply, attack validation + damage.
/// Port of Go input/handler.go. Uses Shared.GameLogic systems for all game logic.
/// </summary>
public sealed class InputHandler
{
    /// <summary>Callback invoked when an entity is killed.</summary>
    public delegate void DeathHandler(EntityState victim, EntityState killer);

    private readonly EcsWorld _world;
    private readonly ILogger _logger;
    private readonly DeathHandler? _onDeath;
    private readonly float _deltaTime;
    private readonly MapBounds _bounds;
    private readonly int _cooldownTicks;
    private readonly int _maxBankedTicks;

    /// <summary>
    /// Running counters for the attack path, exposed on <c>/status</c>.
    ///
    /// <para><b>Why these exist:</b> a rejected attack is dropped with a Debug-level log on a
    /// server that runs at Information, so from the outside a client attacking out of range is
    /// indistinguishable from a client not attacking at all. That exact ambiguity cost a live
    /// investigation: zero leaderboard kills over minutes, with no way to tell whether attacks
    /// were not arriving, arriving and failing to resolve, or arriving and being rejected.
    /// The counters split those three cases without turning on Debug logging.</para>
    ///
    /// <para><b>Threading:</b> written only from the tick thread (input processing runs inside
    /// the world write lock); read without synchronisation by the status endpoint. Reads are
    /// diagnostics — a torn read on a 64-bit field cannot happen on the 64-bit targets this
    /// server ships for, and staleness by a tick is irrelevant here.</para>
    /// </summary>
    public sealed class AttackTelemetry
    {
        /// <summary>Inputs that carried a non-empty attack target id.</summary>
        public long Received;

        /// <summary>Attacks whose target id did not resolve to a live entity (despawned, bogus, or already reaped).</summary>
        public long Unresolved;

        /// <summary>Attacks refused by <see cref="CombatLogic.ValidateAttack"/> (range, cooldown, dead attacker/target…).</summary>
        public long Rejected;

        /// <summary>Attacks that dealt damage.</summary>
        public long Accepted;

        /// <summary>Accepted attacks that killed their target.</summary>
        public long Kills;

        /// <summary>
        /// The reason string of the most recent rejection, verbatim from
        /// <see cref="CombatLogic.ValidateAttack"/>. Every reason the validator returns
        /// is an interned constant (#249), so keeping the reference allocates nothing —
        /// the old formatted out-of-range message allocated per rejection, and its
        /// distance detail now lives in the Debug-guarded log at the rejection site.
        /// </summary>
        public string? LastRejection;
    }

    /// <summary>Attack-path counters. See <see cref="AttackTelemetry"/> for the contract.</summary>
    public AttackTelemetry Attacks { get; } = new();

    /// <summary>Fixed simulation timestep in seconds used for movement integration.</summary>
    public float DeltaTime => _deltaTime;

    /// <summary>Play area movement is clamped into.</summary>
    public MapBounds Bounds => _bounds;

    /// <param name="world">World the handler mutates.</param>
    /// <param name="logger">Logger for dropped/invalid input.</param>
    /// <param name="onDeath">Optional death callback.</param>
    /// <param name="tickRate">
    /// Simulation tick rate in Hz; the movement timestep is <c>1 / tickRate</c>.
    /// Non-positive values fall back to <see cref="GameConstants.DefaultTickRate"/>.
    /// </param>
    /// <param name="bounds">Play area; defaults to <see cref="MapBounds.Default"/>.</param>
    public InputHandler(
        EcsWorld world,
        ILogger logger,
        DeathHandler? onDeath = null,
        int tickRate = GameConstants.DefaultTickRate,
        MapBounds? bounds = null)
    {
        _world = world;
        _logger = logger;
        _onDeath = onDeath;
        _deltaTime = MovementSystem.DeltaTimeForTickRate(
            tickRate > 0 ? tickRate : GameConstants.DefaultTickRate);
        _bounds = bounds ?? MapBounds.Default;
        _cooldownTicks = GameConstants.AttackCooldownTicks(tickRate);
        _maxBankedTicks = GameConstants.MaxBankedMovementTicks(
            tickRate > 0 ? tickRate : GameConstants.DefaultTickRate);
    }

    /// <summary>
    /// How many base ticks a held direction keeps producing movement for after the last
    /// packet that refreshed it, at this handler's rate.
    ///
    /// <para><b>The name is historical.</b> It used to be the ceiling on how much elapsed
    /// time a single step could bank; no step banks anything now, so the same budget is
    /// spent on the length of the coast instead. Both readings answer the one question
    /// <see cref="GameConstants.MaxBankedMovementMs"/> exists to answer — how long may the
    /// server keep moving a player on information it no longer has — which is why the
    /// constant is unchanged at 250ms. Renaming it means releasing
    /// <c>Shared.GameLogic</c> and bumping the client's manifest and lock, so it is a
    /// separate change.</para>
    /// </summary>
    public int MaxBankedTicks => _maxBankedTicks;

    /// <summary>
    /// The timestep for a movement step landing on <paramref name="baseTick"/>. Always one
    /// tick.
    ///
    /// <para>It is a method rather than the constant it returns because the alternative is
    /// what this replaced, and the difference is the whole movement model. #100 was fixed
    /// by making a step cover the elapsed time since the entity last moved, so the inputs
    /// per-tick coalescing discards from a burst did not take their simulated time with
    /// them. That restored distance and broke smoothness: the recovered time arrived as one
    /// oversized step — 1.36 units measured live where a normal step is 0.083 — which a
    /// player reads as the avatar jumping, and which a correctly predicting client is
    /// snapped back by, because it never took that step itself.</para>
    ///
    /// <para>The time is now recovered by <see cref="ApplyHeldMovement"/> stepping on every
    /// tick of the gap instead, so there is nothing left to bank. Keeping the seam here
    /// keeps the packet path and the held path calling one arithmetic, and keeps the
    /// deliberate-stop case (<paramref name="heldFromTick"/> of 0) documented where it is
    /// decided.</para>
    /// </summary>
    private float StepDeltaTime(ulong baseTick, ulong lastMoveTick, ulong heldFromTick)
    {
        // Nothing held means the entity was STOPPED, not stalled: the last thing the client
        // said was "I am not moving", and a deadzone input clears the hold. Standing still
        // is not lost input, so a player who releases the stick, waits, and presses again is
        // owed nothing for the pause — which is the most common thing a player does, and
        // repaying it was a visible lurch on every restart.
        if (heldFromTick == 0) return _deltaTime;

        if (lastMoveTick == 0 || baseTick <= lastMoveTick) return _deltaTime;

        // ONE TICK, ALWAYS. The step never covers more than the tick it is taken on.
        //
        // This is the invariant the whole movement model now rests on: for any interval,
        // both sides apply exactly one step per tick, so both travel speed x ticks and the
        // distances are equal BY CONSTRUCTION rather than by two independent measurements
        // of elapsed time agreeing. Network jitter shifts WHEN a step happens; it can no
        // longer change HOW MANY there are, which is what prediction and reconciliation are
        // built to absorb.
        //
        // Banking -- multiplying by the elapsed ticks to recover time that went missing --
        // is what this replaces. It restored the right distance and was wrong by every other
        // measure: measured on a live server, a 1.36-unit step where a normal one is 0.083,
        // read by a player as the avatar jumping. There is nothing left to recover, because
        // with a step on every tick nothing is missed in the first place.
        return _deltaTime;
    }

    /// <summary>Attack cooldown length in simulation ticks at this handler's tick rate.</summary>
    public int CooldownTicks => _cooldownTicks;

    /// <summary>
    /// Reusable handle buffer for the held-movement pass. Scratch, not simulation state:
    /// it is refilled from a query before every read, so its contents never survive a tick
    /// in any meaningful sense.
    /// </summary>
    [GameServer.Server.SimulationScratch]
    private EntityHandle[] _playerHandles = Array.Empty<EntityHandle>();

    /// <summary>
    /// Advance every player that has a held direction but received no input on this base
    /// tick — the continuous half of the movement model.
    ///
    /// <para><b>Why this exists.</b> <c>move_x</c>/<c>move_y</c> are a direction, and
    /// <see cref="MovementSystem"/> documents that travel distance must depend only on
    /// wall-clock time and the entity's speed, "never on how many input packets a client
    /// sends". That was only true while the simulation rate and the client's send rate
    /// happened to match. Once the critical group runs at 60Hz and a client sends at 10 or
    /// 15, integrating solely on packet arrival makes speed proportional to send rate. This
    /// pass closes that gap: the newest direction is integrated once per critical tick.</para>
    ///
    /// <para><b>Why it is bounded.</b> A held direction expires
    /// <see cref="MaxBankedTicks"/> base ticks after the last packet that refreshed it — a
    /// silence timeout, 250ms at every rate. A client that stops sending therefore coasts
    /// for at most that long rather than drifting forever, and a client that sends an
    /// explicit deadzone input stops immediately, because that clears the hold rather than
    /// refreshing it.</para>
    ///
    /// <para><b>Why it runs at every rate.</b> It used to be gated off when every group ran
    /// at one rate, on the reasoning that a client sending once per tick needs nothing
    /// held. A client that misses a tick needs it at any rate, and a client whose packets
    /// clump into bursts misses several — which made the single-rate configuration
    /// <c>staging</c> runs the worst case for #100 rather than the safe one.</para>
    /// </summary>
    /// <param name="writer">Open world write scope.</param>
    /// <param name="baseTick">The canonical base tick.</param>
    public void ApplyHeldMovement(WorldWriter writer, ulong baseTick)
    {
        int count = writer.QueryWith<PlayerTag>(_playerHandles);
        if (count > _playerHandles.Length)
        {
            // Headroom: exact-size growth re-queries again at count+1 (#249).
            _playerHandles = new EntityHandle[count + (count >> 2)];
            count = writer.QueryWith<PlayerTag>(_playerHandles);
        }

        int n = Math.Min(count, _playerHandles.Length);
        for (int i = 0; i < n; i++)
        {
            ref readonly EntityHandle handle = ref _playerHandles[i];
            if (!writer.IsAlive(in handle)) continue;

            ref InputCursor cursor = ref writer.InputCursorOf(in handle);
            if (cursor.HeldFromTick == 0) continue;          // nothing held
            if (cursor.HeldFromTick == baseTick) continue;   // already stepped this tick
            // Expiry is a SILENCE TIMEOUT, not a send-rate window.
            //
            // The window used to be one world interval -- the nominal spacing of a client
            // sending at the world rate -- which left no slack at all: measured live, a 15Hz
            // client's packets arrive 4.19 base ticks apart against a 4-tick window, so the
            // average interval already overran it and the player stalled for the remainder
            // of most of them. Any fixed window has that problem, because it is a guess
            // about the client's send rate expressed as a deadline.
            //
            // What the expiry is actually for is the case where a client stops talking
            // without saying so, and that is a question about SILENCE, not about rate. The
            // budget is therefore the same 250ms this handler already treats as the limit of
            // tolerable silence.
            //
            // It does not become a coast after the player lets go: a deadzone input clears
            // the held direction outright, and docs/API.md requires a client to send its
            // vector on every input tick, so releasing produces an explicit zero the tick
            // after. This timeout only covers packets that genuinely stopped arriving.
            //
            // The comparison is > rather than >=, and that boundary is load-bearing. The
            // old banking step covered a gap of _maxBankedTicks ticks ENTIRELY, in one
            // multiplied step; reproducing the same coverage one step at a time means
            // stepping on gaps 1.._maxBankedTicks inclusive. With >= the last of them is
            // dropped, and a client whose packets clump into bursts of four at 15Hz -- a
            // 264ms idle against a 266.7ms budget -- stalls for one tick per burst and
            // travels 5.00 units where 6.00 is owed. That is #100 reappearing at a smaller
            // amplitude, so the bound has to be inclusive.
            if (baseTick - cursor.HeldFromTick > (ulong)_maxBankedTicks) continue;

            if (writer.HealthOf(in handle).Dead) continue;

            float speed = writer.LocomotionOf(in handle).Speed;
            ref Position position = ref writer.PositionOf(in handle);
            var probe = new EntityState
            {
                Position = position.Value,
                Speed = speed,
                Dead = false,
            };

            // The same TryMove the packet path calls, with the same dt — one movement
            // model, one arithmetic, whichever path stepped the entity.
            MoveResult result = MovementSystem.TryMove(
                in probe, cursor.HeldMoveX, cursor.HeldMoveY,
                StepDeltaTime(baseTick, cursor.LastMoveTick, cursor.HeldFromTick), in _bounds,
                out Vec2 newPosition);

            if (result is MoveResult.Accepted or MoveResult.Clamped)
            {
                position.Value = newPosition;
                cursor.LastMoveTick = baseTick;
            }
        }
    }

    /// <summary>Process input for a user, taking the world write lock.</summary>
    /// <param name="currentTick">Current simulation tick (drives cooldowns).</param>
    public void ProcessInput(string userId, InputData input, ulong currentTick = 0, bool applyMovement = true)
    {
        _world.UpdateComponents(writer => ProcessInput(writer, userId, input, currentTick, applyMovement));
    }

    /// <summary>
    /// Process input inside an existing component write scope.
    /// 1. Resolve the entity once, skip if unknown or dead.
    /// 2. Track LastInputTick (monotonic) — this is the value the client reconciles against.
    /// 3. Movement: integrate direction * speed * dt via <see cref="MovementSystem"/>,
    ///    clamped to the map bounds. Skipped when <paramref name="applyMovement"/> is false.
    /// 4. Attack: resolve target, validate via CombatLogic, apply damage, handle death.
    ///
    /// <para><b>What changed from the <c>get</c>/<c>set</c> form.</b> Behaviour is
    /// identical; the access pattern is not. The string id is resolved to an
    /// <see cref="EntityHandle"/> once per entity per call instead of on every read and
    /// every write, and the movement path touches only the three components it actually
    /// uses — <c>Health</c>, <c>InputCursor</c>, <c>Position</c>, plus <c>Locomotion</c>
    /// for speed — instead of composing and re-storing all seven. In particular the
    /// <c>EntityIdRef</c> and <c>EntityKind</c> string references, which cannot change
    /// after spawn, are no longer rewritten (and re-barriered) on every input.</para>
    ///
    /// <para><b>Where the round trip survives.</b> The attack branch still composes a
    /// whole <see cref="EntityState"/> for attacker and target, because
    /// <c>CombatLogic.ValidateAttack</c> / <c>CalculateDamage</c> / <c>HandleDeath</c>
    /// and the death callback are <c>Shared.GameLogic</c> entry points shaped that way
    /// and <c>Shared.GameLogic</c> is deliberately not being changed here. The write
    /// back is already component-level: only the fields combat touches are stored.</para>
    /// </summary>
    /// <param name="applyMovement">
    /// False for superseded inputs when several arrived in the same tick: only the newest
    /// input moves the entity, so movement speed cannot be inflated by packet spam.
    /// Attacks are still processed (they have their own cooldown gate).
    /// </param>
    public void ProcessInput(
        WorldWriter writer,
        string userId,
        InputData input,
        ulong currentTick = 0,
        bool applyMovement = true)
        => ProcessInput(writer, writer.Resolve(userId), userId, input, currentTick, applyMovement);

    /// <summary>
    /// Process a queued input that already carries its entity handle, resolved on the
    /// network thread at ingest. This is what the tick loop calls: the simulation thread
    /// never hashes a user id.
    /// </summary>
    /// <inheritdoc cref="ProcessInput(WorldWriter, string, InputData, ulong, bool)"/>
    public void ProcessInput(
        WorldWriter writer,
        in PendingInput pending,
        ulong currentTick = 0,
        bool applyMovement = true)
        => ProcessInput(writer, pending.Handle, pending.UserId, pending.Input, currentTick, applyMovement);

    private void ProcessInput(
        WorldWriter writer,
        EntityHandle self,
        string userId,
        InputData input,
        ulong currentTick,
        bool applyMovement)
    {
        // Revalidate: a handle resolved at ingest can be stale by the time the tick runs
        // if the entity was destroyed in between. TickLoop rebinds first, so this is the
        // backstop, not the mechanism.
        if (!writer.IsAlive(in self)) return;

        // Skip if dead
        if (writer.HealthOf(self).Dead) return;

        // Monotonic tick check
        ref InputCursor cursor = ref writer.InputCursorOf(self);
        if (input.Tick <= cursor.LastInputTick) return;
        cursor.LastInputTick = input.Tick;

        // --- Movement ---
        // move_x/move_y are a DIRECTION, not a displacement: the server integrates
        // direction * speed * dt itself, so a client cannot travel further by sending
        // more packets or larger vectors.

        // An explicit stop must clear the held state even when this input is not the
        // newest in the batch (applyMovement == false). Per-tick coalescing keeps only
        // the highest client tick for movement, so a stop that is followed by a resume
        // in the same drain batch loses its applyMovement flag. Without this, the stop
        // never reaches the MoveResult.None branch below, HeldFromTick stays non-zero,
        // and StepDeltaTime repays the entire pause as a lurch on the first step.
        //
        // The lurch it prevented is gone with banking, but the clear is not: HeldFromTick
        // is what separates a player who released the stick from one whose packets stopped
        // arriving, and only the second is coasted through. Leaving a stop unrecorded makes
        // every deliberate pause coast for the silence timeout, which is the most common
        // thing a player does.
        //
        // The deadzone check mirrors MovementSystem.ResolveDirection: same constant,
        // same squared-magnitude test, so the two cannot disagree on what counts as a
        // stop.
        float moveMagSq = (float)((float)(input.MoveX * input.MoveX)
            + (float)(input.MoveY * input.MoveY));
        if (moveMagSq <= GameConstants.InputDeadzoneSq)
        {
            cursor.HeldFromTick = 0;
            cursor.LastMoveTick = currentTick;
        }

        if (applyMovement)
        {
            // TryMove is called with the three fields it reads, not re-implemented from
            // its parts: the golden vectors (ADR-10) pin this arithmetic bit-exactly and
            // MovementParityTests pins this call site to TryMove's whole-EntityState
            // form, so the two cannot drift.
            float speed = writer.LocomotionOf(self).Speed;
            ref Position position = ref writer.PositionOf(self);
            var probe = new EntityState
            {
                Position = position.Value,
                Speed = speed,
                Dead = false, // already returned above if dead
            };

            MoveResult moveResult = MovementSystem.TryMove(
                in probe, input.MoveX, input.MoveY,
                StepDeltaTime(currentTick, cursor.LastMoveTick, cursor.HeldFromTick), in _bounds, out Vec2 newPosition);

            if (moveResult is MoveResult.Accepted or MoveResult.Clamped)
            {
                position.Value = newPosition;

                // Hold the direction so the critical group can keep integrating between
                // packets (ApplyHeldMovement). Recorded after a successful step, so a
                // rejected or deadzone input never becomes a held one.
                cursor.HeldMoveX = input.MoveX;
                cursor.HeldMoveY = input.MoveY;
                cursor.HeldFromTick = currentTick;
                cursor.LastMoveTick = currentTick;
            }
            else if (moveResult == MoveResult.None)
            {
                // An explicit stop. The held state was already cleared above (outside
                // the applyMovement guard), so this branch is a no-op for the fields it
                // used to set. It remains as the semantic label: if the deadzone check
                // above and ResolveDirection ever diverge, this is the backstop.
                cursor.HeldFromTick = 0;
                cursor.LastMoveTick = currentTick;
            }
            else if (moveResult == MoveResult.Rejected)
            {
                // Grossly invalid vector (NaN/inf/oversized): log and drop, never throw.
                // Guarded like the attack log below: no allocation with Debug off.
                if (_logger.IsEnabled(LogLevel.Debug))
                {
                    _logger.LogDebug("Dropped invalid move from {UserId}: ({MoveX}, {MoveY})",
                        userId, input.MoveX, input.MoveY);
                }
            }
        }

        // --- Attack ---
        if (!string.IsNullOrEmpty(input.AttackTargetId))
        {
            // IsEnabled guard: the LogDebug extension allocates its params array
            // before the level check, so an unguarded call here allocates once per
            // attack input inside the world write lock even with Debug off. Input
            // processing is a no-allocation hot path; the guard makes the disabled
            // case free.
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Attack input from {UserId} targeting {TargetId}", userId, input.AttackTargetId);
            }
            Attacks.Received++;
            EntityHandle target = writer.Resolve(input.AttackTargetId);
            if (!target.IsValid)
            {
                Attacks.Unresolved++;
            }
            else
            {
                // Composed after movement, so the range check sees this tick's position —
                // the same ordering the get/set form had.
                EntityState attacker = writer.Compose(self);
                EntityState t = writer.Compose(target);

                // Cooldown is measured in simulation ticks, never wall-clock: the tick
                // loop is the only clock the simulation has, so replaying the same input
                // sequence always produces the same combat outcome.
                string? attackErr = CombatLogic.ValidateAttack(in attacker, in t, currentTick);
                if (attackErr == null)
                {
                    Attacks.Accepted++;
                    int damage = CombatLogic.CalculateDamage(in attacker, in t);
                    t.Hp -= damage;

                    ulong cooldownUntil = currentTick + (ulong)_cooldownTicks;
                    writer.CombatOf(self).CooldownUntilTick = cooldownUntil;
                    attacker.CooldownUntilTick = cooldownUntil; // the killer state the callback sees

                    if (CombatLogic.HandleDeath(ref t))
                    {
                        Attacks.Kills++;
                        // Debug, guarded: this fires per kill on the tick thread inside
                        // the world write lock, and at Information the console sink
                        // formats and writes synchronously — a wave of AoE kills wrote
                        // N log lines while every network thread waited on the lock
                        // (#249). Kill counts stay observable via /status attack_kills.
                        if (_logger.IsEnabled(LogLevel.Debug))
                        {
                            _logger.LogDebug("Entity {VictimId} killed by {KillerId}",
                                t.Id, attacker.Id);
                        }
                        _onDeath?.Invoke(t, attacker);
                    }

                    // Self-targeted attack: the get/set form ended with `set(userId, e)`,
                    // which overwrote the target write with the attacker's pre-damage
                    // copy — so damage to self was silently discarded while the cooldown
                    // and the death callback still fired. Component writes have no such
                    // last-writer-wins accident, so the discard is made explicit to keep
                    // the wire output identical. See CHANGELOG: this is a latent bug that
                    // is now visible, not one that is being fixed here.
                    if (!self.SameAs(in target))
                    {
                        ref Health targetHealth = ref writer.HealthOf(target);
                        targetHealth.Hp = t.Hp;
                        targetHealth.Dead = t.Dead;
                    }
                }
                else
                {
                    Attacks.Rejected++;
                    Attacks.LastRejection = attackErr;
                    // Guarded like the attack log above: no allocation with Debug off.
                    // The distance detail the out-of-range message used to carry is
                    // computed HERE, only under the guard: the validator returns an
                    // interned constant so the normal rejection path allocates
                    // nothing (#249). ReferenceEquals suffices — the constant is the
                    // only source of that value.
                    if (_logger.IsEnabled(LogLevel.Debug))
                    {
                        if (ReferenceEquals(attackErr, CombatLogic.OutOfRangeRejection))
                        {
                            _logger.LogDebug(
                                "Invalid attack from {UserId}: {Error} (distance {Distance:F2} exceeds {Range:F2})",
                                userId, attackErr,
                                Vec2.Distance(attacker.Position, t.Position),
                                GameConstants.AttackRange);
                        }
                        else
                        {
                            _logger.LogDebug("Invalid attack from {UserId}: {Error}", userId, attackErr);
                        }
                    }
                }
            }
        }
    }
}
