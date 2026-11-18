using System.IO.Compression;
using System.Text;
using System.Text.Json;
using NetworkDiagnostics.Core.Models;
using NetworkDiagnostics.Core.Statistics;

namespace NetworkDiagnostics.Core.Export;

/// <summary>Builds the diagnostic ZIP archive: session.json, internal-ping.csv, external-ping.csv, summary.json.</summary>
public static class SessionReportExporter
{
    public const string SessionFile = "session.json";
    public const string InternalCsvFile = "internal-ping.csv";
    public const string ExternalCsvFile = "external-ping.csv";
    public const string SummaryFile = "summary.json";

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static string SuggestedFileName(DiagnosticSession session) =>
        $"network-diagnostics-{session.StartedUtc.UtcDateTime:yyyyMMdd-HHmmss}Z.zip";

    /// <summary>Writes the archive to <paramref name="destination"/>. The archive is fully built in memory first,
    /// so a failure never leaves a half-written report.</summary>
    public static async Task ExportAsync(DiagnosticSession session, Stream destination, DateTimeOffset generatedUtc, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        Write(session, buffer, generatedUtc);
        buffer.Position = 0;
        await buffer.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static void Write(DiagnosticSession session, Stream destination, DateTimeOffset generatedUtc)
    {
        var internalResults = session.GetResults(TargetType.Internal);
        var externalResults = session.GetResults(TargetType.External);

        using var zip = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        AddText(zip, SessionFile, JsonSerializer.Serialize(BuildSessionReport(session), JsonDefaults.Options));
        AddText(zip, InternalCsvFile, PingCsvWriter.ToCsv(internalResults));
        AddText(zip, ExternalCsvFile, PingCsvWriter.ToCsv(externalResults));
        AddText(zip, SummaryFile, JsonSerializer.Serialize(BuildSummary(session, internalResults, externalResults, generatedUtc), JsonDefaults.Options));
    }

    public static SessionReport BuildSessionReport(DiagnosticSession session)
    {
        var o = session.Options;
        var a = o.Adapter.Adapter;
        return new SessionReport(
            session.Id,
            session.AppVersion,
            session.StartedUtc,
            session.EndedUtc,
            session.EndReason,
            o.Duration.TotalSeconds,
            session.ActualDuration is { } d ? Math.Round(d.TotalSeconds, 3) : null,
            new AdapterReport(
                o.Adapter.IsAutomatic ? "Automatic" : "Explicit",
                a?.Id,
                a?.Name,
                a?.KindText,
                a?.IPv4Address?.ToString(),
                a?.IPv4Gateway?.ToString(),
                a?.ConnectionText,
                session.RoutingDescription),
            new TargetsReport(
                new TargetReport(o.InternalTarget, o.InternalTargetIsGateway ? "DefaultGateway" : "ManualOverride"),
                new TargetReport(o.ExternalTarget, "Configured")),
            (int)o.Interval.TotalMilliseconds,
            (int)o.Timeout.TotalMilliseconds,
            o.Thresholds,
            session.Platform,
            session.GetEvents().Select(e => e with { OffsetMs = Math.Round(e.OffsetMs, 3) }).ToList());
    }

    public static SummaryReport BuildSummary(DiagnosticSession session, IReadOnlyList<PingResult> internalResults, IReadOnlyList<PingResult> externalResults, DateTimeOffset generatedUtc) =>
        new(
            session.Id,
            generatedUtc,
            "packetLossPercent = failed / completed * 100; completed excludes probes cancelled when the session was stopped; latency figures use successful replies only.",
            Summarize(session.Options.InternalTarget, internalResults),
            Summarize(session.Options.ExternalTarget, externalResults));

    public static TargetSummary Summarize(string target, IReadOnlyList<PingResult> results)
    {
        var s = StatisticsAccumulator.Compute(results);
        return new TargetSummary(
            target,
            results.LastOrDefault(r => r.ResolvedIp is not null)?.ResolvedIp,
            s.Sent,
            s.Completed,
            s.Received,
            s.Failed,
            s.Cancelled,
            Round(s.PacketLossPercent),
            Round(s.MinMs),
            Round(s.AverageMs),
            Round(s.MaxMs),
            results.GroupBy(r => r.Status).OrderBy(g => g.Key).ToDictionary(g => g.Key.ToString(), g => (long)g.Count()));
    }

    private static double? Round(double? v) => v is { } x ? Math.Round(x, 3) : null;

    private static void AddText(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, Utf8NoBom);
        writer.Write(content);
    }
}
