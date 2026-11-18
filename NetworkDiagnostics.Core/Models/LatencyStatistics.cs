namespace NetworkDiagnostics.Core.Models;

/// <summary>Immutable statistics snapshot for one destination.</summary>
public sealed record LatencyStatistics
{
    public static readonly LatencyStatistics Empty = new();

    /// <summary>Probes sent, including any still in flight and any cancelled by Stop.</summary>
    public long Sent { get; init; }

    /// <summary>Probes that produced a definitive outcome (success or failure).</summary>
    public long Completed { get; init; }

    public long Received { get; init; }

    public long Failed { get; init; }

    /// <summary>Probes abandoned because the session was stopped. Not counted as completed.</summary>
    public long Cancelled { get; init; }

    /// <summary>Probes currently awaiting a reply.</summary>
    public long InFlight { get; init; }

    /// <summary>Failed / Completed × 100, or null when nothing has completed.</summary>
    public double? PacketLossPercent { get; init; }

    public double? MinMs { get; init; }

    public double? MaxMs { get; init; }

    public double? AverageMs { get; init; }

    public double? LatestMs { get; init; }

    public ProbeStatus? LatestStatus { get; init; }

    public int ConsecutiveFailures { get; init; }
}
