using System.Net;
using NetworkDiagnostics.Core.Interfaces;
using NetworkDiagnostics.Core.Models;
using NetworkDiagnostics.Core.Statistics;

namespace NetworkDiagnostics.Core.Monitoring;

/// <summary>Published after every recorded measurement.</summary>
public sealed record MonitorUpdate(TargetType Target, PingResult Result, LatencyStatistics Statistics, HealthAssessment Health);

/// <summary>
/// Probes a single destination at a fixed interval until the duration elapses or it is cancelled.
/// Exactly one probe is in flight at a time.
/// </summary>
public sealed class PingMonitor
{
    private readonly TargetType _targetType;
    private readonly string _target;
    private readonly IPingProbe _probe;
    private readonly IHostResolver _resolver;
    private readonly TimeProvider _time;
    private readonly Guid _sessionId;
    private readonly string _adapterId;
    private readonly Func<IPAddress?> _localAddress;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _timeout;
    private readonly TimeSpan _duration;
    private readonly StatusThresholds _thresholds;
    private readonly Action<PingResult> _onResult;
    private readonly Action<SessionEvent> _onEvent;
    private IPAddress? _resolved;

    public PingMonitor(
        TargetType targetType,
        string target,
        IPingProbe probe,
        IHostResolver resolver,
        TimeProvider time,
        Guid sessionId,
        string adapterId,
        Func<IPAddress?> localAddress,
        TimeSpan interval,
        TimeSpan timeout,
        TimeSpan duration,
        StatusThresholds thresholds,
        Action<PingResult> onResult,
        Action<SessionEvent> onEvent)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        _targetType = targetType;
        _target = target;
        _probe = probe;
        _resolver = resolver;
        _time = time;
        _sessionId = sessionId;
        _adapterId = adapterId;
        _localAddress = localAddress;
        _interval = interval;
        _timeout = timeout;
        _duration = duration;
        _thresholds = thresholds;
        _onResult = onResult;
        _onEvent = onEvent;
        Statistics = new StatisticsAccumulator(thresholds.WindowSize);
    }

    public StatisticsAccumulator Statistics { get; }

    public TargetType TargetType => _targetType;

    public HealthAssessment CurrentHealth() =>
        HealthEvaluator.Evaluate(_targetType, Statistics.Snapshot(), Statistics.RecentWindow(), _thresholds);

    /// <param name="sessionStartTimestamp">Monotonic timestamp from <see cref="TimeProvider.GetTimestamp"/>.</param>
    /// <param name="stopToken">Signalled when the user stops the session.</param>
    public async Task RunAsync(long sessionStartTimestamp, CancellationToken stopToken)
    {
        long slot = 0;
        long sequence = 0;

        while (!stopToken.IsCancellationRequested)
        {
            var scheduled = ProbeScheduler.SlotTime(slot, _interval);
            if (scheduled >= _duration)
            {
                break;
            }

            // Timers can fire slightly early, so keep waiting until the scheduled time has really been reached.
            var cancelled = false;
            TimeSpan wait;
            while ((wait = scheduled - _time.GetElapsedTime(sessionStartTimestamp)) > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(wait, _time, stopToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    cancelled = true;
                    break;
                }
            }

            if (cancelled)
            {
                break;
            }

            sequence++;
            await ProbeOnceAsync(sequence, scheduled, sessionStartTimestamp, stopToken).ConfigureAwait(false);

            slot = ProbeScheduler.NextSlot(slot, _time.GetElapsedTime(sessionStartTimestamp), _interval);
        }
    }

    private async Task ProbeOnceAsync(long sequence, TimeSpan scheduled, long start, CancellationToken stopToken)
    {
        var sentOffset = _time.GetElapsedTime(start);
        var sentUtc = _time.GetUtcNow();
        Statistics.MarkSent();

        ProbeReply reply;
        try
        {
            if (_resolved is null)
            {
                var resolution = await ResolveAsync(stopToken).ConfigureAwait(false);
                if (resolution is not null)
                {
                    reply = resolution;
                    Publish(sequence, scheduled, sentOffset, sentUtc, reply);
                    return;
                }
            }

            reply = await _probe.SendAsync(_resolved!, _timeout, stopToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
        {
            reply = ProbeReply.Fail(ProbeStatus.Cancelled, "Session stopped before a reply was received.");
        }
        catch (Exception ex)
        {
            // Probes should not throw, but a monitor must never die because one did.
            reply = ProbeReply.Fail(ProbeStatus.Error, ex.GetType().Name + ": " + ex.Message);
        }

        Publish(sequence, scheduled, sentOffset, sentUtc, reply);
    }

    /// <summary>Returns a failed reply if resolution failed, otherwise null and sets <see cref="_resolved"/>.</summary>
    private async Task<ProbeReply?> ResolveAsync(CancellationToken stopToken)
    {
        HostResolution result;
        try
        {
            result = await _resolver.ResolveIPv4Async(_target, stopToken).WaitAsync(_timeout + TimeSpan.FromSeconds(3), _time, stopToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            result = new HostResolution(null, $"Timed out resolving \"{_target}\".");
        }

        if (!result.Succeeded)
        {
            return ProbeReply.Fail(ProbeStatus.DnsFailure, result.Error);
        }

        _resolved = result.Address;
        if (!string.Equals(_resolved!.ToString(), _target, StringComparison.Ordinal))
        {
            _onEvent(new SessionEvent(_time.GetUtcNow(), 0, SessionEventKind.TargetResolved, $"{_targetType} target {_target} resolved to {_resolved}."));
        }

        return null;
    }

    private void Publish(long sequence, TimeSpan scheduled, TimeSpan sentOffset, DateTimeOffset sentUtc, ProbeReply reply)
    {
        var result = new PingResult
        {
            SessionId = _sessionId,
            Sequence = sequence,
            TimestampUtc = sentUtc,
            ScheduledOffsetMs = scheduled.TotalMilliseconds,
            SentOffsetMs = sentOffset.TotalMilliseconds,
            AdapterId = _adapterId,
            AdapterLocalIp = _localAddress()?.ToString(),
            TargetType = _targetType,
            Target = _target,
            ResolvedIp = _resolved?.ToString(),
            RoundTripMs = reply.Status == ProbeStatus.Success ? reply.RoundTripMs : null,
            Status = reply.Status,
            ErrorDetail = reply.Detail,
        };

        Statistics.Record(result);
        _onResult(result);
    }
}
