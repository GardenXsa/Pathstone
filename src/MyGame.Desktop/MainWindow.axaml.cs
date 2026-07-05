using Avalonia.Controls;
using MyGame.Desktop.ViewModels;

namespace MyGame.Desktop;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Set the DataContext to the shell view model. Called by
    /// <see cref="App.OnFrameworkInitializationCompleted"/> after
    /// ServiceHost has been initialized.
    /// </summary>
    public void SetViewModel(MainViewModel vm)
    {
        DataContext = vm;
        // Show the main menu as the initial screen.
        vm.NavigateToMenu();
    }
}
