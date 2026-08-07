using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using GameServer.Net;
using GameServer.Persistence;
using GameServer.Net.Transport;
using GameServer.Server;
using Shared.GameLogic.Components;

namespace GameServer.Tests.Persistence;

/// <summary>
/// Cross-map placement, proved against a real PostgreSQL rather than a fake store.
///
/// <para><c>player_states</c> holds exactly one row per player, overwritten by whichever
/// server currently hosts them. The join path used to restore that row's coordinates
/// unconditionally, so a player last saved on one map who joined another was dropped at
/// the *other* map's coordinates. These tests drive the real join handshake over TCP and
/// read the position back off the wire, because the placement decision is only observable
/// where a client would observe it.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public class MapIdReloadIntegrationTests
{
    private const string JwtSecret = "integration-test-secret";
    private const string HomeMap = "map_home";
    private const string OtherMap = "map_other";

    private readonly PostgresFixture _pg;

    public MapIdReloadIntegrationTests(PostgresFixture pg) => _pg = pg;

    /// <summary>
    /// Same map: the position is exactly why the row is persisted at all, so it must
    /// come back unchanged.
    /// </summary>
    [SkippableFact]
    public async Task SameMapRejoin_RestoresExactPersistedPosition()
    {
        _pg.SkipUnlessAvailable(nameof(SameMapRejoin_RestoresExactPersistedPosition));

        await using var store = await _pg.ConnectStoreAsync();
        await store.MigrateAsync();

        string userId = NewUserId();
        var seeded = new PlayerState(userId, X: 137.5f, Y: -42.25f, Hp: 63, MaxHp: 111, MapId: HomeMap);
        await store.SavePlayerAsync(seeded, default);

        var spawn = await JoinAndReadOwnEntityAsync(store, userId, serverMapId: HomeMap);

        Assert.Equal(137.5f, spawn.X, 3);
        Assert.Equal(-42.25f, spawn.Y, 3);
        Assert.Equal(63, spawn.Hp);
        Assert.Equal(111, spawn.MaxHp);
    }

    /// <summary>
    /// Different map: the stale coordinates are meaningless here and must be dropped for
    /// the spawn point. HP belongs to the character and carries across.
    /// </summary>
    [SkippableFact]
    public async Task CrossMapJoin_IgnoresStaleCoordinatesButKeepsHp()
    {
        _pg.SkipUnlessAvailable(nameof(CrossMapJoin_IgnoresStaleCoordinatesButKeepsHp));

        await using var store = await _pg.ConnectStoreAsync();
        await store.MigrateAsync();

        string userId = NewUserId();
        var seeded = new PlayerState(userId, X: 137.5f, Y: -42.25f, Hp: 63, MaxHp: 111, MapId: OtherMap);
        await store.SavePlayerAsync(seeded, default);

        // Join a server hosting a DIFFERENT map from the one the row was saved on.
        var spawn = await JoinAndReadOwnEntityAsync(store, userId, serverMapId: HomeMap);

        // The regression: this used to be (137.5, -42.25).
        Assert.Equal(0f, spawn.X, 3);
        Assert.Equal(0f, spawn.Y, 3);

        // Map-independent state survives the crossing untouched.
        Assert.Equal(63, spawn.Hp);
        Assert.Equal(111, spawn.MaxHp);
    }

    /// <summary>
    /// A row whose <c>map_id</c> is empty has unknown provenance — the column defaults to
    /// the empty string — so it must not be treated as belonging to the joining map.
    /// </summary>
    [SkippableFact]
    public async Task UnattributedRow_SpawnsAtSpawnPoint()
    {
        _pg.SkipUnlessAvailable(nameof(UnattributedRow_SpawnsAtSpawnPoint));

        await using var store = await _pg.ConnectStoreAsync();
        await store.MigrateAsync();

        string userId = NewUserId();
        await store.SavePlayerAsync(
            new PlayerState(userId, X: 200f, Y: 200f, Hp: 90, MaxHp: 100, MapId: ""), default);

        var spawn = await JoinAndReadOwnEntityAsync(store, userId, serverMapId: HomeMap);

        Assert.Equal(0f, spawn.X, 3);
        Assert.Equal(0f, spawn.Y, 3);
        Assert.Equal(90, spawn.Hp);
    }

    /// <summary>
    /// After a cross-map join the persisted row must describe the map the player is
    /// actually on — otherwise the next join would discard their position all over again
    /// and the row would never converge.
    /// </summary>
    [SkippableFact]
    public async Task AfterCrossMapJoin_PersistedRowReflectsTheMapActuallyJoined()
    {
        _pg.SkipUnlessAvailable(nameof(AfterCrossMapJoin_PersistedRowReflectsTheMapActuallyJoined));

        await using var store = await _pg.ConnectStoreAsync();
        await store.MigrateAsync();

        string userId = NewUserId();
        await store.SavePlayerAsync(
            new PlayerState(userId, X: 137.5f, Y: -42.25f, Hp: 63, MaxHp: 111, MapId: OtherMap), default);

        // Join HomeMap, walk east, and let the saver flush.
        await JoinAndReadOwnEntityAsync(store, userId, serverMapId: HomeMap, moveTicks: 30);

        var persisted = await store.LoadPlayerAsync(userId, default);
        Assert.NotNull(persisted);
        Assert.Equal(HomeMap, persisted!.MapId);

        // Position is the one walked from the spawn point on THIS map, not the seeded one.
        Assert.True(persisted.X > 0f, $"expected X > 0 after walking east, got {persisted.X}");
        Assert.True(persisted.X < 137.5f,
            $"expected X below the stale seed (137.5), got {persisted.X} — the old coordinates leaked in");
        Assert.Equal(0f, persisted.Y, 3);

        // A second join on HomeMap now restores rather than discards.
        var rejoin = await JoinAndReadOwnEntityAsync(store, userId, serverMapId: HomeMap);
        Assert.Equal(persisted.X, rejoin.X, 3);
    }

    // ── Harness ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Boot a game server on <paramref name="serverMapId"/> backed by <paramref name="store"/>,
    /// complete the join handshake as <paramref name="userId"/>, optionally walk east, and
    /// return the player's own entity as the server reports it.
    /// </summary>
    private static async Task<EntitySnapshotMsg> JoinAndReadOwnEntityAsync(
        PostgresPlayerStore store, string userId, string serverMapId, int moveTicks = 0)
    {
        const string serverId = "gs-mapid-itest";
        int port = FreeTcpPort();

        var options = new ServerOptions
        {
            ServerAddr = $":{port}",
            ServerId = serverId,
            MapId = serverMapId,
            Mode = "map",
            // Pinned rather than left to the default: the placement policy runs in the
            // join handler, above the transport, so TCP is chosen here only because this
            // harness dials with a raw TcpClient. Transport coverage lives in the KCP
            // interop tests; a future change of the default must not silently break this.
            Transport = TransportKind.Tcp,
            TickRate = 20,
            Capacity = 4,
            JwtSecret = JwtSecret,
            SaveInterval = TimeSpan.FromMilliseconds(300),
            PlayerStore = store,
            LoggerFactory = NullLoggerFactory.Instance
        };

        var server = new GameServerHost(options);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var runTask = server.RunAsync($":{port}", cts.Token);

        try
        {
            using var client = new TcpClient();
            await ConnectWithRetryAsync(client, port);
            await using var stream = client.GetStream();

            await SendAsync(stream, MsgType.JoinToken,
                new JoinTokenRequest { Token = TestHelpers.CreateTestJwt(userId, serverId, JwtSecret) });

            var joinEnv = await WireProtocol.DecodeAsync(stream, cts.Token);
            Assert.NotNull(joinEnv);
            var joinResp = WireProtocol.GetPayload<JoinTokenResponse>(joinEnv!);
            Assert.True(joinResp.Ok, joinResp.Error);

            // The FIRST snapshot carrying our entity is the spawn state — read it before
            // sending any input, or movement would mask where the server placed us.
            var spawn = await ReadOwnEntityAsync(stream, userId, cts.Token);

            for (ulong tick = 1; tick <= (ulong)moveTicks; tick++)
            {
                await SendAsync(stream, MsgType.Input,
                    new InputMessage { Tick = tick, MoveX = 1f, MoveY = 0f });
                await Task.Delay(25, cts.Token);
            }
            if (moveTicks > 0)
            {
                await Task.Delay(700, cts.Token); // let the async saver flush at least once
            }

            return spawn;
        }
        finally
        {
            cts.Cancel();
            await server.ShutdownAsync(); // triggers the final save
            try { await runTask; } catch (OperationCanceledException) { /* expected */ }
        }
    }

    /// <summary>
    /// Read snapshots until the player's own entity appears. Snapshots are delta-encoded,
    /// so the entity arrives in the join keyframe; later deltas may omit it entirely.
    /// </summary>
    private static async Task<EntitySnapshotMsg> ReadOwnEntityAsync(
        NetworkStream stream, string userId, CancellationToken ct)
    {
        for (int frames = 0; frames < 200; frames++)
        {
            var env = await WireProtocol.DecodeAsync(stream, ct);
            if (env == null) break;
            if ((MsgType)env.Type != MsgType.Snapshot) continue;

            var msg = JsonSerializer.Deserialize(
                env.Payload.GetRawText(), WireJsonContext.Default.SnapshotMessage)!;

            foreach (var e in msg.Entities)
            {
                if (e.Id == userId) return e;
            }
        }
        throw new InvalidOperationException($"player {userId} never appeared in a snapshot");
    }

    private static string NewUserId() => $"mapid-{Guid.NewGuid():N}"[..20];

    private static async Task SendAsync(Stream stream, MsgType type, JoinTokenRequest payload)
        => await WriteFrameAsync(stream, type,
            JsonSerializer.SerializeToUtf8Bytes(payload, WireJsonContext.Default.JoinTokenRequest));

    private static async Task SendAsync(Stream stream, MsgType type, InputMessage payload)
        => await WriteFrameAsync(stream, type,
            JsonSerializer.SerializeToUtf8Bytes(payload, WireJsonContext.Default.InputMessage));

    private static async Task WriteFrameAsync(Stream stream, MsgType type, byte[] payloadBytes)
    {
        using var doc = JsonDocument.Parse(payloadBytes);
        var env = new Envelope { Type = (byte)type, Payload = doc.RootElement.Clone() };
        byte[] frame = WireProtocol.Encode(env);
        await stream.WriteAsync(frame);
        await stream.FlushAsync();
    }

    private static async Task ConnectWithRetryAsync(TcpClient client, int port)
    {
        for (int attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                await client.ConnectAsync(IPAddress.Loopback, port);
                return;
            }
            catch (SocketException)
            {
                await Task.Delay(100); // listener not up yet
            }
        }
        throw new TimeoutException($"game server never started listening on :{port}");
    }

    private static int FreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
