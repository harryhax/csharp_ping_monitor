using NetworkDiagnostics.Core.Models;

namespace NetworkDiagnostics.Core.Export;

/// <summary>Shape of session.json.</summary>
public sealed record SessionReport(
    Guid SessionId,
    string ApplicationVersion,
    DateTimeOffset StartedUtc,
    DateTimeOffset? EndedUtc,
    SessionEndReason EndReason,
    double RequestedDurationSeconds,
    double? ActualDurationSeconds,
    AdapterReport Adapter,
    TargetsReport Targets,
    int PingIntervalMs,
    int PingTimeoutMs,
    StatusThresholds StatusThresholds,
    PlatformDescription Platform,
    IReadOnlyList<SessionEvent> Events);

public sealed record AdapterReport(
    string SelectionMode,
    string? Id,
    string? Name,
    string? Type,
    string? LocalIPv4,
    string? DefaultGateway,
    string? StatusAtStart,
    string Routing);

public sealed record TargetsReport(TargetReport Internal, TargetReport External);

public sealed record TargetReport(string Target, string Source);

/// <summary>Shape of summary.json.</summary>
public sealed record SummaryReport(
    Guid SessionId,
    DateTimeOffset GeneratedUtc,
    string PacketLossFormula,
    TargetSummary Internal,
    TargetSummary External);

public sealed record TargetSummary(
    string Target,
    string? ResolvedIp,
    long Sent,
    long Completed,
    long Received,
    long Failed,
    long Cancelled,
    double? PacketLossPercent,
    double? MinLatencyMs,
    double? AverageLatencyMs,
    double? MaxLatencyMs,
    IReadOnlyDictionary<string, long> StatusCounts);
