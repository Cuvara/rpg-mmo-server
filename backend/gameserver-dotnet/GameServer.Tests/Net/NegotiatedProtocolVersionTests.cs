using GameServer.Net;
using Xunit;

namespace GameServer.Tests.Net;

/// <summary>
/// The successful-join echo is the negotiated version: the peer's own inside the
/// supported window, this build's otherwise. A protocol 2 client accepts only an exact
/// echo, so echoing 3 would make it refuse the server that just admitted it.
/// Mirrors shared/messages TestNegotiatedProtocolVersion in Go.
/// </summary>
public class NegotiatedProtocolVersionTests
{
    [Theory]
    [InlineData(0u, WireProtocol.ProtocolVersion)]
    [InlineData(WireProtocol.MinSupportedProtocolVersion - 1, WireProtocol.ProtocolVersion)]
    [InlineData(WireProtocol.MinSupportedProtocolVersion, WireProtocol.MinSupportedProtocolVersion)]
    [InlineData(WireProtocol.ProtocolVersion, WireProtocol.ProtocolVersion)]
    [InlineData(WireProtocol.ProtocolVersion + 1, WireProtocol.ProtocolVersion)]
    public void EchoesThePeerInsideTheWindow_AndThisBuildOutsideIt(uint peer, uint expected)
    {
        Assert.Equal(expected, WireProtocol.NegotiatedProtocolVersion(peer));
    }
}
