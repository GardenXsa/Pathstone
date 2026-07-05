using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace MyGame.Desktop.Views;

/// <summary>
/// Code-behind for the main menu view. The view is purely
/// data-bound — no event handlers, no manual control updates.
/// </summary>
public partial class MainMenuView : UserControl
{
    public MainMenuView()
    {
        InitializeComponent();
    }
}
