using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using MyGame.Desktop.ViewModels;

namespace MyGame.Desktop.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// Theme-mode RadioButton click handler. The RadioButtons bind
    /// IsChecked to ThemeMode via a OneWay converter (string→bool), so
    /// clicking one does NOT automatically write back to the source —
    /// this handler does that explicitly by reading the Tag (which
    /// carries the mode name: "Dark"/"Light"/"System") and assigning it
    /// to SettingsViewModel.ThemeMode. The setter then fires
    /// ApplyLiveTheme() for the live preview, and the OneWay binding
    /// re-evaluates IsChecked on all three radios.
    ///
    /// <para>
    /// This replaces the previous Mode=TwoWay binding whose ConvertBack
    /// returned AvaloniaProperty.UnsetValue on the unchecked path —
    /// Avalonia's ReflectionConverter tried to instantiate System.String
    /// (which has no parameterless ctor) to "convert" UnsetValue to the
    /// string source type, throwing MissingMethodException and crashing
    /// the dispatcher (caught by the global handler but logged as a
    /// crash dump and left the theme unswitched).
    /// </para>
    /// </summary>
    private void OnThemeModeClick(object? sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb
            && rb.Tag is string mode
            && DataContext is SettingsViewModel vm)
        {
            vm.ThemeMode = mode;
        }
    }
}
