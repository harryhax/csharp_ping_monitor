namespace NetworkDiagnostics.Core.Models;

/// <summary>One recorded measurement. Persisted to CSV.</summary>
public sealed record PingResult
{
    public required Guid SessionId { get; init; }

    /// <summary>1-based sequence number within the target's monitor.</summary>
    public required long Sequence { get; init; }

    /// <summary>UTC wall-clock time at which the probe was sent.</summary>
    public required DateTimeOffset TimestampUtc { get; init; }

    /// <summary>Monotonic offset from session start at which the probe was scheduled.</summary>
    public required double ScheduledOffsetMs { get; init; }

    /// <summary>Monotonic offset from session start at which the probe was actually sent.</summary>
    public required double SentOffsetMs { get; init; }

    public required string AdapterId { get; init; }

    public string? AdapterLocalIp { get; init; }

    public required TargetType TargetType { get; init; }

    /// <summary>The configured hostname or IP.</summary>
    public required string Target { get; init; }

    /// <summary>The IPv4 address actually probed, when resolution succeeded.</summary>
    public string? ResolvedIp { get; init; }

    /// <summary>Round-trip time; null unless <see cref="Status"/> is Success.</summary>
    public double? RoundTripMs { get; init; }

    public required ProbeStatus Status { get; init; }

    public string? ErrorDetail { get; init; }

    public bool IsSuccess => Status == ProbeStatus.Success;

    /// <summary>True when the probe counts towards completed requests in statistics.</summary>
    public bool IsCompleted => Status != ProbeStatus.Cancelled;
}
