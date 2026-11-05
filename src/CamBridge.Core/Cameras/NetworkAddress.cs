using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Numerics;

namespace CamBridge.Core;

/// <summary>
/// IPv4 addressing of a network camera (GigE Vision) as its SDK reports it during enumeration, i.e. without opening
/// the camera. Enough to tell which host network adapter reaches it and whether the two share a subnet.
/// </summary>
/// <param name="DefaultGateway">Null when the camera has none (reported as 0.0.0.0).</param>
public sealed record NetworkAddress(IPAddress IpAddress, IPAddress SubnetMask, IPAddress? DefaultGateway)
{
    /// <summary>Number of leading one bits in <see cref="SubnetMask"/>, or null when the mask is not contiguous.</summary>
    public int? PrefixLength
    {
        get
        {
            byte[] bytes = SubnetMask.GetAddressBytes();
            uint mask = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
            int ones = BitOperations.LeadingZeroCount(~mask);
            return ones == 32 || mask << ones == 0 ? ones : null;
        }
    }

    /// <summary>Builds an address from dotted IPv4 strings; null when the address or the mask is missing or not IPv4.</summary>
    public static NetworkAddress? TryCreate(string? ipAddress, string? subnetMask, string? defaultGateway)
    {
        if (!TryParseIPv4(ipAddress, out IPAddress? address) || address.Equals(IPAddress.Any)
            || !TryParseIPv4(subnetMask, out IPAddress? mask))
        {
            return null;
        }

        IPAddress? gateway = TryParseIPv4(defaultGateway, out IPAddress? parsed) && !parsed.Equals(IPAddress.Any) ? parsed : null;
        return new NetworkAddress(address, mask, gateway);
    }

    public override string ToString() =>
        PrefixLength is int prefix
            ? string.Create(CultureInfo.InvariantCulture, $"{IpAddress}/{prefix}")
            : $"{IpAddress} mask {SubnetMask}";

    private static bool TryParseIPv4(string? text, [NotNullWhen(true)] out IPAddress? address)
    {
        // IPAddress.TryParse also takes short forms such as "10" or "1.2"; the SDKs report four dotted parts.
        address = null;
        return !string.IsNullOrWhiteSpace(text)
            && text.Count(c => c == '.') == 3
            && IPAddress.TryParse(text.Trim(), out address)
            && address.AddressFamily == AddressFamily.InterNetwork;
    }
}
