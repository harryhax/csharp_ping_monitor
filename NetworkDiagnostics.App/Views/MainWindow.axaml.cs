using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using NetworkDiagnostics.App.Models;
using NetworkDiagnostics.App.ViewModels;

namespace NetworkDiagnostics.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        Closing += (_, _) => (DataContext as MainWindowViewModel)?.OnClosing();
    }

    private async void OnDiscordClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            await Launcher.LaunchUriAsync(new Uri(Links.DiscordInvite));
        }
        catch
        {
            // Opening a browser is best effort; the link is also in the tooltip and README.
        }
    }
}
