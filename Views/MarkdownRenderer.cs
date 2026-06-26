using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Controls;

namespace NtfyDesktop.Views;

/// <summary>
/// A small, dependency-free Markdown-to-inlines renderer covering the common
/// inline cases ntfy messages use: bold (**), italic (*/_), inline code (`),
/// links [text](url), and line breaks. Anything else renders as plain text.
/// </summary>
public static class MarkdownRenderer
{
    // Order matters: links first, then bold, then italic, then code.
    private static readonly Regex Token = new(
        @"(?<link>\[(?<text>[^\]]+)\]\((?<url>[^)]+)\))" +
        @"|(?<bold>\*\*(?<b>[^*]+)\*\*)" +
        @"|(?<code>`(?<c>[^`]+)`)" +
        @"|(?<italic>(?<![\*_])[\*_](?<i>[^\*_]+)[\*_](?![\*_]))",
        RegexOptions.Compiled);

    /// <summary>Renders <paramref name="markdown"/> into the TextBlock's Inlines.</summary>
    public static void Render(TextBlock target, string? markdown)
    {
        target.Inlines.Clear();
        if (string.IsNullOrEmpty(markdown))
        {
            return;
        }

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        for (var li = 0; li < lines.Length; li++)
        {
            // Strip leading heading/list markers but keep the text emphasized.
            var line = lines[li];
            var bold = false;
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("# ") || trimmed.StartsWith("## ") || trimmed.StartsWith("### "))
            {
                line = trimmed.TrimStart('#', ' ');
                bold = true;
            }
            else if (trimmed.StartsWith("- ") || trimmed.StartsWith("* "))
            {
                line = "• " + trimmed.Substring(2);
            }

            foreach (var inline in ParseInline(line, bold))
            {
                target.Inlines.Add(inline);
            }

            if (li < lines.Length - 1)
            {
                target.Inlines.Add(new LineBreak());
            }
        }
    }

    private static IEnumerable<Inline> ParseInline(string text, bool boldLine)
    {
        var result = new List<Inline>();
        var pos = 0;
        foreach (Match m in Token.Matches(text))
        {
            if (m.Index > pos)
            {
                result.Add(Run(text.Substring(pos, m.Index - pos), boldLine));
            }

            if (m.Groups["link"].Success)
            {
                var link = new Hyperlink();
                var url = m.Groups["url"].Value.Trim();
                if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
                {
                    link.NavigateUri = uri;
                }
                link.Inlines.Add(new Run { Text = m.Groups["text"].Value });
                result.Add(link);
            }
            else if (m.Groups["bold"].Success)
            {
                result.Add(new Run { Text = m.Groups["b"].Value, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            }
            else if (m.Groups["code"].Success)
            {
                result.Add(new Run { Text = m.Groups["c"].Value, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas") });
            }
            else if (m.Groups["italic"].Success)
            {
                result.Add(new Run { Text = m.Groups["i"].Value, FontStyle = Windows.UI.Text.FontStyle.Italic });
            }

            pos = m.Index + m.Length;
        }

        if (pos < text.Length)
        {
            result.Add(Run(text.Substring(pos), boldLine));
        }
        return result;
    }

    private static Run Run(string text, bool bold) => new()
    {
        Text = text,
        FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
    };
}
