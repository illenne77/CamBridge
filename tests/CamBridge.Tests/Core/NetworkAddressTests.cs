using System.Net;
using CamBridge.Core;

namespace CamBridge.Tests.Core;

public class NetworkAddressTests
{
    [Fact]
    public void TryCreate_ParsesDottedIPv4_AndDropsAnEmptyGateway()
    {
        NetworkAddress? address = NetworkAddress.TryCreate("192.168.10.2", "255.255.255.0", "0.0.0.0");

        Assert.NotNull(address);
        Assert.Equal(IPAddress.Parse("192.168.10.2"), address.IpAddress);
        Assert.Equal(IPAddress.Parse("255.255.255.0"), address.SubnetMask);
        Assert.Null(address.DefaultGateway);
        Assert.Equal(24, address.PrefixLength);
        Assert.Equal("192.168.10.2/24", address.ToString());
    }

    [Fact]
    public void TryCreate_KeepsARealGateway()
    {
        NetworkAddress? address = NetworkAddress.TryCreate("10.0.0.5", "255.0.0.0", "10.0.0.1");

        Assert.Equal(IPAddress.Parse("10.0.0.1"), address?.DefaultGateway);
        Assert.Equal(8, address?.PrefixLength);
    }

    [Theory]
    [InlineData(null, "255.255.255.0")]
    [InlineData("", "255.255.255.0")]
    [InlineData("0.0.0.0", "255.255.255.0")] // not configured
    [InlineData("192.168.10.2", "")]
    [InlineData("192.168.10.2", null)]
    [InlineData("10", "255.0.0.0")] // short form IPAddress.Parse would accept
    [InlineData("fe80::1", "255.255.255.0")]
    [InlineData("not an address", "255.255.255.0")]
    public void TryCreate_RejectsMissingOrNonIPv4Values(string? ip, string? mask)
    {
        Assert.Null(NetworkAddress.TryCreate(ip, mask, null));
    }

    [Theory]
    [InlineData("255.255.255.255", 32)]
    [InlineData("255.255.254.0", 23)]
    [InlineData("0.0.0.0", 0)]
    public void PrefixLength_CountsContiguousMasks(string mask, int prefix)
    {
        Assert.Equal(prefix, NetworkAddress.TryCreate("10.1.2.3", mask, null)?.PrefixLength);
    }

    [Fact]
    public void PrefixLength_IsNullForANonContiguousMask()
    {
        NetworkAddress? address = NetworkAddress.TryCreate("10.1.2.3", "255.0.255.0", null);

        Assert.Null(address?.PrefixLength);
        Assert.Equal("10.1.2.3 mask 255.0.255.0", address?.ToString());
    }
}
