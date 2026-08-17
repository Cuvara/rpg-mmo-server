using Microsoft.Extensions.Logging;

namespace GameServer.Registry;

/// <summary>Registry timing, mirrored from the Go <c>shared/constants</c>.</summary>
public static class RegistryDefaults
{
    /// <summary>
    /// <c>constants.ServerHeartbeatTTL</c> — a server must heartbeat within this
    /// window or the gateway stops handing clients to it. Must match the Go value:
    /// the gateway reads entries this server writes, so a longer TTL here means a
    /// crashed server keeps black-holing joins for the difference.
    /// </summary>
    public static readonly TimeSpan HeartbeatTtl = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Refresh at a third of the TTL, so two consecutive heartbeats can be lost
    /// (network blip, Redis failover) before the entry actually expires.
    /// </summary>
    public static TimeSpan HeartbeatInterval(TimeSpan ttl) =>
        TimeSpan.FromMilliseconds(Math.Max(1000, ttl.TotalMilliseconds / 3));
}

/// <summary>Everything the registration service needs to describe this server.</summary>
public sealed class RegistrationOptions
{
    public required string ServerId { get; init; }
    public required string MapId { get; init; }

    /// <summary>
    /// Address advertised to clients. The gateway hands this back verbatim in
    /// <c>MsgEnterWorldResp.ServerAddr</c>, so it must be dialable BY THE CLIENT
    /// — inside a container the listen address is typically <c>:9000</c> while
    /// clients must reach the published host port. Set
    /// <c>GAMESERVER_PUBLIC_ADDR</c> when the two differ.
    /// </summary>
    public required string PublicAddr { get; init; }

    public string Transport { get; init; } = "tcp";
    public int Capacity { get; init; } = 100;
    public TimeSpan Ttl { get; init; } = RegistryDefaults.HeartbeatTtl;
}

/// <summary>
/// Keeps this server's registry entry alive for as long as the process is
/// serving, and takes it away when the process stops.
///
/// The design rule that matters: <b>every heartbeat is also a repair</b>. If the
/// entry is missing — Redis was wiped, failed over to an empty replica, or the
/// TTL lapsed while Redis was unreachable — the next heartbeat re-registers it
/// instead of reporting an error. That is what makes a Redis outage self-heal
/// within one heartbeat interval with no human intervention, which was the whole
/// failure mode of the deploy-time <c>register-gameserver.sh</c> this replaces.
///
/// A Redis outage never touches gameplay: every registry call is wrapped, and a
/// failure is logged and retried on the next tick of the loop while the tick loop
/// and connections carry on untouched.
/// </summary>
public sealed class RegistrationService : IAsyncDisposable
{
    private readonly IServerRegistry _registry;
    private readonly RegistrationOptions _options;
    private readonly ILogger _logger;
    private readonly Func<int> _playerCount;
    private readonly TimeSpan _interval;

    private Task? _loop;
    private int _lastPublishedCount = -1;

    /// <summary>
    /// The address actually advertised. Seeded from <see cref="RegistrationOptions.PublicAddr"/>
    /// and replaceable up to <see cref="StartAsync"/> — see <see cref="OverridePublicAddr"/>.
    /// </summary>
    private string _publicAddr;

    /// <summary>Successful (re)registrations. Exposed for tests.</summary>
    internal int RegisterCount => _registerCount;
    private int _registerCount;

    public RegistrationService(
        IServerRegistry registry,
        RegistrationOptions options,
        Func<int> playerCount,
        ILogger logger,
        TimeSpan? interval = null)
    {
        _registry = registry;
        _options = options;
        _playerCount = playerCount;
        _logger = logger;
        _interval = interval ?? RegistryDefaults.HeartbeatInterval(options.Ttl);
        _publicAddr = options.PublicAddr;
    }

    /// <summary>The address this service advertises. Diagnostics, logging and tests.</summary>
    public string PublicAddr => Volatile.Read(ref _publicAddr);

    /// <summary>
    /// Replace the advertised address before registration starts.
    ///
    /// <para>This exists for exactly one caller: under Agones with
    /// <c>portPolicy: Dynamic</c> the dialable address is chosen by the scheduler and is
    /// only readable from the GameServer status once the pod is scheduled — after the host
    /// has been constructed, so it cannot come in through
    /// <see cref="RegistrationOptions"/> (ADR-15 decision 2, option A).</para>
    ///
    /// <para>Only valid before <see cref="StartAsync"/>. Changing the address afterwards
    /// would leave the entry already in Redis pointing at the old value until the next
    /// heartbeat repaired it — a window in which the gateway hands clients an address
    /// nothing is listening on — so it throws rather than half-applying.</para>
    /// </summary>
    /// <param name="addr">Non-empty <c>host:port</c> to advertise instead.</param>
    /// <exception cref="InvalidOperationException">Registration has already started.</exception>
    public void OverridePublicAddr(string addr)
    {
        if (string.IsNullOrWhiteSpace(addr))
            throw new ArgumentException("advertised address must not be empty", nameof(addr));
        if (_loop != null)
            throw new InvalidOperationException(
                $"{nameof(OverridePublicAddr)} must be called before {nameof(StartAsync)}");

        Volatile.Write(ref _publicAddr, addr);
    }

    private ServerInfo BuildInfo() => new(
        _options.ServerId,
        _options.MapId,
        PublicAddr,
        _options.Transport,
        _options.Capacity,
        _playerCount());

    /// <summary>
    /// Register immediately, then start the heartbeat loop.
    /// A failed initial registration is NOT fatal — the loop retries, so a server
    /// that boots while Redis is down still joins the registry once Redis returns.
    /// </summary>
    public async Task StartAsync(CancellationToken ct)
    {
        if (!await TryRegisterAsync(ct))
        {
            _logger.LogWarning(
                "Initial registry registration failed for {ServerId}; the heartbeat loop will keep retrying every {Interval}s",
                _options.ServerId, _interval.TotalSeconds);
        }

        _loop = Task.Run(() => RunAsync(ct), CancellationToken.None);
    }

    /// <summary>Heartbeat loop: refresh the TTL, re-register whenever the entry is gone.</summary>
    private async Task RunAsync(CancellationToken ct)
    {
        _logger.LogInformation(
            "Registry heartbeat started for {ServerId} (every {Interval}s, ttl {Ttl}s)",
            _options.ServerId, _interval.TotalSeconds, _options.Ttl.TotalSeconds);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_interval, ct);
            }
            catch (OperationCanceledException) { break; }

            try
            {
                bool alive = await _registry.HeartbeatAsync(_options.ServerId, ct);
                if (!alive)
                {
                    // The entry vanished. This is the Redis-wipe / expiry path and it
                    // is expected to happen in production, so re-register rather than
                    // just complaining about it.
                    _logger.LogWarning(
                        "Registry entry for {ServerId} is gone (Redis wipe, failover or TTL lapse) — re-registering",
                        _options.ServerId);
                    await TryRegisterAsync(ct);
                }
                else
                {
                    await PublishPlayerCountIfChangedAsync(ct);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                // Redis down. Keep serving; the next iteration retries.
                _logger.LogWarning(ex,
                    "Registry heartbeat failed for {ServerId}; retrying in {Interval}s",
                    _options.ServerId, _interval.TotalSeconds);
            }
        }

        _logger.LogInformation("Registry heartbeat stopped for {ServerId}", _options.ServerId);
    }

    /// <summary>Register, swallowing transport errors. Returns whether it succeeded.</summary>
    private async Task<bool> TryRegisterAsync(CancellationToken ct)
    {
        try
        {
            await _registry.RegisterAsync(BuildInfo(), ct);
            Interlocked.Increment(ref _registerCount);
            _lastPublishedCount = _playerCount();
            return true;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Registry registration failed for {ServerId}", _options.ServerId);
            return false;
        }
    }

    /// <summary>
    /// Push the player count when it moved. Called from the heartbeat loop and
    /// directly on join/leave, so the gateway's capacity view is fresh without
    /// putting a Redis round trip on the connection path.
    /// </summary>
    public async Task PublishPlayerCountIfChangedAsync(CancellationToken ct)
    {
        int count = _playerCount();
        if (count == _lastPublishedCount) return;

        try
        {
            if (await _registry.UpdatePlayerCountAsync(_options.ServerId, count, ct))
            {
                _lastPublishedCount = count;
            }
            // A false result means the entry expired; the next heartbeat re-registers
            // it with the current count, so there is nothing to do here.
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Registry player-count update failed for {ServerId}", _options.ServerId);
        }
    }

    /// <summary>
    /// Fire-and-forget player-count publish, for the connection hot path: joining
    /// or leaving must never block on Redis.
    /// </summary>
    public void NotifyPlayerCountChanged()
    {
        _ = Task.Run(async () =>
        {
            try { await PublishPlayerCountIfChangedAsync(CancellationToken.None); }
            catch { /* best effort; the heartbeat loop reconciles */ }
        });
    }

    /// <summary>
    /// Remove the entry so the gateway stops handing clients to a server that is
    /// going away, instead of waiting out the TTL.
    /// </summary>
    public async Task DeregisterAsync()
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _registry.DeregisterAsync(_options.ServerId, _options.MapId, cts.Token);
        }
        catch (Exception ex)
        {
            // Not fatal: the TTL removes the entry within seconds anyway.
            _logger.LogWarning(ex, "Registry deregistration failed for {ServerId}", _options.ServerId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_loop != null)
        {
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch { /* loop already cancelled or stuck; do not block shutdown */ }
        }
        await _registry.DisposeAsync();
    }
}
