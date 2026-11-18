using System.Net;
using System.Net.Sockets;
using NetworkDiagnostics.Core.Interfaces;

namespace NetworkDiagnostics.Core.Monitoring;

/// <summary>Resolves names with the operating-system resolver, IPv4 only.</summary>
public sealed class DnsHostResolver : IHostResolver
{
    public async Task<HostResolution> ResolveIPv4Async(string hostOrAddress, CancellationToken cancellationToken)
    {
        if (TargetValidator.TryParseStrictIPv4(hostOrAddress, out var literal, out _))
        {
            return new HostResolution(literal, null);
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(hostOrAddress, AddressFamily.InterNetwork, cancellationToken).ConfigureAwait(false);
            var first = addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            return first is null
                ? new HostResolution(null, $"\"{hostOrAddress}\" has no IPv4 address.")
                : new HostResolution(first, null);
        }
        catch (SocketException ex)
        {
            return new HostResolution(null, $"Could not resolve \"{hostOrAddress}\": {ex.SocketErrorCode}.");
        }
        catch (ArgumentException ex)
        {
            return new HostResolution(null, ex.Message);
        }
    }
}
