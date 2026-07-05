using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using MyGame.Desktop.ViewModels;

namespace MyGame.Desktop.Views;

public partial class WorldBuildView : UserControl
{
    public WorldBuildView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
