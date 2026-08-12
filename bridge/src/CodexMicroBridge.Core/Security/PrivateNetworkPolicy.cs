using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace CodexMicroBridge.Core.Security;

public static class PrivateNetworkPolicy
{
    public static bool IsAllowedRemote(IPAddress? address)
    {
        if (address is null)
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return IsRfc1918(address) || (bytes[0] == 169 && bytes[1] == 254);
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6 &&
            (address.IsIPv6LinkLocal || (bytes[0] & 0xFE) == 0xFC);
    }

    public static bool IsRfc1918(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
            (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
            (bytes[0] == 192 && bytes[1] == 168);
    }

    public static IPAddress SelectAdvertisedAddress()
    {
        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
            .Where(adapter => adapter.NetworkInterfaceType is not NetworkInterfaceType.Loopback and not NetworkInterfaceType.Tunnel)
            .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses.Select(unicast => new
            {
                adapter.NetworkInterfaceType,
                unicast.Address,
            }))
            .Where(candidate => IsRfc1918(candidate.Address))
            .OrderBy(candidate => candidate.NetworkInterfaceType switch
            {
                NetworkInterfaceType.Wireless80211 => 0,
                NetworkInterfaceType.Ethernet => 1,
                NetworkInterfaceType.GigabitEthernet => 1,
                _ => 2,
            })
            .Select(candidate => candidate.Address)
            .FirstOrDefault();

        return candidates ?? IPAddress.Loopback;
    }
}
