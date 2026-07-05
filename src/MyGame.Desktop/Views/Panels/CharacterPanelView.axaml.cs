using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MyGame.Desktop.Views.Panels;

public partial class CharacterPanelView : UserControl
{
    public CharacterPanelView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
