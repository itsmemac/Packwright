using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;

namespace Packwright.App.Services;

/// <summary>
/// Shows the small part of Markdown that release notes use: headings, bullet lists, **bold**, `code`, [links](url) and simple
/// tables. It is a display aid, not a full Markdown engine; anything else is shown as plain text.
/// </summary>
public static partial class MarkdownLite
{
    [GeneratedRegex(@"(\*\*.+?\*\*|`.+?`|\[[^\]]+\]\([^)]+\))")]
    private static partial Regex InlinePattern();

    public static Control Render(string markdown)
    {
        var panel = new StackPanel { Spacing = 7 };
        foreach (string raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.Length == 0) continue;
            if (line.StartsWith("### ", StringComparison.Ordinal)) panel.Children.Add(Heading(line[4..], 14));
            else if (line.StartsWith("## ", StringComparison.Ordinal)) panel.Children.Add(Heading(line[3..], 17));
            else if (line.StartsWith("# ", StringComparison.Ordinal)) panel.Children.Add(Heading(line[2..], 19));
            else if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
                panel.Children.Add(Bullet(line[2..]));
            else if (line.StartsWith('|'))
            {
                string[] cells = line.Trim('|').Split('|').Select(cell => cell.Trim()).ToArray();
                if (cells.All(cell => cell.Length == 0 || cell.All(ch => ch is '-' or ':' or ' '))) continue;   // the ---|--- row
                panel.Children.Add(Bullet(string.Join("  ·  ", cells.Where(cell => cell.Length > 0)), "▸"));
            }
            else panel.Children.Add(Paragraph(line));
        }
        return panel;
    }

    private static TextBlock Heading(string text, double size)
    {
        var block = new TextBlock { FontSize = size, FontWeight = FontWeight.Bold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        AddInlines(block, text);
        return block;
    }

    private static TextBlock Paragraph(string text)
    {
        var block = new TextBlock { TextWrapping = TextWrapping.Wrap };
        AddInlines(block, text);
        return block;
    }

    private static Control Bullet(string text, string marker = "•")
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("18,*") };
        grid.Children.Add(new TextBlock { Text = marker, Opacity = 0.7 });
        TextBlock body = Paragraph(text);
        Grid.SetColumn(body, 1);
        grid.Children.Add(body);
        return grid;
    }

    private static void AddInlines(TextBlock block, string text)
    {
        int position = 0;
        foreach (Match match in InlinePattern().Matches(text))
        {
            if (match.Index > position) block.Inlines!.Add(new Run(text[position..match.Index]));
            string token = match.Value;
            if (token.StartsWith("**", StringComparison.Ordinal))
                block.Inlines!.Add(new Run(token[2..^2]) { FontWeight = FontWeight.Bold });
            else if (token.StartsWith('`'))
                block.Inlines!.Add(new Run(token[1..^1]) { FontFamily = new FontFamily("Cascadia Mono, Consolas, Menlo, monospace"), FontSize = Math.Max(11, block.FontSize - 1) });
            else
            {
                int close = token.IndexOf("](", StringComparison.Ordinal);
                block.Inlines!.Add(new Run(token[1..close]) { TextDecorations = TextDecorations.Underline });
            }
            position = match.Index + match.Length;
        }
        if (position < text.Length) block.Inlines!.Add(new Run(text[position..]));
    }
}
