using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace CodexPhoneReminder.Agent;

/// <summary>
/// Finds the LAN address that a phone can use to reach the local agent.
/// Windows hosts commonly have Docker, Hyper-V, WSL, or VMware adapters;
/// those must not win over the active physical WLAN adapter.
/// </summary>
public sealed class LanAddressResolver
{
    public LanEndpoint Resolve()
    {
        var selected = SelectBest(ReadCandidates());
        return selected is null
            ? new LanEndpoint("127.0.0.1", "未找到可用局域网网卡", "Loopback")
            : new LanEndpoint(selected.Address, selected.InterfaceName, selected.InterfaceType.ToString());
    }

    /// <summary>
    /// Kept public so the ranking policy can be covered without depending on
    /// the machine running the test having a particular network topology.
    /// </summary>
    public static LanAddressCandidate? SelectBest(IEnumerable<LanAddressCandidate> candidates) =>
        candidates
            .Where(candidate => IsUsableIpv4(candidate.Address))
            // A connected WLAN is the phone's expected path. Wired Ethernet is
            // the fallback, then any remaining non-loopback interface.
            .OrderBy(candidate => InterfacePriority(candidate.InterfaceType))
            .ThenBy(candidate => candidate.IsVirtual ? 1 : 0)
            .ThenByDescending(candidate => candidate.HasIpv4Gateway)
            .ThenBy(candidate => candidate.InterfaceName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.Address, StringComparer.Ordinal)
            .FirstOrDefault();

    private static IEnumerable<LanAddressCandidate> ReadCandidates()
    {
        NetworkInterface[] adapters;
        try { adapters = NetworkInterface.GetAllNetworkInterfaces(); }
        catch (NetworkInformationException) { yield break; }

        foreach (var adapter in adapters)
        {
            if (adapter.OperationalStatus != OperationalStatus.Up ||
                adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            IPInterfaceProperties properties;
            try { properties = adapter.GetIPProperties(); }
            catch (NetworkInformationException) { continue; }

            var hasGateway = properties.GatewayAddresses.Any(gateway => IsUsableIpv4(gateway.Address));
            var isVirtual = IsVirtualAdapter(adapter);
            foreach (var unicast in properties.UnicastAddresses)
            {
                if (!IsUsableIpv4(unicast.Address)) continue;
                yield return new LanAddressCandidate(
                    unicast.Address.ToString(),
                    adapter.Name,
                    adapter.NetworkInterfaceType,
                    hasGateway,
                    isVirtual);
            }
        }
    }

    private static int InterfacePriority(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Wireless80211 => 0,
        NetworkInterfaceType.Ethernet => 1,
        _ => 2
    };

    private static bool IsVirtualAdapter(NetworkInterface adapter)
    {
        var description = $"{adapter.Name} {adapter.Description}";
        return new[] { "virtual", "vmware", "hyper-v", "vethernet", "docker", "wsl", "tailscale", "zerotier", "wireguard", "loopback", "tunnel", "tap" }
            .Any(marker => description.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsUsableIpv4(string address) =>
        IPAddress.TryParse(address, out var parsed) && IsUsableIpv4(parsed);

    private static bool IsUsableIpv4(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address)) return false;
        var bytes = address.GetAddressBytes();
        // 0.0.0.0 and IPv4 link-local addresses are not usable phone endpoints.
        return bytes[0] != 0 && !(bytes[0] == 169 && bytes[1] == 254);
    }
}

public sealed record LanAddressCandidate(
    string Address,
    string InterfaceName,
    NetworkInterfaceType InterfaceType,
    bool HasIpv4Gateway,
    bool IsVirtual);

public sealed record LanEndpoint(string Address, string InterfaceName, string InterfaceType);
