using System.Reflection;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using NetworkDiagnostics.App.Platforms;
using NetworkDiagnostics.App.Services;
using NetworkDiagnostics.App.ViewModels;
using NetworkDiagnostics.Core.Configuration;
using NetworkDiagnostics.Core.Interfaces;
using NetworkDiagnostics.Core.Monitoring;

namespace NetworkDiagnostics.App.Configuration;

/// <summary>Composition root.</summary>
public static class ServiceRegistration
{
    public static string AppVersion =>
        typeof(ServiceRegistration).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "1.0.0";

    public static IServiceProvider Build(TopLevel topLevel)
    {
        var services = new ServiceCollection();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<INetworkInterfaceProvider, SystemNetworkInterfaceProvider>();
        services.AddSingleton<IPingProbeFactory, PlatformPingProbeFactory>();
        services.AddSingleton<IHostResolver, DnsHostResolver>();
        services.AddSingleton<ISettingsStore>(_ => new JsonSettingsStore(JsonSettingsStore.DefaultPath()));
        services.AddSingleton<IFileSaveService>(_ => new AvaloniaFileSaveService(topLevel));
        services.AddSingleton<IUiDispatcher, AvaloniaUiDispatcher>();
        services.AddSingleton<INetworkChangeNotifier, SystemNetworkChangeNotifier>();
        services.AddSingleton(sp => new DiagnosticSessionRunner(
            sp.GetRequiredService<IPingProbeFactory>(),
            sp.GetRequiredService<IHostResolver>(),
            sp.GetRequiredService<INetworkInterfaceProvider>(),
            sp.GetRequiredService<TimeProvider>(),
            AppVersion));
        services.AddSingleton<MainWindowViewModel>();
        return services.BuildServiceProvider();
    }
}
