using System.Collections.Concurrent;

namespace GameServer.Server;

/// <summary>
/// In-memory tracker for consumed JTI (JWT ID) values to prevent join token replay.
/// Each JTI can only be consumed once; after that, TryConsume returns false.
/// Expired entries are cleaned up periodically (every 60 seconds by default).
/// </summary>
public sealed class JtiTracker : IDisposable
{
    /// <summary>Default expiry for consumed JTI entries.</summary>
    public static readonly TimeSpan DefaultExpiry = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, long> _consumed = new();
    private readonly TimeSpan _expiry;
    private readonly Timer _cleanupTimer;

    public JtiTracker() : this(DefaultExpiry) { }

    public JtiTracker(TimeSpan expiry)
    {
        _expiry = expiry;
        // Cleanup every expiry interval
        _cleanupTimer = new Timer(_ => Cleanup(), null, expiry, expiry);
    }

    /// <summary>
    /// Try to consume a JTI. Returns true if the JTI was not previously consumed
    /// (i.e., the token is fresh). Returns false if the JTI was already used.
    /// </summary>
    public bool TryConsume(string jti)
    {
        if (string.IsNullOrEmpty(jti)) return false;

        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return _consumed.TryAdd(jti, now);
    }

    /// <summary>Number of tracked JTI values. Exposed for testing.</summary>
    public int Count => _consumed.Count;

    private void Cleanup()
    {
        long cutoff = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - (long)_expiry.TotalSeconds;
        foreach (var kvp in _consumed)
        {
            if (kvp.Value < cutoff)
            {
                _consumed.TryRemove(kvp);
            }
        }
    }

    public void Dispose()
    {
        _cleanupTimer.Dispose();
    }
}
