namespace NetworkDiagnostics.Core.Models;

/// <summary>
/// A completed or in-progress diagnostic session. Lists are only mutated by the session runner; readers
/// should use the snapshot methods.
/// </summary>
public sealed class DiagnosticSession
{
    private readonly object _gate = new();
    private readonly List<PingResult> _internal = [];
    private readonly List<PingResult> _external = [];
    private readonly List<SessionEvent> _events = [];

    public DiagnosticSession(Guid id, SessionOptions options, DateTimeOffset startedUtc, PlatformDescription platform, string appVersion)
    {
        Id = id;
        Options = options;
        StartedUtc = startedUtc;
        Platform = platform;
        AppVersion = appVersion;
    }

    public Guid Id { get; }

    public SessionOptions Options { get; }

    public DateTimeOffset StartedUtc { get; }

    public DateTimeOffset? EndedUtc { get; private set; }

    /// <summary>Monotonic duration actually measured.</summary>
    public TimeSpan? ActualDuration { get; private set; }

    public SessionEndReason EndReason { get; private set; }

    public string? FailureMessage { get; private set; }

    public PlatformDescription Platform { get; }

    public string AppVersion { get; }

    /// <summary>How probes were routed, e.g. "ICMP socket bound to en0 (IP_BOUND_IF, index 14)".</summary>
    public string RoutingDescription { get; internal set; } = string.Empty;

    public LatencyStatistics InternalStatistics { get; internal set; } = LatencyStatistics.Empty;

    public LatencyStatistics ExternalStatistics { get; internal set; } = LatencyStatistics.Empty;

    public bool IsFinished => EndReason != SessionEndReason.None;

    public IReadOnlyList<PingResult> GetResults(TargetType target)
    {
        lock (_gate)
        {
            return (target == TargetType.Internal ? _internal : _external).ToArray();
        }
    }

    public IReadOnlyList<SessionEvent> GetEvents()
    {
        lock (_gate)
        {
            return _events.ToArray();
        }
    }

    internal void AddResult(PingResult result)
    {
        lock (_gate)
        {
            (result.TargetType == TargetType.Internal ? _internal : _external).Add(result);
        }
    }

    internal void AddEvent(SessionEvent evt)
    {
        lock (_gate)
        {
            _events.Add(evt);
        }
    }

    internal void Finish(SessionEndReason reason, DateTimeOffset endedUtc, TimeSpan actualDuration, string? failureMessage = null)
    {
        EndReason = reason;
        EndedUtc = endedUtc;
        ActualDuration = actualDuration;
        FailureMessage = failureMessage;
    }
}

/// <summary>Non-identifying description of the host platform.</summary>
public sealed record PlatformDescription(string OperatingSystem, string OsVersion, string Architecture, string Runtime)
{
    public static PlatformDescription Current() => new(
        OperatingSystem: System.OperatingSystem.IsWindows() ? "Windows" : System.OperatingSystem.IsMacOS() ? "macOS" : System.OperatingSystem.IsLinux() ? "Linux" : "Other",
        OsVersion: System.Runtime.InteropServices.RuntimeInformation.OSDescription,
        Architecture: System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
        Runtime: System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
}
