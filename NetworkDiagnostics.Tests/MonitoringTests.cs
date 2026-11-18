using System.Net;
using NetworkDiagnostics.Core.Interfaces;
using NetworkDiagnostics.Core.Models;
using NetworkDiagnostics.Core.Monitoring;

namespace NetworkDiagnostics.Tests;

public class ProbeSchedulerTests
{
    private static readonly TimeSpan I = TimeSpan.FromSeconds(1);

    [Fact]
    public void OnTime_AdvancesOneSlot() =>
        Assert.Equal(1, ProbeScheduler.NextSlot(0, TimeSpan.FromMilliseconds(20), I));

    [Fact]
    public void Overrun_SkipsMissedSlots_NoBacklog() =>
        // Probe sent at slot 0 timed out after 2.3 s: next send is slot 3 (t=3 s), not slots 1 and 2 back-to-back.
        Assert.Equal(3, ProbeScheduler.NextSlot(0, TimeSpan.FromMilliseconds(2300), I));

    [Fact]
    public void ExactBoundary_UsesThatSlot() =>
        Assert.Equal(2, ProbeScheduler.NextSlot(0, TimeSpan.FromSeconds(2), I));

    [Fact]
    public void SlotTime_IsFixedGrid() =>
        Assert.Equal(TimeSpan.FromSeconds(7), ProbeScheduler.SlotTime(7, I));
}

public class PingMonitorTests
{
    private static PingMonitor Create(FakeProbe probe, List<PingResult> sink, TimeSpan duration, TimeSpan interval, string target = "192.168.1.1", FakeResolver? resolver = null, List<SessionEvent>? events = null) =>
        new(TargetType.Internal, target, probe, resolver ?? new FakeResolver(), TimeProvider.System, Guid.NewGuid(), "en0",
            () => IPAddress.Parse("192.168.1.20"), interval, TimeSpan.FromMilliseconds(500), duration, new StatusThresholds(),
            r => { lock (sink) { sink.Add(r); } },
            e => events?.Add(e));

    [Fact]
    public async Task Duration_LimitsNumberOfProbes()
    {
        var probe = new FakeProbe(_ => ProbeReply.Ok(1.5));
        var results = new List<PingResult>();
        var monitor = Create(probe, results, TimeSpan.FromMilliseconds(400), TimeSpan.FromMilliseconds(40));

        await monitor.RunAsync(TimeProvider.System.GetTimestamp(), CancellationToken.None);

        // Slots at 0,40,…,360 ms → 10 probes. Under heavy CPU load a late wake-up skips a slot (by design: no
        // catch-up bursts), so assert the invariants rather than an exact count.
        Assert.InRange(results.Count, 7, 10);
        Assert.Equal(Enumerable.Range(1, results.Count).Select(i => (long)i), results.Select(r => r.Sequence));
        Assert.All(results, r => Assert.Equal(1.5, r.RoundTripMs));
        Assert.All(results, r => Assert.Equal(0, r.ScheduledOffsetMs % 40));
        Assert.All(results, r => Assert.True(r.ScheduledOffsetMs < 400));
        Assert.Equal(results.Select(r => r.ScheduledOffsetMs).Order(), results.Select(r => r.ScheduledOffsetMs));
        Assert.Equal(0, results[0].ScheduledOffsetMs);
        Assert.All(results, r => Assert.True(r.SentOffsetMs >= r.ScheduledOffsetMs - 1));
    }

    [Fact]
    public async Task Timeouts_AreRecordedAsFailures_WithoutLatency()
    {
        var probe = new FakeProbe(n => n % 2 == 0 ? ProbeReply.Fail(ProbeStatus.Timeout, "Request timed out.") : ProbeReply.Ok(4));
        var results = new List<PingResult>();
        var monitor = Create(probe, results, TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(20));

        await monitor.RunAsync(TimeProvider.System.GetTimestamp(), CancellationToken.None);

        Assert.InRange(results.Count, 6, 10);
        var timeouts = results.Where(r => r.Status == ProbeStatus.Timeout).ToList();
        Assert.Equal(results.Count / 2, timeouts.Count); // every second probe times out
        Assert.All(timeouts, r => Assert.Null(r.RoundTripMs));
        var stats = monitor.Statistics.Snapshot();
        Assert.Equal(timeouts.Count * 100.0 / results.Count, stats.PacketLossPercent);
        Assert.Equal(4.0, stats.MinMs);
    }

    [Fact]
    public async Task SlowProbe_NeverOverlaps_AndDoesNotBuildBacklog()
    {
        // Each probe takes ~3 intervals.
        var probe = new FakeProbe(_ => ProbeReply.Fail(ProbeStatus.Timeout), delay: TimeSpan.FromMilliseconds(150));
        var results = new List<PingResult>();
        var monitor = Create(probe, results, TimeSpan.FromMilliseconds(600), TimeSpan.FromMilliseconds(50));

        await monitor.RunAsync(TimeProvider.System.GetTimestamp(), CancellationToken.None);

        Assert.Equal(1, probe.MaxInFlight);
        // A backlog would produce ~12 probes; skipping missed slots gives about 600/200 = 3–4.
        Assert.InRange(results.Count, 2, 5);
        // Scheduled offsets stay on the 50 ms grid.
        Assert.All(results, r => Assert.Equal(0, r.ScheduledOffsetMs % 50));
    }

    [Fact]
    public async Task Cancellation_StopsPromptly_AndMarksInFlightAsCancelled()
    {
        var probe = new FakeProbe(_ => ProbeReply.Ok(1), delay: TimeSpan.FromSeconds(5));
        var results = new List<PingResult>();
        var monitor = Create(probe, results, TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(100));
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await monitor.RunAsync(TimeProvider.System.GetTimestamp(), cts.Token);

        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"Took {sw.Elapsed}");
        var last = Assert.Single(results);
        Assert.Equal(ProbeStatus.Cancelled, last.Status);
        var s = monitor.Statistics.Snapshot();
        Assert.Equal(0, s.Completed);
        Assert.Null(s.PacketLossPercent);
    }

    [Fact]
    public async Task DnsFailure_IsRecorded_ThenResolutionRetried()
    {
        var resolver = new FakeResolver((_, n) => n < 3 ? new HostResolution(null, "NXDOMAIN") : new HostResolution(IPAddress.Parse("1.2.3.4"), null));
        var probe = new FakeProbe(_ => ProbeReply.Ok(9));
        var results = new List<PingResult>();
        var events = new List<SessionEvent>();
        var monitor = Create(probe, results, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(50), "example.com", resolver, events);

        await monitor.RunAsync(TimeProvider.System.GetTimestamp(), CancellationToken.None);

        Assert.InRange(results.Count, 3, 5);
        Assert.Equal(ProbeStatus.DnsFailure, results[0].Status);
        Assert.Equal(ProbeStatus.DnsFailure, results[1].Status);
        Assert.Null(results[0].ResolvedIp);
        Assert.All(results.Skip(2), r => Assert.Equal("1.2.3.4", r.ResolvedIp));
        Assert.Equal(3, resolver.Calls); // not re-resolved once successful
        Assert.All(probe.Destinations, d => Assert.Equal(IPAddress.Parse("1.2.3.4"), d));
        Assert.Contains(events, e => e.Kind == SessionEventKind.TargetResolved);
    }

    [Fact]
    public async Task ThrowingProbe_IsRecordedAsError_AndMonitorContinues()
    {
        var probe = new FakeProbe(n => n == 1 ? throw new InvalidOperationException("boom") : ProbeReply.Ok(2));
        var results = new List<PingResult>();
        var monitor = Create(probe, results, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(25));

        await monitor.RunAsync(TimeProvider.System.GetTimestamp(), CancellationToken.None);

        Assert.Equal(ProbeStatus.Error, results[0].Status);
        Assert.Contains("boom", results[0].ErrorDetail);
        Assert.All(results.Skip(1), r => Assert.Equal(ProbeStatus.Success, r.Status));
    }

    [Fact]
    public async Task Results_CarrySessionAndAdapterContext()
    {
        var probe = new FakeProbe(_ => ProbeReply.Ok(1));
        var results = new List<PingResult>();
        var monitor = Create(probe, results, TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(30));
        var before = DateTimeOffset.UtcNow;

        await monitor.RunAsync(TimeProvider.System.GetTimestamp(), CancellationToken.None);

        var r = Assert.Single(results);
        Assert.Equal("en0", r.AdapterId);
        Assert.Equal("192.168.1.20", r.AdapterLocalIp);
        Assert.Equal("192.168.1.1", r.ResolvedIp);
        Assert.Equal(TimeSpan.Zero, r.TimestampUtc.Offset);
        Assert.True(r.TimestampUtc >= before.AddSeconds(-1));
    }
}

public class SessionRunnerTests
{
    private static DiagnosticSessionRunner CreateRunner(FakeProbeFactory factory, FakeAdapterProvider? adapters = null) =>
        new(factory, new FakeResolver(), adapters ?? new FakeAdapterProvider { Adapters = [TestData.WiFi()], DefaultId = "en0" },
            TimeProvider.System, "1.0.0-test", TimeSpan.FromMilliseconds(50));

    [Fact]
    public async Task BothTargets_RunConcurrently()
    {
        var shared = new SharedConcurrency();
        var factory = new FakeProbeFactory(() => new FakeProbe(_ => ProbeReply.Ok(3), TimeSpan.FromMilliseconds(60), shared));
        var runner = CreateRunner(factory);

        var session = runner.Start(TestData.Options(duration: TimeSpan.FromMilliseconds(400), interval: TimeSpan.FromMilliseconds(100)));
        await runner.Completion;

        Assert.Equal(2, factory.Created.Count);
        Assert.Equal(2, shared.Max); // internal and external probes were in flight at the same time
        Assert.All(factory.Created, p => Assert.Equal(1, p.MaxInFlight));
        Assert.InRange(session.GetResults(TargetType.Internal).Count, 3, 4);
        Assert.InRange(session.GetResults(TargetType.External).Count, 3, 4);
        Assert.Equal(SessionEndReason.Completed, session.EndReason);
        Assert.All(factory.Created, p => Assert.True(p.Disposed));
    }

    [Fact]
    public async Task Stop_PreservesResults_AndEndsAsStopped()
    {
        var factory = new FakeProbeFactory(() => new FakeProbe(_ => ProbeReply.Ok(2)));
        var runner = CreateRunner(factory);
        var updates = 0;
        runner.ResultRecorded += (_, _) => Interlocked.Increment(ref updates);
        DiagnosticSession? ended = null;
        runner.SessionEnded += (_, s) => ended = s;

        var session = runner.Start(TestData.Options(duration: TimeSpan.FromMinutes(10), interval: TimeSpan.FromMilliseconds(30)));
        await Task.Delay(200);
        Assert.True(runner.IsRunning);
        await runner.StopAsync();

        Assert.False(runner.IsRunning);
        Assert.Same(session, ended);
        Assert.Equal(SessionEndReason.Stopped, session.EndReason);
        Assert.True(session.GetResults(TargetType.Internal).Count >= 3);
        Assert.True(session.GetResults(TargetType.External).Count >= 3);
        Assert.Equal(updates, session.GetResults(TargetType.Internal).Count + session.GetResults(TargetType.External).Count);
        Assert.NotNull(session.EndedUtc);
        Assert.True(session.ActualDuration < TimeSpan.FromSeconds(5));
        Assert.Equal(session.InternalStatistics.Sent, session.GetResults(TargetType.Internal).Count);
        Assert.Contains(session.GetEvents(), e => e.Kind == SessionEventKind.SessionEnded);
    }

    [Fact]
    public async Task Stop_DuringSlowProbe_RecordsCancelled_ExcludedFromLoss()
    {
        var factory = new FakeProbeFactory(() => new FakeProbe(_ => ProbeReply.Ok(2), TimeSpan.FromSeconds(10)));
        var runner = CreateRunner(factory);
        var session = runner.Start(TestData.Options(duration: TimeSpan.FromMinutes(1)));
        await Task.Delay(100);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await runner.StopAsync();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3));

        var r = Assert.Single(session.GetResults(TargetType.Internal));
        Assert.Equal(ProbeStatus.Cancelled, r.Status);
        Assert.Null(session.InternalStatistics.PacketLossPercent);
        Assert.Equal(1, session.InternalStatistics.Cancelled);
    }

    [Fact]
    public async Task CannotStartTwice()
    {
        var runner = CreateRunner(new FakeProbeFactory(() => new FakeProbe(_ => ProbeReply.Ok(1))));
        runner.Start(TestData.Options(duration: TimeSpan.FromSeconds(5)));
        Assert.Throws<InvalidOperationException>(() => runner.Start(TestData.Options()));
        await runner.StopAsync();
    }

    [Fact]
    public async Task CanStartAgain_AfterStop()
    {
        var runner = CreateRunner(new FakeProbeFactory(() => new FakeProbe(_ => ProbeReply.Ok(1))));
        var first = runner.Start(TestData.Options(duration: TimeSpan.FromSeconds(5)));
        await runner.StopAsync();
        var second = runner.Start(TestData.Options(duration: TimeSpan.FromMilliseconds(100)));
        await runner.Completion;
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(SessionEndReason.Completed, second.EndReason);
    }

    [Fact]
    public void UnsupportedBinding_PreventsStart()
    {
        var factory = new FakeProbeFactory(() => new FakeProbe(_ => ProbeReply.Ok(1)), Core.Interfaces.BindingCheck.Unsupported("nope"));
        var runner = CreateRunner(factory);
        Assert.Throws<NotSupportedException>(() => runner.Start(TestData.Options()));
        Assert.Null(runner.CurrentSession);
    }

    [Fact]
    public async Task AdapterDisconnect_DuringSession_IsRecorded()
    {
        var adapters = new FakeAdapterProvider { Adapters = [TestData.WiFi()], DefaultId = "en0" };
        var runner = CreateRunner(new FakeProbeFactory(() => new FakeProbe(_ => ProbeReply.Ok(1))), adapters);
        var session = runner.Start(TestData.Options(duration: TimeSpan.FromMilliseconds(600)));
        await Task.Delay(150);
        adapters.Adapters = [TestData.WiFi(up: false)];
        await runner.Completion;

        Assert.Contains(session.GetEvents(), e => e.Kind == SessionEventKind.AdapterDisconnected);
    }
}

public class AdapterWatcherTests
{
    private static (AdapterWatcher, FakeAdapterProvider, List<SessionEvent>) Create(bool automatic = false)
    {
        var provider = new FakeAdapterProvider { Adapters = [TestData.WiFi(), TestData.Ethernet()], DefaultId = "en0" };
        var events = new List<SessionEvent>();
        var watcher = new AdapterWatcher(provider, new AdapterSelection(automatic, TestData.WiFi()), TimeProvider.System, TimeSpan.FromSeconds(1), events.Add);
        return (watcher, provider, events);
    }

    [Fact]
    public void DisconnectAndReconnect_AreRecordedOnce()
    {
        var (watcher, provider, events) = Create();
        watcher.Poll(TimeSpan.FromSeconds(1));
        Assert.Empty(events);

        provider.Adapters = [TestData.WiFi(up: false)];
        watcher.Poll(TimeSpan.FromSeconds(2));
        watcher.Poll(TimeSpan.FromSeconds(3));
        provider.Adapters = [TestData.WiFi()];
        watcher.Poll(TimeSpan.FromSeconds(4));

        Assert.Equal([SessionEventKind.AdapterDisconnected, SessionEventKind.AdapterReconnected], events.Select(e => e.Kind));
        Assert.Equal(2000, events[0].OffsetMs);
    }

    [Fact]
    public void AddressChange_IsRecorded_AndExposed()
    {
        var (watcher, provider, events) = Create();
        provider.Adapters = [TestData.WiFi(ip: "192.168.1.99")];
        watcher.Poll(TimeSpan.FromSeconds(1));

        Assert.Equal(SessionEventKind.AdapterAddressChanged, Assert.Single(events).Kind);
        Assert.Equal(IPAddress.Parse("192.168.1.99"), watcher.CurrentAddress);
    }

    [Fact]
    public void RemovedAdapter_IsRecorded()
    {
        var (watcher, provider, events) = Create();
        provider.Adapters = [TestData.Ethernet()];
        watcher.Poll(TimeSpan.FromSeconds(1));
        watcher.Poll(TimeSpan.FromSeconds(2));
        Assert.Equal(SessionEventKind.AdapterMissing, Assert.Single(events).Kind);
        Assert.Null(watcher.CurrentAddress);
    }

    [Fact]
    public void Automatic_DefaultAdapterChange_IsRecorded()
    {
        var (watcher, provider, events) = Create(automatic: true);
        provider.DefaultId = "en5";
        watcher.Poll(TimeSpan.FromSeconds(1));
        provider.DefaultId = null;
        watcher.Poll(TimeSpan.FromSeconds(2));

        Assert.Equal([SessionEventKind.DefaultAdapterChanged, SessionEventKind.AdapterDisconnected], events.Select(e => e.Kind));
    }
}
