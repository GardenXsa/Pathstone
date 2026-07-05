using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyGame.Core.Profile;
using MyGame.Core.Saves;
using MyGame.Core.World;
using MyGame.Core.World.Entities;
using MyGame.Desktop.Services;

namespace MyGame.Desktop.ViewModels;

/// <summary>
/// Landing screen after app startup. Shows an inline nickname editor in
/// the top-right corner (no separate profile screen — nickname is the
/// only profile field, no auth, no settings mixed in) and the primary
/// buttons:
/// <list type="bullet">
///   <item>«Новая игра» — single-player, creates a save with DefaultWorld.</item>
///   <item>«Создать мир (AI)» — open the AI world-build flow.</item>
///   <item>«Хост игры» — start a multiplayer host.</item>
///   <item>«Подключиться» — join a multiplayer host.</item>
///   <item>«Загрузить» — pick from existing saves.</item>
///   <item>«Настройки» — edit AI settings only.</item>
/// </list>
/// </summary>
public partial class MainMenuViewModel : ViewModelBase
{
    private readonly MainViewModel _shell;
    private readonly SaveManager _saveManager;
    private readonly ProfileStore _profileStore;

    // The nickname TextBox binds two-way to this. Editing + Enter (or
    // focus loss) calls SaveNicknameCommand, which validates + persists
    // via ProfileStore. No separate profile screen — the nickname is the
    // only profile field, and there's no auth to wrap it in.
    private string _nickname = string.Empty;
    private string? _nicknameError;

    public MainMenuViewModel(MainViewModel shell, SaveManager saveManager, ProfileStore profileStore)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _saveManager = saveManager ?? throw new ArgumentNullException(nameof(saveManager));
        _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));

        try { _nickname = _profileStore.GetOrCreate().Nickname; }
        catch { _nickname = "Игрок"; }
    }

    /// <summary>
    /// Disables buttons while a navigation action is in flight (e.g.
    /// creating a new save takes a moment). Hides the inherited
    /// ViewModelBase.IsBusy setter — we want this one to be public-set
    /// from within this VM only.
    /// </summary>
    public new bool IsBusy
    {
        get => base.IsBusy;
        private set => base.IsBusy = value;
    }

    /// <summary>
    /// Editable nickname. Bound two-way to the top-right TextBox. The
    /// user can type freely; pressing Enter or losing focus triggers
    /// <see cref="SaveNicknameCommand"/> which validates + persists.
    /// </summary>
    public string Nickname
    {
        get => _nickname;
        set => SetProperty(ref _nickname, value);
    }

    /// <summary>
    /// Inline validation error for the nickname (shown under the TextBox).
    /// Null when the current value is valid or unchanged.
    /// </summary>
    public string? NicknameError
    {
        get => _nicknameError;
        private set => SetProperty(ref _nicknameError, value);
    }

    /// <summary>
    /// List of existing saves, populated when the user expands
    /// «Загрузить». Each item is wrapped in a small VM that exposes
    /// a Load command + display fields.
    /// </summary>
    public ObservableCollection<SaveSlotViewModel> Saves { get; } = new();

    private bool _savesLoaded;
    /// <summary>
    /// True after the first <see cref="RefreshSavesCommand"/> call —
    /// drives whether the saves list panel is visible.
    /// </summary>
    public bool SavesLoaded
    {
        get => _savesLoaded;
        private set => SetProperty(ref _savesLoaded, value);
    }

    // ─── Commands ───────────────────────────────────────────────────

    /// <summary>
    /// «Новая игра» — create a DefaultWorld save and navigate to the
    /// game screen in single-player mode.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private async Task NewGameAsync()
    {
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var world = DefaultWorld.Create();
            var profile = _profileStore.GetOrCreate();
            var meta = _saveManager.CreateSave("Новая игра", world, profile.Id);
            await _shell.NavigateToGame(meta.Id);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Не удалось создать игру: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// «Создать мир» — open the AI world-build flow. The user types a
    /// brief, then the orchestrator plans + commits + narrates a custom
    /// world. Requires an AI API key (set in Настройки → AI settings).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private void CreateWorld() => _shell.NavigateToWorldBrief();

    /// <summary>«Хост игры» — open the multiplayer host setup screen.</summary>
    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private void HostGame() => _shell.NavigateToHost();

    /// <summary>«Подключиться» — open the multiplayer join screen.</summary>
    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private void JoinGame() => _shell.NavigateToJoin();

    /// <summary>
    /// «Настройки» — open the settings screen (AI settings only; the
    /// nickname is edited inline on the main menu).
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private void OpenSettings() => _shell.NavigateToSettings();

    /// <summary>
    /// Validate + persist the current nickname. Called from the TextBox's
    /// KeyDown (Enter) or LostFocus. On validation failure, sets
    /// <see cref="NicknameError"/> and leaves the TextBox content intact
    /// so the user can fix it. On success, clears the error.
    /// </summary>
    [RelayCommand]
    private void SaveNickname()
    {
        var trimmed = (_nickname ?? string.Empty).Trim();
        if (!MyGame.Core.Profile.Profile.ValidateNickname(trimmed, out var error))
        {
            NicknameError = error;
            return;
        }
        try
        {
            _profileStore.Rename(trimmed);
            Nickname = trimmed; // normalize (trim)
            NicknameError = null;
        }
        catch (Exception ex)
        {
            NicknameError = ex.Message;
        }
    }

    /// <summary>«Загрузить» — toggle the saves-list panel. The list is refreshed
    /// every time the panel is opened so newly-created saves appear.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private void ToggleLoad()
    {
        if (SavesLoaded)
        {
            SavesLoaded = false;
            return;
        }
        RefreshSaves();
    }

    /// <summary>Reload the saves list from disk.</summary>
    [RelayCommand]
    private void RefreshSaves()
    {
        Saves.Clear();
        try
        {
            foreach (var meta in _saveManager.ListSaves())
            {
                Saves.Add(new SaveSlotViewModel(meta, id => _shell.NavigateToGame(id)));
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Не удалось прочитать список сохранений: {ex.Message}";
        }
        SavesLoaded = true;
    }

    /// <summary>
    /// Import a portable character sheet (.pathstone-char / .json) from
    /// an arbitrary path (issue #62). The file picker itself is owned by
    /// the View (code-behind — Avalonia's StorageProvider needs the
    /// TopLevel); the View calls this method with the chosen path.
    ///
    /// <para>
    /// Flow: load the sheet via <see cref="CharacterSheetStore.LoadByPath"/>,
    /// create a fresh <see cref="DefaultWorld"/>, replace the auto-spawned
    /// «Странник» with a player built from the sheet (name, race, class,
    /// background, attributes, level, xp, inventory + equipment
    /// materialized from the sheet's item template ids via the new
    /// world's content registry), persist as a new save, and navigate
    /// into the game.
    /// </para>
    /// </summary>
    /// <param name="filePath">
    /// Absolute path to the .pathstone-char / .json sheet file picked by
    /// the user. Null / empty (e.g. user cancelled the picker) is a
    /// silent no-op.
    /// </param>
    public async Task ImportCharacterFromFileAsync(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return; // picker cancelled
        IsBusy = true;
        ErrorMessage = null;
        try
        {
            var store = ServiceHost.Resolve<CharacterSheetStore>();
            var sheet = store.LoadByPath(filePath);
            if (sheet is null)
            {
                ErrorMessage = "Не удалось прочитать файл персонажа. Проверьте, что это корректный экспорт Pathstone (.json).";
                return;
            }
            if (string.IsNullOrWhiteSpace(sheet.Name))
            {
                ErrorMessage = "Файл персонажа не содержит имени — импорт невозможен.";
                return;
            }

            var world = DefaultWorld.Create();
            // Capture the auto-spawned player's location so the imported
            // player starts at the same spot (the starting village).
            var defaultPlayer = world.Players.FirstOrDefault();
            var startLocId = defaultPlayer?.LocationId
                ?? (world.Locations.FirstOrDefault()?.Id ?? default);
            world.Players.Clear();
            world.ActivePlayerId = null;

            var player = EntityFactory.CreatePlayer(new()
            {
                Name = sheet.Name,
                Race = sheet.Race,
                Class = sheet.Class,
                Level = sheet.Level,
                Attributes = sheet.Attributes,
                Resources = sheet.Resources,
                LocationId = startLocId,
                ProficientSkills = sheet.ProficientSkills,
                Background = sheet.Background,
                Speed = sheet.Speed,
                StartingCurrency = 25, // default starting gold
            }, world.Ruleset);
            player.Experience = sheet.Xp;

            // Materialize inventory items from template ids. Templates
            // missing in the new world's content registry are silently
            // skipped (the player just doesn't get that item — better
            // than failing the whole import).
            foreach (var tplId in sheet.InventoryItemIds)
            {
                if (string.IsNullOrEmpty(tplId)) continue;
                var tpl = world.Registries.Items.Get(tplId);
                if (tpl is null) continue;
                player.Inventory.Items.Add(EntityFactory.InstantiateItem(tpl));
            }
            // Materialize equipped items (slot → template id).
            foreach (var kv in sheet.EquippedItemIds)
            {
                if (string.IsNullOrEmpty(kv.Value)) continue;
                var tpl = world.Registries.Items.Get(kv.Value);
                if (tpl is null) continue;
                player.Equipped[kv.Key] = EntityFactory.InstantiateItem(tpl);
            }

            world.SpawnPlayer(player);

            var profile = _profileStore.GetOrCreate();
            var title = $"Импорт: {sheet.Name}";
            var meta = _saveManager.CreateSave(title, world, profile.Id);
            await _shell.NavigateToGame(meta.Id);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Не удалось импортировать персонажа: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanNavigate() => !IsBusy;
}

/// <summary>
/// One row in the «Загрузить» list. Wraps a <see cref="SaveMeta"/> with
/// display fields (name, character, time, turn) and a Load command.
/// </summary>
public sealed class SaveSlotViewModel : ObservableObject
{
    private readonly SaveMeta _meta;
    private readonly Action<string> _load;

    public SaveSlotViewModel(SaveMeta meta, Action<string> load)
    {
        _meta = meta ?? throw new ArgumentNullException(nameof(meta));
        _load = load ?? throw new ArgumentNullException(nameof(load));
        LoadCommand = new RelayCommand(() => _load(_meta.Id));
    }

    public string Name => string.IsNullOrEmpty(_meta.Name) ? "(без названия)" : _meta.Name;
    public string Character => string.IsNullOrEmpty(_meta.CharacterName)
        ? "—"
        : $"{_meta.CharacterName} (ур. {_meta.CharacterLevel?.ToString() ?? "?"})";
    public string World => _meta.WorldTitle ?? _meta.LocationName ?? "—";
    public string UpdatedAt => _meta.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string TurnInfo => $"Ход {_meta.Turn}";
    public ICommand LoadCommand { get; }
}
