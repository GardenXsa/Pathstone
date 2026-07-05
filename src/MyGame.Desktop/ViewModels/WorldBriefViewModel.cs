using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using MyGame.Core.AI.Agents;
using MyGame.Core.Profile;
using MyGame.Core.Saves;

namespace MyGame.Desktop.ViewModels;

/// <summary>
/// World-brief entry screen. Lets the user type a free-form description of
/// the world they want (genre, tone, key locations, starting situation),
/// then navigates to the WorldBuildViewModel which runs the AI build.
///
/// <para>
/// Offers a few preset briefs (dark fantasy village / cyberpunk district /
/// post-apoc bunker) for users who don't want to write from scratch.
/// </para>
///
/// <para>
/// An "advanced" toggle enables optional pet-agent delegations — extra AI
/// sub-tasks that enrich the world after the deterministic committer
/// stage (mass NPC spawning, batch item creation, lore markers). Off by
/// default to keep the simple 3-stage pipeline as the default. When
/// enabled, an editable list of delegations is shown (issue #22 — the
/// user can add/remove/edit rows; the defaults are pre-populated).
/// </para>
///
/// <para>
/// <b>Generation mode (issue #20):</b> a radio-button selector picks
/// between «Полный» (full — planner designs the whole world) and
/// «По регионам» (chunked — planner designs only the start region,
/// others are generated on-demand as the player travels toward them).
/// Default is «Полный».
/// </para>
/// </summary>
public partial class WorldBriefViewModel : ViewModelBase
{
    private readonly ProfileStore _profileStore;
    private readonly SettingsStore _settingsStore;
    private readonly SaveManager _saveManager;
    private readonly MainViewModel _shell;

    private string _brief = string.Empty;
    private bool _usePetDelegations;
    private string _generationMode = "full"; // "full" | "chunked"

    public WorldBriefViewModel(
        ProfileStore profileStore,
        SettingsStore settingsStore,
        SaveManager saveManager,
        MainViewModel shell)
    {
        _profileStore = profileStore;
        _settingsStore = settingsStore;
        _saveManager = saveManager;
        _shell = shell;
        Title = "Создать мир";

        // Issue #22 — pre-populate the default delegations so the user
        // sees the affordance and can edit them rather than starting
        // from a blank list.
        foreach (var del in BuildDefaultDelegations())
            Delegations.Add(new PetDelegationViewModel(del));
    }

    /// <summary>
    /// Free-form world brief. Bound to a multi-line TextBox. Empty brief is
    /// allowed — the planner will make up its own world (defaulting to dark
    /// fantasy).
    /// </summary>
    public string Brief
    {
        get => _brief;
        set => SetProperty(ref _brief, value);
    }

    /// <summary>
    /// If true, the build flow adds optional pet-agent delegations after
    /// the deterministic committer stage. Each delegation is a separate
    /// AI sub-task (mass NPC spawn, batch item creation, lore markers)
    /// that enriches the world. Off by default — adds ~30-60s + extra
    /// token cost. When checked, an editable list of delegations is
    /// shown (issue #22).
    /// </summary>
    public bool UsePetDelegations
    {
        get => _usePetDelegations;
        set => SetProperty(ref _usePetDelegations, value);
    }

    /// <summary>
    /// Generation mode (issue #20): "full" (planner designs the whole
    /// world) or "chunked" (planner designs only the start region; others
    /// are generated on-demand as the player travels toward them).
    /// Default "full". Two-way bound to a radio-button selector in the
    /// view; the Build command passes it through to the
    /// <see cref="WorldPlanRequest"/>.
    /// </summary>
    public string GenerationMode
    {
        get => _generationMode;
        set => SetProperty(ref _generationMode, value);
    }

    /// <summary>
    /// True when <see cref="GenerationMode"/> is "full" — bound to the
    /// «Полный» radio button's IsChecked.
    /// </summary>
    public bool IsGenerationModeFull
    {
        get => _generationMode == "full";
        set { if (value) GenerationMode = "full"; }
    }

    /// <summary>
    /// True when <see cref="GenerationMode"/> is "chunked" — bound to the
    /// «По регионам» radio button's IsChecked.
    /// </summary>
    public bool IsGenerationModeChunked
    {
        get => _generationMode == "chunked";
        set { if (value) GenerationMode = "chunked"; }
    }

    /// <summary>
    /// Editable list of pet delegations (issue #22). Pre-populated with
    /// the two defaults (background population + loot/scatter); the user
    /// can add / remove / edit rows. The Build command filters out rows
    /// with an empty Task before passing them to the orchestrator.
    /// </summary>
    public ObservableCollection<PetDelegationViewModel> Delegations { get; } = new();

    [RelayCommand]
    private void UsePresetDarkFantasy() =>
        Brief = "Тёмное фэнтези. Изолированная долина, отрезанная от большого мира войной и зимой. " +
                "Деревня у тракта, окружённая лесом, в котором завелось что-то голодное. " +
                "Старый культ пытается возродить погребённого бога. " +
                "Магия редкая и пугает; сталь надёжнее. Тон — мрачный, паранойя, мало надежды.";

    [RelayCommand]
    private void UsePresetCyberpunk() =>
        Brief = "Киберпанк. Неоновый район мегаполиса, зажатый между корп-башнями и трущобами. " +
                "Дождь, рекламные голограммы, чёрный рынок имплантов. " +
                "Фиксер собирает команду для ограбления конвоя. " +
                "Тон — паранойя, неон, влажный асфальт, короткие рубленые фразы.";

    [RelayCommand]
    private void UsePresetPostapoc() =>
        Brief = "Постапокалипсис. Бункер под руинами старого города, 80 лет после ядерной войны. " +
                "Жители никогда не выходили наружу. Запасы еды кончаются. " +
                "Разведчики пропадают. Тон — пыль, ржавчина, тишина, одиночество, страх перед外面的世界.";

    /// <summary>
    /// Add a blank delegation row to the editable list (issue #22).
    /// The user fills in the Label + Task + MaxIterations inline.
    /// </summary>
    [RelayCommand]
    private void AddDelegation()
    {
        Delegations.Add(new PetDelegationViewModel(new PetDelegation
        {
            Label = $"Делегация {Delegations.Count + 1}",
            Task = string.Empty,
            MaxIterations = 6,
        }));
    }

    /// <summary>
    /// Remove the given delegation row from the editable list. Bound to
    /// the per-row «Удалить» button (CommandParameter = the row VM).
    /// </summary>
    [RelayCommand]
    private void RemoveDelegation(PetDelegationViewModel? row)
    {
        if (row is null) return;
        Delegations.Remove(row);
    }

    /// <summary>
    /// Continue to the world-build progress screen with the current brief.
    /// </summary>
    [RelayCommand]
    private void Build()
    {
        // Empty brief is fine — the planner prompt has a default branch.
        // Filter the editable delegations to non-empty tasks; pass null
        // when the user hasn't opted in (so the orchestrator skips the
        // pet stage entirely).
        IReadOnlyCollection<PetDelegation>? delegations = null;
        if (UsePetDelegations)
        {
            var filtered = new List<PetDelegation>();
            foreach (var vm in Delegations)
            {
                var del = vm.ToDelegation();
                if (!string.IsNullOrWhiteSpace(del.Task))
                    filtered.Add(del);
            }
            delegations = filtered.Count > 0 ? filtered : null;
        }
        _shell.NavigateToWorldBuild(Brief ?? string.Empty, delegations, GenerationMode);
    }

    /// <summary>
    /// Build the default set of pet-agent delegations. These run after the
    /// deterministic committer stage and enrich the world with extra
    /// detail. Each delegation is a focused AI sub-task with its own
    /// tool-call loop. Used to pre-populate the editable list (issue #22)
    /// — the user can edit/delete/add to taste.
    /// </summary>
    private static IReadOnlyCollection<PetDelegation> BuildDefaultDelegations()
    {
        return new List<PetDelegation>
        {
            new()
            {
                Label = "Фоновое население",
                Task = "Добавь 5-8 фоновых NPC в уже созданные локации. Используй spawn_npc с подходящими шаблонами " +
                       "(npc_villager, npc_merchant, npc_guard если есть, иначе любые подходящие). " +
                       "Дай каждому имя через nameOverride и короткое описание поведения через флаги. " +
                       "Не трогай уже существующих NPC. Заверши вызовом pet_done с кратким summary.",
                MaxIterations = 6,
            },
            new()
            {
                Label = "Лут и сокровища",
                Task = "Размести 4-6 предметов на земле в разных локациях (используй spawn_item_on_ground если доступен, " +
                       "иначе give_item существующим NPC как инвентарь). Подбирай предметы по теме локации: " +
                       "в руинах — старые артефакты, в лесу — травы, в пещерах — ценности. " +
                       "Заверши вызовом pet_done с кратким summary.",
                MaxIterations = 5,
            },
        };
    }

    /// <summary>Back to the main menu.</summary>
    [RelayCommand]
    private void Back() => _shell.NavigateToMenu();
}
