using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace MyGame.Desktop.Services;

/// <summary>
/// Robust, lightweight parser converting Markdown strings to Avalonia TextBlock Inlines.
/// </summary>
public static class MarkdownRenderer
{
    private static readonly Regex MarkdownRegex = new Regex(
        @"(\*\*(.*?)\*\*)|(\*(.*?)\*)|(`(.*?)`)",
        RegexOptions.Compiled);

    public static void Render(TextBlock textBlock, string? markdown)
    {
        textBlock.Inlines.Clear();
        if (markdown is not { Length: > 0 } str) return;

        var lines = str.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];

            // Render headers
            if (line.StartsWith("### "))
            {
                var run = new Run
                {
                    Text = line.Substring(4),
                    FontSize = textBlock.FontSize + 2,
                    FontWeight = FontWeight.Bold
                };
                textBlock.Inlines.Add(run);
                if (i < lines.Length - 1) textBlock.Inlines.Add(new LineBreak());
                continue; 
            }
            if (line.StartsWith("## "))
            {
                var run = new Run
                {
                    Text = line.Substring(3),
                    FontSize = textBlock.FontSize + 4,
                    FontWeight = FontWeight.Bold
                };
                textBlock.Inlines.Add(run);
                if (i < lines.Length - 1) textBlock.Inlines.Add(new LineBreak());
                continue;
            }
            if (line.StartsWith("# "))
            {
                var run = new Run
                {
                    Text = line.Substring(2),
                    FontSize = textBlock.FontSize + 6,
                    FontWeight = FontWeight.Bold
                };
                textBlock.Inlines.Add(run);
                if (i < lines.Length - 1) textBlock.Inlines.Add(new LineBreak());
                continue;
            }

            // Render list bullet points
            if (line.StartsWith("- ") || line.StartsWith("* "))
            {
                textBlock.Inlines.Add(new Run { Text = "  • ", FontWeight = FontWeight.Bold });
                line = line.Substring(2);
            }

            // Format inline elements
            var matches = MarkdownRegex.Matches(line);
            int lastIdx = 0;
            foreach (Match m in matches)
            {
                if (m.Index > lastIdx)
                {
                    textBlock.Inlines.Add(new Run { Text = line.Substring(lastIdx, m.Index - lastIdx) });
                }

                if (m.Groups[1].Success) // Bold **
                {
                    textBlock.Inlines.Add(new Run
                    {
                        Text = m.Groups[2].Value,
                        FontWeight = FontWeight.Bold
                    });
                }
                else if (m.Groups[3].Success) // Italic *
                {
                    textBlock.Inlines.Add(new Run
                    {
                        Text = m.Groups[4].Value,
                        FontStyle = FontStyle.Italic
                    });
                }
                else if (m.Groups[5].Success) // Code block `
                {
                    textBlock.Inlines.Add(new Run
                    {
                        Text = m.Groups[6].Value,
                        FontFamily = FontFamily.Parse("Consolas,Menlo,monospace"),
                        Foreground = Brushes.SandyBrown
                    });
                }
                lastIdx = m.Index + m.Length;
            }

            if (lastIdx < line.Length)
            {
                textBlock.Inlines.Add(new Run { Text = line.Substring(lastIdx) });
            }

            if (i < lines.Length - 1)
            {
                textBlock.Inlines.Add(new LineBreak());
            }
        }
    } 
}
