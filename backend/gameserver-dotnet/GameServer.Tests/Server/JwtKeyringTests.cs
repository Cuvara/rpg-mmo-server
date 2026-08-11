using GameServer.Server;

namespace GameServer.Tests.Server;

/// <summary>
/// The C# keyring must accept exactly what the Go gateway's <c>shared/jwt.Keyring</c>
/// produces, including during a rotation. Any drift here shows up in production as
/// "every join fails right after the secret was rotated".
/// </summary>
public class JwtKeyringTests
{
    private const string Current = "current-secret-32-bytes-aaaaaaaaaaa";
    private const string Previous = "previous-secret-32-bytes-bbbbbbbbb";
    private const string Stranger = "stranger-secret-32-bytes-ccccccccc";
    private const string UserId = "user-42";
    private const string ServerId = "gs-keyring";

    // ── Parsing (mirrors Go jwt.ParseKeyring) ──

    [Theory]
    // spec, expected key count, expected signing key
    [InlineData("one", 1, "one")]
    [InlineData("new,old", 2, "new")]
    [InlineData("new, old", 2, "new")]          // whitespace around entries is trimmed
    [InlineData("  new  ,  old  ", 2, "new")]
    [InlineData("new,old,", 2, "new")]          // trailing comma is harmless
    [InlineData("new,,old", 2, "new")]          // empty entries dropped
    [InlineData("a,b,c", 3, "a")]
    public void Parse_AcceptsGoStyleSpecs(string spec, int expectedCount, string expectedSigning)
    {
        var ring = JwtKeyring.Parse(spec);

        Assert.True(ring.IsValid);
        Assert.Equal(expectedCount, ring.Count);
        Assert.Equal(expectedSigning, ring.Signing);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(",")]
    [InlineData(" , , ")]
    public void Parse_EmptySpec_YieldsInvalidKeyring(string? spec)
    {
        var ring = JwtKeyring.Parse(spec);

        Assert.False(ring.IsValid);
        Assert.Equal(0, ring.Count);
        Assert.Equal("", ring.Signing);
    }

    /// <summary>An empty keyring must fail CLOSED: it verifies nothing, ever.</summary>
    [Fact]
    public void Verify_EmptyKeyring_RejectsEverything()
    {
        var ring = JwtKeyring.Parse("   ");

        Assert.Null(ring.Verify(TestHelpers.CreateTestJwt(UserId, ServerId, Current)));
        Assert.Null(ring.Verify(TestHelpers.CreateTestJwt(UserId, ServerId, "")));
        Assert.Null(ring.Verify(""));
    }

    // ── Verification ──

    [Fact]
    public void Verify_TokenSignedWithOnlyKey_Accepted()
    {
        var ring = JwtKeyring.Parse(Current);

        var claims = ring.Verify(TestHelpers.CreateTestJwt(UserId, ServerId, Current));

        Assert.NotNull(claims);
        Assert.Equal(UserId, claims!.UserId);
        Assert.Equal(ServerId, claims.ServerId);
    }

    [Fact]
    public void Verify_TokenSignedWithUnlistedKey_Rejected()
    {
        var ring = JwtKeyring.Parse($"{Current},{Previous}");

        Assert.Null(ring.Verify(TestHelpers.CreateTestJwt(UserId, ServerId, Stranger)));
    }

    /// <summary>
    /// The rotation contract: while "current,previous" is deployed, tokens signed with
    /// EITHER key verify, so the population signed under the old key drains instead of
    /// being logged out at the deploy.
    /// </summary>
    [Theory]
    [InlineData(Current)]
    [InlineData(Previous)]
    public void Verify_DuringRotation_BothKeysAccepted(string signingSecret)
    {
        var ring = JwtKeyring.Parse($"{Current},{Previous}");

        var claims = ring.Verify(TestHelpers.CreateTestJwt(UserId, ServerId, signingSecret));

        Assert.NotNull(claims);
        Assert.Equal(UserId, claims!.UserId);
    }

    /// <summary>After the rotation window closes, the old key must stop working.</summary>
    [Fact]
    public void Verify_AfterRotationCompletes_PreviousKeyRejected()
    {
        var ring = JwtKeyring.Parse(Current);

        Assert.NotNull(ring.Verify(TestHelpers.CreateTestJwt(UserId, ServerId, Current)));
        Assert.Null(ring.Verify(TestHelpers.CreateTestJwt(UserId, ServerId, Previous)));
    }

    /// <summary>
    /// Expiry beats every key. Go short-circuits on the first key whose signature
    /// matched but whose exp has passed; either way the answer must be "rejected",
    /// whichever position the matching key occupies.
    /// </summary>
    [Theory]
    [InlineData(Current)]   // expired under the FIRST key (Go short-circuits here)
    [InlineData(Previous)]  // expired under the SECOND key
    public void Verify_ExpiredToken_RejectedRegardlessOfMatchingKey(string signingSecret)
    {
        var ring = JwtKeyring.Parse($"{Current},{Previous}");

        Assert.Null(ring.Verify(TestHelpers.CreateExpiredJwt(UserId, ServerId, signingSecret)));
    }

    [Theory]
    [InlineData("not-a-jwt")]
    [InlineData("a.b")]
    [InlineData("a.b.c.d")]
    [InlineData("")]
    public void Verify_MalformedToken_Rejected(string token)
    {
        var ring = JwtKeyring.Parse($"{Current},{Previous}");

        Assert.Null(ring.Verify(token));
    }

}
