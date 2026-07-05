using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MyGame.Desktop.Views.Panels;

public partial class InventoryPanelView : UserControl
{
    public InventoryPanelView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
