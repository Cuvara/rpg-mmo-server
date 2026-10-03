using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using GameServer.Net;
using GameServer.Net.Transport;
using GameServer.Observability;
using GameServer.Persistence;
using GameServer.Server;
using GameServer.Tests.Infrastructure;
using GameServer.World;
using GameServer.World.Components;
using Microsoft.Extensions.Logging.Abstractions;
using RpgMmo.Wire.V1;
using Shared.GameLogic.Components;
using Shared.GameLogic.World;

namespace GameServer.Tests.CoreV3;

/// <summary>
/// ADR-31 on the game server: a join loads the character named by the join token's cid
/// through <see cref="ICharacterStore"/> and spawns it at its saved x/y/z; the save paths
/// write it back (with height and level) through <see cref="AsyncSaver"/>.
/// </summary>
public class CharacterPersistenceTests
{
    private const string MapId = "map_chars";
    private const string ServerId = "gs-chars";
    private const string Secret = "character-test-secret";

    // ── Saver, no network ────────────────────────────────────────────────────

    private static (EcsWorld World, CharacterSessions Sessions) WorldWithCharacter(
        string userId, string characterId, Vec3 at, int hp, int level, CharacterState? loaded = null)
    {
        var world = new EcsWorld();
        world.Gameplay.Configure(content: GameServer.Content.ContentLoader.LoadFromBytes(
            Encoding.UTF8.GetBytes(SimFixture.ContentJson), "test-content.json").Database);
        world.AddEntity(new EntityState
        {
            Id = userId, Type = "player", Position = new Vec2(at.X, at.Y), Hp = hp, MaxHp = 100, Speed = 5f,
        });
        world.WithEntity(userId, (at.Z, level), static (w, in h, s) =>
        {
            w.PositionOf(in h).Z = s.Item1;
            w.Gameplay.TrySetBaseStat(w.RecordOf(in h)!, w.Gameplay.StatIdFor("level"), s.Item2);
        });

        var sessions = new CharacterSessions();
        sessions.Bind(new CharacterBinding(characterId, userId,
            loaded ?? new CharacterState(characterId, userId, MapId, 0, 0, 0, 0, 100, 100, 1, Xp: 1234)));
        return (world, sessions);
    }

    private static AsyncSaver Saver(IPlayerStore legacy, EcsWorld world, ICharacterStore chars, CharacterSessions sessions,
        string mapId = MapId, PlayerSaveScope scope = PlayerSaveScope.Full) =>
        new(legacy, world, mapId, TimeSpan.FromHours(1), NullLogger.Instance, null, scope, 0.5, chars, sessions);

    [Fact]
    public async Task ACharacter_RoundTripsThroughTheSaver_WithHeightLevelAndXp()
    {
        var legacy = new MemoryPlayerStore();
        var store = new MemoryCharacterStore(legacy);
        var (world, sessions) = WorldWithCharacter("u1", "char-1", new Vec3(3f, 4f, 2.5f), hp: 61, level: 7);
        using var _ = world;

        await Saver(legacy, world, store, sessions).SaveAllAsync();

        CharacterState? row = await store.LoadCharacterAsync("char-1", "u1", default);
        Assert.NotNull(row);
        Assert.Equal(MapId, row!.MapId);
        Assert.Equal(3f, row.X);
        Assert.Equal(4f, row.Y);
        Assert.Equal(2.5f, row.Z);
        Assert.Equal(61, row.Hp);
        Assert.Equal(7, row.Level);
        Assert.Equal(1234, row.Xp); // carried from the loaded row: the world holds no XP yet

        // Not the account's default character: the legacy per-account row is left alone.
        Assert.Null(await legacy.LoadPlayerAsync("u1", default));

        // And the spawn policy hands the same place back on this map.
        var spawn = PlayerSpawn.Resolve(row, MapId, MapBounds.Default, new Vec3(0, 0, 0));
        Assert.True(spawn.PositionRestored);
        Assert.Equal(new Vec3(3f, 4f, 2.5f), spawn.Position);
        Assert.Equal(7, spawn.Level);
    }

    [Fact]
    public async Task TheDefaultCharacter_IsAlsoMirroredIntoTheLegacyRow()
    {
        var legacy = new MemoryPlayerStore();
        var store = new MemoryCharacterStore(legacy);
        var (world, sessions) = WorldWithCharacter("u2", "u2", new Vec3(8f, -1f, 0f), hp: 50, level: 2);
        using var _ = world;

        Assert.True(await Saver(legacy, world, store, sessions).SavePlayerAsync("u2"));

        PlayerState? old = await legacy.LoadPlayerAsync("u2", default);
        Assert.NotNull(old);
        Assert.Equal(8f, old!.X);
        Assert.Equal(50, old.Hp);
        Assert.Equal(MapId, old.MapId);
        Assert.Equal(0f, (await store.LoadCharacterAsync("u2", "u2", default))!.Z);
    }

    [Fact]
    public async Task ADungeonSave_KeepsTheOriginMapAndPosition()
    {
        var legacy = new MemoryPlayerStore();
        var store = new MemoryCharacterStore(legacy);
        var origin = new CharacterState("char-d", "u3", "map_origin", 42f, -17f, 3f, 1f, 100, 100, 4, 10);
        await store.SaveCharacterAsync(origin, default);
        var (world, sessions) = WorldWithCharacter("u3", "char-d", new Vec3(-400f, 380f, 9f), hp: 60, level: 5, loaded: origin);
        using var _ = world;

        await Saver(legacy, world, store, sessions, mapId: "dungeon#1", scope: PlayerSaveScope.StatsOnly).SaveAllAsync();

        CharacterState row = (await store.LoadCharacterAsync("char-d", "u3", default))!;
        Assert.Equal("map_origin", row.MapId);
        Assert.Equal(42f, row.X);
        Assert.Equal(3f, row.Z);
        Assert.Equal(60, row.Hp);
        Assert.Equal(5, row.Level);
    }

    [Fact]
    public void AnotherMapsRow_SpawnsAtTheDefaultSpawn_KeepingHpAndLevel()
    {
        var row = new CharacterState("c", "u", "elsewhere", 5f, 5f, 5f, 0f, 40, 100, 9, 0);
        var spawn = PlayerSpawn.Resolve(row, MapId, MapBounds.Default, new Vec3(1f, 2f, 0.5f));

        Assert.False(spawn.PositionRestored);
        Assert.Equal("elsewhere", spawn.DiscardedMapId);
        Assert.Equal(new Vec3(1f, 2f, 0.5f), spawn.Position);
        Assert.Equal(40, spawn.Hp);
        Assert.Equal(9, spawn.Level);
    }

    // ── Through a live server ────────────────────────────────────────────────

    private sealed class Host : IAsyncDisposable
    {
        public required GameServerHost Server { get; init; }
        public required int Port { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public required Task RunTask { get; init; }
        public required GameMetrics Metrics { get; init; }

        public async ValueTask DisposeAsync()
        {
            Cts.Cancel();
            await Server.ShutdownAsync();
            try { await RunTask; } catch (OperationCanceledException) { }
            Cts.Dispose();
            Metrics.Dispose();
        }
    }

    /// <summary>A map with a "default" spawn and a crate whose top is 1.5 units up at (4..6, 5..7).</summary>
    private static MapGeometry Arena() => new(
        MapBounds.FromSize(100, 100), null,
        new[] { new StaticBox(4f, 5f, 0f, 6f, 7f, 1.5f) },
        new[] { new SpawnPoint("default", new Vec3(0f, -10f, 0f)) },
        null);

    private static async Task<Host> StartAsync(ICharacterStore store, IPlayerStore legacy)
    {
        var metrics = new GameMetrics(MapId, $"test.{Guid.NewGuid():N}");
        var server = new GameServerHost(new ServerOptions
        {
            ServerAddr = ":0",
            ServerId = ServerId,
            MapId = MapId,
            Mode = "map",
            Transport = TransportKind.Tcp,
            TickRate = 20,
            Capacity = 10,
            JwtSecret = Secret,
            JoinTokenSecret = Secret,
            HoldTtl = TimeSpan.FromSeconds(10),
            SaveInterval = TimeSpan.FromHours(1),
            PlayerStore = legacy,
            CharacterStore = store,
            Geometry = Arena(),
            Metrics = metrics,
            LoggerFactory = NullLoggerFactory.Instance,
        });
        var cts = new CancellationTokenSource();
        var (runTask, port) = await TestPorts.StartServerAsync(server, cts.Token);
        return new Host { Server = server, Port = port, Cts = cts, RunTask = runTask, Metrics = metrics };
    }

    private static string Token(string userId, string? cid)
    {
        static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        string cidClaim = cid == null ? "" : $",\"cid\":\"{cid}\"";
        string payload = $"{{\"sub\":\"{userId}\",\"sid\":\"{ServerId}\",\"jti\":\"{Guid.NewGuid():N}\"{cidClaim},\"iat\":{now},\"exp\":{now + 3600}}}";
        string header = B64(Encoding.UTF8.GetBytes("""{"alg":"HS256","typ":"JWT"}"""));
        string body = B64(Encoding.UTF8.GetBytes(payload));
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Secret));
        return $"{header}.{body}.{B64(hmac.ComputeHash(Encoding.ASCII.GetBytes($"{header}.{body}")))}";
    }

    private static async Task<(TcpClient Client, JoinTokenResponse Response)> JoinAsync(Host h, string userId, string? cid)
    {
        var client = new TcpClient();
        for (int attempt = 0; ; attempt++)
        {
            try { await client.ConnectAsync(IPAddress.Loopback, h.Port); break; }
            catch (SocketException) when (attempt < 60) { await Task.Delay(50); }
        }

        var stream = client.GetStream();
        var join = WireProtocol.NewEnvelope(MsgType.JoinToken,
            new JoinTokenRequest { Token = Token(userId, cid), ProtocolVersion = WireProtocol.ProtocolVersion },
            WireEncoding.Json);
        await stream.WriteAsync(WireProtocol.Encode(join));
        await stream.FlushAsync();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var env = await WireProtocol.DecodeAsync(stream, cts.Token);
        Assert.NotNull(env);
        return (client, WireProtocol.GetPayload<JoinTokenResponse>(env!));
    }

    private static Vec3 PositionOf(GameServerHost server, string userId)
    {
        Vec3 at = default;
        server.World.UpdateComponents(w =>
        {
            ref Position p = ref w.PositionOf(w.Resolve(userId));
            at = new Vec3(p.Value.X, p.Value.Y, p.Z);
        });
        return at;
    }

    [Fact]
    public async Task AJoinWithACid_LoadsThatCharacter_AtItsSavedPositionAndHeight_AndTheLeaveSaveWritesItBack()
    {
        var legacy = new MemoryPlayerStore();
        var store = new MemoryCharacterStore(legacy);
        await store.SaveCharacterAsync(new CharacterState("char-7", "u1", MapId, 5f, 6f, 1.5f, 0f, 70, 100, 3, 0), default);

        await using var h = await StartAsync(store, legacy);
        var (client, resp) = await JoinAsync(h, "u1", "char-7");
        using var _ = client;
        Assert.True(resp.Ok, resp.Error);

        Assert.True(h.Server.Characters.TryGet("u1", out var binding));
        Assert.Equal("char-7", binding.CharacterId);
        EntityState e = h.Server.World.GetEntity("u1")!.Value;
        Assert.Equal(70, e.Hp);
        Vec3 at = PositionOf(h.Server, "u1");
        Assert.Equal(5f, at.X);
        Assert.Equal(6f, at.Y);
        Assert.Equal(1.5f, at.Z); // standing on the crate it was saved on

        // Move and save as the leave/transfer paths do.
        h.Server.World.UpdateComponents(w =>
        {
            EntityHandle hd = w.Resolve("u1");
            w.PositionOf(hd).Value = new Vec2(-3f, 2f);
            w.PositionOf(hd).Z = 0f;
        });
        Assert.True(await h.Server.SavePlayerNowAsync("u1"));

        CharacterState row = (await store.LoadCharacterAsync("char-7", "u1", default))!;
        Assert.Equal(-3f, row.X);
        Assert.Equal(2f, row.Y);
        Assert.Equal(0f, row.Z);
        Assert.Equal(3, row.Level);
    }

    [Fact]
    public async Task AJoinWithSomeoneElsesCharacter_IsRefused()
    {
        var legacy = new MemoryPlayerStore();
        var store = new MemoryCharacterStore(legacy);
        await store.SaveCharacterAsync(new CharacterState("char-9", "owner", MapId, 0, 0, 0, 0, 100, 100, 1, 0), default);

        await using var h = await StartAsync(store, legacy);
        var (client, resp) = await JoinAsync(h, "thief", "char-9");
        using var _ = client;

        Assert.False(resp.Ok);
        Assert.Contains("character", resp.Error);
        Assert.Null(h.Server.World.GetEntity("thief"));
    }

    [Fact]
    public async Task ANewCharacter_SpawnsAtTheMapsDefaultSpawnPoint()
    {
        var legacy = new MemoryPlayerStore();
        await using var h = await StartAsync(new MemoryCharacterStore(legacy), legacy);
        var (client, resp) = await JoinAsync(h, "fresh", cid: null);
        using var _ = client;
        Assert.True(resp.Ok, resp.Error);

        Vec3 at = PositionOf(h.Server, "fresh");
        Assert.Equal(0f, at.X);
        Assert.Equal(-10f, at.Y);
        Assert.True(h.Server.Characters.TryGet("fresh", out var binding));
        Assert.True(binding.IsDefaultCharacter);
    }
}
