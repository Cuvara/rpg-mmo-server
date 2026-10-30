using GameServer.Server;

namespace GameServer.Tests.Server;

public sealed class AdvertisedAddressTests
{
    [Theory]
    [InlineData("127.0.0.1:9210", true)]
    [InlineData("127.42.1.9:9210", true)]
    [InlineData("localhost:1", true)]
    [InlineData("LOCALHOST:1", true)]
    [InlineData("[::1]:9000", true)]
    [InlineData(":9000", false)]
    [InlineData("example.com:9000", false)]
    [InlineData("1.2.3.4:9000", false)]
    public void IsLoopbackHost_ClassifiesHost(string address, bool expected)
    {
        Assert.Equal(expected, AdvertisedAddress.IsLoopbackHost(address));
    }
}
