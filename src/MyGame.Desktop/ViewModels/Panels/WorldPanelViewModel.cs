using System;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyGame.Core.Common;
using MyGame.Core.World;
using MyGame.Core.World.Entities;

namespace MyGame.Desktop.ViewModels.Panels;

/// <summary>
/// View model for the world panel. Shows the current location (with its
/// description, exits, inhabitants, buildings, ground items), the
/// discovered-locations map (visited + discovered flags), and the world
/// clock. Exits are clickable: <see cref="TravelRequested"/> fires when
/// the user clicks one, and the owning GameViewModel performs the actual
/// world mutation (set LocationId, mark Visited/Discovered, log, save,
/// and roll a random encounter if the destination is dangerous).
/// </summary>
public partial class WorldPanelViewModel : ObservableObject
{
    /// <summary>
    /// Raised when the user clicks an exit row. The owning GameViewModel
    /// subscribes and mutates the world directly (single-player) — moving
    /// the active player, marking the destination visited, and rolling
    /// a random encounter if the destination danger > 0.
    /// </summary>
    public event Action<ExitRow>? TravelRequested;
    /// <summary>Refresh from the given world.</summary>
    public void RefreshFromWorld(World world)
    {
        if (world is null) { Clear(); return; }

        ClockDisplay = world.Clock.ToString();
        Turn = world.Turn;
        WorldTitle = world.Flags?.TryGetValue("worldTitle", out var t) == true
            ? t.ToString() ?? "Мир"
            : "Мир";

        var player = world.ActivePlayer ?? world.Players.FirstOrDefault();
        var loc = player is null ? null : world.GetLocation(player.LocationId);

        CurrentLocationName = loc?.Name ?? "—";
        CurrentLocationDescription = loc?.Description ?? "";
        CurrentLocationTerrain = loc?.Terrain ?? "—";
        CurrentLocationDanger = loc?.Danger ?? 0;

        Exits.Clear();
        Inhabitants.Clear();
        BuildingsHere.Clear();
        GroundItems.Clear();

        if (loc is not null)
        {
            foreach (var exit in loc.Exits)
            {
                // Issue #20 (chunked generation): phantom exits (cold-region
                // boundaries) have To=EntityId.Empty and ToName set. Render
                // them with the ToName + a "⚠" marker so the user knows
                // clicking it triggers region generation.
                string toName;
                if (exit.To == EntityId.Empty && !string.IsNullOrWhiteSpace(exit.ToName))
                {
                    toName = exit.ToName + " ⚠";
                }
                else
                {
                    toName = world.GetLocation(exit.To)?.Name ?? exit.To.ToString();
                }
                Exits.Add(new ExitRow(exit.Direction, toName, exit.Locked == true));
            }

            foreach (var npcId in loc.Npcs)
            {
                var n = world.GetNpc(npcId);
                if (n is not null)
                    Inhabitants.Add(new NpcRow(n.Name, n.Race ?? "—", n.Class ?? "—",
                        n.Level?.ToString() ?? "?"));
            }

            foreach (var bId in loc.Buildings)
            {
                var b = world.GetBuilding(bId);
                if (b is not null)
                    BuildingsHere.Add(new BuildingRow(b.Name, b.Description ?? ""));
            }

            foreach (var iId in loc.Items)
            {
                var it = world.GetItem(iId);
                if (it is not null)
                    GroundItems.Add(new GroundItemRow(it.Name, it.Quantity));
            }
        }

        AllLocations.Clear();
        foreach (var l in world.Locations)
            AllLocations.Add(new LocationRow(l.Name, l.Terrain, l.Danger, l.Visited, l.Discovered));

        OnPropertyChanged(nameof(HasExits));
        OnPropertyChanged(nameof(HasInhabitants));
        OnPropertyChanged(nameof(HasBuildings));
        OnPropertyChanged(nameof(HasGroundItems));
        OnPropertyChanged(nameof(HasLocations));
    }

    private void Clear()
    {
        ClockDisplay = "—";
        Turn = 0;
        WorldTitle = "Мир";
        CurrentLocationName = "—";
        CurrentLocationDescription = "";
        CurrentLocationTerrain = "—";
        CurrentLocationDanger = 0;
        Exits.Clear();
        Inhabitants.Clear();
        BuildingsHere.Clear();
        GroundItems.Clear();
        AllLocations.Clear();
        OnPropertyChanged(nameof(HasExits));
        OnPropertyChanged(nameof(HasInhabitants));
        OnPropertyChanged(nameof(HasBuildings));
        OnPropertyChanged(nameof(HasGroundItems));
        OnPropertyChanged(nameof(HasLocations));
    }

    // ─── Observable properties ───────────────────────────────────────

    [ObservableProperty] private string _clockDisplay = "—";
    [ObservableProperty] private int _turn;
    [ObservableProperty] private string _worldTitle = "Мир";
    [ObservableProperty] private string _currentLocationName = "—";
    [ObservableProperty] private string _currentLocationDescription = "";
    [ObservableProperty] private string _currentLocationTerrain = "—";
    [ObservableProperty] private int _currentLocationDanger;

    public ObservableCollection<ExitRow> Exits { get; } = new();
    public ObservableCollection<NpcRow> Inhabitants { get; } = new();
    public ObservableCollection<BuildingRow> BuildingsHere { get; } = new();
    public ObservableCollection<GroundItemRow> GroundItems { get; } = new();
    public ObservableCollection<LocationRow> AllLocations { get; } = new();

    public bool HasExits => Exits.Count > 0;
    public bool HasInhabitants => Inhabitants.Count > 0;
    public bool HasBuildings => BuildingsHere.Count > 0;
    public bool HasGroundItems => GroundItems.Count > 0;
    public bool HasLocations => AllLocations.Count > 0;

    // ─── Commands ────────────────────────────────────────────────────

    /// <summary>
    /// Raised by the exit-row Button in the view. Forwards the chosen
    /// exit row to <see cref="TravelRequested"/> subscribers (the
    /// GameViewModel owns the actual world mutation).
    /// </summary>
    [RelayCommand]
    private void Travel(ExitRow exit) => TravelRequested?.Invoke(exit);
}

/// <summary>One exit from the current location.</summary>
public sealed record ExitRow(string Direction, string ToName, bool Locked);

/// <summary>One NPC at the current location.</summary>
public sealed record NpcRow(string Name, string Race, string Class, string Level);

/// <summary>One building at the current location.</summary>
public sealed record BuildingRow(string Name, string Description);

/// <summary>One loose item on the ground at the current location.</summary>
public sealed record GroundItemRow(string Name, int Quantity);

/// <summary>One location in the world map list.</summary>
public sealed record LocationRow(string Name, string Terrain, int Danger, bool Visited, bool Discovered);
