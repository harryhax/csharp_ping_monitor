using System.Net;
using NetworkDiagnostics.App.Platforms;
using NetworkDiagnostics.Core.Export;
using NetworkDiagnostics.Core.Models;
using NetworkDiagnostics.Core.Monitoring;
using Xunit.Abstractions;

namespace NetworkDiagnostics.Tests;

/// <summary>
/// Real-network checks. Run with: ND_INTEGRATION=1 dotnet test --filter Category=Integration
/// </summary>
[Trait("Category", "Integration")]
public class IntegrationTests(ITestOutputHelper output)
{
    private readonly SystemNetworkInterfaceProvider _provider = new();
    private readonly PlatformPingProbeFactory _factory = new();

    [IntegrationFact]
    public void Adapters_AreEnumerated_AndDefaultHasGateway()
    {
        var adapters = _provider.GetAdapters();
        foreach (var a in adapters)
        {
            output.WriteLine($"{a.Id} | {a.Name} | {a.KindText} | {a.IPv4Address} | gw {a.IPv4Gateway} | {a.StatusText} | idx {a.InterfaceIndex}");
        }

        var defaultId = _provider.GetSystemDefaultAdapterId();
        output.WriteLine("Default: " + defaultId);
        Assert.NotEmpty(adapters);
        Assert.NotNull(defaultId);
        Assert.NotNull(adapters.Single(a => a.Id == defaultId).IPv4Gateway);
    }

    [IntegrationFact]
    public async Task ExplicitlyBoundProbe_ReachesGateway()
    {
        var adapter = DefaultAdapter();
        var selection = new AdapterSelection(false, adapter);
        var check = _factory.CheckSupport(selection);
        output.WriteLine(check.Message);
        Assert.True(check.IsSupported, check.Message);

        using var probe = _factory.Create(selection);
        output.WriteLine(probe.RoutingDescription);
        var reply = await probe.SendAsync(adapter.IPv4Gateway!, TimeSpan.FromSeconds(2), CancellationToken.None);
        output.WriteLine($"{reply.Status} {reply.RoundTripMs} {reply.Detail}");
        Assert.Equal(ProbeStatus.Success, reply.Status);
        Assert.True(reply.RoundTripMs > 0);
    }

    [IntegrationFact]
    public async Task AutomaticProbe_ReachesGateway()
    {
        var adapter = DefaultAdapter();
        using var probe = _factory.Create(new AdapterSelection(true, adapter));
        var reply = await probe.SendAsync(adapter.IPv4Gateway!, TimeSpan.FromSeconds(2), CancellationToken.None);
        output.WriteLine($"{reply.Status} {reply.RoundTripMs} {reply.Detail}");
        Assert.Equal(ProbeStatus.Success, reply.Status);
    }

    [IntegrationFact]
    public async Task UnroutableAddress_TimesOut_NotZeroLatency()
    {
        var adapter = DefaultAdapter();
        using var probe = _factory.Create(new AdapterSelection(false, adapter));
        // 192.0.2.0/24 is TEST-NET-1: documentation-only, never answers.
        var reply = await probe.SendAsync(IPAddress.Parse("192.0.2.1"), TimeSpan.FromMilliseconds(800), CancellationToken.None);
        output.WriteLine($"{reply.Status} {reply.Detail}");
        Assert.NotEqual(ProbeStatus.Success, reply.Status);
        Assert.Null(reply.RoundTripMs);
    }

    /// <summary>Generates docs/sample-report/ from a real 20-second session when ND_SAMPLE_REPORT is set to an output directory.</summary>
    [IntegrationFact]
    public async Task GenerateSampleReport()
    {
        var outDir = Environment.GetEnvironmentVariable("ND_SAMPLE_REPORT");
        var adapter = DefaultAdapter();
        var settings = new AppSettings { SelectedAdapterId = adapter.Id, DurationMinutes = 1 };
        var validation = SessionOptionsBuilder.Build(settings, new AdapterSelection(false, adapter), _factory);
        Assert.True(validation.IsValid, string.Join("; ", validation.Errors));

        var runner = new DiagnosticSessionRunner(_factory, new DnsHostResolver(), _provider, TimeProvider.System, "1.0.0");
        var session = runner.Start(validation.Options! with { Duration = TimeSpan.FromSeconds(20) });
        await runner.Completion;

        output.WriteLine($"Internal: {session.InternalStatistics}");
        output.WriteLine($"External: {session.ExternalStatistics}");
        Assert.Equal(SessionEndReason.Completed, session.EndReason);
        Assert.InRange(session.InternalStatistics.Sent, 18, 20);
        Assert.InRange(session.ExternalStatistics.Sent, 18, 20);

        if (!string.IsNullOrEmpty(outDir))
        {
            Directory.CreateDirectory(outDir);
            var zipPath = Path.Combine(outDir, "sample-report.zip");
            await using (var fs = File.Create(zipPath))
            {
                await SessionReportExporter.ExportAsync(session, fs, DateTimeOffset.UtcNow);
            }

            System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, Path.Combine(outDir, "extracted"), overwriteFiles: true);
            output.WriteLine("Wrote " + zipPath);
        }
    }

    private AdapterInfo DefaultAdapter()
    {
        var id = _provider.GetSystemDefaultAdapterId() ?? throw new InvalidOperationException("No default adapter.");
        return _provider.GetAdapters().Single(a => a.Id == id);
    }
}
