using MyGame.Core.World.Content;
using MyGame.Core.World.Entities;

namespace MyGame.Tests.World;

/// <summary>
/// Unit tests for the ContentRegistry: embedded data.json loads
/// non-empty item/NPC/building registries, Get returns known templates
/// and returns null for unknown ids.
/// </summary>
public class ContentRegistryTests
{
    [Fact]
    public void LoadDefault_ReturnsNonEmptyRegistries()
    {
        var reg = ContentRegistry.LoadDefault();
        Assert.NotEmpty(reg.Items.All());
        Assert.NotEmpty(reg.Npcs.All());
        Assert.NotEmpty(reg.Buildings.All());
    }

    [Fact]
    public void LoadDefault_ItemsHaveIdsAndNames()
    {
        var reg = ContentRegistry.LoadDefault();
        foreach (var item in reg.Items.All().Take(20))
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Id));
            Assert.False(string.IsNullOrWhiteSpace(item.Name));
        }
    }

    [Fact]
    public void Get_ExistingTemplate_ReturnsIt()
    {
        var reg = ContentRegistry.LoadDefault();
        // Per DefaultWorld.Create: "wpn_shortsword" is one of the
        // canonical starting weapons the default player equips.
        var sword = reg.Items.Get("wpn_shortsword");
        Assert.NotNull(sword);
        Assert.False(string.IsNullOrWhiteSpace(sword.Name));
    }

    [Fact]
    public void Get_Nonexistent_ReturnsNull()
    {
        var reg = ContentRegistry.LoadDefault();
        Assert.Null(reg.Items.Get("does_not_exist"));
        Assert.Null(reg.Npcs.Get("does_not_exist"));
        Assert.Null(reg.Buildings.Get("does_not_exist"));
    }

    [Fact]
    public void LoadDefault_RegistersDefaultBuildingTemplates()
    {
        // DefaultWorld spawns bld_tavern + bld_smithy + bld_temple +
        // bld_guard_tower — assert all four are in the registry.
        var reg = ContentRegistry.LoadDefault();
        Assert.NotNull(reg.Buildings.Get("bld_tavern"));
        Assert.NotNull(reg.Buildings.Get("bld_smithy"));
        Assert.NotNull(reg.Buildings.Get("bld_temple"));
        Assert.NotNull(reg.Buildings.Get("bld_guard_tower"));
    }

    [Fact]
    public void LoadDefault_RegistersDefaultNpcTemplates()
    {
        // DefaultWorld spawns npc_village_elder + npc_wolf + npc_goblin +
        // npc_ghost + npc_owl_bear_cub — assert all are in the registry.
        var reg = ContentRegistry.LoadDefault();
        Assert.NotNull(reg.Npcs.Get("npc_village_elder"));
        Assert.NotNull(reg.Npcs.Get("npc_wolf"));
        Assert.NotNull(reg.Npcs.Get("npc_goblin"));
        Assert.NotNull(reg.Npcs.Get("npc_ghost"));
        Assert.NotNull(reg.Npcs.Get("npc_owl_bear_cub"));
    }

    [Fact]
    public void ByCategory_FiltersByCategory()
    {
        var reg = ContentRegistry.LoadDefault();
        var weapons = reg.Items.ByCategory("weapon");
        Assert.NotEmpty(weapons);
        Assert.All(weapons, t => Assert.Equal("weapon", t.Category));
    }

    [Fact]
    public void LoadEmbedded_IsIdempotent()
    {
        // Loading the embedded pack twice should not throw (later
        // registrations overwrite earlier ones with the same id).
        var reg = new ContentRegistry();
        reg.LoadEmbedded();
        var count1 = reg.Items.All().Count;
        reg.LoadEmbedded();
        var count2 = reg.Items.All().Count;
        Assert.Equal(count1, count2);
    }

    [Fact]
    public void LoadFromJson_EmptyString_NoOp()
    {
        var reg = new ContentRegistry();
        reg.LoadFromJson("");
        reg.LoadFromJson("   ");
        Assert.Empty(reg.Items.All());
    }

    [Fact]
    public void Load_NullPack_NoOp()
    {
        var reg = new ContentRegistry();
        reg.Load(null);
        Assert.Empty(reg.Items.All());
    }

    [Fact]
    public void Register_OverwritesExisting()
    {
        var reg = new ContentRegistry();
        var tpl1 = new ItemTemplate
        {
            Id = "x", Name = "First", Description = "", Category = "misc", Rarity = "common",
        };
        var tpl2 = new ItemTemplate
        {
            Id = "x", Name = "Second", Description = "", Category = "misc", Rarity = "common",
        };
        reg.Items.Register(tpl1);
        reg.Items.Register(tpl2);
        var got = reg.Items.Get("x");
        Assert.NotNull(got);
        Assert.Equal("Second", got.Name);
    }
}
