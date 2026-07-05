using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
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
/// Save-browser sort modes (issue #74). Bound to a ComboBox above the
/// saves list; changing the selection re-sorts
/// <see cref="MainMenuViewModel.Saves"/> in place.
/// </summary>
public enum SaveSortMode
{
    /// <summary>
    /// By <see cref="SaveMeta.UpdatedAt"/> descending (most recently
    /// played first). The default — matches the previous hard-coded
    /// order from <see cref="SaveManager.ListSaves"/>.
    /// </summary>
    ByDate = 0,

    /// <summary>
    /// By <see cref="SaveMeta.Name"/> ascending (case-insensitive).
    /// </summary>
    ByName = 1,

    /// <summary>
    /// By <see cref="SaveMeta.CreatedAt"/> descending (most recently
    /// created first). Useful for finding the save you just started.
    /// </summary>
    ByCreatedDate = 2,
}

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
///
/// <para>
/// <b>Resume prompt (issue #19):</b> on construction the menu checks
/// for a leftover <c>worldbuilder-state.json</c> (written when the user
/// cancelled a previous build). If present, <see cref="HasPendingResumeBuild"/>
/// is true and the UI shows a «Незавершённая генерация мира. Продолжить?»
/// prompt with «Продолжить» (<see cref="ResumeBuildCommand"/>) and
/// «Отменить» (<see cref="DiscardPendingResumeCommand"/>) buttons.
/// </para>
///
/// <para>
/// <b>First-time hints (issue #73):</b> <see cref="ShowHints"/> is true
/// for the first three sessions (tracked via
/// <see cref="Settings.SessionCount"/>). When true, the menu shows small
/// tooltip hints under the primary buttons. After the third session the
/// hints disappear.
/// </para>
///
/// <para>
/// <b>Save browser (issue #74):</b> the saves list now supports:
/// <list type="bullet">
///   <item>Search box — filters by Name / WorldTitle / CharacterName
///     (case-insensitive). Re-filters on every keystroke.</item>
///   <item>Sort dropdown — ByDate (default) / ByName / ByCreatedDate.</item>
///   <item>Multi-select delete — each row has a checkbox; the
///     «Удалить выбранные» button (with a confirm overlay) deletes all
///     checked saves in one go.</item>
///   <item>Richer row metadata — WorldTitle, LocationName, Playtime
///     (formatted «Xч Ym»), SaveSize («X KB» / «X MB»), EngineVersion.</item>
/// </list>
/// </para>
/// </summary>
public partial class MainMenuViewModel : ViewModelBase
{
    private readonly MainViewModel _shell;
    private readonly SaveManager _saveManager;
    private readonly ProfileStore _profileStore;
    private readonly SettingsStore _settingsStore;

    // The nickname TextBox binds two-way to this. Editing + Enter (or
    // focus loss) calls SaveNicknameCommand, which validates + persists
    // via ProfileStore. No separate profile screen — the nickname is the
    // only profile field, and there's no auth to wrap it in.
    private string _nickname = string.Empty;
    private string? _nicknameError;

    // Cached snapshot of the saved world-builder state (issue #19).
    // Populated in the ctor when WorldBuilderStateStore detects a
    // leftover state file. Cleared by DiscardPendingResume (user clicks
    // «Отменить» on the resume prompt) or by ResumeBuild (user clicks
    // «Продолжить» — the state file is consumed by the resumed build).
    private WorldBuilderStateFile? _pendingResume;

    // Source-of-truth list of loaded saves (before search/sort filters
    // are applied). The bound <see cref="Saves"/> collection is the
    // filtered + sorted view of this list. Kept in sync by
    // <see cref="RefreshSaves"/> + <see cref="ApplyFilterAndSort"/>.
    private readonly List<SaveSlotViewModel> _allSaves = new();

    public MainMenuViewModel(
        MainViewModel shell,
        SaveManager saveManager,
        ProfileStore profileStore,
        SettingsStore settingsStore)
    {
        _shell = shell ?? throw new ArgumentNullException(nameof(shell));
        _saveManager = saveManager ?? throw new ArgumentNullException(nameof(saveManager));
        _profileStore = profileStore ?? throw new ArgumentNullException(nameof(profileStore));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));

        try { _nickname = _profileStore.GetOrCreate().Nickname; }
        catch { _nickname = "Игрок"; }

        // Issue #19 — detect a leftover world-builder state file (the
        // user cancelled a previous build). If found, surface a resume
        // prompt in the UI. We snapshot the file into _pendingResume;
        // the user's choice (resume / discard) consumes it.
        try
        {
            if (WorldBuilderStateStore.Exists())
            {
                _pendingResume = WorldBuilderStateStore.Load();
                HasPendingResumeBuild = _pendingResume?.State is not null;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine(
                $"[MainMenuViewModel] failed to check worldbuilder-state.json: {ex.Message}");
        }

        // Issue #73 — show first-time tooltip hints for the first three
        // sessions. Increment the session counter on each visit so the
        // hints disappear after the third session. Reads + writes via
        // SettingsStore.Update (atomic patch + persist).
        try
        {
            var settings = _settingsStore.Load();
            ShowHints = settings.SessionCount < 3;
            _settingsStore.Update(s => s with { SessionCount = s.SessionCount + 1 });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine(
                $"[MainMenuViewModel] failed to update session count: {ex.Message}");
            // Default to showing hints when the settings read fails —
            // it's better to over-show than to silently hide them on a
            // fresh install where the user hasn't seen them yet.
            ShowHints = true;
        }
    }

    /// <summary>
    /// True when there's a leftover world-builder state file on disk
    /// (issue #19). Drives the «Незавершённая генерация мира. Продолжить?»
    /// prompt in the UI. Cleared by <see cref="DiscardPendingResume"/> or
    /// by <see cref="ResumeBuild"/>.
    /// </summary>
    [ObservableProperty] private bool _hasPendingResumeBuild;

    /// <summary>
    /// Issue #54: update notification text. Set by the shell when
    /// UpdateChecker finds a newer version. Bound to the banner in
    /// MainMenuView. Null/empty = no update.
    /// </summary>
    [ObservableProperty] private string? _updateAvailableText;

    /// <summary>
    /// Called by the shell (MainViewModel) when the update check
    /// completes. Sets the notification text.
    /// </summary>
    public void SetUpdateAvailable(MyGame.Core.Tooling.UpdateInfo? info)
    {
        UpdateAvailableText = info is not null
            ? $"Доступна новая версия: {info.TagName}. Скачать: {info.ReleaseUrl}"
            : null;
    }

    /// <summary>
    /// True for the first three sessions (issue #73). Drives small
    /// tooltip hints under the primary buttons (Новая игра / Создать
    /// мир / Хост игры). After the third session, the hints disappear.
    /// Set once in the ctor from <see cref="Settings.SessionCount"/>.
    /// </summary>
    public bool ShowHints { get; }

    /// <summary>
    /// Human-readable summary of the pending resume (e.g. stage + when
    /// it was saved). Bound to the resume-prompt TextBlock.
    /// </summary>
    public string? PendingResumeSummary
    {
        get
        {
            if (_pendingResume is null || _pendingResume.State is null) return null;
            var stage = _pendingResume.State.Stage ?? "неизвестно";
            var saved = _pendingResume.SavedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
            return $"Стадия: {stage}. Сохранено: {saved}.";
        }
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
    /// List of existing saves (filtered + sorted view of
    /// <see cref="_allSaves"/>), populated when the user expands
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

    // ─── Save-browser: search + sort + multi-select (issue #74) ──────

    private string _searchText = string.Empty;
    private SaveSortMode _sortMode = SaveSortMode.ByDate;
    private bool _isDeleteConfirmVisible;

    /// <summary>
    /// Search filter for the saves list. Filters by Name / WorldTitle /
    /// CharacterName (case-insensitive). Re-filters on every keystroke.
    /// Empty string = no filter (show all).
    /// </summary>
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
                ApplyFilterAndSort();
        }
    }

    /// <summary>
    /// Sort mode for the saves list. Defaults to
    /// <see cref="SaveSortMode.ByDate"/> (newest first). Changing the
    /// value re-sorts <see cref="Saves"/> in place.
    /// </summary>
    public SaveSortMode SortMode
    {
        get => _sortMode;
        set
        {
            if (SetProperty(ref _sortMode, value))
            {
                ApplyFilterAndSort();
                OnPropertyChanged(nameof(SortModeIndex));
            }
        }
    }

    /// <summary>
    /// Int wrapper around <see cref="SortMode"/> for the saves-list
    /// ComboBox's <c>SelectedIndex</c> binding (Avalonia's
    /// <c>ComboBox.SelectedIndex</c> is an int — binding directly to
    /// the enum requires a converter). Two-way: the setter forwards to
    /// <see cref="SortMode"/> which triggers the re-sort.
    /// </summary>
    public int SortModeIndex
    {
        get => (int)_sortMode;
        set => SortMode = (SaveSortMode)value;
    }

    /// <summary>
    /// Drives a confirmation overlay for the multi-select delete action
    /// (issue #74). Set to true when the user clicks «Удалить выбранные»;
    /// the overlay's «Да» button calls <see cref="ConfirmDeleteSelectedCommand"/>
    /// (which deletes + resets this to false), «Отмена» calls
    /// <see cref="CancelDeleteSelectedCommand"/>.
    /// </summary>
    public bool IsDeleteConfirmVisible
    {
        get => _isDeleteConfirmVisible;
        private set => SetProperty(ref _isDeleteConfirmVisible, value);
    }

    /// <summary>
    /// True when at least one save row is checked — drives the enabled
    /// state of the «Удалить выбранные» button. Recomputed on every
    /// <see cref="SaveSlotViewModel.IsSelected"/> change.
    /// </summary>
    public bool HasSelectedSaves => _allSaves.Any(s => s.IsSelected);

    // ─── Commands ───────────────────────────────────────────────────

    /// <summary>
    /// «Новая игра» — create a DefaultWorld save and navigate to the
    /// CHARACTER CREATION screen. The user picks name / race / class /
    /// background there; the class profile grants starter gear, then the
    /// user is forwarded into the game. Previously this skipped character
    /// creation entirely and dropped the user into the game with the
    /// auto-player «Странник» (issue #105).
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
            // Route through character creation — the world now ships
            // without a player (issue #106), so the user MUST create one
            // before entering the game.
            _shell.NavigateToCharacterCreation(meta.Id);
            await Task.CompletedTask;
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

    // ─── Resume-pending-build commands (issue #19) ──────────────────

    /// <summary>
    /// Resume the pending world-build. Loads the saved state + brief
    /// from the on-disk <c>worldbuilder-state.json</c> and navigates to
    /// the world-build screen with the state staged for
    /// <see cref="WorldBuilderOrchestrator.LoadState"/>. The resumed
    /// build auto-starts and skips already-completed stages.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private void ResumeBuild()
    {
        if (_pendingResume?.State is null) return;
        var state = _pendingResume.State;
        var brief = _pendingResume.Brief ?? string.Empty;
        // Clear the in-memory snapshot + the HasPendingResumeBuild flag
        // BEFORE navigating (so re-entering the menu doesn't re-prompt).
        // The on-disk state file is left in place — the resumed build
        // will delete it on completion (or rewrite it on another cancel).
        _pendingResume = null;
        HasPendingResumeBuild = false;
        _shell.NavigateToWorldBuildForResume(state, brief);
    }

    /// <summary>
    /// Discard the pending world-build state. Deletes the on-disk file
    /// and clears the resume-prompt UI. Used when the user clicks
    /// «Отменить» on the resume prompt — they don't want to resume, so
    /// the state file is removed (a fresh build will be required next
    /// time).
    /// </summary>
    [RelayCommand]
    private void DiscardPendingResume()
    {
        try { WorldBuilderStateStore.Delete(); }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.WriteLine(
                $"[MainMenuViewModel] failed to delete worldbuilder-state.json: {ex.Message}");
        }
        _pendingResume = null;
        HasPendingResumeBuild = false;
        OnPropertyChanged(nameof(PendingResumeSummary));
    }

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
        // Unsubscribe from the previous batch of slots so we don't
        // leak event handlers when the list is reloaded. (Each slot
        // holds a strong reference to the parent VM via the
        // SelectionChanged event; if we didn't unsubscribe, the slots
        // would stay alive until the next RefreshSaves.)
        foreach (var s in _allSaves) s.SelectionChanged -= OnSlotSelectionChangedInternal;
        _allSaves.Clear();
        try
        {
            var savesRoot = _saveManager.SavesDirectory;
            foreach (var meta in _saveManager.ListSaves())
            {
                var saveDir = Path.Combine(savesRoot, meta.Id);
                var slot = new SaveSlotViewModel(
                    meta, saveDir,
                    id => _shell.NavigateToGame(id),
                    id => _shell.NavigateToRebuild(id));
                slot.SelectionChanged += OnSlotSelectionChangedInternal;
                _allSaves.Add(slot);
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Не удалось прочитать список сохранений: {ex.Message}";
        }
        SavesLoaded = true;
        ApplyFilterAndSort();
    }

    /// <summary>
    /// Internal handler for <see cref="SaveSlotViewModel.SelectionChanged"/>
    /// events from individual slots. Forwards to the public
    /// <see cref="OnSlotSelectionChanged"/> method (which fires
    /// PropertyChanged on <see cref="HasSelectedSaves"/>).
    /// </summary>
    private void OnSlotSelectionChangedInternal(SaveSlotViewModel slot) => OnSlotSelectionChanged();

    /// <summary>
    /// Apply the current <see cref="SearchText"/> filter + <see cref="SortMode"/>
    /// to <see cref="_allSaves"/> and replace <see cref="Saves"/> with the
    /// result. Called whenever the search text or sort mode changes, and
    /// after <see cref="RefreshSaves"/>. Selection state (IsSelected) is
    /// preserved across re-sorts because we mutate the same VM instances
    /// (we just reorder / filter the bound collection, we don't recreate
    /// the row VMs).
    /// </summary>
    private void ApplyFilterAndSort()
    {
        var query = (_searchText ?? string.Empty).Trim();
        IEnumerable<SaveSlotViewModel> filtered = _allSaves;
        if (!string.IsNullOrEmpty(query))
        {
            filtered = _allSaves.Where(s =>
                s.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || s.WorldTitle.Contains(query, StringComparison.OrdinalIgnoreCase)
                || s.Character.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        filtered = _sortMode switch
        {
            SaveSortMode.ByName => filtered.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase),
            SaveSortMode.ByCreatedDate => filtered.OrderByDescending(s => s.CreatedAtRaw),
            _ => filtered.OrderByDescending(s => s.UpdatedAtRaw),
        };

        Saves.Clear();
        foreach (var s in filtered) Saves.Add(s);
        OnPropertyChanged(nameof(HasSelectedSaves));
    }

    /// <summary>
    /// Issue #74 — multi-select delete trigger. Shows a confirmation
    /// overlay (bound to <see cref="IsDeleteConfirmVisible"/>). The
    /// overlay's «Да» button calls <see cref="ConfirmDeleteSelectedCommand"/>,
    /// «Отмена» calls <see cref="CancelDeleteSelectedCommand"/>.
    /// </summary>
    [RelayCommand]
    private void RequestDeleteSelected()
    {
        if (!HasSelectedSaves) return;
        IsDeleteConfirmVisible = true;
    }

    /// <summary>
    /// Confirmation overlay — «Отмена»: hide the overlay without
    /// deleting anything. Selections are preserved (the user can change
    /// their mind and click «Удалить выбранные» again).
    /// </summary>
    [RelayCommand]
    private void CancelDeleteSelected()
    {
        IsDeleteConfirmVisible = false;
    }

    /// <summary>
    /// Confirmation overlay — «Да»: delete every checked save, then
    /// hide the overlay + refresh the list. Best-effort: individual
    /// delete failures are logged to Trace and don't abort the batch
    /// (so one locked file doesn't block the rest of the deletes).
    /// </summary>
    [RelayCommand]
    private void ConfirmDeleteSelected()
    {
        IsDeleteConfirmVisible = false;
        var selected = _allSaves.Where(s => s.IsSelected).ToList();
        if (selected.Count == 0) return;
        foreach (var s in selected)
        {
            try
            {
                _saveManager.DeleteSave(s.Id);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine(
                    $"[MainMenuViewModel] failed to delete save {s.Id}: {ex.Message}");
            }
        }
        RefreshSaves();
    }

    /// <summary>
    /// Issue #74 — clear all row checkboxes. Useful when the user wants
    /// to reset the multi-select state without deleting anything.
    /// </summary>
    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var s in _allSaves) s.IsSelected = false;
        OnPropertyChanged(nameof(HasSelectedSaves));
    }

    /// <summary>
    /// Called by <see cref="SaveSlotViewModel"/> whenever its
    /// <see cref="SaveSlotViewModel.IsSelected"/> flag changes — so the
    /// «Удалить выбранные» button's enabled state refreshes in real time.
    /// </summary>
    internal void OnSlotSelectionChanged() => OnPropertyChanged(nameof(HasSelectedSaves));

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

    /// <summary>
    /// Import a .pathstone-world file into a new save (issue #33).
    /// </summary>
    public async Task ImportWorldFromFileAsync(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return;
        try
        {
            var newSaveId = _saveManager.ImportSave(filePath);
            if (newSaveId is null)
            {
                ErrorMessage = "Не удалось импортировать мир.";
                return;
            }
            RefreshSaves();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Импорт не удался: {ex.Message}";
        }
        await Task.CompletedTask;
    }

    /// <summary>
    /// Export a save to a file path (issue #33). The file path comes from
    /// the code-behind StorageProvider save picker.
    /// </summary>
    public async Task ExportSaveToPathAsync(string saveId, string outputPath)
    {
        try
        {
            _saveManager.ExportSave(saveId, outputPath);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Экспорт не удался: {ex.Message}";
        }
        await Task.CompletedTask;
    }

    private bool CanNavigate() => !IsBusy;
}

/// <summary>
/// One row in the «Загрузить» list. Wraps a <see cref="SaveMeta"/> with
/// display fields (name, character, world, location, turn, playtime,
/// size, engine version, timestamps) and a Load command.
/// </summary>
/// <remarks>
/// Issue #74 expanded the row's display fields beyond the original
/// Name / Character / World / Turn / UpdatedAt. The new fields:
/// <list type="bullet">
///   <item><see cref="WorldTitle"/> — from <see cref="SaveMeta.WorldTitle"/>
///     (the in-fiction world name).</item>
///   <item><see cref="LocationName"/> — from <see cref="SaveMeta.LocationName"/>
///     (current player location).</item>
///   <item><see cref="Playtime"/> — formatted «Xч Ym» from
///     <see cref="SaveMeta.PlaytimeMs"/>.</item>
///   <item><see cref="SaveSize"/> — total size of the save directory's
///     files, formatted «X KB» / «X MB».</item>
///   <item><see cref="EngineVersion"/> — small/muted, from
///     <see cref="SaveMeta.EngineVersion"/>.</item>
/// </list>
/// </remarks>
public sealed class SaveSlotViewModel : ObservableObject
{
    private readonly SaveMeta _meta;
    private readonly Action<string> _load;
    private readonly Action<string>? _rebuild;
    private bool _isSelected;

    public SaveSlotViewModel(SaveMeta meta, string saveDirectoryPath, Action<string> load, Action<string>? rebuild = null, Action<string>? exportSave = null)
    {
        _meta = meta ?? throw new ArgumentNullException(nameof(meta));
        _load = load ?? throw new ArgumentNullException(nameof(load));
        _rebuild = rebuild;
        SaveDirectoryPath = saveDirectoryPath ?? string.Empty;
        LoadCommand = new RelayCommand(() => _load(_meta.Id));
        RebuildCommand = rebuild is null ? null : new RelayCommand(() => rebuild(_meta.Id));
        ExportCommand = exportSave is null ? null : new RelayCommand(() => exportSave(_meta.Id));
        _cachedSizeBytes = ComputeSaveSize();
    }

    /// <summary>
    /// Absolute path to the save directory on disk. Used to compute
    /// <see cref="SaveSize"/>. Empty string when not provided (defensive
    /// — the save browser always passes the real path).
    /// </summary>
    public string SaveDirectoryPath { get; }

    /// <summary>The save's stable id (e.g. <c>save_…</c>). Used for
    /// multi-select delete + the Load command.</summary>
    public string Id => _meta.Id;

    public string Name => string.IsNullOrEmpty(_meta.Name) ? "(без названия)" : _meta.Name;

    public string Character => string.IsNullOrEmpty(_meta.CharacterName)
        ? "—"
        : $"{_meta.CharacterName} (ур. {_meta.CharacterLevel?.ToString() ?? "?"})";

    /// <summary>In-fiction world title (e.g. «Долина Туманов»). Falls
    /// back to location name when the world title is empty (covers
    /// older saves that didn't track WorldTitle separately).</summary>
    public string World => _meta.WorldTitle ?? _meta.LocationName ?? "—";

    /// <summary>World title from <see cref="SaveMeta.WorldTitle"/>
    /// (issue #74). «—» when empty.</summary>
    public string WorldTitle => string.IsNullOrEmpty(_meta.WorldTitle) ? "—" : _meta.WorldTitle!;

    /// <summary>Current location name from <see cref="SaveMeta.LocationName"/>
    /// (issue #74). «—» when empty.</summary>
    public string LocationName => string.IsNullOrEmpty(_meta.LocationName) ? "—" : _meta.LocationName!;

    /// <summary>Formatted updated-at timestamp («yyyy-MM-dd HH:mm», local time).</summary>
    public string UpdatedAt => _meta.UpdatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    /// <summary>Formatted created-at timestamp («yyyy-MM-dd HH:mm», local time).</summary>
    public string CreatedAt => _meta.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    /// <summary>Raw updated-at timestamp (UTC) — used for sorting
    /// without re-parsing the formatted string.</summary>
    public DateTimeOffset UpdatedAtRaw => _meta.UpdatedAt;

    /// <summary>Raw created-at timestamp (UTC) — used for sorting.</summary>
    public DateTimeOffset CreatedAtRaw => _meta.CreatedAt;

    public string TurnInfo => $"Ход {_meta.Turn}";

    /// <summary>
    /// Cumulative playtime formatted as «Xч Ym» (or «Ym» / «Xч» when
    /// one component is zero). Issue #74.
    /// </summary>
    public string Playtime => FormatPlaytime(_meta.PlaytimeMs);

    /// <summary>
    /// Total size of the save directory's files, formatted as
    /// «X KB» / «X MB». Issue #74. Computed once on construction (see
    /// <see cref="ComputeSaveSize"/>) and cached — save files don't
    /// change size while the menu is open.
    /// </summary>
    public string SaveSize => FormatSize(_cachedSizeBytes);

    private readonly long _cachedSizeBytes;

    /// <summary>
    /// Engine version that wrote this save (e.g. «0.2.0»). Shown small
    /// and muted so the user can identify which saves were written by
    /// older/newer engines.
    /// </summary>
    public string EngineVersion => string.IsNullOrEmpty(_meta.EngineVersion) ? "—" : _meta.EngineVersion;

    /// <summary>
    /// Checkbox state for multi-select delete (issue #74). Two-way
    /// bound to a CheckBox in the save row template. Notifies the
    /// parent MainMenuViewModel via the <see cref="SelectionChanged"/>
    /// event so the «Удалить выбранные» button's CanExecute can refresh.
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetProperty(ref _isSelected, value))
                SelectionChanged?.Invoke(this);
        }
    }

    /// <summary>
    /// Raised when <see cref="IsSelected"/> changes. The parent
    /// <see cref="MainMenuViewModel"/> subscribes to refresh its
    /// <c>HasSelectedSaves</c> computed property.
    /// </summary>
    public event Action<SaveSlotViewModel>? SelectionChanged;

    public ICommand LoadCommand { get; }

    /// <summary>
    /// Rebuild command (issue #23). Opens the rebuild dialog for this
    /// save. Null when the parent menu didn't wire a rebuild action
    /// (the button is hidden in that case via IsVisible binding).
    /// </summary>
    public ICommand? RebuildCommand { get; }
    public ICommand? ExportCommand { get; }

    /// <summary>
    /// True when the rebuild button should be visible (the parent menu
    /// wired a rebuild action). Bound to the rebuild button's IsVisible.
    /// </summary>
    public bool CanRebuild => RebuildCommand is not null;

    // ─── Formatting helpers ──────────────────────────────────────────

    /// <summary>
    /// Format playtime as «Xч Ym» (or «Ym» / «Xч» when one component is
    /// zero). Returns «0м» for a fresh save (no playtime yet).
    /// </summary>
    private static string FormatPlaytime(long ms)
    {
        if (ms <= 0) return "0м";
        var ts = TimeSpan.FromMilliseconds(ms);
        int hours = (int)ts.TotalHours;
        int mins = ts.Minutes;
        if (hours > 0 && mins > 0) return $"{hours}ч {mins}м";
        if (hours > 0) return $"{hours}ч";
        return $"{mins}м";
    }

    /// <summary>
    /// Format a byte count as «X B» / «X KB» / «X MB». Returns «—» for
    /// zero / negative (covers the case where the save directory can't
    /// be read — the user sees a placeholder instead of «0 KB»).
    /// </summary>
    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "—";
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024L * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / (1024.0 * 1024):F1} MB";
    }

    /// <summary>
    /// Compute the total size of the save directory's files
    /// (recursive). Uses <see cref="Directory.GetFiles(string, string, EnumerationOptions)"/>
    /// with the AllDirectories option so nested files (none currently,
    /// but defensive for future layout changes) are counted. Swallows
    /// IO errors (locked files, permission denied, …) — returns 0 on
    /// any failure so the row shows «—» instead of crashing the menu.
    /// </summary>
    private long ComputeSaveSize()
    {
        try
        {
            if (string.IsNullOrEmpty(SaveDirectoryPath)) return 0;
            if (!Directory.Exists(SaveDirectoryPath)) return 0;
            // Spec: use Directory.GetFiles(saveDir).Sum(f => new FileInfo(f).Length).
            // We extend to recursive enumeration so a future layout
            // with subdirectories (e.g. assets/, snapshots/) is also
            // counted. The non-recursive case is a subset.
            var files = Directory.GetFiles(SaveDirectoryPath, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
            });
            long total = 0;
            foreach (var f in files)
            {
                try { total += new FileInfo(f).Length; }
                catch { /* skip locked / vanished file */ }
            }
            return total;
        }
        catch
        {
            return 0;
        }
    }
}
