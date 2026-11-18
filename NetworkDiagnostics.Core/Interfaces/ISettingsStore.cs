using NetworkDiagnostics.Core.Models;

namespace NetworkDiagnostics.Core.Interfaces;

public interface ISettingsStore
{
    /// <summary>Loads settings; returns defaults (and never throws) when the file is missing or corrupt.</summary>
    AppSettings Load();

    void Save(AppSettings settings);
}
