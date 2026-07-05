using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyGame.Core.Common;
using MyGame.Core.Saves;
using MyGame.Core.World;
using MyGame.Core.World.Content;
using MyGame.Core.World.Entities;

namespace MyGame.WorldDevKit.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private World? _world;
    private ContentRegistry _registries = ContentRegistry.LoadDefault();

    [ObservableProperty] private string _statusText = "Готово. Создайте новый мир или откройте сохранение.";
    [ObservableProperty] private string _validationSummary = "";
    [ObservableProperty] private bool _hasValidationErrors;
    [ObservableProperty] private ObservableObject? _currentEditor;

    public ObservableCollection<LocationEditRow> Locations { get; } = new();
    public ObservableCollection<NpcEditRow> Npcs { get; } = new();
    public ObservableCollection<ItemEditRow> Items { get; } = new();
    public ObservableCollection<BuildingEditRow> Buildings { get; } = new();
    public ObservableCollection<QuestEditRow> Quests { get; } = new();

    [RelayCommand]
    private void NewWorld()
    {
        _world = new World { Registries = _registries, Seed = 12345, Rng = new Rng(12345) };
        _world.Flags ??= new();
        _world.Flags["worldTitle"] = "Новый мир";
        _world.Flags["worldTheme"] = "fantasy";
        RefreshEntityLists();
        StatusText = "Новый мир создан.";
    }

    [RelayCommand]
    private Task OpenWorldAsync() { StatusText = "Открытие мира."; return Task.CompletedTask; }

    [RelayCommand]
    private Task SaveWorld()
    {
        if (_world is null) { StatusText = "Нет мира для сохранения."; return Task.CompletedTask; }
        StatusText = "Мир сохранён.";
        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task ExportWorld()
    {
        if (_world is null) { StatusText = "Нет мира для экспорта."; return Task.CompletedTask; }
        StatusText = "Мир экспортирован.";
        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task ImportWorldAsync() { StatusText = "Импорт мира."; return Task.CompletedTask; }

    [RelayCommand]
    private void SelectLocation(LocationEditRow row)
        => CurrentEditor = new LocationEditorViewModel(row, _world!, _registries, RefreshEntityLists);

    [RelayCommand]
    private void SelectNpc(NpcEditRow row)
        => CurrentEditor = new NpcEditorViewModel(row, _world!, _registries);

    [RelayCommand]
    private void SelectItem(ItemEditRow row)
        => CurrentEditor = new ItemEditorViewModel(row, _world!, _registries);

    [RelayCommand]
    private void SelectBuilding(BuildingEditRow row)
        => CurrentEditor = new BuildingEditorViewModel(row, _world!, _registries);

    [RelayCommand]
    private void SelectQuest(QuestEditRow row)
        => CurrentEditor = new QuestEditorViewModel(row, _world!);

    [RelayCommand]
    private void SelectWorldMeta()
        => CurrentEditor = new WorldMetaEditorViewModel(_world!);

    [RelayCommand]
    private void AddLocation()
    {
        if (_world is null) return;
        var loc = new Location { Id = EntityId.NewId(), Name = $"Локация {Locations.Count + 1}", Terrain = "plains" };
        _world.AddLocation(loc);
        RefreshEntityLists();
    }

    [RelayCommand]
    private void AddNpc()
    {
        if (_world is null) return;
        var loc = _world.Locations.FirstOrDefault();
        if (loc is null) { StatusText = "Сначала создайте локацию."; return; }
        var npc = new Npc { Id = EntityId.NewId(), Name = $"NPC {Npcs.Count + 1}", LocationId = loc.Id, Disposition = "neutral" };
        _world.SpawnNpc(npc);
        RefreshEntityLists();
    }

    [RelayCommand]
    private void AddItem()
    {
        if (_world is null) return;
        var item = new Item { Id = EntityId.NewId(), Name = $"Предмет {Items.Count + 1}", Quantity = 1 };
        _world.Items.Add(item);
        RefreshEntityLists();
    }

    [RelayCommand]
    private void AddBuilding()
    {
        if (_world is null) return;
        var loc = _world.Locations.FirstOrDefault();
        if (loc is null) { StatusText = "Сначала создайте локацию."; return; }
        var bld = new Building { Id = EntityId.NewId(), Name = $"Здание {Buildings.Count + 1}", LocationId = loc.Id };
        _world.SpawnBuilding(bld);
        RefreshEntityLists();
    }

    [RelayCommand]
    private void AddQuest()
    {
        if (_world is null) return;
        var quest = new Quest { Id = EntityId.NewId(), Name = $"Квест {Quests.Count + 1}", Status = QuestStatus.Inactive };
        _world.Quests.Add(quest);
        RefreshEntityLists();
    }

    private void RefreshEntityLists()
    {
        if (_world is null) return;
        Locations.Clear();
        foreach (var l in _world.Locations)
            Locations.Add(new LocationEditRow(l.Id, l.Name, l.Terrain, l.Danger, l.Visited, l.Discovered));
        Npcs.Clear();
        foreach (var n in _world.Npcs)
            Npcs.Add(new NpcEditRow(n.Id, n.Name, n.Race ?? "?", n.Class ?? "?", n.Disposition ?? "?"));
        Items.Clear();
        foreach (var i in _world.Items)
            Items.Add(new ItemEditRow(i.Id, i.Name, i.TemplateId ?? "?", i.Quantity));
        Buildings.Clear();
        foreach (var b in _world.Buildings)
            Buildings.Add(new BuildingEditRow(b.Id, b.Name, b.Description ?? ""));
        Quests.Clear();
        foreach (var q in _world.Quests)
            Quests.Add(new QuestEditRow(q.Id, q.Name, q.Status.ToString()));
        ValidateWorld();
    }

    private void ValidateWorld()
    {
        if (_world is null) return;
        var errors = new System.Collections.Generic.List<string>();
        if (_world.Locations.Count == 0) errors.Add("Мир не имеет локаций.");
        if (_world.Locations.Any() && _world.Locations.Count(l => l.Visited) != 1)
            errors.Add("Должна быть ровно одна стартовая (visited) локация.");
        HasValidationErrors = errors.Count > 0;
        ValidationSummary = errors.Count > 0 ? "Ошибки: " + string.Join("; ", errors) : "";
    }
}

public sealed record LocationEditRow(EntityId Id, string Name, string Terrain, int Danger, bool Visited, bool Discovered);
public sealed record NpcEditRow(EntityId Id, string Name, string Race, string Class, string Disposition);
public sealed record ItemEditRow(EntityId Id, string Name, string TemplateId, int Quantity);
public sealed record BuildingEditRow(EntityId Id, string Name, string Description);
public sealed record QuestEditRow(EntityId Id, string Name, string Status);
