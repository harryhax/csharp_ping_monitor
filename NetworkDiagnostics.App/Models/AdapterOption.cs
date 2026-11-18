using NetworkDiagnostics.Core.Models;

namespace NetworkDiagnostics.App.Models;

/// <summary>An entry in the adapter dropdown.</summary>
public sealed record AdapterOption(string Id, AdapterInfo? Adapter, bool IsAutomatic)
{
    public string Title => IsAutomatic
        ? "Automatic (System Default)"
        : Adapter!.Name;

    public string Details => IsAutomatic
        ? Adapter is null ? "No default connection detected" : $"Currently {Adapter.Name} · {Adapter.IPv4Address}"
        : $"{Adapter!.KindText} · {Adapter.IPv4Address?.ToString() ?? "no IPv4"} · {Adapter.ConnectionText}";
}
