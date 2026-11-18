using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NetworkDiagnostics.Core.Interfaces;
using NetworkDiagnostics.Core.Models;
using NetworkDiagnostics.Core.Monitoring;

namespace NetworkDiagnostics.App.Platforms.MacOS;

/// <summary>
/// macOS probe that forces traffic out of a specific interface. Uses an unprivileged ICMP datagram socket
/// (<c>SOCK_DGRAM</c>/<c>IPPROTO_ICMP</c>) with the <c>IP_BOUND_IF</c> socket option, which makes the kernel
/// perform a scoped route lookup on that interface only. No routing tables are modified.
/// </summary>
[SupportedOSPlatform("macos")]
public sealed partial class MacBoundIcmpProbe : IPingProbe
{
    private const int IPPROTO_IP = 0;
    private const int IP_BOUND_IF = 25;

    private readonly string _device;
    private readonly uint _ifIndex;
    private readonly ushort _identifier = (ushort)Random.Shared.Next(1, ushort.MaxValue);
    private int _sequence;

    public MacBoundIcmpProbe(AdapterInfo adapter)
    {
        _device = adapter.Id;
        _ifIndex = if_nametoindex(_device);
        if (_ifIndex == 0)
        {
            throw new NotSupportedException($"Interface {_device} was not found.");
        }
    }

    public string RoutingDescription => $"ICMP socket bound to {_device} (IP_BOUND_IF, index {_ifIndex})";

    public static BindingCheck Check(AdapterInfo adapter)
    {
        var index = if_nametoindex(adapter.Id);
        if (index == 0)
        {
            return BindingCheck.Unsupported($"Interface {adapter.Id} was not found by the operating system.");
        }

        try
        {
            using var socket = CreateSocket(index);
            return BindingCheck.Supported($"Probes are bound to {adapter.Id} with IP_BOUND_IF.");
        }
        catch (SocketException ex) when (ex.SocketErrorCode == SocketError.AccessDenied)
        {
            return BindingCheck.Unsupported("macOS refused permission to open an ICMP socket, so probes cannot be bound to the selected adapter.");
        }
        catch (SocketException ex)
        {
            return BindingCheck.Unsupported($"Probes cannot be bound to {adapter.Name}: {ex.Message}");
        }
    }

    public async Task<ProbeReply> SendAsync(IPAddress destination, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var sequence = (ushort)Interlocked.Increment(ref _sequence);
        var packet = IcmpPacket.BuildEchoRequest(_identifier, sequence);
        var buffer = new byte[1500];

        Socket socket;
        try
        {
            // A fresh socket per probe ensures late replies to an earlier, timed-out probe cannot be confused with this one.
            socket = CreateSocket(_ifIndex);
        }
        catch (SocketException ex)
        {
            return ProbeErrors.FromSocketError(ex.SocketErrorCode, ex.Message);
        }

        using (socket)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                await socket.SendToAsync(packet, SocketFlags.None, new IPEndPoint(destination, 0), cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException ex)
            {
                return ProbeErrors.FromSocketError(ex.SocketErrorCode, ex.Message);
            }

            while (true)
            {
                var remaining = timeout - sw.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    return ProbeReply.Fail(ProbeStatus.Timeout, "Request timed out.");
                }

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(remaining);
                SocketReceiveFromResult received;
                try
                {
                    received = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), timeoutCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    return ProbeReply.Fail(ProbeStatus.Timeout, "Request timed out.");
                }
                catch (SocketException ex)
                {
                    return ProbeErrors.FromSocketError(ex.SocketErrorCode, ex.Message);
                }

                var elapsed = sw.Elapsed.TotalMilliseconds;
                var from = (received.RemoteEndPoint as IPEndPoint)?.Address;
                var match = IcmpPacket.Match(buffer.AsSpan(0, received.ReceivedBytes), _identifier, sequence, out var code);
                switch (match)
                {
                    case IcmpMatch.EchoReply when destination.Equals(from):
                        return ProbeReply.Ok(elapsed, from);
                    case IcmpMatch.DestinationUnreachable:
                        return ProbeReply.Fail(ProbeStatus.DestinationUnreachable, $"{IcmpPacket.DescribeUnreachableCode(code)} (reported by {from}).", from);
                    case IcmpMatch.TimeExceeded:
                        return ProbeReply.Fail(ProbeStatus.TtlExpired, $"TTL expired in transit (reported by {from}).", from);
                }
            }
        }
    }

    public void Dispose()
    {
    }

    private static Socket CreateSocket(uint ifIndex)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Icmp);
        try
        {
            socket.SetRawSocketOption(IPPROTO_IP, IP_BOUND_IF, BitConverter.GetBytes((int)ifIndex));
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    [LibraryImport("libc", EntryPoint = "if_nametoindex", StringMarshalling = StringMarshalling.Utf8)]
    private static partial uint if_nametoindex(string name);
}
