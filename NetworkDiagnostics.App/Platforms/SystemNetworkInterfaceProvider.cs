using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using NetworkDiagnostics.App.Platforms.MacOS;
using NetworkDiagnostics.Core.Interfaces;
using NetworkDiagnostics.Core.Models;

namespace NetworkDiagnostics.App.Platforms;

/// <summary>
/// Adapter discovery built on <see cref="NetworkInterface.GetAllNetworkInterfaces"/>, with macOS-specific
/// friendly names and Wi-Fi detection from <c>networksetup</c>.
/// </summary>
public sealed class SystemNetworkInterfaceProvider : INetworkInterfaceProvider
{
    private readonly MacHardwarePortMap? _macPorts = OperatingSystem.IsMacOS() ? new MacHardwarePortMap() : null;

    public IReadOnlyList<AdapterInfo> GetAdapters()
    {
        var result = new List<AdapterInfo>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
            {
                continue;
            }

            IPInterfaceProperties props;
            try
            {
                props = ni.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            var ipv4 = props.UnicastAddresses
                .Select(u => u.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));

            // Adapters without an IPv4 address cannot be tested, so they are not offered.
            if (ipv4 is null)
            {
                continue;
            }

            var gateway = props.GatewayAddresses
                .Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));

            int? index = null;
            try
            {
                index = props.GetIPv4Properties()?.Index;
            }
            catch (NetworkInformationException)
            {
            }

            var (name, kind) = Describe(ni);
            result.Add(new AdapterInfo(
                Id: ni.Id,
                Name: name,
                Description: ni.Description,
                Kind: kind,
                IPv4Address: ipv4,
                IPv4Gateway: gateway,
                IsUp: ni.OperationalStatus == OperationalStatus.Up,
                StatusText: ni.OperationalStatus.ToString(),
                InterfaceIndex: index));
        }

        return result
            .OrderByDescending(a => a.IPv4Gateway is not null)
            .ThenBy(a => a.Kind)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public string? GetSystemDefaultAdapterId()
    {
        // Connecting a UDP socket performs a route lookup without sending any packets.
        IPAddress? local;
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect(new IPEndPoint(IPAddress.Parse("8.8.8.8"), 53));
            local = (s.LocalEndPoint as IPEndPoint)?.Address;
        }
        catch (SocketException)
        {
            return null;
        }

        if (local is null || local.Equals(IPAddress.Any))
        {
            return null;
        }

        return GetAdapters().FirstOrDefault(a => local.Equals(a.IPv4Address))?.Id;
    }

    private (string Name, AdapterKind Kind) Describe(NetworkInterface ni)
    {
        var kind = ni.NetworkInterfaceType switch
        {
            NetworkInterfaceType.Wireless80211 => AdapterKind.WiFi,
            NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
                or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit => AdapterKind.Ethernet,
            _ => AdapterKind.Other,
        };

        if (_macPorts?.TryGet(ni.Name) is { } port)
        {
            // networksetup knows the user-facing name ("Wi-Fi", "USB 10/100/1000 LAN").
            var macKind = port.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase) || port.Contains("AirPort", StringComparison.OrdinalIgnoreCase)
                ? AdapterKind.WiFi
                : port.Contains("Ethernet", StringComparison.OrdinalIgnoreCase) || port.Contains("LAN", StringComparison.OrdinalIgnoreCase) || port.Contains("Thunderbolt", StringComparison.OrdinalIgnoreCase)
                    ? AdapterKind.Ethernet
                    : kind;
            return ($"{port} ({ni.Name})", macKind);
        }

        return (ni.Name, kind);
    }
}
