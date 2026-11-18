using System.Net;
using NetworkDiagnostics.Core.Models;

namespace NetworkDiagnostics.Core.Interfaces;

/// <summary>Sends a single ICMP echo request. Implementations must not throw for network failures; they
/// return a failed <see cref="ProbeReply"/> instead. Cancellation is signalled with
/// <see cref="OperationCanceledException"/>.</summary>
public interface IPingProbe : IDisposable
{
    /// <summary>Human-readable description of how probes are routed, e.g. "Bound to en0 (IP_BOUND_IF)".</summary>
    string RoutingDescription { get; }

    Task<ProbeReply> SendAsync(IPAddress destination, TimeSpan timeout, CancellationToken cancellationToken);
}

/// <summary>Result of checking whether probes can be forced through a given adapter.</summary>
public sealed record BindingCheck(bool IsSupported, string Message)
{
    public static BindingCheck Supported(string message) => new(true, message);

    public static BindingCheck Unsupported(string message) => new(false, message);
}

/// <summary>Creates probes appropriate for the current platform and adapter selection.</summary>
public interface IPingProbeFactory
{
    /// <summary>Verifies that probes can be sent through the selection without falling back to another interface.</summary>
    BindingCheck CheckSupport(AdapterSelection selection);

    /// <summary>Creates a probe. Throws <see cref="NotSupportedException"/> if <see cref="CheckSupport"/> would fail.</summary>
    IPingProbe Create(AdapterSelection selection);
}
