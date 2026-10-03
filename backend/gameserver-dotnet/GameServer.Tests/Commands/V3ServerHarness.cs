using System.Net;
using System.Net.Sockets;
using System.Text;
using Google.Protobuf;
using GameServer.Content;
using GameServer.Net;
using GameServer.Net.Transport;
using GameServer.Observability;
using GameServer.Persistence;
using GameServer.Server;
using GameServer.Tests.CoreV3;
using GameServer.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using RpgMmo.Wire.V1;
using Envelope = GameServer.Net.Envelope;

namespace GameServer.Tests.Commands;

/// <summary>
/// A live <see cref="GameServerHost"/> with protocol 3 content (stats, statuses, an equippable
/// item) and an in-memory character store, for the command channel and the v2/v3 handshake and
/// snapshot tests that must go through a real socket.
/// </summary>
internal sealed class V3ServerHarness : IAsyncDisposable
{
    public const string Secret = "v3-harness-secret";
    public const string ServerId = "gs-v3";
    public const string MapId = "map_v3";

    public required GameServerHost Server { get; init; }
    public required GameMetrics Metrics { get; init; }
    public required MemoryCharacterStore Store { get; init; }
    public required int Port { get; init; }
    public required CancellationTokenSource Cts { get; init; }
    public required Task RunTask { get; init; }

    /// <summary>SimFixture's content plus an equippable weapon.</summary>
    public static LoadedContent LoadContent()
    {
        string json = SimFixture.ContentJson.Replace(
            "\"items\": [",
            "\"items\": [ { \"id\": \"iron_sword\", \"name\": \"Iron Sword\", \"slot\": \"weapon\", \"rarity\": \"common\", " +
            "\"stackMax\": 1, \"attack\": 5, \"defense\": 0, \"levelRequirement\": 0 },");
        return ContentLoader.LoadFromBytes(Encoding.UTF8.GetBytes(json), "v3-harness.json");
    }

    public static async Task<V3ServerHarness> StartAsync(uint minProtocolVersion = 0)
    {
        var metrics = new GameMetrics(MapId, $"test.{Guid.NewGuid():N}");
        LoadedContent content = LoadContent();
        var store = new MemoryCharacterStore();
        var options = new ServerOptions
        {
            ServerAddr = ":0",
            ServerId = ServerId,
            MapId = MapId,
            Mode = "map",
            Transport = TransportKind.Tcp,
            TickRate = 20,
            MinProtocolVersion = minProtocolVersion,
            JwtSecret = Secret,
            JoinTokenSecret = Secret,
            HoldTtl = TimeSpan.FromMilliseconds(200),
            SaveInterval = TimeSpan.FromHours(1),
            Content = content.Database,
            Loot = content.Loot,
            CharacterStore = store,
            Metrics = metrics,
            LoggerFactory = NullLoggerFactory.Instance,
        };

        var server = new GameServerHost(options);
        var cts = new CancellationTokenSource();
        var (runTask, port) = await TestPorts.StartServerAsync(server, cts.Token);
        return new V3ServerHarness
        {
            Server = server, Metrics = metrics, Store = store, Port = port, Cts = cts, RunTask = runTask,
        };
    }

    public sealed class Client : IDisposable
    {
        public required TcpClient Tcp { get; init; }
        public required NetworkStream Stream { get; init; }
        public required WireEncoding Encoding { get; init; }
        public required JoinTokenResponse Join { get; init; }
        private uint _seq;

        public uint NextSeq() => ++_seq;

        public Task SendAsync(Envelope env) => Stream.WriteAsync(WireProtocol.Encode(env)).AsTask();

        public Task SendCommandAsync(uint seq, uint opcode, byte[] payload) =>
            SendAsync(WireProtocol.NewEnvelope(MsgType.Command,
                new CommandRequest { Seq = seq, Opcode = opcode, Payload = ByteString.CopyFrom(payload) }, Encoding));

        /// <summary>Read until a frame of <paramref name="type"/> arrives; other frames are skipped (and collected).</summary>
        public async Task<Envelope> ReadUntilAsync(MsgType type, TimeSpan? timeout = null, List<Envelope>? skipped = null)
        {
            using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
            while (true)
            {
                Envelope? env = await WireProtocol.DecodeAsync(Stream, cts.Token);
                Assert.NotNull(env);
                if (env!.Type == (uint)type) return env;
                skipped?.Add(env);
            }
        }

        /// <summary>
        /// Send one command and return its result plus every ServerPush that arrived before it.
        /// </summary>
        public async Task<(CommandResult Result, List<ServerPush> PushesBefore)> CommandAsync(uint opcode, byte[] payload)
        {
            uint seq = NextSeq();
            await SendCommandAsync(seq, opcode, payload);
            var pushes = new List<ServerPush>();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                Envelope? env = await WireProtocol.DecodeAsync(Stream, cts.Token);
                Assert.NotNull(env);
                if (env!.Type == (uint)MsgType.ServerPush) pushes.Add(WireProtocol.GetPayload<ServerPush>(env));
                if (env.Type != (uint)MsgType.CommandResult) continue;
                CommandResult r = WireProtocol.GetPayload<CommandResult>(env);
                Assert.Equal(seq, r.Seq);
                return (r, pushes);
            }
        }

        public void Dispose() => Tcp.Dispose();
    }

    public async Task<Client> JoinAsync(string userId, uint protocolVersion, WireEncoding encoding,
        string? characterId = null, bool requireOk = true)
    {
        var tcp = new TcpClient();
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await tcp.ConnectAsync(IPAddress.Loopback, Port);
                break;
            }
            catch (SocketException) when (attempt < 60)
            {
                await Task.Delay(50);
            }
        }

        NetworkStream stream = tcp.GetStream();
        await stream.WriteAsync(WireProtocol.Encode(WireProtocol.NewEnvelope(MsgType.JoinToken,
            new JoinTokenRequest
            {
                Token = TestHelpers.CreateTestJwt(userId, ServerId, Secret, characterId: characterId),
                ProtocolVersion = protocolVersion,
            }, encoding)));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Envelope? env = await WireProtocol.DecodeAsync(stream, cts.Token);
        Assert.NotNull(env);
        Assert.Equal((uint)MsgType.JoinTokenResp, env!.Type);
        var resp = WireProtocol.GetPayload<JoinTokenResponse>(env);
        if (requireOk) Assert.True(resp.Ok, resp.Error);
        return new Client { Tcp = tcp, Stream = stream, Encoding = encoding, Join = resp };
    }

    public async Task WaitForAsync(Func<bool> predicate, string what)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (predicate()) return;
            await Task.Delay(20);
        }
        Assert.Fail($"{what} not met within 10s");
    }

    public async ValueTask DisposeAsync()
    {
        Cts.Cancel();
        await Server.ShutdownAsync();
        try { await RunTask; } catch (OperationCanceledException) { /* expected */ }
        Cts.Dispose();
        Metrics.Dispose();
    }
}
