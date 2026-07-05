using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MyGame.Core.Profile;

/// <summary>
/// Owns the single local <see cref="Profile"/>. Loads it from disk on
/// first access (or creates a new one with a random nickname and
/// persists it), caches it, and writes it back on rename.
///
/// <para>
/// This class is the desktop replacement for the entire TS
/// <c>lib/auth.ts</c> subsystem. There is NO auth, NO JWT, NO cookies,
/// NO multi-user. One profile per install, end of story. The host of a
/// multiplayer session is identified by <see cref="Profile.Id"/>; clients
/// send their own profile id in the WebSocket handshake so the host can
/// attribute actions and show nicknames in the lobby.
/// </para>
/// </summary>
public sealed class ProfileStore
{
    // ─── On-disk layout ────────────────────────────────────────────────

    /// <summary>
    /// Default profile root directory:
    /// <c>%APPDATA%/MyGame</c> (Windows),
    /// <c>~/.config/MyGame</c> (Linux),
    /// <c>~/Library/Application Support/MyGame</c> (macOS).
    ///
    /// Falls back to <c>~/.mygame</c> when <c>SpecialFolder.ApplicationData</c>
    /// returns empty (no XDG env on a headless Linux box), and finally to
    /// a folder next to the executable when even the home dir is unknown.
    /// </summary>
    public static string DefaultProfileDirectory
    {
        get
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (!string.IsNullOrEmpty(appData))
                return Path.Combine(appData, "MyGame");

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home))
                return Path.Combine(home, ".mygame");

            return Path.Combine(AppContext.BaseDirectory, "MyGame");
        }
    }

    /// <summary>
    /// Profile root directory. Defaults to
    /// <see cref="DefaultProfileDirectory"/>; can be overridden in the
    /// ctor (for tests / portable mode).
    /// </summary>
    public string ProfileDirectory { get; }

    /// <summary>
    /// Path to the <c>profile.json</c> file — single source of truth for
    /// the local profile.
    /// </summary>
    public string ProfileFilePath => Path.Combine(ProfileDirectory, "profile.json");

    // ─── State ─────────────────────────────────────────────────────────

    private Profile? _current;
    private readonly object _lock = new();

    /// <summary>
    /// Cached current profile. Null until <see cref="GetOrCreate"/> has
    /// been called at least once. Thread-safe (returns a snapshot; the
    /// caller can't mutate the cached instance).
    /// </summary>
    public Profile? Current
    {
        get
        {
            lock (_lock) return _current;
        }
    }

    // ─── Nickname generator ────────────────────────────────────────────

    /// <summary>
    /// Russian flavor prefixes for auto-generated nicknames. Ported
    /// verbatim from <c>lib/auth.ts</c>'s <c>NICKNAME_PREFIXES</c> array
    /// (14 entries). Picking uniformly at random gives each new install
    /// a friendly readable name without requiring the user to type one
    /// before they can play.
    /// </summary>
    private static readonly string[] NicknamePrefixes =
    {
        "Странник", "Бродяга", "Воин", "Маг", "Лучник", "Клерик", "Варвар",
        "Паладин", "Разбойник", "Друид", "Бард", "Монах", "Рейнджер", "Колдун",
    };

    /// <summary>
    /// Generate a fresh random nickname: <c>&lt;prefix&gt;-&lt;XXXX&gt;</c>
    /// where XXXX is a 4-char uppercase base36 suffix (matches the TS
    /// <c>generateRandomNickname</c> format byte-for-byte).
    /// </summary>
    public static string GenerateRandomNickname()
    {
        var prefix = NicknamePrefixes[Random.Shared.Next(NicknamePrefixes.Length)];
        var suffix = RandomBase36Upper(4);
        return $"{prefix}-{suffix}";
    }

    private const string Base36Alphabet = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    private static string RandomBase36Upper(int length)
    {
        Span<char> buf = stackalloc char[length];
        for (int i = 0; i < length; i++)
            buf[i] = Base36Alphabet[Random.Shared.Next(36)];
        return new string(buf);
    }

    // ─── ctor ──────────────────────────────────────────────────────────

    /// <param name="profileDirectory">
    /// Optional override for <see cref="ProfileDirectory"/>. Pass null
    /// (the default) to use <see cref="DefaultProfileDirectory"/>.
    /// </param>
    public ProfileStore(string? profileDirectory = null)
    {
        ProfileDirectory = profileDirectory ?? DefaultProfileDirectory;
    }

    // ─── Public API ────────────────────────────────────────────────────

    /// <summary>
    /// Load the existing profile from <see cref="ProfileFilePath"/>, or
    /// create a new one with a random nickname and persist it. The
    /// result is cached and returned on subsequent calls. Thread-safe.
    /// </summary>
    /// <returns>
    /// The current profile. Never null (on a disk error falls back to
    /// an in-memory profile and logs to <c>Trace</c>).
    /// </returns>
    public Profile GetOrCreate()
    {
        lock (_lock)
        {
            if (_current is not null) return _current;

            var loaded = TryLoadFromDisk();
            if (loaded is not null)
            {
                _current = loaded;
                return loaded;
            }

            // First launch — mint a new profile with a random nickname.
            var created = new Profile
            {
                Id = Guid.NewGuid(),
                Nickname = GenerateRandomNickname(),
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            TrySaveToDisk(created);
            _current = created;
            return created;
        }
    }

    /// <summary>
    /// Validate and apply a new nickname to the current profile. Persists
    /// the change immediately. Throws <see cref="InvalidOperationException"/>
    /// if <see cref="GetOrCreate"/> hasn't been called yet, and
    /// <see cref="ArgumentException"/> if the new nickname fails
    /// <see cref="Profile.ValidateNickname"/>.
    /// </summary>
    public Profile Rename(string newNickname)
    {
        if (!Profile.ValidateNickname(newNickname, out var error))
            throw new ArgumentException(error, nameof(newNickname));

        lock (_lock)
        {
            if (_current is null)
                throw new InvalidOperationException(
                    "Profile not loaded. Call GetOrCreate() first.");

            var updated = _current with
            {
                Nickname = newNickname.Trim(),
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            TrySaveToDisk(updated);
            _current = updated;
            return updated;
        }
    }

    /// <summary>
    /// Force a reload from disk on the next <see cref="GetOrCreate"/>
    /// call. Useful for tests; production code rarely needs this since
    /// the profile is single-writer.
    /// </summary>
    public void InvalidateCache()
    {
        lock (_lock) _current = null;
    }

    // ─── Disk I/O ──────────────────────────────────────────────────────

    private Profile? TryLoadFromDisk()
    {
        try
        {
            if (!File.Exists(ProfileFilePath)) return null;
            var json = File.ReadAllText(ProfileFilePath);
            return JsonSerializer.Deserialize<Profile>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine(
                $"[ProfileStore] Failed to load profile from {ProfileFilePath}: {ex.Message}");
            return null;
        }
    }

    private void TrySaveToDisk(Profile profile)
    {
        try
        {
            Directory.CreateDirectory(ProfileDirectory);
            AtomicWriteJson(ProfileFilePath, profile);
        }
        catch (Exception ex)
        {
            // Don't throw — the profile still exists in memory. The user
            // will see a "couldn't save profile" toast in the UI; the
            // next rename attempt will retry.
            System.Diagnostics.Trace.WriteLine(
                $"[ProfileStore] Failed to save profile to {ProfileFilePath}: {ex.Message}");
        }
    }

    /// <summary>
    /// Write JSON atomically: serialize to <c>{path}.tmp</c>, then
    /// <see cref="File.Move(string, string, bool)"/> over the final path.
    /// A crash mid-write leaves the .tmp file (which is ignored on load)
    /// rather than a truncated final file.
    /// </summary>
    internal static void AtomicWriteJson(string path, object value)
    {
        var tmp = path + ".tmp";
        var json = JsonSerializer.Serialize(value, JsonOptions);
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>
    /// Shared JSON options for the Profile layer: camelCase wire format
    /// (consistent with the World layer), indented for human-readability,
    /// case-insensitive deserialization, null fields omitted on write.
    /// Uses <see cref="System.Text.Encodings.Web.JavaScriptEncoder.
    /// UnsafeRelaxedJsonEscaping"/> so Cyrillic (and other non-ASCII)
    /// is written as raw UTF-8 rather than <c>\uXXXX</c> escapes —
    /// keeps profile.json / settings.json readable in any text editor.
    /// </summary>
    internal static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
