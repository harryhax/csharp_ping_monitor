using System.Net;

namespace NetworkDiagnostics.Core.Models;

/// <summary>Fully validated inputs for a diagnostic session.</summary>
public sealed record SessionOptions
{
    /// <summary>Adapter used for the session. For Automatic mode this is the detected system default adapter (if any).</summary>
    public required AdapterSelection Adapter { get; init; }

    public required string InternalTarget { get; init; }

    /// <summary>True when the internal target came from the detected gateway rather than a manual override.</summary>
    public required bool InternalTargetIsGateway { get; init; }

    public required string ExternalTarget { get; init; }

    public required TimeSpan Duration { get; init; }

    public required TimeSpan Interval { get; init; }

    public required TimeSpan Timeout { get; init; }

    public StatusThresholds Thresholds { get; init; } = new();
}

/// <summary>The user's adapter choice resolved against the current adapter list.</summary>
/// <param name="IsAutomatic">True for "Automatic (System Default)": probes follow operating-system routing.</param>
/// <param name="Adapter">The explicit adapter, or the detected default adapter in automatic mode (may be null).</param>
public sealed record AdapterSelection(bool IsAutomatic, AdapterInfo? Adapter)
{
    public const string AutomaticId = "automatic";

    public string Id => IsAutomatic ? AutomaticId : Adapter!.Id;

    public IPAddress? LocalAddress => Adapter?.IPv4Address;

    public IPAddress? Gateway => Adapter?.IPv4Gateway;

    public string DisplayName => IsAutomatic
        ? Adapter is null ? "Automatic (System Default)" : $"Automatic (System Default: {Adapter.Name})"
        : Adapter!.Name;
}
