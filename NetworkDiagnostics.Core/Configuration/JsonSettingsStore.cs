using System.Text.Json;
using NetworkDiagnostics.Core.Export;
using NetworkDiagnostics.Core.Interfaces;
using NetworkDiagnostics.Core.Models;

namespace NetworkDiagnostics.Core.Configuration;

/// <summary>Stores <see cref="AppSettings"/> as JSON. Writes atomically via a temporary file.</summary>
public sealed class JsonSettingsStore : ISettingsStore
{
    public JsonSettingsStore(string filePath)
    {
        FilePath = filePath;
    }

    public string FilePath { get; }

    public string? LastError { get; private set; }

    /// <summary>Default per-user location: %LOCALAPPDATA%\NetworkDiagnostics (Windows) or ~/Library/Application Support/NetworkDiagnostics (macOS).</summary>
    public static string DefaultPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "NetworkDiagnostics", "settings.json");

    public AppSettings Load()
    {
        LastError = null;
        try
        {
            if (!File.Exists(FilePath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(FilePath);
            return Sanitize(Deserialize(json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LastError = $"Saved settings could not be read and defaults were used ({ex.Message}).";
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tmp = FilePath + ".tmp";
        File.WriteAllText(tmp, Serialize(settings));
        File.Move(tmp, FilePath, overwrite: true);
    }

    public static string Serialize(AppSettings settings) => JsonSerializer.Serialize(settings, JsonDefaults.Options);

    public static AppSettings Deserialize(string json) =>
        JsonSerializer.Deserialize<AppSettings>(json, JsonDefaults.Options) ?? new AppSettings();

    /// <summary>Replaces out-of-range values with defaults so a hand-edited file cannot break the app.</summary>
    public static AppSettings Sanitize(AppSettings s)
    {
        if (!AppSettings.AllowedDurationsMinutes.Contains(s.DurationMinutes))
        {
            s.DurationMinutes = AppSettings.DefaultDurationMinutes;
        }

        if (s.PingIntervalMs is < Monitoring.SessionOptionsBuilder.MinIntervalMs or > Monitoring.SessionOptionsBuilder.MaxIntervalMs)
        {
            s.PingIntervalMs = AppSettings.DefaultIntervalMs;
        }

        if (s.PingTimeoutMs is < Monitoring.SessionOptionsBuilder.MinTimeoutMs or > Monitoring.SessionOptionsBuilder.MaxTimeoutMs)
        {
            s.PingTimeoutMs = AppSettings.DefaultTimeoutMs;
        }

        if (string.IsNullOrWhiteSpace(s.ExternalTarget))
        {
            s.ExternalTarget = AppSettings.DefaultExternalTarget;
        }

        s.Thresholds ??= new StatusThresholds();
        if (s.Thresholds.WindowSize < 1)
        {
            s.Thresholds.WindowSize = new StatusThresholds().WindowSize;
        }

        return s;
    }
}
