using Avalonia;
using Avalonia.Controls;
using MyGame.Desktop.Services;

namespace MyGame.Desktop.Controls;

/// <summary>
/// Custom Avalonia control extending TextBlock to natively support and render Markdown strings.
/// </summary>
public class MarkdownTextBlock : TextBlock
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownTextBlock, string?>(nameof(Markdown));

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MarkdownProperty)
        {
            MarkdownRenderer.Render(this, change.GetNewValue<string?>());
        }
    }
}
