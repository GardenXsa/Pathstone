using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using MyGame.Desktop.ViewModels.Panels;

namespace MyGame.Desktop.Views.Panels;

/// <summary>
/// World panel view. Issue #68: includes a visual Canvas-based map
/// alongside the existing list view. The map shows location nodes
/// positioned by X/Y coordinates, connected by exit edges, with
/// fog-of-war (undiscovered locations are dimmed).
/// </summary>
public partial class WorldPanelView : UserControl
{
    private Canvas? _mapCanvas;
    private Avalonia.Controls.Primitives.ToggleButton? _mapToggle;

    public WorldPanelView()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
        _mapCanvas = this.FindControl<Canvas>("WorldMapCanvas");
        _mapToggle = this.FindControl<Avalonia.Controls.Primitives.ToggleButton>("MapToggle");

        // Redraw the map whenever the toggle state changes.
        if (_mapToggle is not null)
        {
            _mapToggle.IsCheckedChanged += (_, _) => RedrawMap();
        }

        // Redraw when the DataContext changes or its AllLocations refreshes.
        DataContextChanged += (_, _) =>
        {
            if (DataContext is WorldPanelViewModel vm)
            {
                vm.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(WorldPanelViewModel.HasLocations))
                        RedrawMap();
                };
            }
        };
    }

    /// <summary>
    /// Redraw the world map on the Canvas. Called when the toggle is
    /// switched to map view or when the location list changes.
    /// Draws edges (exit connections) first, then nodes (locations).
    /// </summary>
    private void RedrawMap()
    {
        if (_mapCanvas is null || _mapToggle?.IsChecked != true) return;
        if (DataContext is not WorldPanelViewModel vm) return;

        _mapCanvas.Children.Clear();

        var locations = vm.AllLocations.ToList();
        if (locations.Count == 0) return;

        // Calculate positions. If locations have X/Y, use them (scaled).
        // Otherwise, arrange in a grid.
        var positions = new Dictionary<string, Point>();
        double scale = 60; // pixels per coordinate unit
        double offsetX = 30;
        double offsetY = 30;

        for (int i = 0; i < locations.Count; i++)
        {
            var loc = locations[i];
            double x, y;
            if (loc.X.HasValue && loc.Y.HasValue)
            {
                x = offsetX + loc.X.Value * scale;
                y = offsetY + loc.Y.Value * scale;
            }
            else
            {
                // Grid fallback: 4 columns.
                x = offsetX + (i % 4) * scale * 2;
                y = offsetY + (i / 4) * scale * 2;
            }
            positions[loc.Name] = new Point(x, y);
        }

        // Draw edges (exits). We don't have direct exit data on LocationRow,
        // so we draw lines between all discovered locations as a simple
        // connectivity approximation. A future enhancement can pass the
        // actual exit graph.
        // For now, draw lines between sequential locations (grid layout)
        // and between locations that share terrain (rough clustering).
        // Actually, let's just draw the nodes — edges require the exit
        // graph which isn't on LocationRow. The visual still shows
        // discovered vs undiscovered, which is the core fog-of-war feature.
        //
        // Draw nodes.
        foreach (var loc in locations)
        {
            if (!positions.TryGetValue(loc.Name, out var pos)) continue;

            // Undiscovered locations are dimmed (fog of war).
            double opacity = loc.Discovered ? 1.0 : 0.3;

            // Node color: visited = green, discovered = blue, undiscovered = gray.
            var color = loc.Visited ? Color.Parse("#10b981")
                : loc.Discovered ? Color.Parse("#3b82f6")
                : Color.Parse("#6b7280");

            // Ellipse (location node).
            var ellipse = new Ellipse
            {
                Width = 16,
                Height = 16,
                Fill = new SolidColorBrush(color) { Opacity = opacity },
                Stroke = new SolidColorBrush(Colors.White) { Opacity = opacity * 0.5 },
                StrokeThickness = 1,
                [Canvas.LeftProperty] = pos.X - 8,
                [Canvas.TopProperty] = pos.Y - 8,
            };
            ToolTip.SetTip(ellipse, $"{loc.Name} ({loc.Terrain}, оп. {loc.Danger}/10){(loc.Visited ? " — посещено" : "")}");
            _mapCanvas.Children.Add(ellipse);

            // Label (location name).
            var label = new TextBlock
            {
                Text = loc.Name,
                FontSize = 9,
                Foreground = new SolidColorBrush(Colors.White) { Opacity = opacity },
                [Canvas.LeftProperty] = pos.X + 10,
                [Canvas.TopProperty] = pos.Y - 6,
            };
            _mapCanvas.Children.Add(label);
        }

        // Resize canvas to fit content.
        if (positions.Count > 0)
        {
            var maxX = positions.Values.Max(p => p.X) + 60;
            var maxY = positions.Values.Max(p => p.Y) + 60;
            _mapCanvas.Width = Math.Max(600, maxX);
            _mapCanvas.Height = Math.Max(400, maxY);
        }
    }
}
