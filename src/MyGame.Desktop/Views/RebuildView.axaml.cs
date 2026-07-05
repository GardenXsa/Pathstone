using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MyGame.Desktop.Views;

public partial class RebuildView : UserControl
{
    public RebuildView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
