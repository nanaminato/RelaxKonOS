using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using RelaxKonOS.Examples.HelpCenter.Services;
using RelaxKonOS.UI.Themes;

namespace RelaxKonOS.Examples.HelpCenter.Controls;

/// <summary>
/// Small, safe, read-only Markdown presenter for offline package documentation. It deliberately
/// supports the guide subset (headings, paragraphs, lists, code fences, and rules) and never
/// interprets HTML or executes external content.
/// </summary>
public sealed class MarkdownDocumentView : UserControl
{
    private readonly StackPanel _content = new() { Spacing = 10 };

    public MarkdownDocumentView() => Content = _content;

    public void SetDocument(HelpDocument? document)
    {
        _content.Children.Clear();
        if (document is null)
        {
            _content.Children.Add(new TextBlock { Text = "No guide is available.", FontSize = 18 });
            return;
        }

        var lines = document.Markdown.Replace("\r\n", "\n").Split('\n');
        var paragraph = new List<string>();
        var codeLines = new List<string>();
        var inCode = false;

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            _content.Children.Add(CreateText(string.Join(" ", paragraph), 16));
            paragraph.Clear();
        }

        void FlushCode()
        {
            if (codeLines.Count == 0) return;
            _content.Children.Add(new Border
            {
                Background = ThemeResources.Brush("SurfaceSunkenBrush"),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(14),
                Child = new TextBlock
                {
                    Text = string.Join(Environment.NewLine, codeLines),
                    FontFamily = FontFamily.Default,
                    Foreground = ThemeResources.Brush("TextPrimaryBrush"),
                    TextWrapping = TextWrapping.Wrap,
                },
            });
            codeLines.Clear();
        }

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd();
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                FlushParagraph();
                if (inCode) FlushCode();
                inCode = !inCode;
                continue;
            }
            if (inCode)
            {
                codeLines.Add(rawLine);
                continue;
            }
            if (string.IsNullOrWhiteSpace(line))
            {
                FlushParagraph();
                continue;
            }
            if (line is "---" or "***")
            {
                FlushParagraph();
                _content.Children.Add(new Border { Height = 1, Background = ThemeResources.Brush("BorderDefaultBrush"), Margin = new Thickness(0, 8) });
                continue;
            }
            var heading = line.TakeWhile(character => character == '#').Count();
            if (heading > 0 && line.Length > heading && line[heading] == ' ')
            {
                FlushParagraph();
                _content.Children.Add(CreateText(line[(heading + 1)..], heading == 1 ? 28 : heading == 2 ? 22 : 18, FontWeight.SemiBold));
                continue;
            }
            if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
            {
                FlushParagraph();
                _content.Children.Add(CreateText("• " + line[2..], 16, margin: new Thickness(12, 0, 0, 0)));
                continue;
            }
            if (char.IsDigit(line[0]) && line.Contains(". ", StringComparison.Ordinal))
            {
                FlushParagraph();
                _content.Children.Add(CreateText(line, 16, margin: new Thickness(12, 0, 0, 0)));
                continue;
            }
            paragraph.Add(line.Trim());
        }
        FlushParagraph();
        if (inCode) FlushCode();
    }

    private static TextBlock CreateText(string text, double fontSize, FontWeight? weight = null, Thickness? margin = null) => new()
    {
        Text = text,
        FontSize = fontSize,
        FontWeight = weight ?? FontWeight.Normal,
        TextWrapping = TextWrapping.Wrap,
        Margin = margin ?? default,
    };
}
