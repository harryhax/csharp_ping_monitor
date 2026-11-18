namespace NetworkDiagnostics.Core.Models;

/// <summary>Something noteworthy that happened during a session (adapter loss, DNS resolution…).</summary>
public sealed record SessionEvent(DateTimeOffset TimestampUtc, double OffsetMs, SessionEventKind Kind, string Message);
