using System.Net;

namespace NetworkDiagnostics.Core.Models;

/// <summary>Result returned by an <see cref="Interfaces.IPingProbe"/> for one echo request.</summary>
/// <param name="Status">Outcome.</param>
/// <param name="RoundTripMs">Round-trip time in milliseconds; only set when <paramref name="Status"/> is Success.</param>
/// <param name="Detail">Human-readable error detail, if any.</param>
/// <param name="RespondingAddress">Address that sent the reply or ICMP error, if known.</param>
public sealed record ProbeReply(ProbeStatus Status, double? RoundTripMs = null, string? Detail = null, IPAddress? RespondingAddress = null)
{
    public static ProbeReply Ok(double rttMs, IPAddress? from = null) => new(ProbeStatus.Success, rttMs, null, from);

    public static ProbeReply Fail(ProbeStatus status, string? detail = null, IPAddress? from = null)
    {
        if (status == ProbeStatus.Success)
        {
            throw new ArgumentException("Use Ok() for successful replies.", nameof(status));
        }

        return new ProbeReply(status, null, detail, from);
    }
}
