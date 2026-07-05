using System;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyGame.Core.Multiplayer;
using MyGame.Core.Profile;

namespace MyGame.Desktop.ViewModels;

/// <summary>
/// Join screen. Lets the player enter a host IP/hostname + port, then
/// opens a GameClient, performs the HelloMsg→WelcomeMsg handshake,
/// and on success navigates to the game view as a client.
/// </summary>
public partial class JoinGameViewModel : ViewModelBase
{
    private readonly ProfileStore _profileStore;
    private readonly SettingsStore _settingsStore;
    private readonly MainViewModel _shell;

    private string _host = "localhost";
    private int _port = 51920;

    public JoinGameViewModel(
        ProfileStore profileStore,
        SettingsStore settingsStore,
        MainViewModel shell)
    {
        _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));

        // Pre-fill from the last-used server (if any).
        try
        {
            var s = _settingsStore.Load();
            if (!string.IsNullOrWhiteSpace(s.LastServerHost))
                _host = s.LastServerHost;
            if (s.LastServerPort is int p && p > 0)
                _port = p;
        }
        catch { /* fall back to defaults */ }
    }

    // ─── Form fields ─────────────────────────────────────────────────

    public string Host
    {
        get => _host;
        set => SetProperty(ref _host, value);
    }

    public int Port
    {
        get => _port;
        set => SetProperty(ref _port, Math.Max(1, Math.Min(65535, value)));
    }

    // ─── Commands ────────────────────────────────────────────────────

    /// <summary>
    /// Connect to the host. Builds a ClientSession, runs the handshake,
    /// remembers the host/port in settings for next time, and on
    /// success hands the session off to the GameViewModel.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var profile = _profileStore.GetOrCreate();
            var session = new ClientSession(profile);
            await session.ConnectAsync(_host.Trim(), _port, CancellationToken.None);

            // Remember for next time.
            try
            {
                _settingsStore.Update(s => s with
                {
                    LastServerHost = _host.Trim(),
                    LastServerPort = _port,
                });
            }
            catch { /* non-fatal */ }

            // Hand off to the GameViewModel.
            PendingClientSessionTransfer.Set(session);
            _shell.NavigateToGameClient(_host.Trim(), _port).FireAndForget();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Не удалось подключиться: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Back to the main menu.</summary>
    [RelayCommand]
    private void Back() => _shell.NavigateToMenu();

    private bool CanConnect() => !IsBusy && !string.IsNullOrWhiteSpace(_host) && _port > 0;
}

/// <summary>
/// Tiny channel for transferring a freshly-connected ClientSession from
/// the JoinGame screen to the Game screen (mirrors the host-side
/// PendingHostSessionTransfer).
/// </summary>
internal static class PendingClientSessionTransfer
{
    private static MyGame.Core.Multiplayer.ClientSession? _session;
    private static readonly object _lock = new();

    public static void Set(MyGame.Core.Multiplayer.ClientSession session)
    {
        lock (_lock) _session = session;
    }

    public static MyGame.Core.Multiplayer.ClientSession? Take()
    {
        lock (_lock)
        {
            var s = _session;
            _session = null;
            return s;
        }
    }
}
