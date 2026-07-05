using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using MyGame.Core.Saves;
using MyGame.Core.World;

using GameWorld = MyGame.Core.World.World;

namespace MyGame.Core.Tooling;

/// <summary>
/// Records each GM turn to a replay file. Issue #85. Local-only.
/// </summary>
public sealed class ReplayRecorder
{
    private readonly string _replayDir;
    private readonly string _saveId;
    private readonly List<ReplayTurn> _turns = new();

    public ReplayRecorder(string replayDir, string saveId)
    {
        _replayDir = replayDir;
        _saveId = saveId;
        Directory.CreateDirectory(_replayDir);
    }

    public void RecordTurn(int turnNumber, string action, string narrative, GameWorld world)
    {
        _turns.Add(new ReplayTurn
        {
            TurnNumber = turnNumber,
            Timestamp = DateTimeOffset.UtcNow,
            Action = action,
            Narrative = narrative,
            WorldSnapshot = world.ToJson(),
        });
    }

    public void Save()
    {
        if (_turns.Count == 0) return;
        var path = Path.Combine(_replayDir, $"{_saveId}.pathstone-replay");
        var json = JsonSerializer.Serialize(_turns, new JsonSerializerOptions { WriteIndented = false });
        File.WriteAllText(path, json);
    }

    public static List<ReplayTurn>? Load(string filePath)
    {
        try { return JsonSerializer.Deserialize<List<ReplayTurn>>(File.ReadAllText(filePath)); }
        catch { return null; }
    }
}

public sealed class ReplayTurn
{
    public int TurnNumber { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public string Action { get; set; } = "";
    public string Narrative { get; set; } = "";
    public string WorldSnapshot { get; set; } = "";
}
