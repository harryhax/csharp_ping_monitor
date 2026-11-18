using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace NetworkDiagnostics.App.Views;

public partial class MonitorPanelView : UserControl
{
    public static readonly StyledProperty<IBrush> GraphBrushProperty =
        AvaloniaProperty.Register<MonitorPanelView, IBrush>(nameof(GraphBrush), Brushes.DodgerBlue);

    public MonitorPanelView()
    {
        InitializeComponent();
    }

    public IBrush GraphBrush { get => GetValue(GraphBrushProperty); set => SetValue(GraphBrushProperty, value); }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
