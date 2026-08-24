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
    /// Ceiling on how many base ticks of elapsed time one movement step may cover, at this
    /// handler's rate. See <see cref="GameConstants.MaxBankedMovementMs"/>.
    /// </summary>
    public int MaxBankedTicks => _maxBankedTicks;

    /// <summary>
    /// The timestep for a movement step landing on <paramref name="baseTick"/> for an
    /// entity that last moved on <paramref name="lastMoveTick"/>.
    ///
    /// <para>This is the whole of #100's fix. A step covers the time since the entity last
    /// moved rather than one fixed tick, so the three inputs that per-tick coalescing
    /// discards from a burst no longer take their simulated time with them. It does not
    /// weaken what coalescing defends: a client sending every tick always has
    /// <c>lastMoveTick == baseTick - 1</c> and so earns exactly one tick per tick, and a
    /// client that was silent is bounded by <see cref="MaxBankedTicks"/>.</para>
    ///
    /// <para>A first-ever move (<paramref name="lastMoveTick"/> of 0) is one tick, not the
    /// whole age of the server.</para>
    /// </summary>
    private float StepDeltaTime(ulong baseTick, ref InputCursor cursor)
    {
        ulong lastMoveTick = cursor.LastMoveTick;
        ulong heldFromTick = cursor.HeldFromTick;

        // Nothing held means the entity was STOPPED, not stalled: the last thing the client
        // said was "I am not moving", and a deadzone input clears the hold. Standing still
        // is not lost input, so a player who releases the stick, waits, and presses again is
        // owed nothing for the pause — which is the most common thing a player does, and
        // repaying it was a visible lurch on every restart.
        if (heldFromTick == 0) return _deltaTime;

        if (lastMoveTick == 0 || baseTick <= lastMoveTick) return _deltaTime;

        // Elapsed base ticks are owed too -- a stall is lost time exactly as a coalesced
        // packet is -- but they are ACCRUED rather than paid here. Paying them in one step
        // is what produced a 1.36-unit jump against a normal 0.083 step (#104).
        ulong elapsed = baseTick - lastMoveTick;
        if (elapsed > 1)
        {
            cursor.OwedTicks += elapsed - 1;
            if (cursor.OwedTicks > _maxBankedTicks) cursor.OwedTicks = _maxBankedTicks;
        }

        float pay = 0f;
        if (cursor.OwedTicks > 0f)
        {
            pay = MathF.Min(cursor.OwedTicks, GameConstants.MaxCatchUpFraction);
            cursor.OwedTicks -= pay;
        }

        return _deltaTime * (1f + pay);
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
    /// <paramref name="holdTicks"/> base ticks after it was accepted — one world interval.
    /// A client that stops sending therefore coasts for at most that long (66ms at the
    /// default 60/15) rather than drifting forever, and a client that sends an explicit
    /// deadzone input stops immediately, because that clears the hold rather than
    /// refreshing it.</para>
    ///
    /// <para><b>Why it is a no-op on a single-rate server.</b> With one rate,
    /// <paramref name="holdTicks"/> is 1, and a held direction is by definition at least one
    /// tick old on any tick where it would be applied here — so the condition can never be
    /// true and behaviour is exactly the pre-multi-rate model, packet for packet. That is
    /// what lets the byte-identity and characterization tests stand unchanged.</para>
    /// </summary>
    /// <param name="writer">Open world write scope.</param>
    /// <param name="baseTick">The canonical base tick.</param>
    /// <param name="holdTicks">
    /// How many base ticks a direction stays valid for. One world interval; 1 disables the
    /// pass entirely.
    /// </param>
    public void ApplyHeldMovement(WorldWriter writer, ulong baseTick, int holdTicks)
    {
        // Single-rate servers (holdTicks <= 1) have no window to hold across, but they can
        // still owe time: a burst of packets coalesces there exactly as it does anywhere
        // else. The pass therefore runs, and the per-entity guard below lets only entities
        // with a debt through -- so "one packet, one step" is unchanged for a client that
        // is not owed anything, which is every client that is keeping up.
        bool multiRate = holdTicks > 1;

        int count = writer.QueryWith<PlayerTag>(_playerHandles);
        if (count > _playerHandles.Length)
        {
            _playerHandles = new EntityHandle[count];
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
            // The nominal window expires, but a DEBT keeps the entity moving until it is
            // square. That is the whole point of carrying the debt rather than discharging
            // it: the time is repaid by continuing to move at a bounded catch-up rate, and
            // moving is the only thing that can repay it -- a stalled entity takes no steps,
            // so a debt that only drained on steps would never drain at all.
            //
            // This does not become a coast after the player lets go. A deadzone input clears
            // the held direction outright, and the client sends its vector unconditionally
            // every input tick, so releasing produces an explicit zero the next tick and
            // nothing here runs. What it does cover is the case the window was too small
            // for: measured live, a 15Hz client's packets arrive 4.19 base ticks apart
            // against a 4-tick window, so the average interval already overruns it.
            bool windowExpired = !multiRate
                                 || baseTick - cursor.HeldFromTick >= (ulong)holdTicks;
            if (windowExpired && cursor.OwedTicks <= 0f) continue;

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
                StepDeltaTime(baseTick, ref cursor), in _bounds,
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

        // Relate the two clocks, then charge for the steps that went missing.
        //
        // The client tick says WHICH of the client's steps this is; currentTick says when it
        // landed. Their ratio is how long one of this client's steps lasts in base ticks --
        // nothing else on the server knows it, because the input rate is the client's choice
        // and never crosses the wire.
        //
        // A jump in the client tick is the only witness to coalesced input. Four packets
        // arriving in one base tick become one movement step, and the three that lost are
        // invisible to arrival timing -- they landed at the same instant as the one that
        // won. Their client ticks say plainly that three steps happened.
        if (cursor.LastInputTick != 0 && cursor.LastInputBaseTick != 0
            && currentTick > cursor.LastInputBaseTick)
        {
            ulong clientSteps = input.Tick - cursor.LastInputTick;
            if (clientSteps > 0)
            {
                float perStep = (float)(currentTick - cursor.LastInputBaseTick) / clientSteps;
                cursor.TicksPerInput = cursor.TicksPerInput <= 0f
                    ? perStep
                    : (cursor.TicksPerInput * 0.8f) + (perStep * 0.2f);

                // Only the steps BEYOND this one are owed: this input is about to be paid
                // for in the normal way.
                if (clientSteps > 1 && cursor.TicksPerInput > 0f)
                {
                    cursor.OwedTicks += (clientSteps - 1) * cursor.TicksPerInput;
                    if (cursor.OwedTicks > _maxBankedTicks) cursor.OwedTicks = _maxBankedTicks;
                }
            }
        }

        cursor.LastInputTick = input.Tick;
        cursor.LastInputBaseTick = currentTick;

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
        // In multi-rate mode the lurch is masked: ApplyHeldMovement integrates between
        // packets and keeps LastMoveTick current, so the elapsed gap the resume sees is
        // at most one tick. In single-rate mode ApplyHeldMovement is a no-op
        // (holdTicks <= 1), so the full pause is visible and capped at MaxBankedTicks.
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
                StepDeltaTime(currentTick, ref cursor), in _bounds, out Vec2 newPosition);

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
                _logger.LogDebug("Dropped invalid move from {UserId}: ({MoveX}, {MoveY})",
                    userId, input.MoveX, input.MoveY);
            }
        }

        // --- Attack ---
        if (!string.IsNullOrEmpty(input.AttackTargetId))
        {
            _logger.LogDebug("Attack input from {UserId} targeting {TargetId}", userId, input.AttackTargetId);
            EntityHandle target = writer.Resolve(input.AttackTargetId);
            if (target.IsValid)
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
                    int damage = CombatLogic.CalculateDamage(in attacker, in t);
                    t.Hp -= damage;

                    ulong cooldownUntil = currentTick + (ulong)_cooldownTicks;
                    writer.CombatOf(self).CooldownUntilTick = cooldownUntil;
                    attacker.CooldownUntilTick = cooldownUntil; // the killer state the callback sees

                    if (CombatLogic.HandleDeath(ref t))
                    {
                        _logger.LogInformation("Entity {VictimId} killed by {KillerId}",
                            t.Id, attacker.Id);
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
                    _logger.LogDebug("Invalid attack from {UserId}: {Error}", userId, attackErr);
                }
            }
        }
    }
}
