using MyGame.Core.AI.Prompts;

namespace MyGame.Tests.AI.Prompts;

/// <summary>
/// Unit tests for <see cref="PromptLoader"/>'s hot-reload feature
/// (issue #82). Verifies that when <see cref="PromptLoader.EnableHotReload"/>
/// is true, the loader reads from a disk <c>prompts/</c> folder before
/// falling back to the embedded resource — and that disk reads are
/// NOT cached (so editing the .md file + re-running Get picks up changes).
/// </summary>
public class PromptLoaderHotReloadTests : IDisposable
{
    private readonly string _tempDir;

    public PromptLoaderHotReloadTests()
    {
        // Per-test temp dir for the hot-reload folder. Using a fresh dir
        // per test avoids cross-test interference and doesn't touch the
        // process working directory.
        _tempDir = Path.Combine(Path.GetTempPath(), "MyGamePromptTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public void Get_HotReloadDisabled_FallsBackToEmbeddedResource()
    {
        // When EnableHotReload is false, the disk check is skipped
        // entirely — even if a prompts/ folder exists with the file,
        // the embedded resource is used. This is the production behaviour.
        var loader = new PromptLoader(enableHotReload: false)
        {
            HotReloadFolderOverride = _tempDir,
        };

        // Drop a disk file that would override the embedded system.md.
        File.WriteAllText(Path.Combine(_tempDir, "system.md"),
            "DISK OVERRIDE — should NOT be used when hot-reload is off.");

        var content = loader.Get("system");
        Assert.DoesNotContain("DISK OVERRIDE", content);
        // The embedded system.md should be non-empty + contain the
        // expected placeholder (system.md is a known resource).
        Assert.False(string.IsNullOrWhiteSpace(content));
    }

    [Fact]
    public void Get_HotReloadEnabled_ReadsDiskFileWhenPresent()
    {
        // When EnableHotReload is true AND a disk file exists, the disk
        // content wins over the embedded resource. This is the dev
        // workflow: edit prompts/system.md on disk → next Get picks it up.
        var loader = new PromptLoader(enableHotReload: true)
        {
            HotReloadFolderOverride = _tempDir,
        };

        File.WriteAllText(Path.Combine(_tempDir, "system.md"),
            "DISK OVERRIDE — hot reload is on, this should win.");

        var content = loader.Get("system");
        Assert.Equal("DISK OVERRIDE — hot reload is on, this should win.", content);
    }

    [Fact]
    public void Get_HotReloadEnabled_FallsBackToEmbeddedWhenDiskFileMissing()
    {
        // When EnableHotReload is true BUT no disk file exists for the
        // requested name, the loader falls back to the embedded resource
        // (current behaviour). This is important: hot-reload is opt-in
        // per-prompt, not all-or-nothing.
        var loader = new PromptLoader(enableHotReload: true)
        {
            HotReloadFolderOverride = _tempDir,
        };

        // No file dropped — the embedded resource is used.
        var content = loader.Get("system");
        Assert.False(string.IsNullOrWhiteSpace(content));
        Assert.DoesNotContain("DISK OVERRIDE", content);
    }

    [Fact]
    public void Get_HotReloadEnabled_DiskReadsAreNotCached()
    {
        // Disk reads must NOT be cached — editing the .md file + re-running
        // Get picks up the change immediately. This is the whole point of
        // hot-reload: the dev shouldn't have to restart the app to see
        // prompt edits.
        var loader = new PromptLoader(enableHotReload: true)
        {
            HotReloadFolderOverride = _tempDir,
        };

        var path = Path.Combine(_tempDir, "system.md");
        File.WriteAllText(path, "version 1");
        Assert.Equal("version 1", loader.Get("system"));

        // Edit the file and re-Get — must see the new content.
        File.WriteAllText(path, "version 2");
        Assert.Equal("version 2", loader.Get("system"));

        // And again — every call re-reads.
        File.WriteAllText(path, "version 3");
        Assert.Equal("version 3", loader.Get("system"));
    }

    [Fact]
    public void Get_HotReloadEnabled_DiskReadFailure_FallsBackToEmbedded()
    {
        // If the disk file exists but can't be read (e.g. locked by an
        // editor — we simulate by throwing via a missing-folder edge
        // case), the loader falls through to the embedded resource
        // rather than throwing. The dev workflow shouldn't crash the
        // app because a prompt file is briefly inaccessible.
        //
        // We can't easily simulate an IOException on File.ReadAllText
        // without a real lock, but we CAN test the "folder doesn't
        // exist" case: HotReloadFolderOverride pointing at a nonexistent
        // path → File.Exists returns false → fall through to embedded.
        var loader = new PromptLoader(enableHotReload: true)
        {
            HotReloadFolderOverride = Path.Combine(_tempDir, "does_not_exist"),
        };

        var content = loader.Get("system");
        Assert.False(string.IsNullOrWhiteSpace(content));
    }

    [Fact]
    public void Get_HotReloadEnabled_UnknownPrompt_ThrowsWithHelpfulMessage()
    {
        // When neither disk nor embedded has the prompt, the loader
        // throws FileNotFoundException with a message listing available
        // embedded resources (so the dev sees what's there). The
        // hot-reload-on message also mentions the disk folder.
        var loader = new PromptLoader(enableHotReload: true)
        {
            HotReloadFolderOverride = _tempDir,
        };

        var ex = Assert.Throws<FileNotFoundException>(() => loader.Get("this_prompt_does_not_exist"));
        Assert.Contains("this_prompt_does_not_exist", ex.Message);
        Assert.Contains("prompts/", ex.Message); // hint about the disk folder
    }

    [Fact]
    public void Exists_HotReloadEnabled_ReturnsTrueForDiskFile()
    {
        // Exists must return true when a disk file is present (even if
        // no embedded resource matches). This lets callers probe for
        // optional prompts that exist only on disk during dev.
        var loader = new PromptLoader(enableHotReload: true)
        {
            HotReloadFolderOverride = _tempDir,
        };

        // No disk file yet → Exists returns false for an unknown name.
        Assert.False(loader.Exists("nonexistent_prompt_xyz"));

        // Drop a disk file → Exists returns true.
        File.WriteAllText(Path.Combine(_tempDir, "nonexistent_prompt_xyz.md"), "content");
        Assert.True(loader.Exists("nonexistent_prompt_xyz"));
    }

    [Fact]
    public void Exists_HotReloadDisabled_OnlyChecksEmbeddedResources()
    {
        // When hot-reload is off, Exists only checks the embedded
        // resources — disk files are invisible. This is the production
        // behaviour: the disk folder is a dev-time convenience.
        var loader = new PromptLoader(enableHotReload: false)
        {
            HotReloadFolderOverride = _tempDir,
        };

        // Drop a disk file for a prompt that DOESN'T exist as an
        // embedded resource. Exists must return false.
        File.WriteAllText(Path.Combine(_tempDir, "disk_only_prompt.md"), "content");
        Assert.False(loader.Exists("disk_only_prompt"));

        // But a prompt that DOES exist as an embedded resource still
        // returns true.
        Assert.True(loader.Exists("system"));
    }

    [Fact]
    public void Constructor_DefaultHotReloadFlag_MatchesBuildConfiguration()
    {
        // The parameterless ctor sets EnableHotReload based on the build
        // configuration: true in DEBUG, false in RELEASE. We can't test
        // both configurations from a single test run, but we CAN verify
        // the flag is set (whatever the default is for THIS build).
        var loader = new PromptLoader();
#if DEBUG
        Assert.True(loader.EnableHotReload);
#else
        Assert.False(loader.EnableHotReload);
#endif
    }

    [Fact]
    public void EnableHotReload_CanBeToggledAtRuntime()
    {
        // The EnableHotReload flag is settable at runtime so a host (or
        // test) can flip the behaviour without rebuilding. This is
        // useful for a release build with a developer-mode flag.
        var loader = new PromptLoader(enableHotReload: false)
        {
            HotReloadFolderOverride = _tempDir,
        };
        Assert.False(loader.EnableHotReload);

        // Drop a disk file — initially ignored.
        File.WriteAllText(Path.Combine(_tempDir, "system.md"), "DISK VERSION");
        Assert.DoesNotContain("DISK VERSION", loader.Get("system"));

        // Flip the flag — now the disk version is used.
        loader.EnableHotReload = true;
        Assert.Equal("DISK VERSION", loader.Get("system"));
    }
}
