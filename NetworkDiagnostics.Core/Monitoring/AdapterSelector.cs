using NetworkDiagnostics.Core.Models;

namespace NetworkDiagnostics.Core.Monitoring;

public sealed record AdapterSelectionResult(AdapterSelection Selection, string? Warning);

/// <summary>Pure logic for mapping a saved adapter id to the current adapter list.</summary>
public static class AdapterSelector
{
    public static bool IsAutomatic(string? id) =>
        string.IsNullOrWhiteSpace(id) || string.Equals(id, AdapterSelection.AutomaticId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves <paramref name="selectedId"/> against <paramref name="adapters"/>. A saved adapter that no longer
    /// exists falls back to Automatic with a warning, rather than silently picking a different adapter.
    /// </summary>
    public static AdapterSelectionResult Resolve(string? selectedId, IReadOnlyList<AdapterInfo> adapters, string? systemDefaultId)
    {
        if (!IsAutomatic(selectedId))
        {
            var match = adapters.FirstOrDefault(a => string.Equals(a.Id, selectedId, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return new AdapterSelectionResult(new AdapterSelection(false, match), null);
            }

            var auto = Automatic(adapters, systemDefaultId);
            return new AdapterSelectionResult(auto, $"The previously selected adapter ({selectedId}) is not available. Using Automatic (System Default).");
        }

        return new AdapterSelectionResult(Automatic(adapters, systemDefaultId), null);
    }

    public static AdapterSelection Automatic(IReadOnlyList<AdapterInfo> adapters, string? systemDefaultId)
    {
        var def = systemDefaultId is null
            ? null
            : adapters.FirstOrDefault(a => string.Equals(a.Id, systemDefaultId, StringComparison.OrdinalIgnoreCase));
        return new AdapterSelection(true, def);
    }

    /// <summary>The internal target the UI should show: manual override if enabled, otherwise the adapter gateway.</summary>
    public static string? EffectiveInternalTarget(AdapterSelection selection, bool useOverride, string? overrideTarget) =>
        useOverride ? overrideTarget?.Trim() : selection.Gateway?.ToString();
}
