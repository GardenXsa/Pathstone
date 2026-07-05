namespace MyGame.Core.Profile;

/// <summary>
/// The single local user profile. Replaces the TS <c>lib/auth.ts</c>
/// user/JWT/cookie system entirely — the desktop rewrite has NO auth, NO
/// tokens, NO cookies, NO multi-user. One profile per install, persisted
/// as <c>profile.json</c> in <see cref="ProfileStore.ProfileDirectory"/>.
///
/// <para>
/// The profile carries only the identity the rest of the app needs: a
/// stable <see cref="Id"/> (so saves can record an <c>ownerId</c>), a
/// display <see cref="Nickname"/>, and the two timestamps for the
/// profile-management UI. There is no password, no email, no roles.
/// </para>
/// </summary>
public sealed record Profile
{
    /// <summary>
    /// Stable unique id for this profile. Generated once at first launch
    /// and never changed. Stored on every save as <c>ownerId</c> so a
    /// save always knows which profile created it (useful when the
    /// player exports a save to share with another install).
    /// </summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>
    /// Display name shown in the UI and in multiplayer lobbies. Must
    /// pass <see cref="ValidateNickname"/> (2-20 chars; Latin or Cyrillic
    /// letters, digits, spaces, hyphens, underscores; must start/end with
    /// a letter or digit).
    /// </summary>
    public string Nickname { get; init; } = string.Empty;

    /// <summary>UTC timestamp when this profile was first created.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>UTC timestamp of the last nickname change.</summary>
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;

    // ─── Nickname validation ───────────────────────────────────────────

    /// <summary>
    /// Validate a candidate nickname. Port of <c>validateNickname</c>
    /// from <c>lib/auth.ts</c>. Rules:
    /// <list type="bullet">
    ///   <item>Length 2-20 chars (after trim).</item>
    ///   <item>First and last char must be a letter or digit
    ///         (Latin or Cyrillic).</item>
    ///   <item>Middle chars may be letter, digit, space, hyphen, or
    ///         underscore (Latin or Cyrillic letters).</item>
    /// </list>
    /// Returns true on success; on failure, <paramref name="error"/>
    /// carries a Russian-language message (kept verbatim from the TS
    /// source so existing UI strings keep matching).
    /// </summary>
    public static bool ValidateNickname(string nickname, out string error)
    {
        error = string.Empty;
        if (nickname is null)
        {
            error = "Ник не может быть пустым";
            return false;
        }

        var trimmed = nickname.Trim();
        if (trimmed.Length == 0)
        {
            error = "Ник не может быть пустым";
            return false;
        }
        if (trimmed.Length < 2)
        {
            error = "Минимум 2 символа";
            return false;
        }
        if (trimmed.Length > 20)
        {
            error = "Максимум 20 символов";
            return false;
        }
        if (!IsEdgeChar(trimmed[0]) || !IsEdgeChar(trimmed[^1]))
        {
            error = "Допустимы буквы, цифры, пробелы, дефисы и подчёркивания";
            return false;
        }
        for (int i = 1; i < trimmed.Length - 1; i++)
        {
            if (!IsMiddleChar(trimmed[i]))
            {
                error = "Допустимы буквы, цифры, пробелы, дефисы и подчёркивания";
                return false;
            }
        }
        return true;
    }

    /// <summary>
    /// Convenience wrapper that throws <see cref="ArgumentException"/> on
    /// invalid input. Use <see cref="ValidateNickname"/> for non-throwing
    /// checks (UI validation); use this when the caller already knows the
    /// input is valid.
    /// </summary>
    public static string NormalizeOrThrow(string nickname)
    {
        if (!ValidateNickname(nickname, out var error))
            throw new ArgumentException(error, nameof(nickname));
        return nickname.Trim();
    }

    private static bool IsEdgeChar(char c) =>
        IsLatinLetter(c) || IsDigit(c) || IsCyrillicLetter(c);

    private static bool IsMiddleChar(char c) =>
        IsEdgeChar(c) || c == ' ' || c == '_' || c == '-';

    private static bool IsLatinLetter(char c) =>
        (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');

    private static bool IsDigit(char c) => c >= '0' && c <= '9';

    // Cyrillic block (U+0400..U+04FF) covers modern Russian + legacy
    // Cyrillic letters. Matches the TS regex range \u0400-\u04FF.
    private static bool IsCyrillicLetter(char c) =>
        c >= '\u0400' && c <= '\u04FF';
}
