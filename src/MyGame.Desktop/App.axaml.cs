using System;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using MyGame.Core.Profile;
using MyGame.Core.Saves;
using MyGame.Core.Tooling;
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
        ServiceHost.Initialize();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settingsStore = ServiceHost.Resolve<SettingsStore>();
            try
            {
                ThemeService.ApplyFromSettings(settingsStore.Load());
                settingsStore.Changed += (_, settings) => ThemeService.ApplyFromSettings(settings);
            }
            catch
            {
                ThemeService.ApplyTheme("Dark", "Indigo", enableAnimations: true);
            }

            var shell = new MainViewModel(
                ServiceHost.Resolve<ProfileStore>(),
                settingsStore,
                ServiceHost.Resolve<SaveManager>());

            var window = new MainWindow();
            window.SetViewModel(shell);
            desktop.MainWindow = window;

            try { ThemeService.ApplyFromSettings(settingsStore.Load()); }
            catch { }

            // Issue #54: check for updates in the background. Never blocks
            // startup — runs fire-and-forget. If a newer version is found,
            // a notification is shown on the main menu (via UpdateAvailable
            // property on MainViewModel).
            _ = CheckForUpdatesAsync(shell);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Best-effort update check (issue #54). Fetches the latest GitHub
    /// release version and, if newer than the current version, sets the
    /// shell's UpdateAvailable property so the main menu shows a
    /// notification with a link to the release page.
    /// </summary>
    private static async Task CheckForUpdatesAsync(MainViewModel shell)
    {
        try
        {
            var current = Version.Parse(MyGame.Core.Common.Version.Current);
            var update = await UpdateChecker.CheckAsync(current);
            if (update is not null)
            {
                shell.SetUpdateAvailableOnMenu(update);
            }
        }
        catch { /* best-effort — network errors are silent */ }
    }
}
