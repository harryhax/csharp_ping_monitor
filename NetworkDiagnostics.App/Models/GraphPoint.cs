namespace NetworkDiagnostics.App.Models;

/// <summary>One point on the latency graph. A null latency marks a failed probe.</summary>
public readonly record struct GraphPoint(double TimeSeconds, double? LatencyMs);
