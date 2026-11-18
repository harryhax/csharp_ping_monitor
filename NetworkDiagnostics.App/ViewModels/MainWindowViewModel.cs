using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NetworkDiagnostics.App.Models;
using NetworkDiagnostics.App.Services;
using NetworkDiagnostics.Core.Configuration;
using NetworkDiagnostics.Core.Export;
using NetworkDiagnostics.Core.Interfaces;
using NetworkDiagnostics.Core.Models;
using NetworkDiagnostics.Core.Monitoring;

namespace NetworkDiagnostics.App.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly INetworkInterfaceProvider _adapters;
    private readonly IPingProbeFactory _probeFactory;
    private readonly ISettingsStore _settingsStore;
    private readonly DiagnosticSessionRunner _runner;
    private readonly IFileSaveService _fileSave;
    private readonly IUiDispatcher _ui;
    private readonly INetworkChangeNotifier _networkChanges;
    private readonly TimeProvider _time;

    private const string NoAdaptersMessage = "No active network adapter with an IPv4 address was found. Connect to Wi-Fi or Ethernet, then press Refresh.";

    private AppSettings _settings = new();
    private ITimer? _clock;
    private bool _suppressSelectionChanged;
    private int _refreshQueued;
    private bool _localNetworkWarningShown;
    private bool _localNetworkPrimed;

    public MainWindowViewModel(
        INetworkInterfaceProvider adapters,
        IPingProbeFactory probeFactory,
        ISettingsStore settingsStore,
        DiagnosticSessionRunner runner,
        IFileSaveService fileSave,
        IUiDispatcher ui,
        INetworkChangeNotifier networkChanges,
        TimeProvider time)
    {
        _adapters = adapters;
        _probeFactory = probeFactory;
        _settingsStore = settingsStore;
        _runner = runner;
        _fileSave = fileSave;
        _ui = ui;
        _networkChanges = networkChanges;
        _time = time;

        _runner.ResultRecorded += OnResultRecorded;
        _runner.EventRecorded += OnEventRecorded;
        _runner.SessionEnded += OnSessionEnded;
        _networkChanges.NetworkChanged += OnNetworkChanged;
    }

    public ObservableCollection<AdapterOption> Adapters { get; } = [];

    public IReadOnlyList<int> Durations { get; } = AppSettings.AllowedDurationsMinutes;

    public ObservableCollection<string> EventLog { get; } = [];

    public MonitorPanelViewModel InternalPanel { get; } = new(TargetType.Internal, "Internal network", "Your computer → router (default gateway)");

    public MonitorPanelViewModel ExternalPanel { get; } = new(TargetType.External, "External network", "Your computer → internet");

    [ObservableProperty] private AdapterOption? _selectedAdapter;
    [ObservableProperty] private string _connectionType = "—";
    [ObservableProperty] private string _localIp = "—";
    [ObservableProperty] private string _gatewayText = "—";
    [ObservableProperty] private string _connectionStatus = "—";
    [ObservableProperty] private string _routingText = string.Empty;
    [ObservableProperty] private bool _routingSupported = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveInternalTarget))]
    private bool _useInternalOverride;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EffectiveInternalTarget))]
    private string _internalOverride = string.Empty;

    [ObservableProperty] private string? _internalTargetError;
    [ObservableProperty] private string _externalTarget = AppSettings.DefaultExternalTarget;
    [ObservableProperty] private string? _externalTargetError;
    [ObservableProperty] private int _selectedDuration = AppSettings.DefaultDurationMinutes;
    [ObservableProperty] private decimal? _intervalMs = AppSettings.DefaultIntervalMs;
    [ObservableProperty] private decimal? _timeoutMs = AppSettings.DefaultTimeoutMs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEditSettings))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand), nameof(ExportCommand), nameof(RefreshAdaptersCommand))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _isStopping;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    private bool _canExport;

    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private string _elapsedText = "00:00";
    [ObservableProperty] private string _remainingText = "—";
    [ObservableProperty] private double _progress;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasInfo))]
    private string? _infoMessage;

    public bool CanEditSettings => !IsRunning;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public bool HasInfo => !string.IsNullOrEmpty(InfoMessage);

    public string EffectiveInternalTarget =>
        AdapterSelector.EffectiveInternalTarget(CurrentSelection(), UseInternalOverride, InternalOverride) is { Length: > 0 } t
            ? t
            : "Not detected";

    public string SettingsFilePath => (_settingsStore as JsonSettingsStore)?.FilePath ?? string.Empty;

    /// <summary>Startup sequence: load settings, enumerate adapters, select, show status.</summary>
    public void Initialize()
    {
        _settings = _settingsStore.Load();
        if (_settingsStore is JsonSettingsStore { LastError: { } loadError })
        {
            InfoMessage = loadError;
        }

        UseInternalOverride = _settings.UseInternalTargetOverride;
        InternalOverride = _settings.InternalTargetOverride ?? string.Empty;
        ExternalTarget = _settings.ExternalTarget;
        SelectedDuration = _settings.DurationMinutes;
        IntervalMs = _settings.PingIntervalMs;
        TimeoutMs = _settings.PingTimeoutMs;
        RefreshAdapters(_settings.SelectedAdapterId);
        PrimeLocalNetworkPermission();
    }

    /// <summary>
    /// On macOS, the first packet an app sends to a LAN address triggers the "Local Network" permission prompt.
    /// Sending one echo request to the gateway at startup makes the prompt appear now, rather than silently
    /// failing the first test.
    /// </summary>
    private void PrimeLocalNetworkPermission()
    {
        if (!OperatingSystem.IsMacOS() || _localNetworkPrimed || CurrentSelection().Gateway is not { } gateway)
        {
            return;
        }

        _localNetworkPrimed = true;
        var selection = new AdapterSelection(true, CurrentSelection().Adapter);
        _ = Task.Run(async () =>
        {
            try
            {
                using var probe = _probeFactory.Create(selection);
                await probe.SendAsync(gateway, TimeSpan.FromSeconds(1), CancellationToken.None);
            }
            catch (Exception)
            {
                // Best effort only.
            }
        });
    }

    public void OnClosing()
    {
        TrySaveSettings();
        if (_runner.IsRunning)
        {
            _ = _runner.StopAsync();
        }
    }

    public AdapterSelection CurrentSelection()
    {
        var option = SelectedAdapter;
        if (option is null || option.IsAutomatic)
        {
            return new AdapterSelection(true, option?.Adapter);
        }

        return new AdapterSelection(false, option.Adapter);
    }

    [RelayCommand(CanExecute = nameof(CanEditSettings))]
    private void RefreshAdapters() => RefreshAdapters(SelectedAdapter?.Id);

    [RelayCommand]
    private void DismissMessages()
    {
        ErrorMessage = null;
        InfoMessage = null;
    }

    private bool CanStart() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private void Start()
    {
        ErrorMessage = null;
        InfoMessage = null;

        // Re-read adapter state so we validate against reality, not a stale list.
        var requestedId = SelectedAdapter?.Id;
        var warning = RefreshAdapters(requestedId, reportWarning: false);
        if (warning is not null && !AdapterSelector.IsAutomatic(requestedId))
        {
            ErrorMessage = "The selected network adapter is no longer available. Choose another adapter or Automatic.";
            return;
        }

        var settings = CaptureSettings();
        var validation = SessionOptionsBuilder.Build(settings, CurrentSelection(), _probeFactory);
        if (!validation.IsValid)
        {
            ErrorMessage = string.Join(Environment.NewLine, validation.Errors);
            return;
        }

        _settings = settings;
        TrySaveSettings();

        var options = validation.Options!;
        InternalPanel.Reset(options.InternalTarget, options.Duration);
        ExternalPanel.Reset(options.ExternalTarget, options.Duration);
        EventLog.Clear();
        CanExport = false;
        _localNetworkWarningShown = false;

        try
        {
            _runner.Start(options);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or System.Net.Sockets.SocketException)
        {
            ErrorMessage = "Diagnostics could not start: " + ex.Message;
            return;
        }

        IsRunning = true;
        StatusText = "Running";
        UpdateClock();
        _clock?.Dispose();
        _clock = _time.CreateTimer(_ => _ui.Post(UpdateClock), null, TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250));
    }

    private bool CanStop() => IsRunning && !IsStopping;

    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        IsStopping = true;
        StatusText = "Stopping…";
        await _runner.StopAsync();
    }

    private bool CanExportResults() => CanExport && !IsRunning;

    [RelayCommand(CanExecute = nameof(CanExportResults))]
    private async Task ExportAsync()
    {
        var session = _runner.CurrentSession;
        if (session is not { IsFinished: true })
        {
            return;
        }

        ErrorMessage = null;
        try
        {
            var target = await _fileSave.PickSaveTargetAsync("Export diagnostic results", SessionReportExporter.SuggestedFileName(session), "ZIP archive", "zip");
            if (target is null)
            {
                return;
            }

            await using (var stream = await target.OpenWriteAsync())
            {
                if (stream.CanSeek)
                {
                    stream.SetLength(0);
                }

                await SessionReportExporter.ExportAsync(session, stream, _time.GetUtcNow());
            }

            InfoMessage = "Results exported to " + target.DisplayPath;
        }
        catch (Exception ex)
        {
            ErrorMessage = "Export failed: " + ex.Message;
        }
    }

    partial void OnSelectedAdapterChanged(AdapterOption? value)
    {
        if (!_suppressSelectionChanged)
        {
            UpdateSelectionInfo();
        }
    }

    partial void OnUseInternalOverrideChanged(bool value) => ValidateInternal();

    partial void OnInternalOverrideChanged(string value) => ValidateInternal();

    partial void OnExternalTargetChanged(string value) =>
        ExternalTargetError = TargetValidator.Validate(value) is { IsValid: false } v ? v.Error : null;

    /// <returns>The selection warning, if the requested adapter was not found.</returns>
    private string? RefreshAdapters(string? preferredId, bool reportWarning = true)
    {
        IReadOnlyList<AdapterInfo> list;
        string? defaultId;
        try
        {
            list = _adapters.GetAdapters();
            defaultId = _adapters.GetSystemDefaultAdapterId();
        }
        catch (Exception ex)
        {
            ErrorMessage = "Network adapters could not be listed: " + ex.Message;
            list = [];
            defaultId = null;
        }

        var resolved = AdapterSelector.Resolve(preferredId, list, defaultId);

        _suppressSelectionChanged = true;
        try
        {
            Adapters.Clear();
            var auto = new AdapterOption(AdapterSelection.AutomaticId, AdapterSelector.Automatic(list, defaultId).Adapter, true);
            Adapters.Add(auto);
            foreach (var a in list)
            {
                Adapters.Add(new AdapterOption(a.Id, a, false));
            }

            SelectedAdapter = resolved.Selection.IsAutomatic
                ? auto
                : Adapters.First(o => !o.IsAutomatic && o.Id == resolved.Selection.Adapter!.Id);
        }
        finally
        {
            _suppressSelectionChanged = false;
        }

        UpdateSelectionInfo();

        if (list.Count == 0)
        {
            ErrorMessage = NoAdaptersMessage;
        }
        else if (ErrorMessage == NoAdaptersMessage)
        {
            ErrorMessage = null;
        }
        else if (resolved.Warning is not null && reportWarning)
        {
            InfoMessage = resolved.Warning;
        }

        return resolved.Warning;
    }

    private void UpdateSelectionInfo()
    {
        var selection = CurrentSelection();
        var a = selection.Adapter;
        ConnectionType = a?.KindText ?? "—";
        LocalIp = a?.IPv4Address?.ToString() ?? "—";
        GatewayText = a?.IPv4Gateway?.ToString() ?? "Not detected";
        ConnectionStatus = a is null ? "No active connection" : a.ConnectionText;

        var check = _probeFactory.CheckSupport(selection);
        RoutingSupported = check.IsSupported;
        RoutingText = check.Message;

        OnPropertyChanged(nameof(EffectiveInternalTarget));
        ValidateInternal();
    }

    private void ValidateInternal()
    {
        InternalTargetError = UseInternalOverride && TargetValidator.Validate(InternalOverride) is { IsValid: false } v ? v.Error : null;
        OnPropertyChanged(nameof(EffectiveInternalTarget));
    }

    private AppSettings CaptureSettings()
    {
        var s = _settings.Clone();
        var selection = CurrentSelection();
        s.SelectedAdapterId = selection.IsAutomatic ? null : selection.Adapter!.Id;
        s.UseInternalTargetOverride = UseInternalOverride;
        s.InternalTargetOverride = string.IsNullOrWhiteSpace(InternalOverride) ? null : InternalOverride.Trim();
        s.ExternalTarget = (ExternalTarget ?? string.Empty).Trim();
        s.DurationMinutes = SelectedDuration;
        s.PingIntervalMs = (int)(IntervalMs ?? AppSettings.DefaultIntervalMs);
        s.PingTimeoutMs = (int)(TimeoutMs ?? AppSettings.DefaultTimeoutMs);
        return s;
    }

    private void TrySaveSettings()
    {
        try
        {
            var s = CaptureSettings();
            _settingsStore.Save(s);
            _settings = s;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            InfoMessage = "Settings could not be saved: " + ex.Message;
        }
    }

    private void UpdateClock()
    {
        var session = _runner.CurrentSession;
        if (session is null)
        {
            return;
        }

        var elapsed = _runner.Elapsed;
        var duration = session.Options.Duration;
        var remaining = duration - elapsed;
        if (remaining < TimeSpan.Zero)
        {
            remaining = TimeSpan.Zero;
        }

        ElapsedText = Format(elapsed);
        RemainingText = session.IsFinished ? "—" : Format(remaining);
        Progress = Math.Clamp(elapsed.TotalSeconds / duration.TotalSeconds * 100.0, 0, 100);
    }

    private void OnResultRecorded(object? sender, MonitorUpdate update)
    {
        _ui.Post(() => (update.Target == TargetType.Internal ? InternalPanel : ExternalPanel).Apply(update));

        var session = _runner.CurrentSession;
        if (OperatingSystem.IsMacOS() && !_localNetworkWarningShown && session is not null &&
            LocalNetworkAccessHeuristic.LooksBlocked(session.InternalStatistics, session.ExternalStatistics))
        {
            _localNetworkWarningShown = true;
            _runner.RecordWarning("Possible macOS Local Network permission block: internal target not replying while external target replies.");
            _ui.Post(() => InfoMessage = LocalNetworkAccessHeuristic.MacOSMessage);
        }
    }

    private void OnEventRecorded(object? sender, SessionEvent e) =>
        _ui.Post(() => EventLog.Insert(0, $"{e.TimestampUtc.ToLocalTime():HH:mm:ss}  {e.Message}"));

    private void OnSessionEnded(object? sender, DiagnosticSession session) =>
        _ui.Post(() =>
        {
            _clock?.Dispose();
            _clock = null;
            UpdateClock();
            IsStopping = false;
            IsRunning = false;
            StatusText = session.EndReason switch
            {
                SessionEndReason.Completed => "Completed",
                SessionEndReason.Stopped => "Stopped — results so far are kept",
                _ => "Failed: " + session.FailureMessage,
            };
            var finished = session.EndReason == SessionEndReason.Completed ? "Test finished" : "Test stopped";
            InternalPanel.MarkFinished(finished);
            ExternalPanel.MarkFinished(finished);
            CanExport = true;
            RefreshAdapters(SelectedAdapter?.Id);
        });

    private void OnNetworkChanged(object? sender, EventArgs e)
    {
        // Coalesce bursts of OS notifications into a single refresh.
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1)
        {
            return;
        }

        _ = Task.Delay(750).ContinueWith(_ => _ui.Post(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            if (!IsRunning)
            {
                RefreshAdapters(SelectedAdapter?.Id);
            }
        }), TaskScheduler.Default);
    }

    private static string Format(TimeSpan t) =>
        t.TotalHours >= 1
            ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : t.ToString(@"mm\:ss", CultureInfo.InvariantCulture);

    public void Dispose()
    {
        _clock?.Dispose();
        _runner.ResultRecorded -= OnResultRecorded;
        _runner.EventRecorded -= OnEventRecorded;
        _runner.SessionEnded -= OnSessionEnded;
        _networkChanges.NetworkChanged -= OnNetworkChanged;
    }
}
