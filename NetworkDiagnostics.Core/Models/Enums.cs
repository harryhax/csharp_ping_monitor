namespace NetworkDiagnostics.Core.Models;

/// <summary>Broad adapter category shown to the user.</summary>
public enum AdapterKind
{
    WiFi,
    Ethernet,
    Other,
}

/// <summary>Which of the two monitored destinations a measurement belongs to.</summary>
public enum TargetType
{
    Internal,
    External,
}

/// <summary>Outcome of a single probe.</summary>
public enum ProbeStatus
{
    /// <summary>An echo reply was received.</summary>
    Success,

    /// <summary>No reply arrived before the timeout.</summary>
    Timeout,

    /// <summary>A router or the local stack reported the destination (network/host/port) as unreachable.</summary>
    DestinationUnreachable,

    /// <summary>The TTL expired before the packet reached the destination.</summary>
    TtlExpired,

    /// <summary>The selected local adapter could not send (down, no address, address removed).</summary>
    LocalAdapterFailure,

    /// <summary>The destination hostname could not be resolved.</summary>
    DnsFailure,

    /// <summary>The operating system refused permission to send ICMP.</summary>
    PermissionDenied,

    /// <summary>The probe was abandoned because the session was stopped. Excluded from statistics.</summary>
    Cancelled,

    /// <summary>Any other failure.</summary>
    Error,
}

/// <summary>Traffic-light level for a destination.</summary>
public enum HealthLevel
{
    /// <summary>Not enough data yet.</summary>
    Unknown,
    Green,
    Yellow,
    Red,
}

/// <summary>Why a session ended.</summary>
public enum SessionEndReason
{
    /// <summary>The session has not ended.</summary>
    None,

    /// <summary>The configured duration elapsed.</summary>
    Completed,

    /// <summary>The user pressed Stop.</summary>
    Stopped,

    /// <summary>An unexpected error ended the session.</summary>
    Failed,
}

public enum SessionEventKind
{
    SessionStarted,
    SessionEnded,
    AdapterDisconnected,
    AdapterReconnected,
    AdapterAddressChanged,
    AdapterMissing,
    DefaultAdapterChanged,
    TargetResolved,
    Warning,
}
