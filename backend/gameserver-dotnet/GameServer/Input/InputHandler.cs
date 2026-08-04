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

    public InputHandler(GameWorld world, ILogger logger, DeathHandler? onDeath = null)
    {
        _world = world;
        _logger = logger;
        _onDeath = onDeath;
    }

    /// <summary>Process input for a user, taking the world write lock.</summary>
    public void ProcessInput(string userId, InputData input)
    {
        _world.Update((get, set) => ProcessInputLocked(get, set, userId, input));
    }

    /// <summary>
    /// Process input inside an existing write lock.
    /// 1. Get entity, skip if null or dead.
    /// 2. Track LastInputTick (monotonic).
    /// 3. Movement: validate via MovementLogic, apply delta.
    /// 4. Attack: get target, validate via CombatLogic, apply damage, handle death.
    /// </summary>
    public void ProcessInputLocked(
        Func<string, EntityState?> get,
        Action<string, EntityState> set,
        string userId,
        InputData input)
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
        if (input.MoveX != 0 || input.MoveY != 0)
        {
            string? moveErr = MovementLogic.ValidateMove(in e, input.MoveX, input.MoveY);
            if (moveErr == null)
            {
                e.Position = MovementLogic.ApplyMove(in e.Position, input.MoveX, input.MoveY);
            }
            else
            {
                _logger.LogDebug("Invalid move from {UserId}: {Error}", userId, moveErr);
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
