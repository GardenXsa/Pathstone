using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Threading;
using MyGame.Core.Logging;
using MyGame.Core.Profile;
using MyGame.Desktop.Services;

namespace MyGame.Desktop;

class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Initialize the structured logger FIRST, before anything else.
        // Issue #71: writes to %APPDATA%/MyGame/logs/game-{date}.log.
        var logDir = Path.Combine(ProfileStore.DefaultProfileDirectory, "logs");
        GameLogger.Initialize(logDir, GameLogger.Level.Info);
        GameLogger.Instance.Info("=== Pathstone starting ===");

        // Register global exception handlers.
        RegisterGlobalExceptionHandlers();

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            // Top-level catch — the global handlers don't always fire
            // for exceptions on the main thread during startup. This
            // ensures we always write a dump + try to show a dialog
            // before the process dies.
            HandleFatalException(ex, context: "Program.Main top-level catch");
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .WithInterFont()
            .LogToTrace();

    // ─── Global exception handlers ────────────────────────────────────

    /// <summary>
    /// Wire <see cref="AppDomain.UnhandledException"/> +
    /// <see cref="TaskScheduler.UnobservedTaskException"/> so even
    /// background-thread faults get a crash dump + (best-effort) dialog.
    /// </summary>
    private static void RegisterGlobalExceptionHandlers()
    {
        // AppDomain.UnhandledException fires for any uncaught exception
        // in any thread (managed). The CLR may terminate the process
        // after the handler returns (depending on
        // UnhandledExceptionEventArgs.IsTerminating), so we MUST write
        // the dump synchronously here.
        AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            var terminating = e.IsTerminating;
            var context = terminating
                ? "AppDomain.UnhandledException (terminating)"
                : "AppDomain.UnhandledException (non-terminating)";
            HandleFatalException(ex, context: context);
        };

        // TaskScheduler.UnobservedTaskException fires when a Task's
        // exception is never observed (no await, no .Wait, no
        // .Exception access) and the Task is GC'd. By default this
        // would tear down the process in .NET 4.5+; we mark it observed
        // (so it doesn't escalate) and log a crash dump.
        TaskScheduler.UnobservedTaskException += (sender, e) =>
        {
            HandleFatalException(e.Exception, context: "TaskScheduler.UnobservedTaskException");
            e.SetObserved(); // prevent process termination
        };
    }

    /// <summary>
    /// Central fatal-exception handler. Writes a crash dump via
    /// <see cref="CrashLogger"/> and tries to show a user-visible dialog
    /// (best-effort — if Avalonia isn't initialized yet, or the dialog
    /// itself throws, we just leave the dump on disk). Never rethrows.
    /// </summary>
    /// <param name="exception">The exception (may be null when the
    /// AppDomain handler couldn't surface one).</param>
    /// <param name="context">Short string identifying which handler
    /// caught this — included as a header in the dump.</param>
    private static void HandleFatalException(Exception? exception, string context)
    {
        // 1) Always write the crash dump first. CrashLogger is robust
        //    (wraps its own I/O in try/catch) so this won't throw.
        string? dumpPath = null;
        try
        {
            dumpPath = CrashLogger.Log(exception, context);
        }
        catch
        {
            // CrashLogger's own try/catch should have caught everything,
            // but if even that fails, we have nothing more we can do.
        }

        // 2) Try to show a dialog. Best-effort — if Avalonia isn't
        //    initialized yet (crash during startup), or if we're on a
        //    non-UI thread we can't marshal to, or the dialog itself
        //    throws, we just skip the dialog and leave the dump on disk.
        try
        {
            TryShowCrashDialog(exception, dumpPath);
        }
        catch
        {
            // Swallow — the dump is already on disk.
        }
    }

    /// <summary>
    /// Best-effort crash dialog. Marshals to the Avalonia UI thread (if
    /// one exists) and shows a small modal Window with the error message
    /// + dump file path + a "Copy to clipboard" button. No-op if no
    /// Avalonia application is running yet (crash during startup before
    /// the UI thread exists).
    /// </summary>
    private static void TryShowCrashDialog(Exception? exception, string? dumpPath)
    {
        // If the Avalonia application isn't running, we can't show a
        // dialog. Application.Current is null until
        // OnFrameworkInitializationCompleted runs.
        if (Application.Current is null) return;

        // The IClassicDesktopStyleApplicationLifetime exposes MainWindow;
        // if it's null, no main window has been created yet.
        if (Application.Current.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;

        var message = exception?.Message ?? "(no exception object available)";
        var dumpText = dumpPath is not null && File.Exists(dumpPath)
            ? File.ReadAllText(dumpPath)
            : "(no dump file available)";

        // Marshal to the UI thread — the global handlers may fire on
        // background threads.
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var dialog = BuildCrashDialog(message, dumpPath ?? "(no dump file)", dumpText);
                dialog.ShowDialog(desktop.MainWindow ?? new Window());
            }
            catch
            {
                // If building/showing the dialog fails, just give up —
                // the dump file is already on disk and the user can
                // find it there.
            }
        });
    }

    /// <summary>
    /// Build the crash dialog window programmatically (no XAML file).
    /// Two-column layout: an error message on top, a ScrollViewer with
    /// the full dump text below, and a "Copy to clipboard" + "Close"
    /// button row at the bottom.
    /// </summary>
    private static Window BuildCrashDialog(string message, string dumpPath, string dumpText)
    {
        var titleText = new TextBlock
        {
            Text = "Pathstone crashed",
            FontSize = 18,
            FontWeight = Avalonia.Media.FontWeight.Bold,
            Margin = new Thickness(0, 0, 0, 8),
        };

        var introText = new TextBlock
        {
            Text = $"A crash dump was written to:\n{dumpPath}\n\nPlease report this on GitHub.\n\nError:\n{message}",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12),
        };

        var dumpBox = new TextBox
        {
            Text = dumpText,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            FontFamily = Avalonia.Media.FontFamily.Parse("Cascadia Mono,Consolas,Menlo,monospace"),
            MinHeight = 200,
            MaxHeight = 400,
        };

        var copyButton = new Button
        {
            Content = "Copy to clipboard",
            Padding = new Thickness(12, 4),
        };
        copyButton.Click += async (s, e) =>
        {
            try
            {
                var clipboard = TopLevel.GetTopLevel(dumpBox)?.Clipboard;
                if (clipboard is not null)
                    await clipboard.SetTextAsync(dumpText);
            }
            catch { /* best-effort */ }
        };

        var closeButton = new Button
        {
            Content = "Close",
            Padding = new Thickness(12, 4),
        };

        var window = new Window
        {
            Title = "Pathstone — Crash",
            Width = 600,
            Height = 480,
            CanResize = true,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            Content = new DockPanel
            {
                LastChildFill = true,
                Margin = new Thickness(16),
                Children =
                {
                    // Button row docked at the bottom
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Margin = new Thickness(0, 12, 0, 0),
                        Children = { copyButton, closeButton },
                        [DockPanel.DockProperty] = Dock.Bottom,
                    },
                    // Header (title + intro) docked at the top
                    new StackPanel
                    {
                        [DockPanel.DockProperty] = Dock.Top,
                        Children = { titleText, introText },
                    },
                    // Dump text fills the rest
                    dumpBox,
                },
            },
        };

        closeButton.Click += (s, e) => window.Close();
        return window;
    }
}
