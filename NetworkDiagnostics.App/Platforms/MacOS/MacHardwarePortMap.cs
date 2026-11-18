using System.Diagnostics;

namespace NetworkDiagnostics.App.Platforms.MacOS;

/// <summary>
/// Maps BSD device names (en0) to macOS hardware port names (Wi-Fi) using
/// <c>networksetup -listallhardwareports</c>. Read-only; cached for the app lifetime.
/// </summary>
public sealed class MacHardwarePortMap
{
    private readonly Lazy<IReadOnlyDictionary<string, string>> _map = new(Load);

    public string? TryGet(string device) => _map.Value.TryGetValue(device, out var port) ? port : null;

    /// <summary>Parses networksetup output. Public for tests.</summary>
    public static IReadOnlyDictionary<string, string> Parse(string output)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        string? port = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("Hardware Port:", StringComparison.Ordinal))
            {
                port = line["Hardware Port:".Length..].Trim();
            }
            else if (line.StartsWith("Device:", StringComparison.Ordinal) && port is not null)
            {
                map[line["Device:".Length..].Trim()] = port;
                port = null;
            }
        }

        return map;
    }

    private static IReadOnlyDictionary<string, string> Load()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("/usr/sbin/networksetup", "-listallhardwareports")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null)
            {
                return new Dictionary<string, string>();
            }

            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            return Parse(output);
        }
        catch (Exception)
        {
            return new Dictionary<string, string>();
        }
    }
}
