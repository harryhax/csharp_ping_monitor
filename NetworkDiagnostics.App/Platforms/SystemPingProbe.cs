using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using NetworkDiagnostics.Core.Interfaces;
using NetworkDiagnostics.Core.Models;

namespace NetworkDiagnostics.App.Platforms;

/// <summary>
/// Uses <see cref="Ping"/>; the operating system chooses the outgoing interface. Used only for
/// "Automatic (System Default)".
/// </summary>
public sealed class SystemPingProbe : IPingProbe
{
    private static readonly byte[] Payload = Enumerable.Range(0, 32).Select(i => (byte)('a' + (i % 23))).ToArray();
    private readonly Ping _ping = new();

    public string RoutingDescription => "operating-system routing (System.Net.NetworkInformation.Ping)";

    public async Task<ProbeReply> SendAsync(IPAddress destination, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var reply = await _ping.SendPingAsync(destination, timeout, Payload, new PingOptions(64, true), cancellationToken).ConfigureAwait(false);
            var elapsed = sw.Elapsed.TotalMilliseconds;
            return reply.Status switch
            {
                IPStatus.Success => ProbeReply.Ok(elapsed, reply.Address),
                IPStatus.TimedOut => ProbeReply.Fail(ProbeStatus.Timeout, "Request timed out."),
                IPStatus.DestinationHostUnreachable or IPStatus.DestinationNetworkUnreachable or IPStatus.DestinationPortUnreachable
                    or IPStatus.DestinationProtocolUnreachable or IPStatus.DestinationUnreachable or IPStatus.BadRoute
                    => ProbeReply.Fail(ProbeStatus.DestinationUnreachable, reply.Status.ToString(), reply.Address),
                IPStatus.TtlExpired or IPStatus.TimeExceeded or IPStatus.TtlReassemblyTimeExceeded
                    => ProbeReply.Fail(ProbeStatus.TtlExpired, reply.Status.ToString(), reply.Address),
                _ => ProbeReply.Fail(ProbeStatus.Error, reply.Status.ToString(), reply.Address),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (PingException ex) when (ex.InnerException is SocketException se)
        {
            return ProbeErrors.FromSocketError(se.SocketErrorCode, se.Message);
        }
        catch (PingException ex)
        {
            return ProbeReply.Fail(ProbeStatus.Error, ex.InnerException?.Message ?? ex.Message);
        }
        catch (SocketException se)
        {
            return ProbeErrors.FromSocketError(se.SocketErrorCode, se.Message);
        }
    }

    public void Dispose() => _ping.Dispose();
}

internal static class ProbeErrors
{
    public static ProbeReply FromSocketError(SocketError error, string message) => error switch
    {
        SocketError.AccessDenied => ProbeReply.Fail(ProbeStatus.PermissionDenied, $"The operating system denied permission to send ICMP ({message})."),
        SocketError.NetworkDown or SocketError.AddressNotAvailable or SocketError.NetworkUnreachable
            => ProbeReply.Fail(ProbeStatus.LocalAdapterFailure, $"{error}: {message}"),
        SocketError.HostUnreachable or SocketError.HostDown
            => ProbeReply.Fail(ProbeStatus.DestinationUnreachable, $"{error}: {message}"),
        SocketError.TimedOut => ProbeReply.Fail(ProbeStatus.Timeout, "Request timed out."),
        _ => ProbeReply.Fail(ProbeStatus.Error, $"{error}: {message}"),
    };
}
