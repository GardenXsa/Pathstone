using System.Collections.Generic;
using MyGame.Core.Common;

namespace MyGame.Core.World.Entities;

/// <summary>
/// A crafting recipe. Defines input items + output item. Stored in the
/// content registry. The GM (or player via UI) can craft the output by
/// consuming the inputs. Port of issue #65.
/// </summary>
public sealed record CraftingRecipe
{
    /// <summary>Unique recipe id, e.g. "craft_health_potion".</summary>
    public required string Id { get; init; }

    /// <summary>Display name, e.g. "Зелье лечения".</summary>
    public required string Name { get; init; }

    /// <summary>Input items: templateId → quantity required.</summary>
    public required IReadOnlyDictionary<string, int> Inputs { get; init; }

    /// <summary>Output item template id.</summary>
    public required string OutputTemplateId { get; init; }

    /// <summary>Output quantity (default 1).</summary>
    public int OutputQuantity { get; init; } = 1;

    /// <summary>Optional required tool (e.g. "tool_alchemists_supplies").</summary>
    public string? RequiredTool { get; init; }

    /// <summary>Optional skill check (e.g. "alchemy" — GM rolls if set).</summary>
    public string? SkillCheck { get; init; }

    /// <summary>Description for the UI.</summary>
    public string? Description { get; init; }
}
