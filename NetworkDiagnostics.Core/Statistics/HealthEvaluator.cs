using System.Globalization;
using NetworkDiagnostics.Core.Models;

namespace NetworkDiagnostics.Core.Statistics;

/// <summary>
/// Maps recent measurements to a preliminary traffic-light status. These are measurement statuses, not a
/// diagnosis: a slow gateway reply, for example, can simply mean the router de-prioritises ICMP.
/// </summary>
public static class HealthEvaluator
{
    /// <summary>Loss-percentage rule only applies once the window holds at least this many samples.</summary>
    public const int MinimumSamplesForLossRule = 5;

    public static HealthAssessment Evaluate(
        TargetType target,
        LatencyStatistics stats,
        IReadOnlyList<WindowSample> window,
        StatusThresholds thresholds)
    {
        if (window.Count == 0)
        {
            return stats.InFlight > 0
                ? new HealthAssessment(HealthLevel.Unknown, "Measuring", "Waiting for the first reply.")
                : HealthAssessment.Unknown;
        }

        if (stats.ConsecutiveFailures >= thresholds.RedConsecutiveFailures)
        {
            return new HealthAssessment(
                HealthLevel.Red,
                "Not responding",
                string.Create(CultureInfo.InvariantCulture, $"{stats.ConsecutiveFailures} consecutive failed requests."));
        }

        var failures = window.Count(s => !s.Success);
        var lossPercent = failures * 100.0 / window.Count;

        if (window.Count >= MinimumSamplesForLossRule && lossPercent >= thresholds.RedLossPercent)
        {
            return new HealthAssessment(
                HealthLevel.Red,
                "Heavy packet loss",
                string.Create(CultureInfo.InvariantCulture, $"{lossPercent:0}% of the last {window.Count} requests failed."));
        }

        if (failures > 0)
        {
            return new HealthAssessment(
                HealthLevel.Yellow,
                "Intermittent failures",
                string.Create(CultureInfo.InvariantCulture, $"{failures} of the last {window.Count} requests failed."));
        }

        var limit = target == TargetType.Internal ? thresholds.InternalElevatedLatencyMs : thresholds.ExternalElevatedLatencyMs;
        var average = window.Where(s => s.RoundTripMs.HasValue).Average(s => s.RoundTripMs!.Value);
        if (average > limit)
        {
            return new HealthAssessment(
                HealthLevel.Yellow,
                "Elevated latency",
                string.Create(CultureInfo.InvariantCulture, $"Recent average {average:0.0} ms exceeds {limit:0} ms."));
        }

        return new HealthAssessment(HealthLevel.Green, "Responding normally", "All recent requests succeeded.");
    }
}
