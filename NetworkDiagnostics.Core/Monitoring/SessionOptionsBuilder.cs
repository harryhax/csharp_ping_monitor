using NetworkDiagnostics.Core.Interfaces;
using NetworkDiagnostics.Core.Models;

namespace NetworkDiagnostics.Core.Monitoring;

public sealed record SessionValidation(SessionOptions? Options, IReadOnlyList<string> Errors)
{
    public bool IsValid => Options is not null && Errors.Count == 0;
}

/// <summary>Validates user settings against the current adapter state and builds <see cref="SessionOptions"/>.</summary>
public static class SessionOptionsBuilder
{
    public const int MinIntervalMs = 200;
    public const int MaxIntervalMs = 60_000;
    public const int MinTimeoutMs = 100;
    public const int MaxTimeoutMs = 30_000;

    public static SessionValidation Build(AppSettings settings, AdapterSelection selection, IPingProbeFactory probeFactory)
    {
        var errors = new List<string>();

        // 1. Adapter
        if (selection.IsAutomatic)
        {
            if (selection.Adapter is null)
            {
                errors.Add("No active network connection was found. Check that Wi-Fi or Ethernet is connected.");
            }
        }
        else
        {
            var a = selection.Adapter!;
            if (!a.IsUp)
            {
                errors.Add($"The selected adapter \"{a.Name}\" is disconnected.");
            }
            else if (a.IPv4Address is null)
            {
                errors.Add($"The selected adapter \"{a.Name}\" has no IPv4 address.");
            }
        }

        // 2. Targets
        string? internalTarget = null;
        if (settings.UseInternalTargetOverride)
        {
            var v = TargetValidator.Validate(settings.InternalTargetOverride);
            if (v.IsValid)
            {
                internalTarget = v.Normalized;
            }
            else
            {
                errors.Add("Internal target: " + v.Error);
            }
        }
        else if (selection.Gateway is { } gw)
        {
            internalTarget = gw.ToString();
        }
        else if (selection.Adapter is not null)
        {
            errors.Add($"No default gateway was detected for \"{selection.Adapter.Name}\". Enable the manual internal target in Advanced settings to enter one.");
        }

        var external = TargetValidator.Validate(settings.ExternalTarget);
        if (!external.IsValid)
        {
            errors.Add("External target: " + external.Error);
        }

        // 3. Timing
        if (!AppSettings.AllowedDurationsMinutes.Contains(settings.DurationMinutes))
        {
            errors.Add($"Test duration must be one of {string.Join(", ", AppSettings.AllowedDurationsMinutes)} minutes.");
        }

        if (settings.PingIntervalMs is < MinIntervalMs or > MaxIntervalMs)
        {
            errors.Add($"Ping interval must be between {MinIntervalMs} and {MaxIntervalMs} ms.");
        }

        if (settings.PingTimeoutMs is < MinTimeoutMs or > MaxTimeoutMs)
        {
            errors.Add($"Ping timeout must be between {MinTimeoutMs} and {MaxTimeoutMs} ms.");
        }

        // 4. Binding support (only meaningful when the adapter itself is usable)
        if (errors.Count == 0)
        {
            var check = probeFactory.CheckSupport(selection);
            if (!check.IsSupported)
            {
                errors.Add(check.Message);
            }
        }

        if (errors.Count > 0)
        {
            return new SessionValidation(null, errors);
        }

        return new SessionValidation(
            new SessionOptions
            {
                Adapter = selection,
                InternalTarget = internalTarget!,
                InternalTargetIsGateway = !settings.UseInternalTargetOverride,
                ExternalTarget = external.Normalized,
                Duration = TimeSpan.FromMinutes(settings.DurationMinutes),
                Interval = TimeSpan.FromMilliseconds(settings.PingIntervalMs),
                Timeout = TimeSpan.FromMilliseconds(settings.PingTimeoutMs),
                Thresholds = settings.Thresholds.Clone(),
            },
            errors);
    }
}
