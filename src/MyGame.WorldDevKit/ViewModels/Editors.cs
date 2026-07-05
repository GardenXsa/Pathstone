using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyGame.Core.Common;
using MyGame.Core.World;
using MyGame.Core.World.Content;
using MyGame.Core.World.Entities;

namespace MyGame.WorldDevKit.ViewModels;

// ─── Location Editor (#92) ───────────────────────────────────────────

public partial class LocationEditorViewModel : ObservableObject
{
    private readonly Location _location;
    private readonly World _world;
    private readonly ContentRegistry _registries;
    private readonly Action _refresh;

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _terrain;
    [ObservableProperty] private int _danger;
    [ObservableProperty] private string _description;
    [ObservableProperty] private bool _visited;
    [ObservableProperty] private bool _discovered;
    [ObservableProperty] private int? _x;
    [ObservableProperty] private int? _y;

    public ObservableCollection<string> Exits { get; } = new();
    public ObservableCollection<string> AvailableLocations { get; } = new();
    [ObservableProperty] private string _selectedTargetLocation = "";
    [ObservableProperty] private string _exitDirection = "север";

    public LocationEditorViewModel(LocationEditRow row, World world, ContentRegistry registries, Action refresh)
    {
        _world = world; _registries = registries; _refresh = refresh;
        _location = world.GetLocation(row.Id) ?? new Location { Id = row.Id };
        _name = _location.Name; _terrain = _location.Terrain; _danger = _location.Danger;
        _description = _location.Description ?? ""; _visited = _location.Visited;
        _discovered = _location.Discovered; _x = _location.X; _y = _location.Y;
        RefreshExits();
    }

    partial void OnNameChanged(string value) { _location.Name = value; _refresh(); }
    partial void OnTerrainChanged(string value) => _location.Terrain = value;
    partial void OnDangerChanged(int value) => _location.Danger = value;
    partial void OnDescriptionChanged(string value) => _location.Description = value;
    partial void OnVisitedChanged(bool value) => _location.Visited = value;
    partial void OnDiscoveredChanged(bool value) => _location.Discovered = value;
    partial void OnXChanged(int? value) => _location.X = value;
    partial void OnYChanged(int? value) => _location.Y = value;

    private void RefreshExits()
    {
        Exits.Clear();
        AvailableLocations.Clear();
        foreach (var e in _location.Exits)
        {
            var toName = _world.GetLocation(e.To)?.Name ?? e.To.ToString();
            Exits.Add($"{e.Direction} → {toName}");
        }
        foreach (var l in _world.Locations.Where(l => l.Id != _location.Id))
            AvailableLocations.Add(l.Name);
    }

    [RelayCommand]
    private void AddExit()
    {
        if (string.IsNullOrWhiteSpace(SelectedTargetLocation)) return;
        var target = _world.Locations.FirstOrDefault(l => l.Name == SelectedTargetLocation);
        if (target is null) return;
        _location.Exits.Add(new LocationExit { To = target.Id, Direction = ExitDirection });
        target.Exits.Add(new LocationExit { To = _location.Id, Direction = "обратно" });
        RefreshExits();
    }

    [RelayCommand]
    private void DeleteLocation()
    {
        _world.Locations.Remove(_location);
        _refresh();
    }
}

// ─── NPC Editor (#93) ───────────────────────────────────────────────

public partial class NpcEditorViewModel : ObservableObject
{
    private readonly Npc _npc;
    private readonly World _world;
    private readonly ContentRegistry _registries;

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _race;
    [ObservableProperty] private string _npcClass;
    [ObservableProperty] private int _level;
    [ObservableProperty] private string _disposition;
    [ObservableProperty] private string _behavior;
    [ObservableProperty] private string _description;

    public ObservableCollection<string> AttributeKeys { get; } = new() { "str", "dex", "con", "int", "wis", "cha" };
    public ObservableCollection<string> LocationNames { get; } = new();
    [ObservableProperty] private string _selectedLocation = "";

    public NpcEditorViewModel(NpcEditRow row, World world, ContentRegistry registries)
    {
        _world = world; _registries = registries;
        _npc = world.GetNpc(row.Id) ?? new Npc { Id = row.Id };
        _name = _npc.Name; _race = _npc.Race ?? ""; _npcClass = _npc.Class ?? "";
        _level = _npc.Level ?? 1; _disposition = _npc.Disposition ?? "neutral";
        _behavior = ""; _description = "";
        foreach (var l in world.Locations) LocationNames.Add(l.Name);
        var loc = world.GetLocation(_npc.LocationId);
        _selectedLocation = loc?.Name ?? "";
    }

    partial void OnNameChanged(string value) => _npc.Name = value;
    partial void OnRaceChanged(string value) => _npc.Race = value;
    partial void OnNpcClassChanged(string value) => _npc.Class = value;
    partial void OnLevelChanged(int value) => _npc.Level = value;
    partial void OnDispositionChanged(string value) => _npc.Disposition = value;
    partial void OnSelectedLocationChanged(string value)
    {
        var loc = _world.Locations.FirstOrDefault(l => l.Name == value);
        if (loc is not null) _npc.LocationId = loc.Id;
    }

    [RelayCommand]
    private void DeleteNpc()
    {
        _world.Npcs.Remove(_npc);
        var loc = _world.GetLocation(_npc.LocationId);
        loc?.Npcs.Remove(_npc.Id);
    }
}

// ─── Item Editor (#94) ──────────────────────────────────────────────

public partial class ItemEditorViewModel : ObservableObject
{
    private readonly Item _item;
    private readonly World _world;
    private readonly ContentRegistry _registries;

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _description;
    [ObservableProperty] private int _quantity;
    public ObservableCollection<string> TemplateIds { get; } = new();
    [ObservableProperty] private string _selectedTemplate = "";

    public ItemEditorViewModel(ItemEditRow row, World world, ContentRegistry registries)
    {
        _world = world; _registries = registries;
        _item = world.Items.FirstOrDefault(i => i.Id == row.Id) ?? new Item { Id = row.Id };
        _name = _item.Name; _description = _item.Description ?? ""; _quantity = _item.Quantity;
        foreach (var t in registries.Items.All().OrderBy(t => t.Id).Take(50))
            TemplateIds.Add(t.Id);
        _selectedTemplate = _item.TemplateId ?? "";
    }

    partial void OnNameChanged(string value) => _item.Name = value;
    partial void OnDescriptionChanged(string value) => _item.Description = value;
    partial void OnQuantityChanged(int value) => _item.Quantity = value;
    partial void OnSelectedTemplateChanged(string value) => _item.TemplateId = value;

    [RelayCommand]
    private void DeleteItem() => _world.Items.Remove(_item);
}

// ─── Building Editor (#95) ──────────────────────────────────────────

public partial class BuildingEditorViewModel : ObservableObject
{
    private readonly Building _building;
    private readonly World _world;

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _description;
    public ObservableCollection<string> LocationNames { get; } = new();
    [ObservableProperty] private string _selectedLocation = "";

    public BuildingEditorViewModel(BuildingEditRow row, World world, ContentRegistry registries)
    {
        _world = world;
        _building = world.GetBuilding(row.Id) ?? new Building { Id = row.Id };
        _name = _building.Name; _description = _building.Description ?? "";
        foreach (var l in world.Locations) LocationNames.Add(l.Name);
        var loc = world.GetLocation(_building.LocationId);
        _selectedLocation = loc?.Name ?? "";
    }

    partial void OnNameChanged(string value) => _building.Name = value;
    partial void OnDescriptionChanged(string value) => _building.Description = value;

    [RelayCommand]
    private void DeleteBuilding()
    {
        _world.Buildings.Remove(_building);
        var loc = _world.GetLocation(_building.LocationId);
        loc?.Buildings.Remove(_building.Id);
    }
}

// ─── Quest Editor (#96) ─────────────────────────────────────────────

public partial class QuestEditorViewModel : ObservableObject
{
    private readonly Quest _quest;
    private readonly World _world;

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _description;
    [ObservableProperty] private string _status;

    public ObservableCollection<string> Objectives { get; } = new();
    [ObservableProperty] private string _newObjective = "";

    public QuestEditorViewModel(QuestEditRow row, World world)
    {
        _world = world;
        _quest = world.GetQuest(row.Id) ?? new Quest { Id = row.Id };
        _name = _quest.Name; _description = _quest.Description ?? ""; _status = _quest.Status.ToString();
        foreach (var o in _quest.Objectives ?? new()) Objectives.Add(o.Description);
    }

    partial void OnNameChanged(string value) => _quest.Name = value;
    partial void OnDescriptionChanged(string value) => _quest.Description = value;

    [RelayCommand]
    private void AddObjective()
    {
        if (string.IsNullOrWhiteSpace(NewObjective)) return;
        _quest.Objectives ??= new();
        _quest.Objectives.Add(new QuestObjective { Id = $"obj_{_quest.Objectives.Count + 1}", Description = NewObjective });
        Objectives.Add(NewObjective);
        NewObjective = "";
    }

    [RelayCommand]
    private void DeleteQuest() => _world.Quests.Remove(_quest);
}

// ─── World Metadata Editor (#97) ───────────────────────────────────

public partial class WorldMetaEditorViewModel : ObservableObject
{
    private readonly World _world;

    [ObservableProperty] private string _title;
    [ObservableProperty] private string _theme;
    [ObservableProperty] private string _setting;
    [ObservableProperty] private string _atmosphere;
    [ObservableProperty] private string _startingHook;
    [ObservableProperty] private int _day = 1;
    [ObservableProperty] private int _hour = 8;
    [ObservableProperty] private int _minute = 0;

    public WorldMetaEditorViewModel(World world)
    {
        _world = world;
        _world.Flags ??= new();
        _title = GetFlag("worldTitle");
        _theme = GetFlag("worldTheme");
        _setting = GetFlag("worldSetting");
        _atmosphere = GetFlag("worldAtmosphere");
        _startingHook = GetFlag("startingHook");
        _day = world.Clock.Day; _hour = world.Clock.Hour; _minute = world.Clock.Minute;
    }

    private string GetFlag(string key)
    {
        if (_world.Flags!.TryGetValue(key, out var v) && v is not null) return v.ToString() ?? "";
        return "";
    }

    partial void OnTitleChanged(string value) => _world.Flags!["worldTitle"] = value;
    partial void OnThemeChanged(string value) => _world.Flags!["worldTheme"] = value;
    partial void OnSettingChanged(string value) => _world.Flags!["worldSetting"] = value;
    partial void OnAtmosphereChanged(string value) => _world.Flags!["worldAtmosphere"] = value;
    partial void OnStartingHookChanged(string value) => _world.Flags!["startingHook"] = value;
    partial void OnDayChanged(int value) => _world.Clock = new GameTime(value, _hour, _minute);
    partial void OnHourChanged(int value) => _world.Clock = new GameTime(_day, value, _minute);
    partial void OnMinuteChanged(int value) => _world.Clock = new GameTime(_day, _hour, value);
}
