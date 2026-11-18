using System.Net;

namespace NetworkDiagnostics.Core.Interfaces;

public interface IHostResolver
{
    /// <summary>Resolves a hostname or IPv4 literal to an IPv4 address. Returns null if resolution fails.</summary>
    Task<HostResolution> ResolveIPv4Async(string hostOrAddress, CancellationToken cancellationToken);
}

public sealed record HostResolution(IPAddress? Address, string? Error)
{
    public bool Succeeded => Address is not null;
}
