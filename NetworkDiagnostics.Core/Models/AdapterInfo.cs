using System.Net;

namespace NetworkDiagnostics.Core.Models;

/// <summary>
/// Immutable snapshot of a network adapter as seen at a point in time.
/// </summary>
/// <param name="Id">Stable platform identifier (Windows: GUID, macOS: BSD name such as <c>en0</c>).</param>
/// <param name="Name">Friendly name, e.g. "Wi-Fi".</param>
/// <param name="Description">Driver / hardware description or BSD device name.</param>
/// <param name="Kind">Wi-Fi, Ethernet or Other.</param>
/// <param name="IPv4Address">First IPv4 unicast address, if any.</param>
/// <param name="IPv4Gateway">Default IPv4 gateway associated with the adapter, if any.</param>
/// <param name="IsUp">Whether the operating system reports the adapter as operational.</param>
/// <param name="StatusText">Operating-system status text (Up, Down, Dormant…).</param>
/// <param name="InterfaceIndex">OS interface index, used for binding probes.</param>
public sealed record AdapterInfo(
    string Id,
    string Name,
    string Description,
    AdapterKind Kind,
    IPAddress? IPv4Address,
    IPAddress? IPv4Gateway,
    bool IsUp,
    string StatusText,
    int? InterfaceIndex)
{
    public string KindText => Kind switch
    {
        AdapterKind.WiFi => "Wi-Fi",
        AdapterKind.Ethernet => "Ethernet",
        _ => "Other",
    };

    public string ConnectionText => IsUp && IPv4Address is not null ? "Connected" : IsUp ? "No IPv4 address" : "Disconnected";
}
