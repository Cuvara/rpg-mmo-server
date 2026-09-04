using System.Text;
using GameServer.Events;
using Xunit;

namespace GameServer.Tests.Events;

/// <summary>
/// The <c>session_superseded</c> payload contract with the Go publisher
/// (<c>gateway/server/kick.go</c>, <c>SessionSupersededEvent</c>): snake_case
/// fields, three mandatory (<c>user_id</c>, <c>server_id</c>, <c>jti</c>), two
/// diagnostic. <see cref="KickEvents.TryParse"/> must return null — never throw —
/// for anything malformed, because the consumer ACKs unparseable entries instead
/// of letting them poison the redelivery loop.
/// </summary>
public class KickEventParseTests
{
    [Fact]
    public void Parses_TheGoPublisherShape()
    {
        // Byte-for-byte what Go's json.Marshal(SessionSupersededEvent{...}) emits.
        var payload = Encoding.UTF8.GetBytes(
            """{"user_id":"u1","server_id":"srv1","jti":"abc123","old_gateway":"gw-a","new_gateway":"gw-b"}""");

        var p = KickEvents.TryParse(payload);
        Assert.NotNull(p);
        Assert.Equal("u1", p!.UserId);
        Assert.Equal("srv1", p.ServerId);
        Assert.Equal("abc123", p.Jti);
        Assert.Equal("gw-a", p.OldGateway);
        Assert.Equal("gw-b", p.NewGateway);
    }

    [Fact]
    public void Parses_WithoutTheOptionalGatewayFields()
    {
        // Go marshals them with omitempty; their absence is legal.
        var p = KickEvents.TryParse(
            Encoding.UTF8.GetBytes("""{"user_id":"u1","server_id":"srv1","jti":"j"}"""));
        Assert.NotNull(p);
        Assert.Null(p!.OldGateway);
    }

    [Theory]
    [InlineData("")]                                              // empty
    [InlineData("not json at all")]                               // garbage
    [InlineData("{}")]                                            // no fields
    [InlineData("""{"user_id":"u1","server_id":"srv1"}""")]       // no jti
    [InlineData("""{"user_id":"u1","jti":"j"}""")]                // no server_id
    [InlineData("""{"server_id":"srv1","jti":"j"}""")]            // no user_id
    [InlineData("""{"user_id":"","server_id":"srv1","jti":"j"}""")] // empty user_id
    [InlineData("""{"user_id":"u1","server_id":"srv1","jti":""}""")] // empty jti
    [InlineData("null")]                                          // JSON null
    public void Malformed_ReturnsNull_NeverThrows(string raw)
    {
        Assert.Null(KickEvents.TryParse(Encoding.UTF8.GetBytes(raw)));
    }
}
