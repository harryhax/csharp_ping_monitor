using System.IO.Compression;
using NetworkDiagnostics.App.Services;
using NetworkDiagnostics.App.ViewModels;
using NetworkDiagnostics.Core.Configuration;
using NetworkDiagnostics.Core.Interfaces;
using NetworkDiagnostics.Core.Models;
using NetworkDiagnostics.Core.Monitoring;

namespace NetworkDiagnostics.Tests;

/// <summary>Exercises the UI logic end-to-end with fakes (no Avalonia window).</summary>
public sealed class ViewModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nd-vm-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public void Post(Action action)
        {
            lock (this)
            {
                action();
            }
        }
    }

    private sealed class NoNetworkChanges : INetworkChangeNotifier
    {
        public event EventHandler? NetworkChanged { add { } remove { } }
    }

    private sealed class FileSave(string path) : IFileSaveService
    {
        public Task<SaveTarget?> PickSaveTargetAsync(string title, string suggestedFileName, string fileTypeName, string extension) =>
            Task.FromResult<SaveTarget?>(new SaveTarget(path, () => Task.FromResult<Stream>(File.Create(path))));
    }

    private (MainWindowViewModel Vm, JsonSettingsStore Store, FakeAdapterProvider Adapters) Create(FakeProbeFactory? factory = null)
    {
        factory ??= new FakeProbeFactory(() => new FakeProbe(n => n % 4 == 0 ? ProbeReply.Fail(ProbeStatus.Timeout) : ProbeReply.Ok(3)));
        var adapters = new FakeAdapterProvider { Adapters = [TestData.WiFi(), TestData.Ethernet()], DefaultId = "en0" };
        var store = new JsonSettingsStore(Path.Combine(_dir, "settings.json"));
        var runner = new DiagnosticSessionRunner(factory, new FakeResolver(), adapters, TimeProvider.System, "test");
        var vm = new MainWindowViewModel(adapters, factory, store, runner, new FileSave(Path.Combine(_dir, "out.zip")),
            new InlineDispatcher(), new NoNetworkChanges(), TimeProvider.System);
        return (vm, store, adapters);
    }

    [Fact]
    public void Startup_ListsAutomaticPlusAdapters_AndShowsGateway()
    {
        var (vm, _, _) = Create();
        vm.Initialize();
        Assert.Equal(3, vm.Adapters.Count);
        Assert.True(vm.SelectedAdapter!.IsAutomatic);
        Assert.Equal("192.168.1.1", vm.GatewayText);
        Assert.Equal("192.168.1.1", vm.EffectiveInternalTarget);

        vm.SelectedAdapter = vm.Adapters.Single(a => a.Id == "en5");
        Assert.Equal("10.0.0.1", vm.EffectiveInternalTarget);
        Assert.Equal("Ethernet", vm.ConnectionType);
    }

    [Fact]
    public async Task StartStopExport_FullFlow()
    {
        var (vm, store, _) = Create();
        vm.Initialize();
        vm.SelectedAdapter = vm.Adapters.Single(a => a.Id == "en5");
        vm.ExternalTarget = "1.1.1.1";
        vm.IntervalMs = 200;
        vm.SelectedDuration = 1;

        vm.StartCommand.Execute(null);
        Assert.Null(vm.ErrorMessage);
        Assert.True(vm.IsRunning);
        Assert.False(vm.CanEditSettings);
        Assert.False(vm.ExportCommand.CanExecute(null));

        await Task.Delay(1000);
        Assert.NotEqual("0", vm.InternalPanel.Sent);
        Assert.NotEqual("0", vm.ExternalPanel.Sent);
        Assert.NotEmpty(vm.InternalPanel.GraphPoints);
        Assert.Contains(vm.InternalPanel.GraphPoints, p => p.LatencyMs is null); // timeouts plotted as gaps
        Assert.DoesNotContain(vm.InternalPanel.GraphPoints, p => p.LatencyMs == 0);
        Assert.Equal("1.1.1.1", vm.ExternalPanel.Destination);

        await vm.StopCommand.ExecuteAsync(null);
        Assert.False(vm.IsRunning);
        Assert.StartsWith("Stopped", vm.StatusText);
        Assert.True(vm.ExportCommand.CanExecute(null));

        await vm.ExportCommand.ExecuteAsync(null);
        Assert.Null(vm.ErrorMessage);
        using var zip = ZipFile.OpenRead(Path.Combine(_dir, "out.zip"));
        Assert.Equal(4, zip.Entries.Count);

        // Settings were persisted on Start.
        var saved = store.Load();
        Assert.Equal("en5", saved.SelectedAdapterId);
        Assert.Equal("1.1.1.1", saved.ExternalTarget);
        Assert.Equal(1, saved.DurationMinutes);
        Assert.Equal(200, saved.PingIntervalMs);
    }

    [Fact]
    public void SavedSettings_AreRestoredOnStartup()
    {
        var (_, store, _) = Create();
        store.Save(new AppSettings { SelectedAdapterId = "en5", ExternalTarget = "example.com", DurationMinutes = 5, UseInternalTargetOverride = true, InternalTargetOverride = "10.0.0.2" });
        var (vm, _, _) = Create();
        vm.Initialize();
        Assert.Equal("en5", vm.SelectedAdapter!.Id);
        Assert.Equal("example.com", vm.ExternalTarget);
        Assert.Equal(5, vm.SelectedDuration);
        Assert.Equal("10.0.0.2", vm.EffectiveInternalTarget);
    }

    [Fact]
    public void InvalidTarget_BlocksStart_WithMessage()
    {
        var (vm, _, _) = Create();
        vm.Initialize();
        vm.ExternalTarget = "8.8.8";
        Assert.NotNull(vm.ExternalTargetError);
        vm.StartCommand.Execute(null);
        Assert.False(vm.IsRunning);
        Assert.Contains("External target", vm.ErrorMessage);
    }

    [Fact]
    public void UnsupportedBinding_IsShown_AndBlocksStart()
    {
        var factory = new FakeProbeFactory(() => new FakeProbe(_ => ProbeReply.Ok(1)), BindingCheck.Unsupported("Cannot bind on this platform."));
        var (vm, _, _) = Create(factory);
        vm.Initialize();
        vm.SelectedAdapter = vm.Adapters.Single(a => a.Id == "en0");
        Assert.False(vm.RoutingSupported);
        vm.StartCommand.Execute(null);
        Assert.False(vm.IsRunning);
        Assert.Contains("Cannot bind", vm.ErrorMessage);
    }

    [Fact]
    public void SelectedAdapterRemovedBeforeStart_IsAnError_NotASilentFallback()
    {
        var (vm, _, adapters) = Create();
        vm.Initialize();
        vm.SelectedAdapter = vm.Adapters.Single(a => a.Id == "en5");
        adapters.Adapters = [TestData.WiFi()];
        vm.StartCommand.Execute(null);
        Assert.False(vm.IsRunning);
        Assert.Contains("no longer available", vm.ErrorMessage);
    }

    [Fact]
    public void NoAdapters_ShowsError()
    {
        var (vm, _, adapters) = Create();
        adapters.Adapters = [];
        adapters.DefaultId = null;
        vm.Initialize();
        Assert.Contains("No active network adapter", vm.ErrorMessage);
    }
}
