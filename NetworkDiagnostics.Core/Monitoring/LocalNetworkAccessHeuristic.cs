using NetworkDiagnostics.Core.Models;

namespace NetworkDiagnostics.Core.Monitoring;

/// <summary>
/// macOS 15+ blocks traffic to local-network devices for apps the user has not granted "Local Network" access,
/// and the packets are dropped silently. That looks exactly like a dead router. This heuristic flags the
/// tell-tale pattern: every internal probe failing while the external target replies.
/// </summary>
public static class LocalNetworkAccessHeuristic
{
    public const int MinimumInternalProbes = 5;

    public const string MacOSMessage =
        "Your router is not replying, but the internet is. On macOS this usually means the app has not been allowed to " +
        "access the local network: open System Settings > Privacy & Security > Local Network, enable Network Diagnostics, " +
        "then restart the test. Results gathered while access was blocked do not reflect the router's real behaviour.";

    public static bool LooksBlocked(LatencyStatistics @internal, LatencyStatistics external) =>
        @internal.Completed >= MinimumInternalProbes && @internal.Received == 0 && external.Received > 0;
}
