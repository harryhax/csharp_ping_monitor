using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using NetworkDiagnostics.Core.Interfaces;
using NetworkDiagnostics.Core.Models;

namespace NetworkDiagnostics.App.Platforms.Windows;

/// <summary>
/// Windows probe that sends from the selected adapter's IPv4 address using <c>IcmpSendEcho2Ex</c> (iphlpapi).
/// Windows uses the strong host send model by default, so a packet with a given source address leaves through
/// the interface that owns that address. No elevation and no routing changes are required.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsSourceBoundIcmpProbe : IPingProbe
{
    private const uint IP_SUCCESS = 0;
    private const uint IP_DEST_NET_UNREACHABLE = 11002;
    private const uint IP_DEST_HOST_UNREACHABLE = 11003;
    private const uint IP_DEST_PROT_UNREACHABLE = 11004;
    private const uint IP_DEST_PORT_UNREACHABLE = 11005;
    private const uint IP_REQ_TIMED_OUT = 11010;
    private const uint IP_TTL_EXPIRED_TRANSIT = 11013;
    private const uint IP_GENERAL_FAILURE = 11050;
    private const int ERROR_INVALID_PARAMETER = 87;
    private const int ERROR_ACCESS_DENIED = 5;
    private const int ERROR_NETWORK_UNREACHABLE = 1231;
    private const int ERROR_HOST_UNREACHABLE = 1232;
    private const int WSAEADDRNOTAVAIL = 10049;

    private static readonly byte[] Payload = Enumerable.Range(0, 32).Select(i => (byte)('a' + (i % 23))).ToArray();

    private readonly IPAddress _source;
    private readonly string _name;

    public WindowsSourceBoundIcmpProbe(AdapterInfo adapter)
    {
        _source = adapter.IPv4Address ?? throw new NotSupportedException($"Adapter {adapter.Name} has no IPv4 address.");
        _name = adapter.Name;
    }

    public string RoutingDescription => $"IcmpSendEcho2Ex with source address {_source} ({_name})";

    public static BindingCheck Check(AdapterInfo adapter)
    {
        if (adapter.IPv4Address is null)
        {
            return BindingCheck.Unsupported($"Adapter {adapter.Name} has no IPv4 address, so probes cannot be bound to it.");
        }

        try
        {
            var handle = IcmpCreateFile();
            if (handle == IntPtr.Zero || handle == new IntPtr(-1))
            {
                return BindingCheck.Unsupported($"Windows ICMP API is unavailable (error {Marshal.GetLastPInvokeError()}).");
            }

            IcmpCloseHandle(handle);
        }
        catch (DllNotFoundException)
        {
            return BindingCheck.Unsupported("Windows ICMP API (iphlpapi.dll) is unavailable.");
        }

        return BindingCheck.Supported($"Probes are sent from {adapter.IPv4Address} on {adapter.Name}.");
    }

    public async Task<ProbeReply> SendAsync(IPAddress destination, TimeSpan timeout, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var source = _source;
        // The native call is synchronous and cannot be cancelled; it is bounded by the timeout. On Stop we stop
        // waiting for it, and it finishes (and frees its handle) in the background.
        var work = Task.Run(() => SendBlocking(source, destination, (uint)Math.Max(1, timeout.TotalMilliseconds)), CancellationToken.None);
        return await work.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
    }

    private static ProbeReply SendBlocking(IPAddress source, IPAddress destination, uint timeoutMs)
    {
        var handle = IcmpCreateFile();
        if (handle == IntPtr.Zero || handle == new IntPtr(-1))
        {
            return ProbeReply.Fail(ProbeStatus.Error, $"IcmpCreateFile failed (error {Marshal.GetLastPInvokeError()}).");
        }

        var replySize = 256 + Payload.Length;
        var reply = Marshal.AllocHGlobal(replySize);
        try
        {
            var sw = Stopwatch.StartNew();
            var count = IcmpSendEcho2Ex(handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                ToIpAddr(source), ToIpAddr(destination), Payload, (ushort)Payload.Length, IntPtr.Zero,
                reply, (uint)replySize, timeoutMs);
            var elapsed = sw.Elapsed.TotalMilliseconds;

            if (count == 0)
            {
                var error = Marshal.GetLastPInvokeError();
                return MapError(error);
            }

            // ICMP_ECHO_REPLY: Address (4), Status (4), RoundTripTime (4) — identical offsets on x86/x64/ARM64.
            var from = new IPAddress(BitConverter.GetBytes(unchecked((uint)Marshal.ReadInt32(reply, 0))));
            var status = unchecked((uint)Marshal.ReadInt32(reply, 4));
            return status switch
            {
                IP_SUCCESS => ProbeReply.Ok(elapsed, from),
                IP_REQ_TIMED_OUT => ProbeReply.Fail(ProbeStatus.Timeout, "Request timed out."),
                IP_TTL_EXPIRED_TRANSIT => ProbeReply.Fail(ProbeStatus.TtlExpired, $"TTL expired in transit (reported by {from}).", from),
                IP_DEST_NET_UNREACHABLE or IP_DEST_HOST_UNREACHABLE or IP_DEST_PROT_UNREACHABLE or IP_DEST_PORT_UNREACHABLE
                    => ProbeReply.Fail(ProbeStatus.DestinationUnreachable, $"Destination unreachable (status {status}, reported by {from}).", from),
                IP_GENERAL_FAILURE => ProbeReply.Fail(ProbeStatus.LocalAdapterFailure, "General failure sending from the selected adapter (status 11050)."),
                _ => ProbeReply.Fail(ProbeStatus.Error, $"ICMP status {status}."),
            };
        }
        finally
        {
            Marshal.FreeHGlobal(reply);
            IcmpCloseHandle(handle);
        }
    }

    private static ProbeReply MapError(int error) => (uint)error switch
    {
        IP_REQ_TIMED_OUT => ProbeReply.Fail(ProbeStatus.Timeout, "Request timed out."),
        IP_DEST_NET_UNREACHABLE or IP_DEST_HOST_UNREACHABLE or IP_DEST_PROT_UNREACHABLE or IP_DEST_PORT_UNREACHABLE or ERROR_HOST_UNREACHABLE
            => ProbeReply.Fail(ProbeStatus.DestinationUnreachable, $"Destination unreachable (error {error})."),
        IP_TTL_EXPIRED_TRANSIT => ProbeReply.Fail(ProbeStatus.TtlExpired, "TTL expired in transit."),
        ERROR_NETWORK_UNREACHABLE => ProbeReply.Fail(ProbeStatus.LocalAdapterFailure, "No route from the selected adapter to the destination (error 1231)."),
        IP_GENERAL_FAILURE or ERROR_INVALID_PARAMETER or WSAEADDRNOTAVAIL
            => ProbeReply.Fail(ProbeStatus.LocalAdapterFailure, $"The selected adapter could not send (error {error}). It may be disconnected or its address may have changed."),
        ERROR_ACCESS_DENIED => ProbeReply.Fail(ProbeStatus.PermissionDenied, "Windows denied permission to send ICMP (error 5)."),
        _ => ProbeReply.Fail(ProbeStatus.Error, $"IcmpSendEcho2Ex failed (error {error})."),
    };

    /// <summary>IPAddr is the address in network byte order, i.e. the address bytes as they appear in memory.</summary>
    private static uint ToIpAddr(IPAddress address) => BitConverter.ToUInt32(address.GetAddressBytes(), 0);

    [LibraryImport("iphlpapi.dll", SetLastError = true)]
    private static partial IntPtr IcmpCreateFile();

    [LibraryImport("iphlpapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IcmpCloseHandle(IntPtr handle);

    [LibraryImport("iphlpapi.dll", SetLastError = true)]
    private static partial uint IcmpSendEcho2Ex(
        IntPtr icmpHandle, IntPtr hEvent, IntPtr apcRoutine, IntPtr apcContext,
        uint sourceAddress, uint destinationAddress,
        byte[] requestData, ushort requestSize, IntPtr requestOptions,
        IntPtr replyBuffer, uint replySize, uint timeout);
}
