namespace NetworkDiagnostics.Core.Models;

/// <summary>User configuration persisted between launches.</summary>
public sealed class AppSettings
{
    public const string DefaultExternalTarget = "8.8.8.8";
    public const int DefaultDurationMinutes = 10;
    public const int DefaultIntervalMs = 1000;
    public const int DefaultTimeoutMs = 2000;

    public static readonly IReadOnlyList<int> AllowedDurationsMinutes = [1, 5, 10, 30];

    /// <summary>Settings schema version, for forward compatibility.</summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Selected adapter id; null means Automatic (System Default).</summary>
    public string? SelectedAdapterId { get; set; }

    /// <summary>When true, <see cref="InternalTargetOverride"/> replaces the detected gateway.</summary>
    public bool UseInternalTargetOverride { get; set; }

    public string? InternalTargetOverride { get; set; }

    public string ExternalTarget { get; set; } = DefaultExternalTarget;

    public int DurationMinutes { get; set; } = DefaultDurationMinutes;

    /// <summary>Delay between probe start times, per destination.</summary>
    public int PingIntervalMs { get; set; } = DefaultIntervalMs;

    /// <summary>How long to wait for each reply before recording a timeout.</summary>
    public int PingTimeoutMs { get; set; } = DefaultTimeoutMs;

    public StatusThresholds Thresholds { get; set; } = new();

    public AppSettings Clone()
    {
        var copy = (AppSettings)MemberwiseClone();
        copy.Thresholds = Thresholds.Clone();
        return copy;
    }
}

/// <summary>
/// Central definition of the traffic-light thresholds. Values are preliminary and deliberately simple.
/// </summary>
public sealed class StatusThresholds
{
    /// <summary>Number of most recent completed probes considered.</summary>
    public int WindowSize { get; set; } = 20;

    /// <summary>Average latency (ms) over the window above which the internal target is Yellow.</summary>
    public double InternalElevatedLatencyMs { get; set; } = 30;

    /// <summary>Average latency (ms) over the window above which the external target is Yellow.</summary>
    public double ExternalElevatedLatencyMs { get; set; } = 100;

    /// <summary>This many consecutive failures turns the status Red.</summary>
    public int RedConsecutiveFailures { get; set; } = 3;

    /// <summary>Packet loss (%) within the window at or above which the status is Red.</summary>
    public double RedLossPercent { get; set; } = 20;

    public StatusThresholds Clone() => (StatusThresholds)MemberwiseClone();
}
