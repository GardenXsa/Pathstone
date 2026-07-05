using System.IO;
using System.Text.Json;

namespace MyGame.Core.Profile;

/// <summary>
/// Loads and saves <see cref="Settings"/> to
/// <c>{ProfileDirectory}/settings.json</c>. Notifies subscribers via
/// <see cref="Changed"/> when settings are written (so the UI / AI client
/// can re-read without polling).
/// </summary>
public sealed class SettingsStore
{
    private readonly string _settingsFilePath;
    private readonly object _lock = new();
    private Settings? _cached;

    /// <param name="profileDirectory">
    /// Directory containing <c>settings.json</c>. Defaults to
    /// <see cref="ProfileStore.DefaultProfileDirectory"/>.
    /// </param>
    public SettingsStore(string? profileDirectory = null)
    {
        var dir = profileDirectory ?? ProfileStore.DefaultProfileDirectory;
        _settingsFilePath = Path.Combine(dir, "settings.json");
    }

    /// <summary>Path to the settings file.</summary>
    public string SettingsFilePath => _settingsFilePath;

    /// <summary>
    /// Raised whenever <see cref="Save"/> writes a new settings object
    /// to disk. Subscribers receive the just-saved instance. Handlers
    /// are invoked on the calling thread; exceptions are caught and
    /// logged to <c>Trace</c> so a misbehaving handler can't break
    /// the save path.
    /// </summary>
    public event EventHandler<Settings>? Changed;

    /// <summary>
    /// Load settings from disk. Returns a <see cref="Settings"/> with
    /// defaults if the file doesn't exist or is corrupt (the corrupt
    /// file is left in place — the next <see cref="Save"/> will overwrite
    /// it with a known-good copy).
    /// </summary>
    public Settings Load()
    {
        lock (_lock)
        {
            if (_cached is not null) return _cached;

            try
            {
                if (File.Exists(_settingsFilePath))
                {
                    var json = File.ReadAllText(_settingsFilePath);
                    var loaded = JsonSerializer.Deserialize<Settings>(json, ProfileStore.JsonOptions);
                    if (loaded is not null)
                    {
                        _cached = loaded;
                        return loaded;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine(
                    $"[SettingsStore] Failed to load settings from {_settingsFilePath}: {ex.Message}");
            }

            // First launch OR corrupt file — fall back to defaults.
            _cached = new Settings();
            return _cached;
        }
    }

    /// <summary>
    /// Persist <paramref name="settings"/> to disk atomically (write to
    /// <c>{path}.tmp</c> then move), update the cached value, and raise
    /// <see cref="Changed"/>. Swallows disk errors (logged to
    /// <c>Trace</c>) — settings are also kept in-memory so the running
    /// session continues to work; the user sees a toast in the UI.
    /// </summary>
    public void Save(Settings settings)
    {
        if (settings is null) throw new ArgumentNullException(nameof(settings));

        lock (_lock)
        {
            _cached = settings;
        }

        try
        {
            var dir = Path.GetDirectoryName(_settingsFilePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            ProfileStore.AtomicWriteJson(_settingsFilePath, settings);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine(
                $"[SettingsStore] Failed to save settings to {_settingsFilePath}: {ex.Message}");
        }

        // Raise outside the lock so handlers can call back into Load()
        // without deadlocking.
        RaiseChanged(settings);
    }

    /// <summary>
    /// Convenience: apply a patch to the current settings and save.
    /// Equivalent to <c>Save(Load() with { ...patch })</c> but reuses
    /// the cached instance for fewer allocations.
    /// </summary>
    public Settings Update(Func<Settings, Settings> patch)
    {
        if (patch is null) throw new ArgumentNullException(nameof(patch));
        var current = Load();
        var next = patch(current);
        Save(next);
        return next;
    }

    private void RaiseChanged(Settings settings)
    {
        var handlers = Changed;
        if (handlers is null) return;
        foreach (EventHandler<Settings>? h in handlers.GetInvocationList())
        {
            if (h is null) continue;
            try { h(this, settings); }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine(
                    $"[SettingsStore] Settings.Changed handler threw: {ex.Message}");
            }
        }
    }
}
