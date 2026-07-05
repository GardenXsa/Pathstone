using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;

namespace MyGame.Core.AI.Prompts;

/// <summary>
/// Loads prompt-template markdown files embedded in the assembly and
/// renders them with <c>{{VAR}}</c> substitution. Port of the
/// <c>loadPrompt</c> + <c>fill</c> helpers from <c>ai/prompts/index.ts</c>.
///
/// Prompt files live under <c>AI/Prompts/*.md</c> in the MyGame.Core
/// project and are embedded as manifest resources at build time (see the
/// <c>EmbeddedResource</c> entry in <c>MyGame.Core.csproj</c>). The
/// resource names follow the convention
/// <c>MyGame.Core.AI.Prompts.&lt;name&gt;.md</c> (project default namespace
/// + folder path with dots + filename). This loader resolves a logical
/// name like <c>"system"</c> to that resource.
///
/// The advanced builder functions in <c>prompts/index.ts</c>
/// (<c>buildStableSystemPrompt</c>, <c>buildLiveStatePrompt</c>,
/// <c>buildWorldBuilderPrompt</c>, etc.) are NOT ported here — they depend
/// on World state-rendering helpers (<c>buildWorldStateBlock</c>,
/// <c>carriedWeight</c>, <c>derivedResourceMax</c>, etc.) that aren't part
/// of this task. Callers (the GameMaster / WorldBuilder agents) fill in
/// the <c>{{WORLD_STATE}}</c>, <c>{{ITEM_TEMPLATES}}</c>, etc.
/// placeholders themselves using a simple World-rendering helper.
/// </summary>
/// <remarks>
/// <b>Hot-reload of prompts (issue #82):</b> during development it's
/// painful to rebuild the whole solution just to tweak a prompt's wording.
/// This loader therefore supports an optional disk fallback controlled
/// by <see cref="EnableHotReload"/>:
///
/// <list type="bullet">
///   <item>When <see cref="EnableHotReload"/> is <c>true</c> (default in
///     DEBUG builds), <see cref="Get"/> first checks for a
///     <c>prompts/{name}.md</c> file in the current working directory.
///     If the file exists, it's read from disk (NO caching — each call
///     re-reads so editing the .md + re-running Get picks up changes).
///     Otherwise, it falls back to the embedded resource (cached as
///     before).</item>
///   <item>When <see cref="EnableHotReload"/> is <c>false</c> (default in
///     RELEASE builds), the disk check is skipped entirely — embedded
///     resources only. This is the production behaviour: prompts ship
///     inside the assembly, can't be tampered with, and load once + cache.</item>
/// </list>
///
/// <para>
/// <b>Dev workflow:</b> drop a <c>prompts/</c> folder next to the
/// executable (or run the desktop app from the project root where a
/// <c>prompts/</c> sibling exists — e.g. symlink
/// <c>desktop-app/prompts/</c> → <c>src/MyGame.Core/AI/Prompts/</c>).
/// Edit the .md files there; the next <see cref="Get"/> call picks up
/// the change without a rebuild. The embedded resources remain the
/// source of truth for release builds — the disk folder is purely a
/// dev-time convenience and is ignored when <see cref="EnableHotReload"/>
/// is false.
/// </para>
/// </remarks>
public sealed class PromptLoader
{
    private static readonly Assembly s_asm = typeof(PromptLoader).Assembly;

    // Lazily-built map of logical name → full resource name. Scans the
    // manifest resource names ONCE on first use; subsequent loads are
    // direct dictionary lookups.
    private static readonly Lazy<Dictionary<string, string>> s_resourceMap = new(BuildResourceMap);

    // Per-instance cache of (name → rendered template text) for the
    // EMBEDDED-resource path only. Disk reads (hot-reload) are NEVER
    // cached — see <see cref="Get"/>. Different loader instances share
    // the static resource map but not this cache (intentional — tests
    // may want to reset).
    private readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);

    // Folder name (relative to the current working directory) that holds
    // hot-reloadable prompt overrides. Kept as a const so the dev
    // workflow is documented in one place; tests override the working
    // directory rather than this const.
    private const string HotReloadFolder = "prompts";

#if DEBUG
    private const bool DefaultEnableHotReload = true;
#else
    private const bool DefaultEnableHotReload = false;
#endif

    /// <summary>
    /// Default shared loader. Convenience for callers that don't need to
    /// inject a custom instance — equivalent to <c>new PromptLoader()</c>.
    /// </summary>
    public static PromptLoader Default { get; } = new();

    /// <summary>
    /// Create a loader with the default hot-reload behaviour (on in DEBUG,
    /// off in RELEASE). Use the parameterless ctor in production; tests
    /// can flip <see cref="EnableHotReload"/> after construction.
    /// </summary>
    public PromptLoader()
        : this(enableHotReload: DefaultEnableHotReload) { }

    /// <summary>
    /// Create a loader with an explicit hot-reload setting. Used by tests
    /// and by hosts that want to override the DEBUG/RELEASE default
    /// (e.g. a release build with a developer-mode flag turned on).
    /// </summary>
    /// <param name="enableHotReload">
    /// When true, <see cref="Get"/> checks the disk folder
    /// (<c>prompts/{name}.md</c> in the working directory) before
    /// falling back to the embedded resource. When false, the disk
    /// check is skipped entirely.
    /// </param>
    public PromptLoader(bool enableHotReload)
    {
        EnableHotReload = enableHotReload;
    }

    /// <summary>
    /// Whether <see cref="Get"/> should check the disk for a
    /// <c>prompts/{name}.md</c> file before falling back to the embedded
    /// resource. Default is <c>true</c> in DEBUG builds, <c>false</c> in
    /// RELEASE. Settable at runtime so a host (or test) can flip the
    /// behaviour without rebuilding.
    ///
    /// <para>
    /// When true, disk reads are NEVER cached — editing the .md file +
    /// re-running Get picks up the change immediately. Embedded-resource
    /// reads (the fallback path) are cached as before.
    /// </para>
    /// </summary>
    public bool EnableHotReload { get; set; }

    /// <summary>
    /// Optional override for the hot-reload folder. Defaults to
    /// <c>"prompts"</c> (relative to the current working directory).
    /// Tests set this to an absolute temp path so they don't have to
    /// change the process working directory (which would interfere with
    /// other tests + the host's relative-path assumptions).
    /// </summary>
    public string? HotReloadFolderOverride { get; set; }

    /// <summary>
    /// Get the raw markdown content of the named prompt.
    ///
    /// <para>
    /// <b>Hot-reload path (issue #82):</b> when <see cref="EnableHotReload"/>
    /// is true, this method first checks for a <c>prompts/{name}.md</c>
    /// file (in the working directory, or in
    /// <see cref="HotReloadFolderOverride"/> if set). If the file exists,
    /// it's read from disk and returned WITHOUT caching (so the next call
    /// re-reads and picks up edits). Disk-read failures (permission
    /// denied, file in use, etc.) fall through to the embedded resource
    /// path rather than throwing — the dev workflow shouldn't crash the
    /// app because a prompt file is briefly locked by an editor.
    /// </para>
    ///
    /// <para>
    /// <b>Embedded path:</b> when the disk check is disabled or the file
    /// doesn't exist, the prompt is loaded from the assembly's manifest
    /// resources and cached per-instance (so subsequent loads are O(1)).
    /// </para>
    ///
    /// Throws <see cref="FileNotFoundException"/> if neither path yields
    /// a prompt — i.e. the disk file doesn't exist AND no embedded
    /// resource matches <c>&lt;name&gt;.md</c>.
    /// </summary>
    /// <param name="name">
    /// Logical prompt name without extension (e.g. <c>"system"</c>,
    /// <c>"worldbuilder"</c>, <c>"world-planner"</c>).
    /// </param>
    public string Get(string name)
    {
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("Prompt name is required.", nameof(name));

        // Hot-reload path: read from disk WITHOUT caching so the next
        // call picks up edits. Failures fall through to the embedded path.
        if (EnableHotReload && TryReadFromDisk(name, out var diskText))
            return diskText!;

        if (_cache.TryGetValue(name, out var cached))
            return cached;

        var map = s_resourceMap.Value;
        if (!map.TryGetValue(name, out var resourceName))
        {
            throw new FileNotFoundException(
                $"Prompt '{name}.md' not found among embedded resources" +
                (EnableHotReload ? " or on disk in the prompts/ folder." : ".") +
                $" Available embedded: {string.Join(", ", map.Keys.OrderBy(k => k, StringComparer.Ordinal))}");
        }

        using var stream = s_asm.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException(
                $"Embedded resource '{resourceName}' is listed but could not be opened.");
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd();
        _cache[name] = text;
        return text;
    }

    /// <summary>
    /// Try to read <c>{HotReloadFolder}/{name}.md</c> from disk. Returns
    /// false (rather than throwing) if the file doesn't exist, the folder
    /// doesn't exist, or the read fails for any reason — the caller falls
    /// through to the embedded-resource path. Logged to <c>Trace</c> on
    /// unexpected IO errors so a misconfigured dev folder is visible
    /// without breaking the app.
    /// </summary>
    private bool TryReadFromDisk(string name, out string? text)
    {
        text = null;
        try
        {
            var folder = !string.IsNullOrWhiteSpace(HotReloadFolderOverride)
                ? HotReloadFolderOverride!
                : HotReloadFolder;
            var path = Path.Combine(folder, name + ".md");
            if (!File.Exists(path))
                return false;
            text = File.ReadAllText(path);
            return true;
        }
        catch (IOException ex)
        {
            // File locked by an editor, permission denied, etc. — fall
            // through to the embedded resource. Log so the dev notices.
            Trace.WriteLine($"[PromptLoader] hot-reload read for '{name}' failed: {ex.Message}. Falling back to embedded resource.");
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            Trace.WriteLine($"[PromptLoader] hot-reload read for '{name}' denied: {ex.Message}. Falling back to embedded resource.");
            return false;
        }
    }

    /// <summary>
    /// Return true if a prompt with the given logical name is available
    /// — either as a hot-reloadable disk file (when
    /// <see cref="EnableHotReload"/> is true) or as an embedded resource.
    /// Does NOT throw — call this to probe for optional prompts before
    /// calling <see cref="Get"/>.
    /// </summary>
    public bool Exists(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (_cache.ContainsKey(name)) return true;
        if (EnableHotReload)
        {
            try
            {
                var folder = !string.IsNullOrWhiteSpace(HotReloadFolderOverride)
                    ? HotReloadFolderOverride!
                    : HotReloadFolder;
                if (File.Exists(Path.Combine(folder, name + ".md")))
                    return true;
            }
            catch { /* ignore — fall through to the embedded check */ }
        }
        return s_resourceMap.Value.ContainsKey(name);
    }

    /// <summary>
    /// Load the named prompt and substitute <c>{{VAR}}</c> placeholders
    /// with the supplied <paramref name="variables"/>. Placeholders with no
    /// matching variable are left as-is (so the caller can do partial
    /// substitution in multiple passes if needed).
    /// </summary>
    /// <param name="name">Logical prompt name (no extension).</param>
    /// <param name="variables">
    /// Key/value pairs to substitute. Keys are matched case-insensitively
    /// against the placeholder name. Both <c>UPPER_SNAKE</c> and
    /// <c>lower</c> placeholders work — the regex matches any characters
    /// between <c>{{</c> and <c>}}</c> except braces. Pass an empty
    /// dictionary to get the raw template with no substitution.
    /// </param>
    public string Render(string name, IReadOnlyDictionary<string, string> variables)
    {
        var template = Get(name);
        if (variables is null || variables.Count == 0)
            return template;
        return Substitute(template, variables);
    }

    /// <summary>
    /// Static helper: perform <c>{{VAR}}</c> substitution on any string.
    /// Used by callers that compose prompts from fragments not stored as
    /// embedded resources (e.g. agent-side prompt assembly).
    /// </summary>
    public static string Substitute(string template, IReadOnlyDictionary<string, string> variables)
    {
        if (string.IsNullOrEmpty(template) || variables.Count == 0)
            return template;

        // Match {{KEY}} where KEY is any non-brace text. Compare keys
        // case-insensitively so {{WORLD_STATE}} and {{world_state}} both
        // resolve to the same variable.
        return Regex.Replace(
            template,
            @"\{\{([^{}]*)\}\}",
            match =>
            {
                var key = match.Groups[1].Value.Trim();
                if (key.Length == 0) return match.Value;
                foreach (var kv in variables)
                {
                    if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                        return kv.Value;
                }
                return match.Value; // no match — leave as-is
            });
    }

    /// <summary>
    /// Scan the assembly's manifest resource names and build a map from
    /// logical prompt name (e.g. <c>"system"</c>) to the full resource
    /// name (e.g. <c>MyGame.Core.AI.Prompts.system.md</c>). Only resources
    /// ending in <c>.md</c> that live under the AI.Prompts namespace are
    /// included.
    /// </summary>
    private static Dictionary<string, string> BuildResourceMap()
    {
        const string prefix = "MyGame.Core.AI.Prompts.";
        const string suffix = ".md";
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rn in s_asm.GetManifestResourceNames())
        {
            if (!rn.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (!rn.EndsWith(suffix, StringComparison.Ordinal)) continue;
            var logical = rn.Substring(prefix.Length, rn.Length - prefix.Length - suffix.Length);
            // Resource names from MSBuild can use either . or \ as path
            // separators; normalize to dot. (AI/Prompts/*.md already comes
            // through as dotted, so this is a defensive no-op for our case.)
            logical = logical.Replace('\\', '.').Replace('/', '.');
            map[logical] = rn;
        }
        return map;
    }

    /// <summary>
    /// Enumerate all known prompt names (logical, without extension).
    /// Handy for diagnostic UI / settings panels.
    /// </summary>
    public IEnumerable<string> ListNames() => s_resourceMap.Value.Keys.OrderBy(k => k, StringComparer.Ordinal);
}
