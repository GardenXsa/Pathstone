using System;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using MyGame.Desktop.ViewModels;

namespace MyGame.Desktop;

/// <summary>
/// Standard Avalonia view locator: maps
/// <c>MyGame.Desktop.ViewModels.XxxViewModel</c> →
/// <c>MyGame.Desktop.Views.XxxView</c> by name. Registered as a
/// <c>DataTemplate</c> in <c>App.axaml</c> so any control bound to a
/// ViewModel gets its matching view rendered automatically.
///
/// <para>
/// The locator uses a naming convention: a ViewModel named
/// <c>FooBarViewModel</c> resolves to <c>FooBarView</c>. If the view
/// type can't be found (e.g. the ViewModel is a built-in type or the
/// view hasn't been authored yet), a placeholder <c>TextBlock</c>
/// showing the ViewModel's type name is returned — keeps the UI from
/// crashing on missing views during development.
/// </para>
/// </summary>
public class ViewLocator : IDataTemplate
{
    private const string ViewModelSuffix = "ViewModel";
    private const string ViewSuffix = "View";
    private const string ViewModelNamespace = "MyGame.Desktop.ViewModels";
    private const string ViewNamespace = "MyGame.Desktop.Views";

    /// <summary>
    /// Supports any object — the data template is registered
    /// application-wide and Avalonia asks each DataContext whether the
    /// locator supports it. We answer true for anything in the
    /// ViewModels namespace.
    /// </summary>
    public bool SupportsRecycling => false;

    public bool Match(object? data)
    {
        return data is ViewModelBase
            || (data is not null
                && data.GetType().Namespace?.StartsWith(ViewModelNamespace, StringComparison.Ordinal) == true);
    }

    /// <summary>
    /// Build the view for the given ViewModel. Uses
    /// <see cref="Activator.CreateInstance(Type)"/> so views don't need
    /// a parameterless constructor registered anywhere — the type just
    /// has to exist.
    /// </summary>
    public Control? Build(object? data)
    {
        if (data is null) return null;

        var name = data.GetType().FullName;
        if (string.IsNullOrEmpty(name)) return Fallback(data);

        // Replace the namespace and the suffix.
        if (!name.EndsWith(ViewModelSuffix, StringComparison.Ordinal)) return Fallback(data);
        var viewName = name.Substring(0, name.Length - ViewModelSuffix.Length) + ViewSuffix;
        viewName = viewName.Replace(ViewModelNamespace, ViewNamespace, StringComparison.Ordinal);

        var type = Type.GetType(viewName);
        if (type is null)
        {
            // Try loading via the same assembly (the FullName above is
            // already assembly-qualified-less, so the runtime won't
            // search other assemblies). Walk the loaded assemblies in
            // this AppDomain as a fallback.
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(viewName);
                if (t is not null) { type = t; break; }
            }
        }

        if (type is null) return Fallback(data);

        try
        {
            return (Control?)Activator.CreateInstance(type);
        }
        catch
        {
            return Fallback(data);
        }
    }

    /// <summary>
    /// Build a placeholder TextBlock so the UI doesn't crash on a
    /// missing/broken view. Useful during early development when not
    /// every screen has its XAML yet.
    /// </summary>
    private static Control Fallback(object data) => new TextBlock
    {
        Text = $"(no view for {data.GetType().Name})",
        Margin = new Avalonia.Thickness(8),
    };
}
