using NetworkDiagnostics.Core.Models;

namespace NetworkDiagnostics.Core.Statistics;

/// <summary>A completed probe as seen by the health evaluator.</summary>
public readonly record struct WindowSample(bool Success, double? RoundTripMs);

/// <summary>
/// Thread-safe running statistics for one destination.
/// Failed and cancelled probes never contribute latency values.
/// </summary>
public sealed class StatisticsAccumulator
{
    private readonly object _gate = new();
    private readonly Queue<WindowSample> _window = new();
    private readonly int _windowSize;

    private long _sent;
    private long _received;
    private long _failed;
    private long _cancelled;
    private double _sumMs;
    private double? _minMs;
    private double? _maxMs;
    private double? _latestMs;
    private ProbeStatus? _latestStatus;
    private int _consecutiveFailures;

    public StatisticsAccumulator(int windowSize = 20)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(windowSize, 1);
        _windowSize = windowSize;
    }

    /// <summary>Records that a probe has been sent and is now in flight.</summary>
    public void MarkSent()
    {
        lock (_gate)
        {
            _sent++;
        }
    }

    /// <summary>Records the outcome of a probe previously passed to <see cref="MarkSent"/>.</summary>
    public void Record(ProbeStatus status, double? roundTripMs)
    {
        lock (_gate)
        {
            if (status == ProbeStatus.Cancelled)
            {
                _cancelled++;
                return;
            }

            _latestStatus = status;

            if (status == ProbeStatus.Success && roundTripMs is { } rtt && double.IsFinite(rtt) && rtt >= 0)
            {
                _received++;
                _sumMs += rtt;
                _minMs = _minMs is null ? rtt : Math.Min(_minMs.Value, rtt);
                _maxMs = _maxMs is null ? rtt : Math.Max(_maxMs.Value, rtt);
                _latestMs = rtt;
                _consecutiveFailures = 0;
                Push(new WindowSample(true, rtt));
            }
            else
            {
                // A "success" without a usable RTT is treated as a failure rather than as 0 ms.
                _failed++;
                _latestMs = null;
                _consecutiveFailures++;
                Push(new WindowSample(false, null));
            }
        }
    }

    public void Record(PingResult result) => Record(result.Status, result.RoundTripMs);

    public LatencyStatistics Snapshot()
    {
        lock (_gate)
        {
            var completed = _received + _failed;
            return new LatencyStatistics
            {
                Sent = _sent,
                Completed = completed,
                Received = _received,
                Failed = _failed,
                Cancelled = _cancelled,
                InFlight = Math.Max(0, _sent - completed - _cancelled),
                PacketLossPercent = completed == 0 ? null : _failed * 100.0 / completed,
                MinMs = _minMs,
                MaxMs = _maxMs,
                AverageMs = _received == 0 ? null : _sumMs / _received,
                LatestMs = _latestMs,
                LatestStatus = _latestStatus,
                ConsecutiveFailures = _consecutiveFailures,
            };
        }
    }

    /// <summary>Most recent completed probes, oldest first.</summary>
    public IReadOnlyList<WindowSample> RecentWindow()
    {
        lock (_gate)
        {
            return _window.ToArray();
        }
    }

    private void Push(WindowSample sample)
    {
        _window.Enqueue(sample);
        while (_window.Count > _windowSize)
        {
            _window.Dequeue();
        }
    }

    /// <summary>Computes statistics for a finished list of results (used for export and verification).</summary>
    public static LatencyStatistics Compute(IEnumerable<PingResult> results)
    {
        var acc = new StatisticsAccumulator();
        foreach (var r in results)
        {
            acc.MarkSent();
            acc.Record(r);
        }

        return acc.Snapshot();
    }
}
