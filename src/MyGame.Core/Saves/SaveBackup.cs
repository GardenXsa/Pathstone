namespace MyGame.Core.Saves;

/// <summary>
/// One entry in a save's backup history (issue #81 — auto-rotating
/// backups). Returned by <see cref="SaveManager.ListBackups"/>. The
/// <see cref="Timestamp"/> string is the on-disk backup directory name
/// (a sortable <c>yyyyMMddHHmmssfff</c> UTC timestamp, with an optional
/// <c>_N</c> suffix for same-millisecond collisions); pass it to
/// <see cref="SaveManager.RestoreBackup"/> to restore that snapshot.
/// </summary>
public readonly record struct SaveBackup
{
    /// <summary>
    /// On-disk backup directory name (e.g.
    /// <c>20240115143256000</c> or <c>20240115143256000_2</c>). Use
    /// this string verbatim as the <c>backupTimestamp</c> argument to
    /// <see cref="SaveManager.RestoreBackup"/>.
    /// </summary>
    public string Timestamp { get; init; }

    /// <summary>
    /// Parsed UTC timestamp of when the backup was created. Derived
    /// from <see cref="Timestamp"/> (the leading 17 chars before any
    /// <c>_N</c> suffix). Used by the UI to show a human-readable
    /// «2024-01-15 14:32» label and to sort backups newest-first.
    /// </summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// Total byte size of all files in the backup directory (recursive).
    /// Lets the UI show «4.2 KB» next to each backup so the user can
    /// spot an abnormally large one.
    /// </summary>
    public long SizeBytes { get; init; }
}
