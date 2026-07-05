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
public sealed class PromptLoader
{
    private static readonly Assembly s_asm = typeof(PromptLoader).Assembly;

    // Lazily-built map of logical name → full resource name. Scans the
    // manifest resource names ONCE on first use; subsequent loads are
    // direct dictionary lookups.
    private static readonly Lazy<Dictionary<string, string>> s_resourceMap = new(BuildResourceMap);

    // Per-instance cache of (name → rendered template text). Templates are
    // read-once from the manifest stream and reused for the lifetime of the
    // loader. Different loader instances share the static resource map but
    // not this cache (intentional — tests may want to reset).
    private readonly Dictionary<string, string> _cache = new(StringComparer.Ordinal);

    /// <summary>
    /// Default shared loader. Convenience for callers that don't need to
    /// inject a custom instance — equivalent to <c>new PromptLoader()</c>.
    /// </summary>
    public static PromptLoader Default { get; } = new();

    /// <summary>
    /// Get the raw markdown content of the named prompt. Caches after the
    /// first load. Throws <see cref="FileNotFoundException"/> if no
    /// embedded resource matches <c>&lt;name&gt;.md</c>.
    /// </summary>
    /// <param name="name">
    /// Logical prompt name without extension (e.g. <c>"system"</c>,
    /// <c>"worldbuilder"</c>, <c>"world-planner"</c>).
    /// </param>
    public string Get(string name)
    {
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("Prompt name is required.", nameof(name));

        if (_cache.TryGetValue(name, out var cached))
            return cached;

        var map = s_resourceMap.Value;
        if (!map.TryGetValue(name, out var resourceName))
        {
            throw new FileNotFoundException(
                $"Prompt '{name}.md' not found among embedded resources. " +
                $"Available: {string.Join(", ", map.Keys.OrderBy(k => k, StringComparer.Ordinal))}");
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
    /// Return true if a prompt with the given logical name is available
    /// as an embedded resource. Does NOT throw — call this to probe for
    /// optional prompts before calling <see cref="Get"/>.
    /// </summary>
    public bool Exists(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (_cache.ContainsKey(name)) return true;
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
