using System.Linq;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using MyGame.Desktop.ViewModels;
using System.Collections.Specialized;

namespace MyGame.Desktop.Views;

/// <summary>
/// Code-behind for the game view. Handles two UI-only concerns the
/// XAML can't express cleanly:
/// <list type="bullet">
///   <item>Auto-scrolling the narrative log to the bottom when new
///     entries arrive (subscribe to the Log collection's
///     <see cref="INotifyCollectionChanged.CollectionChanged"/> event).</item>
///   <item>Pressing Enter in the action input submits the action
///     (without needing a global keybinding).</item>
/// </list>
/// </summary>
public partial class GameView : UserControl
{
    private GameViewModel? _vm;
    private bool _autoScroll = true;

    public GameView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        // Track scroll position so the user can scroll up without the
        // view yanking them back down.
        NarrativeScroll.ScrollChanged += OnScrollChanged;
    }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_vm is not null)
        {
            ((INotifyCollectionChanged)_vm.Log).CollectionChanged -= OnLogChanged;
        }
        _vm = DataContext as GameViewModel;
        if (_vm is not null)
        {
            ((INotifyCollectionChanged)_vm.Log).CollectionChanged += OnLogChanged;
        }
    }

    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        // If the user scrolled away from the bottom, pause auto-scroll.
        var sv = (ScrollViewer)sender!;
        _autoScroll = sv.Offset.Y + sv.Viewport.Height >= sv.Extent.Height - 24;
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_autoScroll && e.Action == NotifyCollectionChangedAction.Add)
        {
            // Defer to next frame so the new items have actually laid
            // out before we try to scroll to them.
            NarrativeScroll.ScrollToEnd();
        }
    }

    /// <summary>
    /// Enter (without Shift) submits the action; Shift+Enter inserts a
    /// newline. Escape clears the input.
    /// </summary>
    private void OnActionKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        if (e.Key == Key.Enter && (e.KeyModifiers & KeyModifiers.Shift) == 0)
        {
            if (_vm.SubmitActionCommand.CanExecute(null))
            {
                _vm.SubmitActionCommand.Execute(null);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Escape)
        {
            _vm.CurrentAction = string.Empty;
            e.Handled = true;
        }
    }
}
