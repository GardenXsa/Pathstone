using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyGame.Core.Common;
using MyGame.Core.Saves;
using MyGame.Core.World;
using MyGame.Core.World.Entities;

namespace MyGame.Desktop.ViewModels;

/// <summary>
/// Character-creation screen. Runs between world-build and game entry.
/// Lets the player pick a name / race / class / background, then
/// materializes a fresh <see cref="Player"/> (replacing the auto-created
/// «Странник» that the world-builder seeded), grants class-appropriate
/// starter gear, persists, and navigates into the game in single-player
/// mode.
/// </summary>
/// <remarks>
/// Flow: <c>WorldBuildViewModel</c> → on success creates a save →
/// <c>MainViewModel.NavigateToCharacterCreation(saveId)</c> → this VM
/// → on <c>Create</c> loads the save, swaps the player, saves again,
/// <c>_shell.NavigateToGame(saveId)</c>.
/// </remarks>
public partial class CharacterCreationViewModel : ViewModelBase
{
    private readonly SaveManager _saveManager;
    private readonly MainViewModel _shell;
    private readonly string _saveId;

    public CharacterCreationViewModel(
        SaveManager saveManager,
        MainViewModel shell,
        string saveId)
    {
        _saveManager = saveManager ?? throw new ArgumentNullException(nameof(saveManager));
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _saveId = saveId ?? throw new ArgumentNullException(nameof(saveId));
        Title = "Создание персонажа";
    }

    // ─── Observable properties ───────────────────────────────────────

    /// <summary>Character name. Defaults to «Странник» (matches the
    /// auto-created player the world-builder seeds).</summary>
    [ObservableProperty] private string _name = "Странник";

    /// <summary>Selected race (key into <see cref="RaceOptions"/>).</summary>
    [ObservableProperty] private string _race = "human";

    /// <summary>Selected class (key into <see cref="ClassOptions"/>).</summary>
    [ObservableProperty] private string _class = "fighter";

    /// <summary>Selected background (key into <see cref="BackgroundOptions"/>).</summary>
    [ObservableProperty] private string _background = "soldier";

    // ─── Dropdown options ────────────────────────────────────────────

    public string[] RaceOptions { get; } =
        { "human", "elf", "dwarf", "halfling", "orc", "tiefling" };

    public string[] ClassOptions { get; } =
        { "fighter", "wizard", "rogue", "cleric", "ranger", "barbarian" };

    public string[] BackgroundOptions { get; } =
        { "soldier", "sage", "criminal", "acolyte", "folk-hero", "urchin" };

    // ─── Class preview (live, derived from selected class) ───────────

    /// <summary>
    /// Human-readable summary of the selected class's starting attributes
    /// + starter gear + proficient skills. Updates whenever the user
    /// changes the class dropdown (CommunityToolkit source-generator
    /// wires the property-changed notification via OnClassChanged partial).
    /// </summary>
    public string PreviewText => BuildPreviewText(Class);

    partial void OnClassChanged(string value)
    {
        OnPropertyChanged(nameof(PreviewText));
    }

    private static string BuildPreviewText(string cls)
    {
        var (attrs, gear, skills) = GetClassProfile(cls);
        var attrLine = string.Join(", ",
            attrs.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key.ToUpperInvariant()}={kv.Value}"));
        var gearLine = gear.Length > 0 ? string.Join(", ", gear) : "—";
        var skillsLine = skills.Length > 0 ? string.Join(", ", skills) : "—";
        return $"Характеристики: {attrLine}\nСнаряжение: {gearLine}\nВладения: {skillsLine}";
    }

    // ─── Commands ────────────────────────────────────────────────────

    /// <summary>
    /// Validate the name, load the save, swap the auto-player for the
    /// chosen one (attributes + gear + skills based on class), save, and
    /// navigate into the game in single-player mode.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateAsync()
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            ErrorMessage = "Введите имя персонажа.";
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var loaded = _saveManager.LoadAll(_saveId);
            if (loaded is null)
            {
                ErrorMessage = $"Сохранение {_saveId} не найдено.";
                return;
            }
            var (world, meta, log) = loaded.Value;

            // Remember where the auto-player was standing so the new
            // character starts at the same spot.
            var oldPlayer = world.ActivePlayer ?? world.Players.FirstOrDefault();
            var startLocationId = oldPlayer?.LocationId
                ?? world.Locations.FirstOrDefault()?.Id
                ?? EntityId.Empty;

            // Remove the auto-player. SpawnPlayer set ActivePlayerId on
            // the first spawn, so we clear both the Players list and
            // the active id before re-spawning.
            world.Players.Clear();
            world.ActivePlayerId = null;

            // Build the chosen class profile (attributes + starter gear
            // template ids + proficient skills).
            var (attrs, gearTplIds, skills) = GetClassProfile(Class);

            var player = EntityFactory.CreatePlayer(new()
            {
                Name = Name.Trim(),
                Race = Race,
                Class = Class,
                Level = 1,
                LocationId = startLocationId,
                Background = Background,
                StartingCurrency = 25,
                Attributes = attrs,
                ProficientSkills = skills,
            }, world.Ruleset);

            // Equip starter weapon/armor from the world's content
            // registry. Each class gets a different loadout; armor is
            // only granted to melee classes (fighter/rogue/cleric/
            // barbarian).
            foreach (var tplId in gearTplIds)
            {
                var tpl = world.Registries.Items.Get(tplId);
                if (tpl is null) continue;
                var inst = EntityFactory.InstantiateItem(tpl);
                // Slot from category: weapon → "weapon", armor → "armor".
                string slot = tpl.Weapon is not null ? "weapon"
                    : tpl.Armor is not null ? "armor"
                    : "misc";
                player.Equipped[slot] = inst;
            }

            // Everyone gets a basic survival kit: 2 health potions, a
            // torch, and 3 rations. Matches the auto-player defaults so
            // the player isn't worse off than «Странник» was.
            foreach (var (tplId, qty) in new[]
                { ("cns_health_potion", 2), ("tool_torch", 1), ("cns_ration", 3) })
            {
                var t = world.Registries.Items.Get(tplId);
                if (t is not null)
                    player.Inventory.Items.Add(EntityFactory.InstantiateItem(t, qty));
            }

            // Recompute AC after equipping (D&D-style worlds).
            EntityFactory.RecomputeAcResource(player, world.Ruleset);

            world.SpawnPlayer(player);

            // Persist the swapped world so the game screen loads the
            // chosen character (not «Странник»).
            _saveManager.SaveAll(_saveId, world, meta, log);

            // Hand off to the game screen.
            await _shell.NavigateToGame(_saveId);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Не удалось создать персонажа: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanCreate() => !IsBusy && !string.IsNullOrWhiteSpace(Name);

    /// <summary>
    /// Discard the save (the auto-player «Странник» remains in place;
    /// the user can still load it later from the main menu) and go back
    /// to the main menu.
    /// </summary>
    [RelayCommand]
    private void Back() => _shell.NavigateToMenu();

    // ─── Class profile lookup ────────────────────────────────────────

    /// <summary>
    /// Hardcoded class → (attributes, starter-gear-template-ids, skills)
    /// lookup. Attributes use the DefaultDnd ruleset keys (str/dex/con/
    /// int/per/cha). Gear uses standard template ids from data.json
    /// (wpn_shortsword, arm_leather, wpn_dagger, wpn_staff, wpn_club,
    /// wpn_shortbow, wpn_greataxe).
    /// </summary>
    private static (
        Dictionary<string, int> attrs,
        string[] gear,
        string[] skills) GetClassProfile(string cls) => cls switch
    {
        "fighter" => (
            new() { ["str"] = 16, ["dex"] = 12, ["con"] = 14, ["int"] = 10, ["per"] = 10, ["cha"] = 10 },
            new[] { "wpn_shortsword", "arm_leather" },
            new[] { "Athletics", "Intimidation" }),
        "wizard" => (
            new() { ["str"] = 8, ["dex"] = 12, ["con"] = 12, ["int"] = 16, ["per"] = 12, ["cha"] = 10 },
            new[] { "wpn_staff" },
            new[] { "Arcana", "Investigation" }),
        "rogue" => (
            new() { ["str"] = 10, ["dex"] = 16, ["con"] = 12, ["int"] = 12, ["per"] = 14, ["cha"] = 10 },
            new[] { "wpn_dagger", "arm_leather" },
            new[] { "Stealth", "Sleight of Hand" }),
        "cleric" => (
            new() { ["str"] = 12, ["dex"] = 10, ["con"] = 14, ["int"] = 10, ["per"] = 15, ["cha"] = 12 },
            new[] { "wpn_club", "arm_leather" },
            new[] { "Religion", "Medicine" }),
        "ranger" => (
            new() { ["str"] = 12, ["dex"] = 15, ["con"] = 12, ["int"] = 10, ["per"] = 14, ["cha"] = 10 },
            new[] { "wpn_shortbow" },
            new[] { "Survival", "Animal Handling" }),
        "barbarian" => (
            new() { ["str"] = 16, ["dex"] = 12, ["con"] = 16, ["int"] = 8, ["per"] = 10, ["cha"] = 10 },
            new[] { "wpn_greataxe", "arm_leather" },
            new[] { "Athletics", "Survival" }),
        _ => (
            new() { ["str"] = 12, ["dex"] = 12, ["con"] = 12, ["int"] = 12, ["per"] = 12, ["cha"] = 12 },
            Array.Empty<string>(),
            Array.Empty<string>()),
    };

    // Keep the create-command enabled state in sync as Name/IsBusy change.
    partial void OnNameChanged(string value) => CreateCommand.NotifyCanExecuteChanged();
}
