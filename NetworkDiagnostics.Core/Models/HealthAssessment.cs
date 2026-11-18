namespace NetworkDiagnostics.Core.Models;

/// <summary>Preliminary traffic-light status. This is a measurement status, not a diagnosis.</summary>
public sealed record HealthAssessment(HealthLevel Level, string Label, string Reason)
{
    public static readonly HealthAssessment Unknown = new(HealthLevel.Unknown, "Waiting", "No measurements yet.");
}
