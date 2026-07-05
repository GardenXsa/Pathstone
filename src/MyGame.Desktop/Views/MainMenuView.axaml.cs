using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using MyGame.Desktop.ViewModels;

namespace MyGame.Desktop.Views;

/// <summary>
/// Code-behind for the main menu view. Mostly data-bound; the nickname
/// TextBox uses KeyDown / LostFocus handlers to trigger save-on-Enter /
/// save-on-blur without needing a separate Save button. The
/// «Импорт персонажа» button uses a code-behind Click handler because
/// the file picker needs Avalonia's StorageProvider API, which requires
/// the TopLevel — the VM can't reach it directly (issue #62). The
/// multi-select delete confirmation overlay (issue #74) uses a code-
/// behind PointerPressed handler on its dim backdrop to dismiss the
/// overlay when the user clicks outside the centered panel.
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

    /// <summary>
    /// Open the Avalonia StorageProvider file picker, filtered to
    /// Pathstone character sheets (.json / .pathstone-char). On
    /// selection, hand the path to the VM's
    /// <see cref="MainMenuViewModel.ImportCharacterFromFileAsync"/>
    /// which loads the sheet, builds a fresh world with the imported
    /// player, saves it, and navigates to the game. Picker cancellation
    /// is a silent no-op.
    /// </summary>
    private async void OnImportCharacterClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainMenuViewModel vm) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        // The actual character-sheet files are written as
        // `char_{Guid:N}.json` by CharacterSheetStore, so the picker
        // accepts `*.json`. We also accept the conceptual
        // `*.pathstone-char` extension (registered in the Windows NSIS
        // installer as a file association) so a renamed file still loads.
        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Импорт персонажа Pathstone",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Персонаж Pathstone")
                {
                    Patterns = new[] { "*.json", "*.pathstone-char" }
                },
                FilePickerFileTypes.All,
            },
        });

        if (files is null || files.Count == 0) return;
        try
        {
            var path = files[0].Path.LocalPath;
            await vm.ImportCharacterFromFileAsync(path);
        }
        catch (Exception ex)
        {
            // The VM surfaces errors via ErrorMessage; this catch is just
            // a safety net for unexpected StorageProvider exceptions.
            System.Diagnostics.Trace.WriteLine($"[MainMenuView] import picker failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Click on the delete-confirm overlay's dim backdrop cancels the
    /// delete (matches the «Отмена» button behavior). Clicks on the
    /// inner panel don't reach this handler — they're eaten by the
    /// inner Border (which is a child of the backdrop Border, but the
    /// backdrop's PointerPressed only fires when the pointer is over
    /// the backdrop area not covered by the child).
    /// </summary>
    private void OnDeleteConfirmOverlayClick(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MainMenuViewModel vm) return;
        vm.CancelDeleteSelectedCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>
    /// Export a save to a .pathstone-world file (issue #33). Opens a
    /// save-file picker, then calls SaveManager.ExportSave via the VM.
    /// </summary>
    private async void OnExportSaveClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainMenuViewModel vm) return;
        // The save id comes from the button's DataContext (the SaveSlotViewModel).
        if (sender is not Avalonia.Controls.Button btn) return;
        if (btn.DataContext is not SaveSlotViewModel slot) return;

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Экспорт мира Pathstone",
            DefaultExtension = "pathstone-world",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("Мир Pathstone")
                {
                    Patterns = new[] { "*.pathstone-world" }
                },
            },
        });

        if (file is null) return;
        try
        {
            var path = file.Path.LocalPath;
            await vm.ExportSaveToPathAsync(slot.Id, path);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[MainMenuView] export failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Import a .pathstone-world file (issue #33). Opens an open-file
    /// picker, then calls SaveManager.ImportSave via the VM.
    /// </summary>
    private async void OnImportWorldClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not MainMenuViewModel vm) return;
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Импорт мира Pathstone",
            AllowMultiple = false,
            FileTypeFilter = new[]
            {
                new FilePickerFileType("Мир Pathstone")
                {
                    Patterns = new[] { "*.pathstone-world", "*.zip" }
                },
                FilePickerFileTypes.All,
            },
        });

        if (files is null || files.Count == 0) return;
        try
        {
            var path = files[0].Path.LocalPath;
            await vm.ImportWorldFromFileAsync(path);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine($"[MainMenuView] import world failed: {ex.Message}");
        }
    }
}
