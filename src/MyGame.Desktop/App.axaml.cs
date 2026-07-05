using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MyGame.Core.Profile;
using MyGame.Core.Saves;
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
            // THEME (issue #47): apply the saved theme + accent + animation
            // flag BEFORE the main window is shown so the user doesn't see
            // a dark-then-light flicker on startup. We re-apply on every
            // SettingsStore.Changed so runtime toggles from the Settings
            // screen take effect immediately.
            var settingsStore = ServiceHost.Resolve<SettingsStore>();
            try
            {
                ThemeService.ApplyFromSettings(settingsStore.Load());
                settingsStore.Changed += (_, settings) => ThemeService.ApplyFromSettings(settings);
            }
            catch
            {
                // Defensive: if the profile dir isn't writable yet (first
                // launch, onboarding hasn't run), fall back to defaults.
                ThemeService.ApplyTheme("Dark", "Indigo", enableAnimations: true);
            }

            // Build the shell view model from resolved Core services.
            var shell = new MainViewModel(
                ServiceHost.Resolve<ProfileStore>(),
                settingsStore,
                ServiceHost.Resolve<SaveManager>());

            var window = new MainWindow();
            window.SetViewModel(shell);
            desktop.MainWindow = window;

            // THEME: now that the main window exists, re-apply so the
            // Window.Anim class is toggled on it (the earlier call happened
            // before desktop.MainWindow was assigned).
            try { ThemeService.ApplyFromSettings(settingsStore.Load()); }
            catch { /* fall back to whatever was applied above */ }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
