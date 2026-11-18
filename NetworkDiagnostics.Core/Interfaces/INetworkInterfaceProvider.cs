using NetworkDiagnostics.Core.Models;

namespace NetworkDiagnostics.Core.Interfaces;

/// <summary>Platform abstraction for adapter discovery.</summary>
public interface INetworkInterfaceProvider
{
    /// <summary>Returns the adapters that can be offered to the user (non-loopback, non-tunnel).</summary>
    IReadOnlyList<AdapterInfo> GetAdapters();

    /// <summary>
    /// Returns the id of the adapter the operating system would use for internet traffic, or null if there
    /// is no default route. No packets are sent.
    /// </summary>
    string? GetSystemDefaultAdapterId();
}
