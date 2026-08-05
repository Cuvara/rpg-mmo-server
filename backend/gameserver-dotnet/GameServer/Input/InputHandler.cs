using Microsoft.Extensions.Logging;
using Shared.GameLogic.Components;
using Shared.GameLogic.Systems;
using GameServer.World;

namespace GameServer.Input;

/// <summary>
/// Processes player input: movement validation + apply, attack validation + damage.
/// Port of Go input/handler.go. Uses Shared.GameLogic systems for all game logic.
/// </summary>
public sealed class InputHandler
{
    /// <summary>Callback invoked when an entity is killed.</summary>
    public delegate void DeathHandler(EntityState victim, EntityState killer);

    private readonly GameWorld _world;
    private readonly ILogger _logger;
    private readonly DeathHandler? _onDeath;
    private readonly float _deltaTime;
    private readonly MapBounds _bounds;

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
        GameWorld world,
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
    }

    /// <summary>Process input for a user, taking the world write lock.</summary>
    public void ProcessInput(string userId, InputData input, bool applyMovement = true)
    {
        _world.Update((get, set) => ProcessInputLocked(get, set, userId, input, applyMovement));
    }

    /// <summary>
    /// Process input inside an existing write lock.
    /// 1. Get entity, skip if null or dead.
    /// 2. Track LastInputTick (monotonic) — this is the value the client reconciles against.
    /// 3. Movement: integrate direction * speed * dt via <see cref="MovementSystem"/>,
    ///    clamped to the map bounds. Skipped when <paramref name="applyMovement"/> is false.
    /// 4. Attack: get target, validate via CombatLogic, apply damage, handle death.
    /// </summary>
    /// <param name="applyMovement">
    /// False for superseded inputs when several arrived in the same tick: only the newest
    /// input moves the entity, so movement speed cannot be inflated by packet spam.
    /// Attacks are still processed (they have their own cooldown gate).
    /// </param>
    public void ProcessInputLocked(
        Func<string, EntityState?> get,
        Action<string, EntityState> set,
        string userId,
        InputData input,
        bool applyMovement = true)
    {
        var entity = get(userId);
        if (entity == null) return;
        var e = entity.Value;

        // Skip if dead
        if (e.Dead) return;

        // Monotonic tick check
        if (input.Tick <= e.LastInputTick) return;
        e.LastInputTick = input.Tick;

        // --- Movement ---
        // move_x/move_y are a DIRECTION, not a displacement: the server integrates
        // direction * speed * dt itself, so a client cannot travel further by sending
        // more packets or larger vectors.
        if (applyMovement)
        {
            MoveResult moveResult = MovementSystem.TryMove(
                in e, input.MoveX, input.MoveY, _deltaTime, in _bounds, out Vec2 newPosition);

            if (moveResult is MoveResult.Accepted or MoveResult.Clamped)
            {
                e.Position = newPosition;
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
            var target = get(input.AttackTargetId);
            if (target != null)
            {
                var t = target.Value;
                long nowTicks = DateTime.UtcNow.Ticks;

                string? attackErr = CombatLogic.ValidateAttack(in e, in t, nowTicks);
                if (attackErr == null)
                {
                    int damage = CombatLogic.CalculateDamage(in e, in t);
                    t.Hp -= damage;
                    e.CooldownUntilTicks = nowTicks + TimeSpan.FromMilliseconds(GameConstants.AttackCooldownMs).Ticks;

                    if (CombatLogic.HandleDeath(ref t))
                    {
                        _logger.LogInformation("Entity {VictimId} killed by {KillerId}",
                            t.Id, e.Id);
                        _onDeath?.Invoke(t, e);
                    }

                    set(input.AttackTargetId, t);
                }
                else
                {
                    _logger.LogDebug("Invalid attack from {UserId}: {Error}", userId, attackErr);
                }
            }
        }

        set(userId, e);
    }
}
