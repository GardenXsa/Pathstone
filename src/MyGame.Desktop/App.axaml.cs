using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MyGame.Desktop.Services;
using MyGame.Desktop.ViewModels;

namespace MyGame.Desktop;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        // Initialize the DI container first so every ViewModel can
        // resolve Core services on construction.
        ServiceHost.Initialize();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Build the shell view model from resolved Core services.
            var shell = new MainViewModel(
                ServiceHost.Resolve<MyGame.Core.Profile.ProfileStore>(),
                ServiceHost.Resolve<MyGame.Core.Profile.SettingsStore>(),
                ServiceHost.Resolve<MyGame.Core.Saves.SaveManager>());

            var window = new MainWindow();
            window.SetViewModel(shell);
            desktop.MainWindow = window;
        }

        base.OnFrameworkInitializationCompleted();
    }
}
