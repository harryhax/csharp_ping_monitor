using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using NetworkDiagnostics.App.Configuration;
using NetworkDiagnostics.App.ViewModels;
using NetworkDiagnostics.App.Views;

namespace NetworkDiagnostics.App;

public partial class App : Application
{
    public IServiceProvider? Services { get; private set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            Services = ServiceRegistration.Build(window);
            var vm = Services.GetRequiredService<MainWindowViewModel>();
            window.DataContext = vm;
            desktop.MainWindow = window;
            vm.Initialize();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
