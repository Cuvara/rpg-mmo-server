using System.Collections.Concurrent;

namespace GameServer.Persistence;

/// <summary>
/// Which character each in-world player is (ADR-31), and what was loaded for it.
/// </summary>
/// <param name="CharacterId">The character (the join token's <c>cid</c>, or the user id for the default character).</param>
/// <param name="UserId">The owning account; also the entity id in the world.</param>
/// <param name="Loaded">
/// The row as loaded at join (or a fresh one for a new character). Carries the fields the
/// world does not hold — XP, and in a dungeon the ORIGIN map and position, which a dungeon
/// save must not overwrite (ADR-26 decision 5).
/// </param>
public sealed record CharacterBinding(string CharacterId, string UserId, CharacterState Loaded)
{
    /// <summary>
    /// True for the account's default character (no <c>cid</c> claim: the character id is
    /// the user id). Only that character is mirrored into the legacy <c>player_states</c>
    /// row, which holds one row per account (expand/contract, ADR-31 decision 2).
    /// </summary>
    public bool IsDefaultCharacter => string.Equals(CharacterId, UserId, System.StringComparison.Ordinal);
}

/// <summary>
/// Thread-safe map from user id to the <see cref="CharacterBinding"/> of the character that
/// user has in this world. Written by the join path, read by the save paths; a rejoin
/// overwrites. Never touched by the tick thread.
/// </summary>
public sealed class CharacterSessions
{
    private readonly ConcurrentDictionary<string, CharacterBinding> _byUser = new(System.StringComparer.Ordinal);

    /// <summary>Record (or replace) the character bound to a user.</summary>
    public void Bind(CharacterBinding binding) => _byUser[binding.UserId] = binding;

    /// <summary>The character bound to <paramref name="userId"/>, if any.</summary>
    public bool TryGet(string userId, out CharacterBinding binding)
    {
        if (_byUser.TryGetValue(userId, out var b))
        {
            binding = b;
            return true;
        }

        binding = null!;
        return false;
    }

    /// <summary>Forget a user's binding (their entity left the world for good).</summary>
    public void Unbind(string userId) => _byUser.TryRemove(userId, out _);

    /// <summary>Number of bindings (diagnostics, tests).</summary>
    public int Count => _byUser.Count;
}
