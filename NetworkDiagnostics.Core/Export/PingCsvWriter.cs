using System.Globalization;
using System.Text;
using NetworkDiagnostics.Core.Models;

namespace NetworkDiagnostics.Core.Export;

/// <summary>Writes measurements as RFC 4180 CSV with invariant number formatting and ISO-8601 UTC timestamps.</summary>
public static class PingCsvWriter
{
    public static readonly string[] Header =
    [
        "session_id", "sequence", "timestamp_utc", "scheduled_offset_ms", "sent_offset_ms", "adapter_id",
        "adapter_local_ip", "target_type", "target", "resolved_ip", "rtt_ms", "status", "error_detail",
    ];

    public static void Write(TextWriter writer, IEnumerable<PingResult> results)
    {
        writer.Write(string.Join(',', Header));
        writer.Write("\r\n");
        foreach (var r in results)
        {
            WriteRow(writer,
            [
                r.SessionId.ToString("D"),
                r.Sequence.ToString(CultureInfo.InvariantCulture),
                JsonDefaults.FormatUtc(r.TimestampUtc),
                FormatNumber(r.ScheduledOffsetMs),
                FormatNumber(r.SentOffsetMs),
                r.AdapterId,
                r.AdapterLocalIp,
                r.TargetType.ToString(),
                r.Target,
                r.ResolvedIp,
                r.RoundTripMs is { } rtt ? FormatNumber(rtt) : string.Empty,
                r.Status.ToString(),
                r.ErrorDetail,
            ]);
        }
    }

    public static string ToCsv(IEnumerable<PingResult> results)
    {
        var sb = new StringBuilder();
        using var w = new StringWriter(sb, CultureInfo.InvariantCulture);
        Write(w, results);
        return sb.ToString();
    }

    public static string FormatNumber(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    public static string Escape(string? field)
    {
        if (string.IsNullOrEmpty(field))
        {
            return string.Empty;
        }

        var needsQuotes = field.AsSpan().IndexOfAny(",\"\r\n") >= 0 || field[0] == ' ' || field[^1] == ' ';
        return needsQuotes ? "\"" + field.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : field;
    }

    private static void WriteRow(TextWriter writer, string?[] fields)
    {
        for (var i = 0; i < fields.Length; i++)
        {
            if (i > 0)
            {
                writer.Write(',');
            }

            writer.Write(Escape(fields[i]));
        }

        writer.Write("\r\n");
    }
}
