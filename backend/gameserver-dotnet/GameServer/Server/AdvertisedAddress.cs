using System.Net;

namespace GameServer.Server;

internal static class AdvertisedAddress
{
    internal static bool IsLoopbackHost(string address)
    {
        int separator = address.LastIndexOf(':');
        if (separator <= 0 || separator == address.Length - 1)
            return false;

        string host = address[..separator].Trim('[', ']');
        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));
    }
}
