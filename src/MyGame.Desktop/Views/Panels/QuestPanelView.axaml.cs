using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MyGame.Desktop.Views.Panels;

public partial class QuestPanelView : UserControl
{
    public QuestPanelView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
