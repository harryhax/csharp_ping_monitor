using NetworkDiagnostics.App.Platforms.MacOS;
using NetworkDiagnostics.App.Platforms.Windows;
using NetworkDiagnostics.Core.Interfaces;
using NetworkDiagnostics.Core.Models;

namespace NetworkDiagnostics.App.Platforms;

/// <summary>Chooses the probe implementation for the platform and adapter selection.</summary>
public sealed class PlatformPingProbeFactory : IPingProbeFactory
{
    public BindingCheck CheckSupport(AdapterSelection selection)
    {
        if (selection.IsAutomatic)
        {
            return BindingCheck.Supported("Probes follow the operating system's default routing.");
        }

        var adapter = selection.Adapter!;
        if (OperatingSystem.IsWindows())
        {
            return WindowsSourceBoundIcmpProbe.Check(adapter);
        }

        if (OperatingSystem.IsMacOS())
        {
            return MacBoundIcmpProbe.Check(adapter);
        }

        return BindingCheck.Unsupported("Sending probes through a specific adapter is not supported on this platform. Choose \"Automatic (System Default)\".");
    }

    public IPingProbe Create(AdapterSelection selection)
    {
        if (selection.IsAutomatic)
        {
            return new SystemPingProbe();
        }

        var check = CheckSupport(selection);
        if (!check.IsSupported)
        {
            throw new NotSupportedException(check.Message);
        }

        if (OperatingSystem.IsWindows())
        {
            return new WindowsSourceBoundIcmpProbe(selection.Adapter!);
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacBoundIcmpProbe(selection.Adapter!);
        }

        throw new NotSupportedException(check.Message);
    }
}
