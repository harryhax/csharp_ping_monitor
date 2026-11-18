namespace NetworkDiagnostics.Core.Monitoring;

/// <summary>
/// Fixed-grid scheduling. Probes are planned at slot × interval from session start. If a probe overruns
/// (e.g. a timeout longer than the interval), missed slots are skipped rather than queued, so there is never
/// a backlog or a burst of catch-up probes.
/// </summary>
public static class ProbeScheduler
{
    public static TimeSpan SlotTime(long slot, TimeSpan interval) => TimeSpan.FromTicks(checked(interval.Ticks * slot));

    /// <summary>Returns the next slot to use after <paramref name="currentSlot"/> given the current elapsed time.</summary>
    public static long NextSlot(long currentSlot, TimeSpan elapsed, TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval));
        }

        var earliest = (long)Math.Ceiling(elapsed.Ticks / (double)interval.Ticks);
        return Math.Max(currentSlot + 1, earliest);
    }
}
