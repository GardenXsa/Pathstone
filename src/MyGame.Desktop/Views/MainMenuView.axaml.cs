using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using MyGame.Desktop.ViewModels;

namespace MyGame.Desktop.Views;

/// <summary>
/// Code-behind for the main menu view. Mostly data-bound; the nickname
/// TextBox uses KeyDown / LostFocus handlers to trigger save-on-Enter /
/// save-on-blur without needing a separate Save button.
/// </summary>
public partial class MainMenuView : UserControl
{
    public MainMenuView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Enter → save nickname. Other keys pass through to the TextBox.
    /// (Esc intentionally does nothing — the validation error clears
    /// automatically on the next successful save.)
    /// </summary>
    private void OnNicknameKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainMenuViewModel vm) return;
        if (e.Key == Key.Enter)
        {
            vm.SaveNicknameCommand.Execute(null);
            e.Handled = true;
        }
    }

    /// <summary>Lost focus → save nickname (so edits don't get lost).</summary>
    private void OnNicknameLostFocus(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainMenuViewModel vm) return;
        vm.SaveNicknameCommand.Execute(null);
    }
}
