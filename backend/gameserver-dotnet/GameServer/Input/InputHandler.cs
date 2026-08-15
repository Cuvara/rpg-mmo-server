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
    private float StepDeltaTime(ulong baseTick, ulong lastMoveTick)
    {
        if (lastMoveTick == 0 || baseTick <= lastMoveTick) return _deltaTime;

        ulong elapsed = baseTick - lastMoveTick;
        if (elapsed > (ulong)_maxBankedTicks) elapsed = (ulong)_maxBankedTicks;
        return _deltaTime * elapsed;
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
        if (holdTicks <= 1) return;

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
            if (baseTick - cursor.HeldFromTick >= (ulong)holdTicks) continue; // expired

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
                StepDeltaTime(baseTick, cursor.LastMoveTick), in _bounds,
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
                StepDeltaTime(currentTick, cursor.LastMoveTick), in _bounds, out Vec2 newPosition);

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
                // An explicit stop. Clearing the hold is the difference between "the client
                // released the stick" and "the client went quiet": the first must stop the
                // entity now, the second is what the hold window is for.
                cursor.HeldFromTick = 0;
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
