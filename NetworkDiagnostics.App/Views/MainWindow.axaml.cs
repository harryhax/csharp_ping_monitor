using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using NetworkDiagnostics.App.ViewModels;

namespace NetworkDiagnostics.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        AvaloniaXamlLoader.Load(this);
        Closing += (_, _) => (DataContext as MainWindowViewModel)?.OnClosing();
    }
}
