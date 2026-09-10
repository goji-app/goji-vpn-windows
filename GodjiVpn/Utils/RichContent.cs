using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace GodjiVpn.Utils;

/// <summary>
/// Упрощённый порт RichContent.kt (Android) — рендерер "Rich Markdown" + части Telegram
/// HTML-разметки, в которой приходит content новостей (GET /api/broadcasts). Оригинал
/// поддерживает ещё таблицы, коллажи/слайдшоу картинок, интерактивные карты, спойлеры с
/// тап-раскрытием, сноски и details/summary — для новостной ленты VPN-приложения на Windows
/// это оправданно не переносить (WPF FlowDocument этого не умеет "из коробки", а ради
/// редких элементов разметки в паре новостей городить своё не стоило — тот же принцип
/// сознательного упрощения, что и с пингом или "выбор за меня" раньше в этом проекте).
///
/// Поддержано: **жирный**/*курсив*/~~зачёркнутый~~/`код`/[ссылка](url), теги
/// &lt;b&gt;/&lt;i&gt;/&lt;u&gt;/&lt;s&gt;/&lt;code&gt;/&lt;br&gt;/&lt;a href&gt;, заголовки
/// #..######, списки (- / 1. / - [ ]), цитаты (&gt;), разделитель (---), картинки
/// ![]("подпись"). Спойлер (||..||) и выделение (==..==) — снимаются как обычный текст/лёгкая
/// подсветка, без скрытия. Остальное (таблицы, tg-collage/slideshow/map, details, сноски)
/// вырезается на препроцессинге, чтобы хотя бы не показывать сырую разметку пользователю.
/// </summary>
public static class RichContent
{
    public static FlowDocument Build(string raw)
    {
        var doc = new FlowDocument { PagePadding = new Thickness(0), FontSize = 12.5 };
        doc.SetResourceReference(TextElement.ForegroundProperty, "TextPrimaryBrush");
        doc.SetResourceReference(TextElement.FontFamilyProperty, "ManropeMediumFamily");
        foreach (var block in ParseBlocks(PreClean(raw)))
            doc.Blocks.Add(block);
        return doc;
    }

    private static string PreClean(string raw)
    {
        var s = raw.Replace("\r\n", "\n");
        const RegexOptions opts = RegexOptions.IgnoreCase | RegexOptions.Singleline;
        s = Regex.Replace(s, "<tg-collage>.*?</tg-collage>", "", opts);
        s = Regex.Replace(s, "<tg-slideshow>.*?</tg-slideshow>", "", opts);
        s = Regex.Replace(s, "<tg-map[^>]*/?>", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, "<details>.*?</details>", "", opts);
        s = Regex.Replace(s, @"^\[\^[^\]]+]:\s*.+$", "", RegexOptions.Multiline);
        return s;
    }

    // ── Блочный уровень ──────────────────────────────────────────────────

    private static readonly Regex HeadingRegex = new(@"^(#{1,6})\s+(.*)$");
    private static readonly Regex CheckRegex = new(@"^-\s+\[([ xX])]\s+(.*)$");
    private static readonly Regex ImageRegex = new(@"^!\[([^\]]*)]\(([^\s)]+)(?:\s+""([^""]*)"")?\)$");
    private static readonly Regex OrderedRegex = new(@"^(\d+)\.\s+(.*)$");

    private static List<Block> ParseBlocks(string text)
    {
        var lines = text.Split('\n');
        var blocks = new List<Block>();
        var paragraphLines = new List<string>();

        void FlushParagraph()
        {
            var content = string.Join("\n", paragraphLines).Trim();
            paragraphLines.Clear();
            if (content.Length == 0) return;
            var p = NewParagraph();
            AddInlines(content, p.Inlines);
            blocks.Add(p);
        }

        foreach (var raw in lines)
        {
            var trimmed = raw.Trim();
            Match m;
            if (trimmed.Length == 0) { FlushParagraph(); continue; }
            if (trimmed is "---" or "***" or "___") { FlushParagraph(); blocks.Add(Divider()); continue; }
            if ((m = HeadingRegex.Match(trimmed)).Success)
            {
                FlushParagraph();
                var level = m.Groups[1].Value.Length;
                var p = NewParagraph();
                p.FontSize = level switch { 1 => 21, 2 => 18.5, 3 => 16.5, 4 => 14.5, _ => 13.5 };
                p.FontWeight = FontWeights.Bold;
                AddInlines(m.Groups[2].Value, p.Inlines);
                blocks.Add(p);
                continue;
            }
            if (trimmed.StartsWith('>'))
            {
                FlushParagraph();
                var p = NewParagraph();
                p.FontStyle = FontStyles.Italic;
                p.Margin = new Thickness(10, 2, 0, 6);
                p.SetResourceReference(TextElement.ForegroundProperty, "TextMutedBrush");
                AddInlines(trimmed.TrimStart('>').Trim(), p.Inlines);
                blocks.Add(p);
                continue;
            }
            if ((m = CheckRegex.Match(trimmed)).Success)
            {
                FlushParagraph();
                var isChecked = m.Groups[1].Value is "x" or "X";
                var p = NewParagraph();
                AddInlines((isChecked ? "☑  " : "☐  ") + m.Groups[2].Value, p.Inlines);
                blocks.Add(p);
                continue;
            }
            if ((m = ImageRegex.Match(trimmed)).Success)
            {
                FlushParagraph();
                blocks.Add(ImageBlock(m.Groups[2].Value, m.Groups[3].Success ? m.Groups[3].Value : null));
                continue;
            }
            if (trimmed.StartsWith("- "))
            {
                FlushParagraph();
                var p = NewParagraph();
                AddInlines("•  " + trimmed[2..].Trim(), p.Inlines);
                blocks.Add(p);
                continue;
            }
            if ((m = OrderedRegex.Match(trimmed)).Success)
            {
                FlushParagraph();
                var p = NewParagraph();
                AddInlines($"{m.Groups[1].Value}.  {m.Groups[2].Value}", p.Inlines);
                blocks.Add(p);
                continue;
            }
            paragraphLines.Add(raw);
        }
        FlushParagraph();
        return blocks;
    }

    private static Paragraph NewParagraph()
    {
        var p = new Paragraph { Margin = new Thickness(0, 0, 0, 6), LineHeight = 17 };
        p.SetResourceReference(TextElement.ForegroundProperty, "TextPrimaryBrush");
        return p;
    }

    private static Block Divider()
    {
        var border = new Border { Height = 1, Margin = new Thickness(0, 6, 0, 6) };
        border.SetResourceReference(Border.BackgroundProperty, "CardBorderBrush");
        return new BlockUIContainer(border) { Margin = new Thickness(0) };
    }

    private static Block ImageBlock(string url, string? caption)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 2, 0, 6) };
        try
        {
            var image = new Image
            {
                Source = new BitmapImage(new Uri(url, UriKind.Absolute)),
                Stretch = Stretch.Uniform,
                MaxHeight = 220,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            panel.Children.Add(image);
        }
        catch { /* битый/относительный URL — best effort, просто пропускаем картинку */ }
        if (!string.IsNullOrWhiteSpace(caption))
        {
            var cap = new TextBlock { Text = caption, FontSize = 10, Margin = new Thickness(0, 3, 0, 0) };
            cap.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
            panel.Children.Add(cap);
        }
        return new BlockUIContainer(panel) { Margin = new Thickness(0) };
    }

    // ── Инлайны ───────────────────────────────────────────────────────────

    private static readonly Regex LinkRegex = new(@"^\[([^\]]+)]\(([^)\s]+)\)");
    private static readonly Regex HtmlOpenTagRegex = new(@"^<([a-zA-Z][a-zA-Z0-9-]*)((?:\s+[a-zA-Z-]+=""[^""]*"")*)\s*/?>");
    private static readonly SolidColorBrush CodeBackground = new(Color.FromArgb(0x22, 0, 0, 0));
    private static readonly SolidColorBrush HighlightBackground = new(Color.FromArgb(0x55, 0x2F, 0xB3, 0x9A));

    private static void AddInlines(string text, InlineCollection target)
    {
        var buffer = new StringBuilder();
        void FlushBuffer() { if (buffer.Length > 0) { target.Add(new Run(buffer.ToString())); buffer.Clear(); } }

        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '<')
            {
                var afterTag = TryHtmlTag(text, i, target, FlushBuffer);
                if (afterTag != null) { i = afterTag.Value; continue; }
            }
            if (text.AsSpan(i).StartsWith("**"))
            {
                var end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end >= 0)
                {
                    FlushBuffer();
                    var bold = new Bold();
                    AddInlines(text[(i + 2)..end], bold.Inlines);
                    target.Add(bold);
                    i = end + 2;
                    continue;
                }
            }
            else if (text.AsSpan(i).StartsWith("~~"))
            {
                var end = text.IndexOf("~~", i + 2, StringComparison.Ordinal);
                if (end >= 0)
                {
                    FlushBuffer();
                    var span = new Span { TextDecorations = TextDecorations.Strikethrough };
                    AddInlines(text[(i + 2)..end], span.Inlines);
                    target.Add(span);
                    i = end + 2;
                    continue;
                }
            }
            else if (text.AsSpan(i).StartsWith("||"))
            {
                var end = text.IndexOf("||", i + 2, StringComparison.Ordinal);
                if (end >= 0)
                {
                    // Спойлер — без тап-раскрытия (см. заголовок файла), просто снимаем маркеры.
                    FlushBuffer();
                    AddInlines(text[(i + 2)..end], target);
                    i = end + 2;
                    continue;
                }
            }
            else if (text.AsSpan(i).StartsWith("=="))
            {
                var end = text.IndexOf("==", i + 2, StringComparison.Ordinal);
                if (end >= 0)
                {
                    FlushBuffer();
                    var span = new Span { Background = HighlightBackground };
                    AddInlines(text[(i + 2)..end], span.Inlines);
                    target.Add(span);
                    i = end + 2;
                    continue;
                }
            }
            else if (c == '`')
            {
                var end = text.IndexOf('`', i + 1);
                if (end >= 0)
                {
                    FlushBuffer();
                    var run = new Run(text[(i + 1)..end]) { FontFamily = new FontFamily("Consolas"), Background = CodeBackground };
                    target.Add(run);
                    i = end + 1;
                    continue;
                }
            }
            else if (c == '[')
            {
                var m = LinkRegex.Match(text, i);
                if (m.Success && m.Index == i)
                {
                    FlushBuffer();
                    var url = m.Groups[2].Value;
                    var link = new Hyperlink { Foreground = Brushes.Transparent };
                    link.SetResourceReference(TextElement.ForegroundProperty, "TealDeepBrush");
                    link.TextDecorations = TextDecorations.Underline;
                    AddInlines(m.Groups[1].Value, link.Inlines);
                    link.Click += (_, _) => OpenUrl(url);
                    target.Add(link);
                    i = m.Index + m.Length;
                    continue;
                }
            }
            else if (c == '*')
            {
                var end = text.IndexOf('*', i + 1);
                if (end >= 0)
                {
                    FlushBuffer();
                    var italic = new Italic();
                    AddInlines(text[(i + 1)..end], italic.Inlines);
                    target.Add(italic);
                    i = end + 1;
                    continue;
                }
            }
            buffer.Append(c);
            i++;
        }
        FlushBuffer();
    }

    /// <returns>Индекс сразу после обработанного тега, или null — если по этой позиции
    /// распознаваемого тега нет (тогда символ '&lt;' обрабатывается как обычный текст).</returns>
    private static int? TryHtmlTag(string text, int i, InlineCollection target, Action flushBuffer)
    {
        var openMatch = HtmlOpenTagRegex.Match(text[i..]);
        if (!openMatch.Success) return null;
        var tag = openMatch.Groups[1].Value.ToLowerInvariant();
        var attrs = openMatch.Groups[2].Value;
        var afterOpen = i + openMatch.Value.Length;

        if (tag == "br") { flushBuffer(); target.Add(new LineBreak()); return afterOpen; }
        if (openMatch.Value.EndsWith("/>")) return afterOpen;

        var closeTag = $"</{tag}>";
        var closeIdx = text.IndexOf(closeTag, afterOpen, StringComparison.OrdinalIgnoreCase);
        if (closeIdx < 0) return afterOpen;
        var inner = text[afterOpen..closeIdx];
        var next = closeIdx + closeTag.Length;
        flushBuffer();

        switch (tag)
        {
            case "b" or "strong":
                { var el = new Bold(); AddInlines(inner, el.Inlines); target.Add(el); break; }
            case "i" or "em":
                { var el = new Italic(); AddInlines(inner, el.Inlines); target.Add(el); break; }
            case "u" or "ins":
                { var el = new Underline(); AddInlines(inner, el.Inlines); target.Add(el); break; }
            case "s" or "strike" or "del":
                { var el = new Span { TextDecorations = TextDecorations.Strikethrough }; AddInlines(inner, el.Inlines); target.Add(el); break; }
            case "code":
                target.Add(new Run(inner) { FontFamily = new FontFamily("Consolas"), Background = CodeBackground });
                break;
            case "pre":
                target.Add(new Run(inner) { FontFamily = new FontFamily("Consolas") });
                break;
            case "tg-spoiler" or "spoiler":
                AddInlines(inner, target);
                break;
            case "a":
                {
                    var hrefMatch = Regex.Match(attrs, "href=\"([^\"]*)\"", RegexOptions.IgnoreCase);
                    var link = new Hyperlink { TextDecorations = TextDecorations.Underline };
                    link.SetResourceReference(TextElement.ForegroundProperty, "TealDeepBrush");
                    AddInlines(inner, link.Inlines);
                    if (hrefMatch.Success)
                    {
                        var url = hrefMatch.Groups[1].Value;
                        link.Click += (_, _) => OpenUrl(url);
                    }
                    target.Add(link);
                    break;
                }
            default:
                AddInlines(inner, target);
                break;
        }
        return next;
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* некликабельная/битая ссылка — не критично */ }
    }
}
