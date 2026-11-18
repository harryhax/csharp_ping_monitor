using NetworkDiagnostics.Core.Interfaces;
using NetworkDiagnostics.Core.Models;

namespace NetworkDiagnostics.Core.Monitoring;

/// <summary>
/// Runs a diagnostic session: two concurrent <see cref="PingMonitor"/>s plus an <see cref="AdapterWatcher"/>.
/// Events are raised on background threads; UI consumers must marshal to their own thread.
/// </summary>
public sealed class DiagnosticSessionRunner
{
    private readonly IPingProbeFactory _probeFactory;
    private readonly IHostResolver _resolver;
    private readonly INetworkInterfaceProvider _adapters;
    private readonly TimeProvider _time;
    private readonly string _appVersion;
    private readonly TimeSpan _adapterPollInterval;
    private readonly object _gate = new();

    private CancellationTokenSource? _stopCts;
    private Task? _running;
    private long _startTimestamp;
    private DiagnosticSession? _current;

    public DiagnosticSessionRunner(
        IPingProbeFactory probeFactory,
        IHostResolver resolver,
        INetworkInterfaceProvider adapters,
        TimeProvider time,
        string appVersion,
        TimeSpan? adapterPollInterval = null)
    {
        _probeFactory = probeFactory;
        _resolver = resolver;
        _adapters = adapters;
        _time = time;
        _appVersion = appVersion;
        _adapterPollInterval = adapterPollInterval ?? TimeSpan.FromSeconds(2);
    }

    public event EventHandler<MonitorUpdate>? ResultRecorded;

    public event EventHandler<SessionEvent>? EventRecorded;

    public event EventHandler<DiagnosticSession>? SessionEnded;

    public DiagnosticSession? CurrentSession => _current;

    public bool IsRunning
    {
        get
        {
            lock (_gate)
            {
                return _running is { IsCompleted: false };
            }
        }
    }

    /// <summary>Monotonic time since the current session started (frozen once it has ended).</summary>
    public TimeSpan Elapsed
    {
        get
        {
            var s = _current;
            if (s is null)
            {
                return TimeSpan.Zero;
            }

            return s.ActualDuration ?? _time.GetElapsedTime(Interlocked.Read(ref _startTimestamp));
        }
    }

    /// <summary>Task that completes when the current session has fully finished.</summary>
    public Task Completion
    {
        get
        {
            lock (_gate)
            {
                return _running ?? Task.CompletedTask;
            }
        }
    }

    public DiagnosticSession Start(SessionOptions options)
    {
        lock (_gate)
        {
            if (_running is { IsCompleted: false })
            {
                throw new InvalidOperationException("A session is already running.");
            }

            // Create probes first so a binding failure prevents the session from starting.
            var internalProbe = _probeFactory.Create(options.Adapter);
            IPingProbe externalProbe;
            try
            {
                externalProbe = _probeFactory.Create(options.Adapter);
            }
            catch
            {
                internalProbe.Dispose();
                throw;
            }

            var session = new DiagnosticSession(Guid.NewGuid(), options, _time.GetUtcNow(), PlatformDescription.Current(), _appVersion);
            session.RoutingDescription = internalProbe.RoutingDescription;
            _current = session;
            _stopCts = new CancellationTokenSource();
            Interlocked.Exchange(ref _startTimestamp, _time.GetTimestamp());
            _running = Task.Run(() => RunAsync(session, internalProbe, externalProbe, _startTimestamp, _stopCts.Token));
            return session;
        }
    }

    /// <summary>Adds a warning to the current session's event log (e.g. a UI-detected caveat).</summary>
    public void RecordWarning(string message)
    {
        var session = _current;
        if (session is null || session.IsFinished)
        {
            return;
        }

        var e = new SessionEvent(_time.GetUtcNow(), Elapsed.TotalMilliseconds, SessionEventKind.Warning, message);
        session.AddEvent(e);
        EventRecorded?.Invoke(this, e);
    }

    /// <summary>Requests a stop and waits for in-flight probes to finish or cancel.</summary>
    public async Task StopAsync()
    {
        Task? running;
        lock (_gate)
        {
            _stopCts?.Cancel();
            running = _running;
        }

        if (running is not null)
        {
            await running.ConfigureAwait(false);
        }
    }

    private async Task RunAsync(DiagnosticSession session, IPingProbe internalProbe, IPingProbe externalProbe, long start, CancellationToken stopToken)
    {
        var options = session.Options;
        using var watcherCts = CancellationTokenSource.CreateLinkedTokenSource(stopToken);

        void OnEvent(SessionEvent e)
        {
            session.AddEvent(e);
            EventRecorded?.Invoke(this, e);
        }

        var watcher = new AdapterWatcher(_adapters, options.Adapter, _time, _adapterPollInterval, OnEvent);

        PingMonitor CreateMonitor(TargetType type, string target, IPingProbe probe)
        {
            PingMonitor? monitor = null;
            monitor = new PingMonitor(
                type, target, probe, _resolver, _time, session.Id, options.Adapter.Id,
                () => watcher.CurrentAddress, options.Interval, options.Timeout, options.Duration, options.Thresholds,
                result =>
                {
                    session.AddResult(result);
                    var stats = monitor!.Statistics.Snapshot();
                    if (type == TargetType.Internal)
                    {
                        session.InternalStatistics = stats;
                    }
                    else
                    {
                        session.ExternalStatistics = stats;
                    }

                    ResultRecorded?.Invoke(this, new MonitorUpdate(type, result, stats, monitor.CurrentHealth()));
                },
                OnEvent);
            return monitor;
        }

        var internalMonitor = CreateMonitor(TargetType.Internal, options.InternalTarget, internalProbe);
        var externalMonitor = CreateMonitor(TargetType.External, options.ExternalTarget, externalProbe);

        OnEvent(new SessionEvent(session.StartedUtc, 0, SessionEventKind.SessionStarted,
            $"Session started on {options.Adapter.DisplayName} via {internalProbe.RoutingDescription}."));

        var reason = SessionEndReason.Completed;
        string? failure = null;
        try
        {
            var watcherTask = watcher.RunAsync(start, watcherCts.Token);
            await Task.WhenAll(
                Task.Run(() => internalMonitor.RunAsync(start, stopToken), CancellationToken.None),
                Task.Run(() => externalMonitor.RunAsync(start, stopToken), CancellationToken.None)).ConfigureAwait(false);
            watcherCts.Cancel();
            await watcherTask.ConfigureAwait(false);
            reason = stopToken.IsCancellationRequested ? SessionEndReason.Stopped : SessionEndReason.Completed;
        }
        catch (Exception ex)
        {
            reason = SessionEndReason.Failed;
            failure = ex.Message;
        }
        finally
        {
            internalProbe.Dispose();
            externalProbe.Dispose();
        }

        session.InternalStatistics = internalMonitor.Statistics.Snapshot();
        session.ExternalStatistics = externalMonitor.Statistics.Snapshot();
        var elapsed = _time.GetElapsedTime(start);
        var ended = _time.GetUtcNow();
        OnEvent(new SessionEvent(ended, elapsed.TotalMilliseconds, SessionEventKind.SessionEnded,
            reason switch
            {
                SessionEndReason.Stopped => "Session stopped by user.",
                SessionEndReason.Completed => "Session completed.",
                _ => "Session failed: " + failure,
            }));
        session.Finish(reason, ended, elapsed, failure);
        SessionEnded?.Invoke(this, session);
    }
}
