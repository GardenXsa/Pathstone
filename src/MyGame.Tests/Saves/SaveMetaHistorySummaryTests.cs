using System.Text.Json;
using MyGame.Core.Saves;
using MyGame.Core.World;
using MyGame.Core.World.Content;

// 'World' is both a namespace (MyGame.Core.World) and a type
// (MyGame.Core.World.World). Alias to GameWorld to disambiguate, matching
// the convention used by SaveManager.cs.
using GameWorld = MyGame.Core.World.World;

namespace MyGame.Tests.Saves;

/// <summary>
/// Unit tests for the <see cref="SaveMeta.HistorySummary"/> field (issue #25).
/// Verifies that the summary round-trips through JSON (save/load) and
/// that old saves without the field load with null (backward compat).
/// </summary>
public class SaveMetaHistorySummaryTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "MyGameSaveMetaTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void HistorySummary_DefaultsToNull()
    {
        // A fresh SaveMeta has HistorySummary = null (no summarization
        // has happened yet on a new save).
        var meta = new SaveMeta { Id = "save_test", Name = "test" };
        Assert.Null(meta.HistorySummary);
    }

    [Fact]
    public void HistorySummary_RoundTripsThroughJson()
    {
        // Serialising a meta with a non-null HistorySummary + deserialising
        // back must preserve the summary text exactly. This is the
        // save/load round-trip the GameViewModel relies on to persist
        // the GM's context across reloads.
        var meta = new SaveMeta
        {
            Id = "save_test",
            Name = "test",
            HistorySummary = "Игрок встретил торговца Арвина в Деревне, купил зелье, отправился в Тёмный лес.",
        };

        var json = JsonSerializer.Serialize(meta, WorldJson.Options);
        var back = JsonSerializer.Deserialize<SaveMeta>(json, WorldJson.Options);

        Assert.NotNull(back);
        Assert.Equal(meta.HistorySummary, back!.HistorySummary);
    }

    [Fact]
    public void HistorySummary_NullRoundTripsAsNull()
    {
        // A null HistorySummary must round-trip as null (not as empty
        // string, not as undefined). The GM treats null as "no summary
        // yet" and starts fresh summarization when the history grows.
        var meta = new SaveMeta
        {
            Id = "save_test",
            Name = "test",
            HistorySummary = null,
        };

        var json = JsonSerializer.Serialize(meta, WorldJson.Options);
        var back = JsonSerializer.Deserialize<SaveMeta>(json, WorldJson.Options);

        Assert.NotNull(back);
        Assert.Null(back!.HistorySummary);
    }

    [Fact]
    public void HistorySummary_OldSaveWithoutField_LoadsAsNull()
    {
        // A settings.json written before issue #25 doesn't have the
        // HistorySummary field. Deserialising it must not throw, and the
        // field defaults to null. This is the backward-compatibility
        // guarantee — old saves load fine, the GM just starts with no
        // summary and will create one when the history grows.
        var oldJson = """
            {
              "Id": "save_test",
              "Name": "old save",
              "OwnerId": "00000000-0000-0000-0000-000000000000",
              "CreatedAt": "2024-01-01T00:00:00+00:00",
              "UpdatedAt": "2024-01-01T00:00:00+00:00",
              "PlaytimeMs": 0,
              "Turn": 0,
              "BuildStatus": 2,
              "EngineVersion": "0.1.0",
              "StorageVersion": 2,
              "SessionPromptTokens": 1000,
              "SessionCompletionTokens": 500
            }
            """;

        var back = JsonSerializer.Deserialize<SaveMeta>(oldJson, WorldJson.Options);

        Assert.NotNull(back);
        Assert.Null(back!.HistorySummary);
        // Other fields load as expected.
        Assert.Equal("save_test", back.Id);
        Assert.Equal(1000, back.SessionPromptTokens);
    }

    [Fact]
    public void SaveManager_SaveAll_LoadAll_RoundTripsHistorySummary()
    {
        // End-to-end: a SaveManager.SaveAll that persists a meta with
        // HistorySummary set, followed by a LoadAll, must return the
        // same summary text. This is the actual save/load path the
        // GameViewModel uses.
        var tempDir = TempDir();
        try
        {
            var manager = new SaveManager(savesDirectory: tempDir, registries: ContentRegistry.LoadDefault());
            var world = DefaultWorld.Create(seed: 1);
            var meta = manager.CreateSave("summary test", world);
            // Attach a summary as the GameViewModel would after a GM
            // summarization pass.
            meta = meta with
            {
                HistorySummary = "Игрок исследовал руины, нашёл артефакт, сразился с гоблином.",
                SessionPromptTokens = 1234,
                SessionCompletionTokens = 567,
            };
            manager.SaveAll(meta.Id, world, meta, Array.Empty<LogEntry>());

            var loaded = manager.LoadAll(meta.Id);
            Assert.NotNull(loaded);
            Assert.Equal("Игрок исследовал руины, нашёл артефакт, сразился с гоблином.",
                loaded.Value.meta.HistorySummary);
            Assert.Equal(1234, loaded.Value.meta.SessionPromptTokens);
            Assert.Equal(567,  loaded.Value.meta.SessionCompletionTokens);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void SaveManager_OldSave_LoadsAsNullSummary()
    {
        // End-to-end: a meta.json written WITHOUT the HistorySummary
        // field (simulating an old save) loads with null. The SaveManager
        // must not throw on the missing field.
        var tempDir = TempDir();
        try
        {
            var manager = new SaveManager(savesDirectory: tempDir, registries: ContentRegistry.LoadDefault());
            var world = DefaultWorld.Create(seed: 1);
            var meta = manager.CreateSave("old style save", world);

            // Overwrite meta.json with a hand-crafted JSON that's missing
            // the HistorySummary field — simulating an old save.
            var metaPath = Path.Combine(tempDir, meta.Id, "meta.json");
            var oldStyleJson = $$"""
                {
                  "Id": "{{meta.Id}}",
                  "Name": "old style save",
                  "OwnerId": "00000000-0000-0000-0000-000000000000",
                  "CreatedAt": "2024-01-01T00:00:00+00:00",
                  "UpdatedAt": "2024-01-01T00:00:00+00:00",
                  "PlaytimeMs": 0,
                  "Turn": 0,
                  "BuildStatus": 2,
                  "EngineVersion": "0.1.0",
                  "StorageVersion": 2,
                  "SessionPromptTokens": 0,
                  "SessionCompletionTokens": 0
                }
                """;
            File.WriteAllText(metaPath, oldStyleJson);

            var loaded = manager.LoadAll(meta.Id);
            Assert.NotNull(loaded);
            Assert.Null(loaded.Value.meta.HistorySummary);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }
}
