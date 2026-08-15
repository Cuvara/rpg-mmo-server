using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using GameServer.Net;
using GameServer.Persistence;
using GameServer.Server;
using Microsoft.Extensions.Logging.Abstractions;
using GameServer.Tests.Infrastructure;
using RpgMmo.Wire.V1;

namespace GameServer.Tests.Server;

/// <summary>
/// The join response carries the CRITICAL simulation rate (#93).
///
/// <para>Before this, server and client agreed on 15Hz only because two hardcoded
/// literals in two repositories happened to match, and the server could be moved off
/// that value by configuration the client had no way to observe. Nothing failed when
/// they diverged: the client predicted at the wrong rate, every snapshot corrected it,
/// and a player saw rubber-banding rather than a misconfiguration. These tests are the
/// thing that now fails instead.</para>
///
/// <para>Every case drives the real TCP handshake in <b>both</b> encodings, because the
/// defect returns the moment the two disagree — a field present in protobuf and missing
/// from JSON is a client on the legacy encoding silently back to guessing.</para>
/// </summary>
public class JoinTickRateTests
{
    private const string JoinSecret = "join-secret-32-bytes-bbbbbbbbbbbbb";
    private const string ServerId = "gs-tick-rate-test";

    /// <summary>
    /// A server left on the legacy single-rate setting reports that rate. The uniform
    /// configuration derived from <c>TickRate</c> makes the critical rate the tick rate.
    /// </summary>
    [Theory]
    [InlineData(WireEncoding.Json)]
    [InlineData(WireEncoding.Proto)]
    public async Task Join_LegacySingleRate_ReportsThatRate(WireEncoding encoding)
    {
        var resp = await JoinAsync(encoding, tickRate: 15, rates: null);
        Assert.True(resp.Ok);
        Assert.Equal(15u, resp.TickRate);
    }

    /// <summary>
    /// The case the issue is about: a server tuned off 15 must say so. If this returned
    /// 15 the client would predict at 15 against a 30Hz simulation and never be told.
    /// </summary>
    [Theory]
    [InlineData(WireEncoding.Json)]
    [InlineData(WireEncoding.Proto)]
    public async Task Join_NonDefaultSingleRate_ReportsThatRateNot15(WireEncoding encoding)
    {
        var resp = await JoinAsync(encoding, tickRate: 30, rates: null);
        Assert.True(resp.Ok);
        Assert.Equal(30u, resp.TickRate);
        Assert.NotEqual(15u, resp.TickRate);
    }

    /// <summary>
    /// Multi-rate: the wire carries the CRITICAL rate, not the world rate and not the
    /// snapshot cadence. 60/15/5 is the configuration where picking the wrong one is
    /// invisible in a single-rate test — the world rate here is exactly the old 15.
    /// </summary>
    [Theory]
    [InlineData(WireEncoding.Json)]
    [InlineData(WireEncoding.Proto)]
    public async Task Join_MultiRate_ReportsCriticalRateNotWorldRate(WireEncoding encoding)
    {
        var rates = Rates(60, 15, 5);
        // TickRate is deliberately left at a third value: if the server ever read it
        // instead of the critical rate, this test would see 20 and fail.
        var resp = await JoinAsync(encoding, tickRate: 20, rates: rates);
        Assert.True(resp.Ok);
        Assert.Equal(60u, resp.TickRate);
        Assert.NotEqual((uint)rates.WorldHz, resp.TickRate);
    }

    /// <summary>
    /// A rejected join carries no rate. 0 is the schema's "not supplied", which tells a
    /// client to refuse to predict — the right answer for a session that does not exist,
    /// and it keeps the server from handing tuning data to a caller that failed auth.
    /// </summary>
    [Theory]
    [InlineData(WireEncoding.Json)]
    [InlineData(WireEncoding.Proto)]
    public async Task Join_Rejected_CarriesNoRate(WireEncoding encoding)
    {
        var resp = await JoinAsync(encoding, tickRate: 30, rates: null, tokenSecret: "wrong-secret-32-bytes-ddddddddddd");
        Assert.False(resp.Ok);
        Assert.Equal(0u, resp.TickRate);
    }

    /// <summary>
    /// The JSON field is spelled <c>tick_rate</c>. The legacy encoding is snake_case by
    /// contract with the Go side, and a decoder there matches the literal bytes, so a
    /// camelCase slip would decode as absent rather than as an error.
    /// </summary>
    [Fact]
    public async Task Join_JsonEncoding_UsesSnakeCaseFieldName()
    {
        byte[] payload = await JoinRawPayloadAsync(WireEncoding.Json, tickRate: 30, rates: null);
        using var doc = JsonDocument.Parse(payload);
        Assert.True(doc.RootElement.TryGetProperty("tick_rate", out var value),
            $"join response JSON must carry tick_rate, got: {doc.RootElement}");
        Assert.Equal(30u, value.GetUInt32());
    }

    // ── Helpers ──

    private static SimulationRates Rates(int critical, int world, int background)
    {
        Assert.True(SimulationRates.TryCreate(critical, world, background, out var rates, out string? error), error);
        return rates!;
    }

    private static async Task<JoinTokenResponse> JoinAsync(
        WireEncoding encoding, int tickRate, SimulationRates? rates, string? tokenSecret = null)
    {
        byte[] payload = await JoinRawPayloadAsync(encoding, tickRate, rates, tokenSecret);
        var env = new GameServer.Net.Envelope
        {
            Type = (byte)MsgType.JoinTokenResp, Payload = payload, Encoding = encoding
        };
        return WireProtocol.GetPayload<JoinTokenResponse>(env);
    }

    /// <summary>
    /// Drive a full handshake and hand back the raw response body, so a test can assert
    /// on the decoded message or on the bytes.
    /// </summary>
    private static async Task<byte[]> JoinRawPayloadAsync(
        WireEncoding encoding, int tickRate, SimulationRates? rates, string? tokenSecret = null)
    {
        var options = new ServerOptions
        {
            ServerAddr = ":0",
            ServerId = ServerId,
            MapId = "map_tick_rate",
            TickRate = tickRate,
            SimulationRates = rates,
            Capacity = 8,
            JwtSecret = "",
            JoinTokenSecret = JoinSecret,
            SaveInterval = TimeSpan.FromSeconds(30),
            HoldTtl = TimeSpan.FromSeconds(1),
            PlayerStore = new MemoryPlayerStore(),
            LoggerFactory = NullLoggerFactory.Instance
        };

        var server = new GameServerHost(options);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var (runTask, port) = await TestPorts.StartServerAsync(server, cts.Token);

        try
        {
            using var client = new TcpClient();
            await ConnectWithRetryAsync(client, port);
            await using var stream = client.GetStream();

            string token = TestHelpers.CreateTestJwt("user-tick-rate", ServerId, tokenSecret ?? JoinSecret);

            // The server answers in the encoding the client used, so the request encoding
            // is what selects which codec this test exercises.
            var env = WireProtocol.NewEnvelope(MsgType.JoinToken, new JoinTokenRequest { Token = token }, encoding);
            await stream.WriteAsync(WireProtocol.Encode(env), cts.Token);
            await stream.FlushAsync(cts.Token);

            var respEnv = await WireProtocol.DecodeAsync(stream, cts.Token);
            Assert.NotNull(respEnv);
            Assert.Equal((byte)MsgType.JoinTokenResp, respEnv!.Type);
            Assert.Equal(encoding, respEnv.Encoding);
            return respEnv.Payload;
        }
        finally
        {
            cts.Cancel();
            try { await runTask; } catch (OperationCanceledException) { /* expected */ }
            await server.DisposeAsync();
        }
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
}
