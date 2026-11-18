using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using NetworkDiagnostics.Core.Configuration;
using NetworkDiagnostics.Core.Export;
using NetworkDiagnostics.Core.Models;
using NetworkDiagnostics.Core.Monitoring;

namespace NetworkDiagnostics.Tests;

public sealed class ConfigurationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nd-tests-" + Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_dir, "sub", "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    [Fact]
    public void RoundTrip_PreservesAllValues()
    {
        var store = new JsonSettingsStore(FilePath);
        var original = new AppSettings
        {
            SelectedAdapterId = "en5",
            UseInternalTargetOverride = true,
            InternalTargetOverride = "10.0.0.254",
            ExternalTarget = "one.one.one.one",
            DurationMinutes = 30,
            PingIntervalMs = 500,
            PingTimeoutMs = 1500,
            Thresholds = new StatusThresholds { InternalElevatedLatencyMs = 42, RedLossPercent = 15 },
        };

        store.Save(original);
        var loaded = new JsonSettingsStore(FilePath).Load();

        Assert.Equal(JsonSettingsStore.Serialize(original), JsonSettingsStore.Serialize(loaded));
        Assert.Equal("en5", loaded.SelectedAdapterId);
        Assert.Equal(42, loaded.Thresholds.InternalElevatedLatencyMs);
    }

    [Fact]
    public void MissingFile_ReturnsDefaults()
    {
        var s = new JsonSettingsStore(FilePath).Load();
        Assert.Null(s.SelectedAdapterId);
        Assert.Equal("8.8.8.8", s.ExternalTarget);
        Assert.Equal(10, s.DurationMinutes);
        Assert.Equal(1000, s.PingIntervalMs);
    }

    [Fact]
    public void CorruptFile_ReturnsDefaults_WithError()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, "{ this is not json");
        var store = new JsonSettingsStore(FilePath);
        var s = store.Load();
        Assert.Equal("8.8.8.8", s.ExternalTarget);
        Assert.NotNull(store.LastError);
    }

    [Fact]
    public void InvalidValues_AreSanitized()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, """{ "durationMinutes": 7, "pingIntervalMs": 5, "pingTimeoutMs": -1, "externalTarget": "", "thresholds": null }""");
        var s = new JsonSettingsStore(FilePath).Load();
        Assert.Equal(10, s.DurationMinutes);
        Assert.Equal(1000, s.PingIntervalMs);
        Assert.Equal(2000, s.PingTimeoutMs);
        Assert.Equal("8.8.8.8", s.ExternalTarget);
        Assert.NotNull(s.Thresholds);
    }

    [Fact]
    public void UnknownProperties_AreIgnored()
    {
        var s = JsonSettingsStore.Deserialize("""{ "externalTarget": "1.1.1.1", "futureSetting": 123 }""");
        Assert.Equal("1.1.1.1", s.ExternalTarget);
    }
}

public class CsvExportTests
{
    [Fact]
    public void Header_AndRows_AreWritten()
    {
        var csv = PingCsvWriter.ToCsv([TestData.Result(ProbeStatus.Success, 12.3456, 1), TestData.Result(ProbeStatus.Timeout, null, 2, detail: "Request timed out.")]);
        var lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(3, lines.Length);
        Assert.Equal(string.Join(',', PingCsvWriter.Header), lines[0]);
        Assert.Equal("11111111-2222-3333-4444-555555555555,1,2026-11-17T09:30:01.000Z,0,0.25,en0,192.168.1.20,Internal,192.168.1.1,192.168.1.1,12.346,Success,", lines[1]);
        Assert.Equal("11111111-2222-3333-4444-555555555555,2,2026-11-17T09:30:02.000Z,1000,1000.25,en0,192.168.1.20,Internal,192.168.1.1,192.168.1.1,,Timeout,Request timed out.", lines[2]);
    }

    [Fact]
    public void UsesInvariantFormatting_RegardlessOfCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var csv = PingCsvWriter.ToCsv([TestData.Result(ProbeStatus.Success, 1234.5, 1)]);
            Assert.Contains(",1234.5,", csv);
            Assert.DoesNotContain("1234,5", csv);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("line1\nline2", "\"line1\nline2\"")]
    [InlineData(null, "")]
    public void Escaping(string? input, string expected) => Assert.Equal(expected, PingCsvWriter.Escape(input));

    [Fact]
    public void FailedRows_ArePreserved_WithEmptyRtt()
    {
        var csv = PingCsvWriter.ToCsv([TestData.Result(ProbeStatus.DestinationUnreachable, null, 1, detail: "Host unreachable, reported by 10.0.0.1")]);
        var row = csv.Split("\r\n")[1];
        Assert.Contains(",,DestinationUnreachable,\"Host unreachable, reported by 10.0.0.1\"", row);
    }
}

public class ZipExportTests
{
    private static async Task<DiagnosticSession> RunShortSessionAsync(bool stop)
    {
        var factory = new FakeProbeFactory(() => new FakeProbe(n => n % 3 == 0 ? ProbeReply.Fail(ProbeStatus.Timeout, "Request timed out.") : ProbeReply.Ok(5 + n)));
        var runner = new DiagnosticSessionRunner(factory, new FakeResolver(), new FakeAdapterProvider { Adapters = [TestData.WiFi()], DefaultId = "en0" }, TimeProvider.System, "1.0.0-test");
        var session = runner.Start(TestData.Options(duration: stop ? TimeSpan.FromMinutes(5) : TimeSpan.FromMilliseconds(300), interval: TimeSpan.FromMilliseconds(50)));
        if (stop)
        {
            await Task.Delay(250);
            await runner.StopAsync();
        }
        else
        {
            await runner.Completion;
        }

        return session;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Archive_ContainsAllFiles_WithConsistentData(bool stopped)
    {
        var session = await RunShortSessionAsync(stopped);
        using var ms = new MemoryStream();
        await SessionReportExporter.ExportAsync(session, ms, DateTimeOffset.UtcNow);
        ms.Position = 0;

        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        Assert.Equal(
            new[] { "external-ping.csv", "internal-ping.csv", "session.json", "summary.json" },
            zip.Entries.Select(e => e.FullName).Order());

        var sessionJson = JsonDocument.Parse(Read(zip, "session.json")).RootElement;
        Assert.Equal(session.Id.ToString(), sessionJson.GetProperty("sessionId").GetString());
        Assert.Equal("1.0.0-test", sessionJson.GetProperty("applicationVersion").GetString());
        Assert.Equal(stopped ? "Stopped" : "Completed", sessionJson.GetProperty("endReason").GetString());
        Assert.EndsWith("Z", sessionJson.GetProperty("startedUtc").GetString());
        Assert.Equal("192.168.1.1", sessionJson.GetProperty("adapter").GetProperty("defaultGateway").GetString());
        Assert.Equal("192.168.1.20", sessionJson.GetProperty("adapter").GetProperty("localIPv4").GetString());
        Assert.Equal(50, sessionJson.GetProperty("pingIntervalMs").GetInt32());
        Assert.Equal("8.8.8.8", sessionJson.GetProperty("targets").GetProperty("external").GetProperty("target").GetString());
        Assert.True(sessionJson.GetProperty("platform").TryGetProperty("operatingSystem", out _));
        Assert.True(sessionJson.GetProperty("events").GetArrayLength() >= 2);

        var internalLines = Read(zip, "internal-ping.csv").Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(session.GetResults(TargetType.Internal).Count + 1, internalLines.Length);
        Assert.Contains(internalLines, l => l.Contains(",Timeout,"));

        var summary = JsonDocument.Parse(Read(zip, "summary.json")).RootElement;
        Assert.Equal(session.GetResults(TargetType.Internal).Count, summary.GetProperty("internal").GetProperty("sent").GetInt64());
        var loss = summary.GetProperty("internal").GetProperty("packetLossPercent").GetDouble();
        var expected = Core.Statistics.StatisticsAccumulator.Compute(session.GetResults(TargetType.Internal)).PacketLossPercent!.Value;
        Assert.Equal(Math.Round(expected, 3), loss);
        Assert.True(summary.GetProperty("internal").GetProperty("statusCounts").TryGetProperty("Timeout", out _));
    }

    [Fact]
    public async Task Export_ContainsNoMachineOrUserIdentifiers()
    {
        var session = await RunShortSessionAsync(false);
        using var ms = new MemoryStream();
        await SessionReportExporter.ExportAsync(session, ms, DateTimeOffset.UtcNow);
        ms.Position = 0;
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        var all = string.Concat(zip.Entries.Select(e => Read(zip, e.FullName)));
        Assert.DoesNotContain(Environment.MachineName, all, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Environment.UserName, all, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SuggestedFileName_IsUtcStamped()
    {
        var options = TestData.Options();
        var session = new DiagnosticSession(Guid.NewGuid(), options, new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.Zero), PlatformDescription.Current(), "1.0.0");
        Assert.Equal("network-diagnostics-20260102-030405Z.zip", SessionReportExporter.SuggestedFileName(session));
    }

    private static string Read(ZipArchive zip, string name)
    {
        using var reader = new StreamReader(zip.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }
}
