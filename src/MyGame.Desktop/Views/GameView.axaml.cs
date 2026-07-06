using System.Linq;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MyGame.Desktop.ViewModels;
using System.Collections.Specialized;
using System.ComponentModel;

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
///   <item>KEYBOARD-SHORTCUTS (issue #51): global keyboard shortcuts
///     wired via the UserControl's <c>KeyDown</c> handler — Esc to
///     clear input, Ctrl+S to save, Ctrl+L to leave (with confirm),
///     Ctrl+Enter to submit + refocus, 1–4 to switch side-panel tabs,
///     F1 to toggle a help overlay.</item>
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
            
            // JUICE/IMPACT: Register global hover sound trigger for all interactive elements
            this.AddHandler(InputElement.PointerEnterEvent, OnPointerEnterGlobal, RoutingStrategies.Bubble);
        }

        private void OnPointerEnterGlobal(object? sender, PointerEventArgs e)
        {
            if (e.Source is Button || e.Source is TabItem)
            {
                MyGame.Desktop.Services.SoundService.Play(MyGame.Desktop.Services.SoundEffect.Click);
            }
        }

    private void OnDataContextChanged(object? sender, System.EventArgs e)
    {
        if (_vm is not null)
        {
            ((INotifyCollectionChanged)_vm.Log).CollectionChanged -= OnLogChanged;
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }
        _vm = DataContext as GameViewModel;
        if (_vm is not null)
        {
            ((INotifyCollectionChanged)_vm.Log).CollectionChanged += OnLogChanged;
            // Subscribe to VM property changes so we can auto-scroll the
            // streaming narrative TextBlock as it grows (the streaming
            // text isn't a Log entry, so the Log CollectionChanged
            // handler above doesn't fire for it).
            _vm.PropertyChanged += OnVmPropertyChanged;
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
    /// VM property-change handler. Used to auto-scroll the streaming
    /// narrative text as it grows (the StreamingNarrativeText property
    /// isn't backed by the Log collection, so OnLogChanged doesn't fire).
    /// </summary>
    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(GameViewModel.StreamingNarrativeText) && _autoScroll)
        {
            // Defer to next frame so the new text has actually laid out.
            NarrativeScroll.ScrollToEnd();
        }
    }

    /// <summary>
    /// Enter (without Shift) submits the action; Shift+Enter inserts a
    /// newline. Escape clears the input.
    ///
    /// <para>
    /// This handler is wired to the action input TextBox's
    /// <see cref="InputElement.KeyDown"/> event. It runs before the
    /// UserControl-level <see cref="OnGlobalKeyDown"/> handler
    /// (KeyDown bubbles from child to parent); when it sets
    /// <see cref="KeyEventArgs.Handled"/> = true, the global handler
    /// never sees the event.
    /// </para>
    /// </summary>
    private void OnActionKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        if (e.Key == Key.Enter && (e.KeyModifiers & KeyModifiers.Shift) == 0)
        {
            // Ctrl+Enter falls through to the global handler (which
            // also re-focuses the input); plain Enter just submits.
            if ((e.KeyModifiers & KeyModifiers.Control) == 0 &&
                _vm.SubmitActionCommand.CanExecute(null))
            {
                _vm.SubmitActionCommand.Execute(null);
                e.Handled = true;
            }
        }
        else if (e.Key == Key.Escape)
        {
            if (!string.IsNullOrEmpty(_vm.CurrentAction))
            {
                _vm.CurrentAction = string.Empty;
                e.Handled = true;
            }
        }
    }

    // ─── Global keyboard shortcuts (issue #51) ────────────────────────

    /// <summary>
    /// Global key handler wired on the UserControl. Routes the
    /// shortcut keys (Esc, Ctrl+S, Ctrl+L, Ctrl+Enter, 1–4, F1) to
    /// their actions. The TextBox-scoped <see cref="OnActionKeyDown"/>
    /// runs first when focus is in the input; this handler picks up
    /// everything else (and everything the TextBox handler didn't
    /// mark handled).
    /// </summary>
    private void OnGlobalKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null) return;

        // F1 toggles the help overlay first — works regardless of
        // other overlays being open.
        if (e.Key == Key.F1)
        {
            HelpToggle.IsChecked = !HelpToggle.IsChecked;
            if (HelpToggle.IsChecked == true) LeaveConfirmOverlay.IsVisible = false;
            e.Handled = true;
            return;
        }

        // If the help overlay is open, only Esc/F1 close it (F1
        // handled above). Everything else is swallowed so the user
        // doesn't accidentally trigger commands while reading.
        if (HelpToggle.IsChecked == true)
        {
            if (e.Key == Key.Escape)
            {
                HelpToggle.IsChecked = false;
                e.Handled = true;
            }
            return;
        }

        // If the leave-confirm overlay is open, Esc cancels and Enter
        // confirms; other keys are swallowed.
        if (LeaveConfirmOverlay.IsVisible)
        {
            if (e.Key == Key.Escape)
            {
                LeaveConfirmOverlay.IsVisible = false;
                e.Handled = true;
            }
            else if (e.Key == Key.Enter)
            {
                LeaveConfirmOverlay.IsVisible = false;
                if (_vm.LeaveGameCommand.CanExecute(null))
                    _vm.LeaveGameCommand.Execute(null);
                e.Handled = true;
            }
            return;
        }

        var mods = e.KeyModifiers;

        // Ctrl+S → save
        if (e.Key == Key.S && (mods & KeyModifiers.Control) != 0)
        {
            if (_vm.SaveCommand.CanExecute(null))
                _vm.SaveCommand.Execute(null);
            e.Handled = true;
            return;
        }

        // Ctrl+L → show leave-confirm overlay
        if (e.Key == Key.L && (mods & KeyModifiers.Control) != 0)
        {
            LeaveConfirmOverlay.IsVisible = true;
            e.Handled = true;
            return;
        }

        // Ctrl+Enter → submit + refocus input (fast play loop)
        if (e.Key == Key.Enter && (mods & KeyModifiers.Control) != 0)
        {
            if (_vm.SubmitActionCommand.CanExecute(null))
                _vm.SubmitActionCommand.Execute(null);
            // Refocus the input so the user can immediately type the
            // next action without clicking.
            ActionInput.Focus();
            e.Handled = true;
            return;
        }

        // Esc → clear the action input. OnActionKeyDown handles this
        // when focus is in the input; this branch covers the case
        // where focus is elsewhere (a button, a list item, …). The
        // spec says "if the input is already empty, do nothing" — we
        // don't leave the game on Esc.
        if (e.Key == Key.Escape)
        {
            if (!string.IsNullOrEmpty(_vm.CurrentAction))
            {
                _vm.CurrentAction = string.Empty;
                e.Handled = true;
            }
            return;
        }

        // 1, 2, 3, 4 → switch side-panel tabs (Character / Inventory /
        // Quests / World). Only when focus is NOT in a TextBox so
        // typing "1" in the action input doesn't switch tabs. Both
        // the top-row digits (D1..D4) and the numpad digits
        // (NumPad1..NumPad4) work.
        if (e.Key is Key.D1 or Key.D2 or Key.D3 or Key.D4
                or Key.NumPad1 or Key.NumPad2 or Key.NumPad3 or Key.NumPad4)
        {
            if (!IsFocusInTextBox())
            {
                int idx = e.Key switch
                {
                    Key.D1 or Key.NumPad1 => 0,
                    Key.D2 or Key.NumPad2 => 1,
                    Key.D3 or Key.NumPad3 => 2,
                    Key.D4 or Key.NumPad4 => 3,
                    _ => -1,
                };
                if (idx >= 0 && idx < SideTabs.ItemCount)
                {
                    SideTabs.SelectedIndex = idx;
                    e.Handled = true;
                }
            }
            return;
        }
    }

    /// <summary>
    /// True when the currently focused element is a TextBox. Used to
    /// suppress digit-key tab switching while the user is typing into
    /// the action input or chat.
    /// </summary>
    private bool IsFocusInTextBox()
    {
        var topLevel = this.FindAncestorOfType<TopLevel>();
        var focused = topLevel?.FocusManager?.GetFocusedElement();
        return focused is TextBox;
    }

    // ─── Help overlay handlers (issue #51) ────────────────────────────

    /// <summary>
    /// Click anywhere on the help overlay's dim backdrop (outside the
    /// centered panel) dismisses the overlay. Clicks on the inner
    /// panel don't reach this handler because the inner Border eats
    /// them (it's a sibling, not a child, of the backdrop — but
    /// PointerPressed still fires on the backdrop only when the
    /// pointer is over the backdrop itself).
    /// </summary>
        private void OnHelpOverlayClick(object? sender, PointerPressedEventArgs e)
    {
        HelpToggle.IsChecked = false;
        e.Handled = true;
    }

    private void OnHelpButtonClick(object? sender, RoutedEventArgs e)
    {
        HelpToggle.IsChecked = false;
    }

    // ─── Leave-confirm overlay handlers (issue #51) ───────────────────

    /// <summary>
    /// Click on the dim backdrop cancels leave (matches the "Отмена"
    /// button behavior).
    /// </summary>
    private void OnLeaveConfirmOverlayClick(object? sender, PointerPressedEventArgs e)
    {
        LeaveConfirmOverlay.IsVisible = false;
        e.Handled = true;
    }

    /// <summary>
    /// "Выйти" button click — the Button's Command is already bound
    /// to <c>LeaveGameCommand</c>, so this handler just hides the
    /// overlay. The command itself navigates back to the menu.
    /// </summary>
    private void OnLeaveConfirmAccept(object? sender, RoutedEventArgs e)
    {
        LeaveConfirmOverlay.IsVisible = false;
        // Don't mark Handled — let the Command binding execute.
    }

    /// <summary>"Отмена" button click — hide the overlay, stay in game.</summary>
    private void OnLeaveConfirmCancel(object? sender, RoutedEventArgs e)
    {
        LeaveConfirmOverlay.IsVisible = false;
        e.Handled = true;
    }

    // ─── Lobby handlers (issue #77) ───────────────────────────────────

    /// <summary>
    /// Copy the lobby's share address to the clipboard so the host can
    /// paste it into a chat / email for friends. Best-effort: if the
    /// clipboard isn't available (headless, no TopLevel), the click is
    /// a silent no-op. The address stays visible in the panel regardless.
    /// </summary>
    private async void OnCopyAddressClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        var address = _vm.ShareAddress;
        if (string.IsNullOrEmpty(address)) return;
        try
        {
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.Clipboard is { } cb)
            {
                await cb.SetTextAsync(address);
            }
        }
        catch (System.Exception ex)
        {
            System.Diagnostics.Trace.WriteLine(
                $"[GameView] failed to copy share address: {ex.Message}");
        }
    }
}
