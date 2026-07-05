using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MyGame.Desktop.Services;

/// <summary>
/// Crash dump writer for the Pathstone desktop app. Writes a timestamped
/// text dump of an uncaught exception to
/// <c>%APPDATA%/MyGame/logs/crash-{yyyyMMdd-HHmmss}.txt</c> (alongside
/// the saves/profile so all per-user state is in one place). The dump
/// contains:
/// <list type="bullet">
///   <item>Timestamp (UTC + local).</item>
///   <item>.NET runtime version + OS description (RuntimeInformation).</item>
///   <item>Exception type + message + full stack trace, walking inner
///     exceptions recursively.</item>
///   <item>A short footer pointing the user at the logs directory.</item>
/// </list>
///
/// <para>
/// <b>Robustness</b>: every step is wrapped in try/catch — the crash
/// logger MUST NOT throw while crashing. If even the file write fails
/// (disk full, permissions, etc.), the dump is written to
/// <see cref="Console.Error"/> as a last resort.
/// </para>
///
/// <para>
/// <b>Thread-safety</b>: <see cref="Log"/> is safe to call from any
/// thread (it takes a process-wide lock so two concurrent crashes don't
/// interleave their dumps).
/// </para>
/// </summary>
public static class CrashLogger
{
    private static readonly object s_lock = new();

    /// <summary>
    /// Root directory for crash dumps:
    /// <c>%APPDATA%/MyGame/logs</c> on Windows,
    /// <c>~/.config/MyGame/logs</c> on Linux,
    /// <c>~/Library/Application Support/MyGame/logs</c> on macOS.
    /// Falls back to <c>{AppContext.BaseDirectory}/logs</c> if APPDATA
    /// isn't available.
    /// </summary>
    public static string LogsDirectory
    {
        get
        {
            try
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                if (!string.IsNullOrEmpty(appData))
                    return Path.Combine(appData, "MyGame", "logs");
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrEmpty(home))
                    return Path.Combine(home, ".mygame", "logs");
                return Path.Combine(AppContext.BaseDirectory, "logs");
            }
            catch
            {
                // Last-resort: folder next to the executable.
                return Path.Combine(AppContext.BaseDirectory, "logs");
            }
        }
    }

    /// <summary>
    /// Write a crash dump for <paramref name="exception"/> and return the
    /// absolute path to the dump file. Returns null if even the file
    /// write fails (the dump is also written to
    /// <see cref="Console.Error"/> in that case so it isn't lost).
    /// </summary>
    /// <param name="exception">The exception to dump. Null is treated as
    /// an "unknown crash" sentinel.</param>
    /// <param name="context">Optional extra context — e.g. which handler
    /// caught the exception (<c>"AppDomain.UnhandledException"</c>,
    /// <c>"TaskScheduler.UnobservedTaskException"</c>, etc.). Included
    /// as a header line in the dump.</param>
    public static string? Log(Exception? exception, string? context = null)
    {
        lock (s_lock)
        {
            string? filePath = null;
            try
            {
                var dir = LogsDirectory;
                Directory.CreateDirectory(dir);
                var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
                filePath = Path.Combine(dir, $"crash-{stamp}.txt");
                var dump = BuildDump(exception, context, filePath);
                File.WriteAllText(filePath, dump, Encoding.UTF8);
            }
            catch (Exception writeEx)
            {
                // The crash logger must NEVER throw. Fall back to
                // Console.Error so the dump isn't lost entirely.
                try
                {
                    Console.Error.WriteLine("=== CRASH LOGGER FAILED TO WRITE FILE ===");
                    Console.Error.WriteLine($"File path attempted: {filePath}");
                    Console.Error.WriteLine($"Write error: {writeEx}");
                    Console.Error.WriteLine("=== ORIGINAL EXCEPTION ===");
                    Console.Error.WriteLine(BuildDump(exception, context, filePath));
                }
                catch { /* nothing else we can do */ }
            }
            return filePath;
        }
    }

    /// <summary>
    /// Build the human-readable crash dump string. Walks inner exceptions
    /// recursively so a deeply-nested AggregateException or
    /// TargetInvocationException is fully visible.
    /// </summary>
    private static string BuildDump(Exception? exception, string? context, string? filePath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== Pathstone Crash Dump ===");
        sb.AppendLine();
        sb.AppendLine($"Timestamp (UTC): {DateTimeOffset.UtcNow:O}");
        sb.AppendLine($"Timestamp (local): {DateTimeOffset.Now:O}");
        sb.AppendLine();
        sb.AppendLine("--- Runtime ---");
        try
        {
            sb.AppendLine($".NET version: {RuntimeInformation.FrameworkDescription}");
            sb.AppendLine($"OS: {RuntimeInformation.OSDescription}");
            sb.AppendLine($"OS architecture: {RuntimeInformation.OSArchitecture}");
            sb.AppendLine($"Process architecture: {RuntimeInformation.ProcessArchitecture}");
        }
        catch (Exception ex)
        {
            sb.AppendLine($"(failed to read RuntimeInformation: {ex.Message})");
        }
        sb.AppendLine();
        sb.AppendLine("--- Context ---");
        sb.AppendLine(string.IsNullOrEmpty(context) ? "(no context provided)" : context);
        sb.AppendLine();
        sb.AppendLine("--- Exception ---");
        AppendException(sb, exception, depth: 0);
        sb.AppendLine();
        sb.AppendLine("--- End of dump ---");
        if (!string.IsNullOrEmpty(filePath))
            sb.AppendLine($"Dump file: {filePath}");
        sb.AppendLine("Please report this on GitHub with the dump file attached.");
        return sb.ToString();
    }

    /// <summary>
    /// Append one exception + walk inner exceptions recursively. Depth
    /// is capped at 10 to prevent infinite loops on pathological
    /// cyclic exception chains (which shouldn't happen, but defense in
    /// depth).
    /// </summary>
    private static void AppendException(StringBuilder sb, Exception? ex, int depth)
    {
        if (ex is null) return;
        if (depth > 10)
        {
            sb.AppendLine("  [max inner-exception depth reached]");
            return;
        }
        var indent = depth > 0 ? new string(' ', depth * 2) : "";
        if (depth > 0)
            sb.AppendLine($"{indent}--- Inner exception (depth {depth}) ---");
        sb.AppendLine($"{indent}Type: {ex.GetType().FullName}");
        sb.AppendLine($"{indent}Message: {ex.Message}");
        if (!string.IsNullOrEmpty(ex.Source))
            sb.AppendLine($"{indent}Source: {ex.Source}");
        if (ex.TargetSite is not null)
            sb.AppendLine($"{indent}TargetSite: {ex.TargetSite}");
        sb.AppendLine($"{indent}StackTrace:");
        var stack = ex.StackTrace;
        if (string.IsNullOrEmpty(stack))
        {
            sb.AppendLine($"{indent}  (no stack trace)");
        }
        else
        {
            // Indent each stack line so it lines up with the header.
            foreach (var line in stack.Split('\n'))
                sb.AppendLine($"{indent}  {line.TrimEnd('\r')}");
        }
        // Recurse into inner exception(s).
        if (ex is AggregateException agg)
        {
            // AggregateException flattens inner exceptions — walk each.
            var inners = agg.InnerExceptions;
            if (inners is { Count: > 0 })
            {
                sb.AppendLine($"{indent}--- Aggregate inner exceptions ({inners.Count}) ---");
                for (int i = 0; i < inners.Count; i++)
                {
                    sb.AppendLine($"{indent}[{i}]");
                    AppendException(sb, inners[i], depth + 1);
                }
                return;
            }
        }
        if (ex.InnerException is not null)
        {
            AppendException(sb, ex.InnerException, depth + 1);
        }
    }
}
