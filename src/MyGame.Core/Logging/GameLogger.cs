using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace MyGame.Core.Logging;

/// <summary>
/// Structured logger writing to %APPDATA%/MyGame/logs/game-{date}.log.
/// Issue #71. Levels: Trace, Debug, Info, Warning, Error.
/// Thread-safe via lock. File is flushed on every write (safe for crashes).
/// </summary>
public sealed class GameLogger
{
    public enum Level
    {
        Trace = 0,
        Debug = 1,
        Info = 2,
        Warning = 3,
        Error = 4,
    }

    private static GameLogger? _instance;
    private static readonly object _initLock = new();

    public static GameLogger Instance => _instance ?? throw new InvalidOperationException("GameLogger not initialized. Call Initialize first.");

    public Level MinLevel { get; set; } = Level.Info;

    private readonly string _logDir;
    private readonly object _writeLock = new();
    private readonly Queue<(Level Level, string Message, DateTimeOffset Ts)> _recent = new();
    private const int MaxRecent = 100;

    private GameLogger(string logDir)
    {
        _logDir = logDir;
        Directory.CreateDirectory(_logDir);
    }

    public static void Initialize(string logDir, Level minLevel = Level.Info)
    {
        lock (_initLock)
        {
            _instance = new GameLogger(logDir) { MinLevel = minLevel };
        }
    }

    public void Log(Level level, string message)
    {
        if (level < MinLevel) return;
        var ts = DateTimeOffset.UtcNow;
        var line = $"[{ts:yyyy-MM-dd HH:mm:ss.fff}] [{level,-5}] {message}";

        lock (_writeLock)
        {
            _recent.Enqueue((level, message, ts));
            while (_recent.Count > MaxRecent) _recent.Dequeue();

            try
            {
                var file = Path.Combine(_logDir, $"game-{ts:yyyyMMdd}.log");
                File.AppendAllText(file, line + "\n");
            }
            catch { /* don't crash on log failure */ }
        }
    }

    public void Trace(string msg) => Log(Level.Trace, msg);
    public void Debug(string msg) => Log(Level.Debug, msg);
    public void Info(string msg) => Log(Level.Info, msg);
    public void Warning(string msg) => Log(Level.Warning, msg);
    public void Error(string msg) => Log(Level.Error, msg);

    public IReadOnlyList<string> GetRecent(int count = 50)
    {
        lock (_writeLock)
        {
            return _recent
                .TakeLast(count)
                .Select(e => $"[{e.Ts:HH:mm:ss}] [{e.Level,-5}] {e.Message}")
                .ToList();
        }
    }
}
