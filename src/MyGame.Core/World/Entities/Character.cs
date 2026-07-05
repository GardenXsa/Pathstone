using MyGame.Core.Common;

namespace MyGame.Core.World.Entities;

/// <summary>
/// Abstract base for any character (player-controlled or NPC) in the world.
/// Port of <c>Character</c> from <c>engine/types/index.ts</c>.
///
/// Character is ruleset-agnostic in the TS source: <see cref="Attributes"/>
/// and <see cref="Resources"/> are string-keyed maps whose keys come from the
/// owning world's Ruleset. There are no D&amp;D-specific required fields
/// (maxHp / ac / speed / proficiencyBonus) here — those, when they exist,
/// live inside <see cref="Resources"/> and <see cref="Attributes"/>.
/// </summary>
public abstract class Character : Entity
{
    /// <summary>Protected ctor — only concrete subclasses call this.</summary>
    protected Character(string kind) : base(kind) { }

    /// <summary>Optional flavor label (race / species / people).</summary>
    public string? Race { get; set; }

    /// <summary>Optional flavor label (class / archetype / role).</summary>
    public string? Class { get; set; }

    /// <summary>Character level, if the ruleset uses level-based progression.</summary>
    public int? Level { get; set; }

    /// <summary>Attribute values keyed by ruleset attribute key.</summary>
    public Dictionary<string, int> Attributes { get; set; } = new();

    /// <summary>Resource pools (hp, mana, sanity, shield…) keyed by ruleset resource key.</summary>
    public Dictionary<string, int> Resources { get; set; } = new();

    /// <summary>Skills the character is proficient in (free-form labels).</summary>
    public List<string>? ProficientSkills { get; set; }

    /// <summary>Filled equipment slots keyed by ruleset slot name (weapon / armor / shield / …).</summary>
    public Dictionary<string, Item> Equipped { get; set; } = new();

    /// <summary>Carried inventory.</summary>
    public Inventory Inventory { get; set; } = new();

    /// <summary>Active status effects on this character.</summary>
    public List<StatusEffect> Effects { get; set; } = new();

    /// <summary>
    /// NPC attitude toward the player. Free-form; defaults from D&amp;D
    /// vocabulary (<c friendly</c>, <c>neutral</c>, <c>hostile</c>,
    /// <c>allied</c>).
    /// </summary>
    public string? Disposition { get; set; }

    /// <summary>Optional dialogue tree id (for AI-driven conversations).</summary>
    public string? DialogueTreeId { get; set; }

    /// <summary>Movement speed (ruleset-defined; D&amp;D default is 30 ft).</summary>
    public int? Speed { get; set; }

    /// <summary>Where the character currently is.</summary>
    public EntityId LocationId { get; set; }

    /// <summary>Whether the character is currently alive.</summary>
    public bool IsAlive { get; set; } = true;
}
