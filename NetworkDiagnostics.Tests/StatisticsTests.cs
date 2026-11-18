using NetworkDiagnostics.Core.Models;
using NetworkDiagnostics.Core.Statistics;

namespace NetworkDiagnostics.Tests;

public class StatisticsTests
{
    [Fact]
    public void Empty_HasNullLossAndLatency()
    {
        var s = new StatisticsAccumulator().Snapshot();
        Assert.Equal(0, s.Sent);
        Assert.Null(s.PacketLossPercent);
        Assert.Null(s.AverageMs);
        Assert.Null(s.MinMs);
        Assert.Null(s.MaxMs);
    }

    [Fact]
    public void PacketLoss_IsFailedOverCompleted()
    {
        var acc = new StatisticsAccumulator();
        foreach (var status in new[] { ProbeStatus.Success, ProbeStatus.Timeout, ProbeStatus.Success, ProbeStatus.DestinationUnreachable })
        {
            acc.MarkSent();
            acc.Record(status, status == ProbeStatus.Success ? 10 : null);
        }

        var s = acc.Snapshot();
        Assert.Equal(4, s.Sent);
        Assert.Equal(4, s.Completed);
        Assert.Equal(2, s.Received);
        Assert.Equal(2, s.Failed);
        Assert.Equal(50.0, s.PacketLossPercent);
    }

    [Fact]
    public void MinAverageMax_UseSuccessfulRepliesOnly()
    {
        var acc = new StatisticsAccumulator();
        (ProbeStatus Status, double? Rtt)[] data =
        [
            (ProbeStatus.Success, 12.5), (ProbeStatus.Timeout, null), (ProbeStatus.Success, 7.5),
            (ProbeStatus.Success, 20.0), (ProbeStatus.Error, null),
        ];
        foreach (var (status, rtt) in data)
        {
            acc.MarkSent();
            acc.Record(status, rtt);
        }

        var s = acc.Snapshot();
        Assert.Equal(7.5, s.MinMs);
        Assert.Equal(20.0, s.MaxMs);
        Assert.Equal(40.0 / 3, s.AverageMs!.Value, 9);
        Assert.Equal(40.0, s.PacketLossPercent);
    }

    [Fact]
    public void Timeouts_AreNeverCountedAsZeroLatency()
    {
        var acc = new StatisticsAccumulator();
        acc.MarkSent();
        acc.Record(ProbeStatus.Success, 30);
        acc.MarkSent();
        acc.Record(ProbeStatus.Timeout, 0); // even if a probe wrongly supplies 0 ms

        var s = acc.Snapshot();
        Assert.Equal(30, s.MinMs);
        Assert.Equal(30, s.AverageMs);
        Assert.Null(s.LatestMs);
        Assert.Equal(1, s.Failed);
    }

    [Fact]
    public void SuccessWithoutRtt_IsTreatedAsFailure()
    {
        var acc = new StatisticsAccumulator();
        acc.MarkSent();
        acc.Record(ProbeStatus.Success, null);
        Assert.Equal(1, acc.Snapshot().Failed);
        Assert.Null(acc.Snapshot().AverageMs);
    }

    [Fact]
    public void InFlight_IsSeparateFromCompleted()
    {
        var acc = new StatisticsAccumulator();
        acc.MarkSent();
        acc.Record(ProbeStatus.Success, 5);
        acc.MarkSent(); // still waiting

        var s = acc.Snapshot();
        Assert.Equal(2, s.Sent);
        Assert.Equal(1, s.Completed);
        Assert.Equal(1, s.InFlight);
        Assert.Equal(0.0, s.PacketLossPercent);
    }

    [Fact]
    public void Cancelled_IsExcludedFromCompletedAndLoss()
    {
        var acc = new StatisticsAccumulator();
        acc.MarkSent();
        acc.Record(ProbeStatus.Success, 5);
        acc.MarkSent();
        acc.Record(ProbeStatus.Cancelled, null);

        var s = acc.Snapshot();
        Assert.Equal(2, s.Sent);
        Assert.Equal(1, s.Completed);
        Assert.Equal(1, s.Cancelled);
        Assert.Equal(0, s.InFlight);
        Assert.Equal(0.0, s.PacketLossPercent);
    }

    [Fact]
    public void ConsecutiveFailures_ResetOnSuccess()
    {
        var acc = new StatisticsAccumulator();
        foreach (var st in new[] { ProbeStatus.Timeout, ProbeStatus.Timeout, ProbeStatus.Success, ProbeStatus.Timeout })
        {
            acc.MarkSent();
            acc.Record(st, st == ProbeStatus.Success ? 1 : null);
        }

        Assert.Equal(1, acc.Snapshot().ConsecutiveFailures);
    }

    [Fact]
    public void Window_KeepsOnlyMostRecent()
    {
        var acc = new StatisticsAccumulator(windowSize: 3);
        for (var i = 1; i <= 5; i++)
        {
            acc.MarkSent();
            acc.Record(ProbeStatus.Success, i);
        }

        Assert.Equal([3.0, 4.0, 5.0], acc.RecentWindow().Select(w => w.RoundTripMs!.Value));
    }

    [Fact]
    public void Compute_MatchesIncremental()
    {
        var results = new[]
        {
            TestData.Result(ProbeStatus.Success, 10, 1),
            TestData.Result(ProbeStatus.Timeout, null, 2),
            TestData.Result(ProbeStatus.Cancelled, null, 3),
        };
        var s = StatisticsAccumulator.Compute(results);
        Assert.Equal(3, s.Sent);
        Assert.Equal(2, s.Completed);
        Assert.Equal(50.0, s.PacketLossPercent);
        Assert.Equal(1, s.Cancelled);
    }
}

public class HealthEvaluatorTests
{
    private static readonly StatusThresholds T = new();

    private static (LatencyStatistics, IReadOnlyList<WindowSample>) Feed(params (ProbeStatus, double?)[] data)
    {
        var acc = new StatisticsAccumulator(T.WindowSize);
        foreach (var (s, r) in data)
        {
            acc.MarkSent();
            acc.Record(s, r);
        }

        return (acc.Snapshot(), acc.RecentWindow());
    }

    [Fact]
    public void NoData_IsUnknown()
    {
        var (s, w) = Feed();
        Assert.Equal(HealthLevel.Unknown, HealthEvaluator.Evaluate(TargetType.Internal, s, w, T).Level);
    }

    [Fact]
    public void AllFastReplies_IsGreen()
    {
        var (s, w) = Feed(Enumerable.Repeat((ProbeStatus.Success, (double?)3.0), 10).ToArray());
        var h = HealthEvaluator.Evaluate(TargetType.Internal, s, w, T);
        Assert.Equal(HealthLevel.Green, h.Level);
        Assert.False(string.IsNullOrEmpty(h.Label));
    }

    [Fact]
    public void ElevatedLatency_IsYellow()
    {
        var (s, w) = Feed(Enumerable.Repeat((ProbeStatus.Success, (double?)250.0), 10).ToArray());
        Assert.Equal(HealthLevel.Yellow, HealthEvaluator.Evaluate(TargetType.External, s, w, T).Level);
    }

    [Fact]
    public void LatencyThreshold_DiffersByTarget()
    {
        var (s, w) = Feed(Enumerable.Repeat((ProbeStatus.Success, (double?)60.0), 10).ToArray());
        Assert.Equal(HealthLevel.Yellow, HealthEvaluator.Evaluate(TargetType.Internal, s, w, T).Level);
        Assert.Equal(HealthLevel.Green, HealthEvaluator.Evaluate(TargetType.External, s, w, T).Level);
    }

    [Fact]
    public void IsolatedFailure_IsYellow()
    {
        var data = Enumerable.Repeat((ProbeStatus.Success, (double?)3.0), 19).Append((ProbeStatus.Timeout, null)).Append((ProbeStatus.Success, 3.0)).ToArray();
        var (s, w) = Feed(data);
        Assert.Equal(HealthLevel.Yellow, HealthEvaluator.Evaluate(TargetType.Internal, s, w, T).Level);
    }

    [Fact]
    public void ConsecutiveFailures_IsRed()
    {
        var (s, w) = Feed((ProbeStatus.Success, 3), (ProbeStatus.Timeout, null), (ProbeStatus.Timeout, null), (ProbeStatus.Timeout, null));
        Assert.Equal(HealthLevel.Red, HealthEvaluator.Evaluate(TargetType.Internal, s, w, T).Level);
    }

    [Fact]
    public void SustainedLoss_IsRed()
    {
        // 1 in 4 failing, never 3 in a row: 25 % loss ≥ 20 % threshold.
        var data = Enumerable.Range(0, 20).Select(i => i % 4 == 0 ? (ProbeStatus.Timeout, (double?)null) : (ProbeStatus.Success, 3.0)).ToArray();
        var (s, w) = Feed(data);
        Assert.Equal(HealthLevel.Red, HealthEvaluator.Evaluate(TargetType.Internal, s, w, T).Level);
    }

    [Fact]
    public void FirstProbeFailing_IsNotImmediatelyRed()
    {
        var (s, w) = Feed((ProbeStatus.Timeout, null));
        Assert.Equal(HealthLevel.Yellow, HealthEvaluator.Evaluate(TargetType.Internal, s, w, T).Level);
    }
}

public class LocalNetworkAccessHeuristicTests
{
    private static LatencyStatistics S(long completed, long received) => new() { Sent = completed, Completed = completed, Received = received, Failed = completed - received };

    [Fact]
    public void InternalAllFailing_ExternalReplying_IsFlagged() =>
        Assert.True(NetworkDiagnostics.Core.Monitoring.LocalNetworkAccessHeuristic.LooksBlocked(S(5, 0), S(5, 5)));

    [Theory]
    [InlineData(4, 0, 5)]   // too early to tell
    [InlineData(10, 1, 10)] // router replied at least once
    [InlineData(10, 0, 0)]  // everything down: a genuine outage, not a permission issue
    public void OtherPatterns_AreNotFlagged(long internalCompleted, long internalReceived, long externalReceived) =>
        Assert.False(NetworkDiagnostics.Core.Monitoring.LocalNetworkAccessHeuristic.LooksBlocked(S(internalCompleted, internalReceived), S(10, externalReceived)));
}
