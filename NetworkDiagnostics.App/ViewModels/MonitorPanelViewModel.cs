using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using NetworkDiagnostics.App.Models;
using NetworkDiagnostics.Core.Models;
using NetworkDiagnostics.Core.Monitoring;

namespace NetworkDiagnostics.App.ViewModels;

/// <summary>Live metrics for one destination.</summary>
public sealed partial class MonitorPanelViewModel : ObservableObject
{
    private const string None = "—";
    private readonly List<GraphPoint> _points = [];

    public MonitorPanelViewModel(TargetType targetType, string title, string subtitle)
    {
        TargetType = targetType;
        Title = title;
        Subtitle = subtitle;
    }

    public TargetType TargetType { get; }

    public string Title { get; }

    public string Subtitle { get; }

    public IReadOnlyList<GraphPoint> GraphPoints => _points;

    [ObservableProperty] private string _destination = None;
    [ObservableProperty] private string _latest = None;
    [ObservableProperty] private string _average = None;
    [ObservableProperty] private string _minimum = None;
    [ObservableProperty] private string _maximum = None;
    [ObservableProperty] private string _sent = "0";
    [ObservableProperty] private string _received = "0";
    [ObservableProperty] private string _lost = "0";
    [ObservableProperty] private string _lossPercent = None;
    [ObservableProperty] private string _connectionStatus = "Not started";
    [ObservableProperty] private HealthLevel _healthLevel = HealthLevel.Unknown;
    [ObservableProperty] private string _healthLabel = "Not started";
    [ObservableProperty] private string _healthReason = string.Empty;
    [ObservableProperty] private int _graphVersion;
    [ObservableProperty] private double _graphDurationSeconds = 600;

    public void Reset(string destination, TimeSpan duration)
    {
        _points.Clear();
        Destination = destination;
        Latest = Average = Minimum = Maximum = LossPercent = None;
        Sent = Received = Lost = "0";
        ConnectionStatus = "Starting…";
        HealthLevel = HealthLevel.Unknown;
        HealthLabel = "Measuring";
        HealthReason = "Waiting for the first reply.";
        GraphDurationSeconds = duration.TotalSeconds;
        GraphVersion++;
    }

    public void Apply(MonitorUpdate update)
    {
        var r = update.Result;
        var s = update.Statistics;

        if (r.ResolvedIp is not null && !string.Equals(r.ResolvedIp, r.Target, StringComparison.Ordinal))
        {
            Destination = $"{r.Target} ({r.ResolvedIp})";
        }

        Latest = r.Status switch
        {
            ProbeStatus.Success => Ms(r.RoundTripMs),
            ProbeStatus.Cancelled => Latest,
            _ => "No reply",
        };
        Average = Ms(s.AverageMs);
        Minimum = Ms(s.MinMs);
        Maximum = Ms(s.MaxMs);
        Sent = s.Sent.ToString(CultureInfo.CurrentCulture);
        Received = s.Received.ToString(CultureInfo.CurrentCulture);
        Lost = s.Failed.ToString(CultureInfo.CurrentCulture);
        LossPercent = s.PacketLossPercent is { } p ? p.ToString("0.0", CultureInfo.CurrentCulture) + " %" : None;

        if (r.Status != ProbeStatus.Cancelled)
        {
            ConnectionStatus = Describe(r);
            _points.Add(new GraphPoint(r.SentOffsetMs / 1000.0, r.IsSuccess ? r.RoundTripMs : null));
            GraphVersion++;
        }

        HealthLevel = update.Health.Level;
        HealthLabel = update.Health.Label;
        HealthReason = update.Health.Reason;
    }

    public void MarkFinished(string status)
    {
        ConnectionStatus = status;
    }

    public static string Describe(PingResult r) => r.Status switch
    {
        ProbeStatus.Success => "Responding",
        ProbeStatus.Timeout => "Timed out",
        ProbeStatus.DestinationUnreachable => "Destination unreachable",
        ProbeStatus.TtlExpired => "TTL expired in transit",
        ProbeStatus.LocalAdapterFailure => "Adapter could not send",
        ProbeStatus.DnsFailure => "Name lookup failed",
        ProbeStatus.PermissionDenied => "ICMP not permitted",
        ProbeStatus.Cancelled => "Stopped",
        _ => "Error",
    } + (r.ErrorDetail is { Length: > 0 } d && r.Status is not (ProbeStatus.Timeout or ProbeStatus.Success) ? $": {d}" : string.Empty);

    private static string Ms(double? v) => v is { } x
        ? (x < 10 ? x.ToString("0.00", CultureInfo.CurrentCulture) : x.ToString("0.0", CultureInfo.CurrentCulture)) + " ms"
        : None;
}
