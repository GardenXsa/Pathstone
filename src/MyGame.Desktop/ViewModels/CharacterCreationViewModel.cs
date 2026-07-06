using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
/// Character-creation screen. Port of
/// <c>components/game/CharacterCreationPanel.tsx</c> from the TS source.
///
/// <para>
/// Layout matches the original: free-form text fields for Имя / Происхождение
/// (race) / Роль (class) / Предыстория — NOT dropdowns — plus a 20-point
/// point-buy block across 6 attributes (СИЛ/ЛОВ/ТЕЛ/ИНТ/ВСП/ХАР) with +/-
/// buttons, a remaining-points counter, and low-stat warnings (INT 0 / CHA 0).
/// </para>
///
/// <para>
/// On create: loads the save, takes the pending player's locationId +
/// inventory/equipped (empty for a fresh world — the StartSceneAgent grants
/// starter gear via AI tools), builds the chosen character with the
/// player-chosen attributes, persists, and navigates into the game (single
/// player) or triggers the deferred host start (multiplayer host).
/// </para>
/// </summary>
public partial class CharacterCreationViewModel : ViewModelBase
{
    private readonly SaveManager _saveManager;
    private readonly MainViewModel _shell;
    private readonly string _saveId;
    private readonly bool _forHost;

    /// <summary>Total points available for the point-buy. Matches TS StatsPointBuy.</summary>
    private const int TotalPoints = 20;

    public CharacterCreationViewModel(
        SaveManager saveManager,
        MainViewModel shell,
        string saveId,
        bool forHost = false)
    {
        _saveManager = saveManager ?? throw new ArgumentNullException(nameof(saveManager));
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _saveId = saveId ?? throw new ArgumentNullException(nameof(saveId));
        _forHost = forHost;
        Title = "Создание персонажа";

        // 6 attributes, all starting at 0 (player distributes 20 points).
        Stats.Add(new StatRow("str", "Сила",          "СИЛ", "Физическая мощь, ближний бой"));
        Stats.Add(new StatRow("dex", "Ловкость",      "ЛОВ", "Скорость, уклонение, стрельба"));
        Stats.Add(new StatRow("con", "Телосложение",  "ТЕЛ", "Выносливость, здоровье"));
        Stats.Add(new StatRow("int", "Интеллект",     "ИНТ", "Знание, логика, речь"));
        Stats.Add(new StatRow("per", "Восприятие",    "ВСП", "Внимательность, чувства"));
        Stats.Add(new StatRow("cha", "Харизма",       "ХАР", "Обаяние, убеждение, социум"));
    }

    // ─── Observable properties (free-form text, per TS original) ─────

    /// <summary>Character name. Empty by default (user types it).</summary>
    [ObservableProperty] private string _name = string.Empty;

    /// <summary>Race / origin. Defaults to «Человек» (matches TS default).</summary>
    [ObservableProperty] private string _race = "Человек";

    /// <summary>Class / role. Defaults to «Странник» (matches TS default).</summary>
    [ObservableProperty] private string _class = "Странник";

    /// <summary>Background. Empty by default (player authors it).</summary>
    [ObservableProperty] private string _background = string.Empty;

    // ─── Point-buy stats ─────────────────────────────────────────────

    /// <summary>
    /// The 6 attribute rows for the point-buy block. Each row carries its
    /// own observable Value so the +/- buttons update live. Bound to an
    /// ItemsControl in the view.
    /// </summary>
    public ObservableCollection<StatRow> Stats { get; } = new();

    /// <summary>Points still available to spend (TotalPoints − sum of all stats).</summary>
    public int RemainingPoints => TotalPoints - Stats.Sum(s => s.Value);

    /// <summary>True when Интеллект is 0 — shows a warning (NPC не понимают).</summary>
    public bool HasLowIntelligence => Stats.FirstOrDefault(s => s.Key == "int")?.Value == 0;

    /// <summary>True when Харизма is 0 — shows a warning (NPC грубят).</summary>
    public bool HasLowCharisma => Stats.FirstOrDefault(s => s.Key == "cha")?.Value == 0;

    // ─── Point-buy increment / decrement commands ───────────────────

    /// <summary>
    /// Increment the named attribute by 1. Enabled while points remain.
    /// CommandParameter is the attribute key (str/dex/con/int/per/cha).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanIncrement))]
    private void Increment(string attr)
    {
        var row = Stats.FirstOrDefault(s => s.Key == attr);
        if (row is null) return;
        if (RemainingPoints <= 0) return;
        row.Value++;
        MyGame.Desktop.Services.SoundService.Play(MyGame.Desktop.Services.SoundEffect.Click);
        RefreshPointBuyState();
    }

    private bool CanIncrement(string attr) => RemainingPoints > 0;

    /// <summary>
    /// Decrement the named attribute by 1. Enabled while that attribute > 0.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanDecrement))]
    private void Decrement(string attr)
    {
        var row = Stats.FirstOrDefault(s => s.Key == attr);
        if (row is null) return;
        if (row.Value <= 0) return;
        row.Value--;
        MyGame.Desktop.Services.SoundService.Play(MyGame.Desktop.Services.SoundEffect.Click);
        RefreshPointBuyState();
    }

    private bool CanDecrement(string attr)
    {
        var row = Stats.FirstOrDefault(s => s.Key == attr);
        return row is not null && row.Value > 0;
    }

    /// <summary>
    /// Re-notify the computed point-buy properties + refresh the commands'
    /// CanExecute so the +/- buttons enable/disable correctly.
    /// </summary>
    private void RefreshPointBuyState()
    {
        OnPropertyChanged(nameof(RemainingPoints));
        OnPropertyChanged(nameof(HasLowIntelligence));
        OnPropertyChanged(nameof(HasLowCharisma));
        IncrementCommand.NotifyCanExecuteChanged();
        DecrementCommand.NotifyCanExecuteChanged();
    }

    // ─── Create command ──────────────────────────────────────────────

    /// <summary>
    /// Validate the name, load the save, take the pending player's
    /// location + inventory (empty for a fresh world — the StartSceneAgent
    /// grants starter gear via AI tools later), build the chosen character
    /// with the player-distributed attributes, persist, and navigate into
    /// the game (single-player) or trigger the deferred host start
    /// (multiplayer host).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanCreate))]
    private async Task CreateAsync()
    {
        MyGame.Desktop.Services.SoundService.Play(MyGame.Desktop.Services.SoundEffect.Chime);
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

            // The pending player carries the start location + (empty)
            // inventory/equipped. We take those and replace the identity
            // with the player's choices — matching TS createPlayerInBuiltWorld.
            var pending = world.ActivePlayer ?? world.Players.FirstOrDefault();
            var startLocationId = pending?.LocationId
                ?? world.Locations.FirstOrDefault(l => l.Visited || l.Discovered)?.Id
                ?? world.Locations.FirstOrDefault()?.Id
                ?? EntityId.Empty;

            // Build the attributes map from the point-buy.
            var attributes = new Dictionary<string, int>();
            foreach (var s in Stats)
                attributes[s.Key] = s.Value;

            var player = EntityFactory.CreatePlayer(new()
            {
                Name = Name.Trim(),
                Race = string.IsNullOrWhiteSpace(Race) ? "Человек" : Race.Trim(),
                Class = string.IsNullOrWhiteSpace(Class) ? "Странник" : Class.Trim(),
                Level = 1,
                LocationId = startLocationId,
                Background = string.IsNullOrWhiteSpace(Background) ? null : Background.Trim(),
                StartingCurrency = pending?.Inventory?.Currency ?? 15,
                Attributes = attributes,
                ProficientSkills = pending?.ProficientSkills,
            }, world.Ruleset);

            // Copy the pending player's inventory + equipped (empty for a
            // fresh world; the StartSceneAgent grants starter gear via AI
            // tools during the opening turn). Mirrors TS:
            //   player.inventory.items = structuredClone(pending.inventory.items)
            //   player.equipped = structuredClone(pending.equipped)
            if (pending is not null)
            {
                if (pending.Inventory?.Items is { } pendingItems)
                    foreach (var it in pendingItems)
                        player.Inventory.Items.Add(it);
                if (pending.Equipped is not null)
                    foreach (var kv in pending.Equipped)
                        player.Equipped[kv.Key] = kv.Value;
            }

            EntityFactory.RecomputeAcResource(player, world.Ruleset);

            // Replace the pending player with the real one.
            world.Players.Clear();
            world.ActivePlayerId = null;
            world.SpawnPlayer(player);

            // Mark the start location visited/discovered (TS does this too).
            var startLoc = world.GetLocation(startLocationId);
            if (startLoc is not null)
            {
                startLoc.Visited = true;
                startLoc.Discovered = true;
            }

            _saveManager.SaveAll(_saveId, world, meta, log);

            // Hand off: single-player game by default; host lobby if this
            // character-creation was launched from the HostGame screen.
            if (_forHost)
                await _shell.CompleteHostStartAsync(_saveId);
            else
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

    /// <summary>Back to the main menu.</summary>
    [RelayCommand]
    private void Back() => _shell.NavigateToMenu();

    partial void OnNameChanged(string value) => CreateCommand.NotifyCanExecuteChanged();
}

/// <summary>
/// One row in the point-buy block. Carries the attribute's display metadata
/// + an observable Value. The parent VM owns the Increment/Decrement commands
/// (they need the RemainingPoints context); this row is purely display + state.
/// </summary>
public partial class StatRow : ObservableObject
{
    /// <summary>Attribute key (str/dex/con/int/per/cha) — passed as CommandParameter.</summary>
    public string Key { get; }

    /// <summary>Full Russian name (Сила, Ловкость, …).</summary>
    public string Name { get; }

    /// <summary>3-letter abbreviation (СИЛ, ЛОВ, …).</summary>
    public string Abbr { get; }

    /// <summary>Short description of what the attribute governs.</summary>
    public string Desc { get; }

    [ObservableProperty] private int _value;

    public StatRow(string key, string name, string abbr, string desc)
    {
        Key = key;
        Name = name;
        Abbr = abbr;
        Desc = desc;
    }
}
