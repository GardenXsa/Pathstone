namespace MyGame.Core.World.Entities;

/// <summary>
/// A non-player character. Port of <c>NPC</c> from
/// <c>engine/types/index.ts</c>.
/// </summary>
public sealed class Npc : Character
{
    /// <summary>Create a new blank NPC.</summary>
    public Npc() : base("npc") { }

    /// <summary>Template this NPC was spawned from (if any).</summary>
    public string? TemplateId { get; set; }

    /// <summary>
    /// AI GM behavior hint. Free-form; D&amp;D defaults listed in the TS
    /// (<c>aggressive</c>, <c>defensive</c>, <c>passive</c>, <c>merchant</c>,
    /// <c>questGiver</c>, <c>custom</c>).
    /// </summary>
    public string Behavior { get; set; } = "passive";

    /// <summary>Aggro radius (ft) — only meaningful for hostile NPCs.</summary>
    public int? AggroRange { get; set; }

    /// <summary>Merchant inventory template ids (if this NPC is a merchant).</summary>
    public List<string>? ShopInventory { get; set; }
}
