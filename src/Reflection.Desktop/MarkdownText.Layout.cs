using System.Diagnostics;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Data;
using System.Windows.Media;
using Markdig.Extensions.Alerts;
using Markdig.Extensions.DefinitionLists;
using Markdig.Extensions.Footnotes;
using Markdig.Extensions.Mathematics;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Reflection.Core;
using MdTable = Markdig.Extensions.Tables.Table;

namespace Reflection.Desktop;

public partial class MarkdownText
{
    protected static readonly FontFamily SansSerifFont = new("Microsoft YaHei UI");
    private (double Digit, double Period)? markerWidths;
    private int tableColumns;
    private string? markdownSource;
    private ReaderFormulaIndex? formulaIndex;
    private readonly Dictionary<string, Paragraph> headings = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<Footnote, Paragraph> footnoteTargets = [];
    private readonly Dictionary<Footnote, Hyperlink> footnoteReferences = [];
    protected bool InTable => tableColumns > 0;
    protected virtual FontFamily BodyFont => FontFamily;
    protected virtual double LayoutFontSize => 14;
    protected virtual double LayoutLeading => 1.5;
    protected virtual ReadingPiece? RenderPiece => null;

    protected void ResetLayout(string? source = null, ReaderFormulaIndex? formulas = null)
    {
        markdownSource = source;
        formulaIndex = formulas;
        headings.Clear();
        footnoteTargets.Clear();
        footnoteReferences.Clear();
        markerWidths = null;
        tableColumns = 0;
        FormulaCount = 0;
        FormulaFallbackCount = 0;
        TableCount = 0;
        ResetFormulaBudget();
    }

    private string MathSource(MarkdownObject node, string fallback) =>
        markdownSource is { } source && node.Span.Start >= 0 && node.Span.End < source.Length && node.Span.Length > 0
            ? source.Substring(node.Span.Start, node.Span.Length) : fallback;

    private Span Link(string? address, bool allowLink = true)
    {
        if (!allowLink || string.IsNullOrWhiteSpace(address) || !CanNavigateLink(address)) return new Span();
        var hyperlink = new Hyperlink { Foreground = Brush("Accent"), ToolTip = address };
        hyperlink.Click += (_, _) => NavigateLink(address);
        return hyperlink;
    }

    protected virtual bool CanNavigateLink(string address) => address.StartsWith('#')
        || Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto";

    protected virtual void NavigateLink(string address)
    {
        if (address.StartsWith('#'))
        {
            var anchor = ReaderDocuments.HeadingAnchor(Uri.UnescapeDataString(address[1..]));
            if (headings.TryGetValue(anchor, out var heading)) RevealText(heading.ContentStart);
            return;
        }
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https" or "mailto")) return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException) { ToolTip = error.Message; }
    }

    protected virtual void AddImage(InlineCollection target, LinkInline link, bool allowLink)
    {
        var placeholder = new Run(ImagePlaceholder(link)) { Foreground = Brush("Muted") };
        if (!allowLink || !Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            target.Add(placeholder);
            return;
        }
        var hyperlink = Link(uri.AbsoluteUri);
        hyperlink.ToolTip = "在浏览器中打开图片";
        hyperlink.Inlines.Add(placeholder);
        target.Add(hyperlink);
    }

    protected static string ImagePlaceholder(LinkInline link)
    {
        var alt = ReaderDocuments.InlineText(link);
        return string.IsNullOrWhiteSpace(alt) ? "[图片]" : $"[图片：{alt}]";
    }

    protected ScrollViewer FormulaScroller(FrameworkElement image, bool block)
    {
        var scroll = new ScrollViewer { Content = image, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = block ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled, MaxHeight = block ? 600 : double.PositiveInfinity, Padding = block ? new Thickness(0, 10, 0, 12) : new Thickness(0) };
        scroll.SetBinding(MaxWidthProperty, new Binding(nameof(ActualWidth)) { Source = this, Converter = new FormulaWidthConverter(), ConverterParameter = InTable ? tableColumns : null });
        return scroll;
    }
    protected void HeadingStyle(Paragraph paragraph, double scale)
    {
        paragraph.FontFamily = SansSerifFont;
        paragraph.FontSize = LayoutFontSize * scale;
        paragraph.FontWeight = FontWeights.SemiBold;
        paragraph.LineHeight = paragraph.FontSize * LayoutLeading;
    }

    private void MarkdownHeadingStyle(Paragraph paragraph, int level)
    {
        HeadingStyle(paragraph, level switch
        {
            1 => 1.7,
            2 => 1.48,
            3 => 1.33,
            4 => 1.22,
            5 => 1.13,
            _ => 1.05
        });
        var line = LayoutFontSize * LayoutLeading;
        // Keep heading spacing inside the block at virtualized document boundaries.
        paragraph.Margin = new Thickness(0);
        paragraph.Padding = new Thickness(0, line * (level <= 2 ? .65 : .45), 0, line * .45);
    }
    private void StyleBulletList(System.Windows.Documents.List result, ListBlock list)
    {
        var level = 0;
        for (var parent = list.Parent; parent is not null; parent = parent.Parent)
            if (parent is ListBlock) level++;

        var (marker, scale) = level switch
        {
            0 => (TextMarkerStyle.Box, 1.0),
            1 => (TextMarkerStyle.Disc, .78),
            _ => (TextMarkerStyle.Disc, .48)
        };
        result.MarkerStyle = marker;
        result.FontSize = LayoutFontSize * scale;
    }

    private void SizeNumberedList(System.Windows.Documents.List list)
    {
        if (markerWidths is not { } widths)
        {
            var typeface = new Typeface(BodyFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            double Width(char character) => new FormattedText(character.ToString(), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, typeface, LayoutFontSize, Brushes.Black, dpi).Width;
            widths = ("0123456789".Max(Width), Width('.'));
            markerWidths = widths;
        }

        var last = Math.Min(int.MaxValue, (long)list.StartIndex + list.ListItems.Count - 1);
        var digits = last.ToString(CultureInfo.InvariantCulture).Length;
        var indent = digits * widths.Digit + widths.Period + list.MarkerOffset;
        list.Padding = new Thickness(Math.Max(26, Math.Ceiling(indent) + 2), 0, 0, 0);
    }

    protected void AddBlock(BlockCollection target, Markdig.Syntax.Block node, int depth)
    {
        if (depth > 24) { target.Add(new Paragraph(new Run("内容嵌套过深"))); return; }
        if (node is MathBlock math)
        {
            if (math.ClosingFencedCharCount == 0)
                target.Add(new Paragraph(new Run(MathSource(math, "$$" + math.Lines))));
            else
                AddDisplayMath(target, math, math.Lines.ToString());
            return;
        }
        if (node is ParagraphBlock { Inline: { } content } && content.FirstChild is MathInline { DelimiterCount: 2 } display
            && ReferenceEquals(content.LastChild, display))
        {
            AddDisplayMath(target, display, display.Content.ToString());
            return;
        }
        if (node is MdTable table) { AddTable(target, table, depth); return; }
        if (node is CodeBlock code) { AddCode(target, code.Lines.ToString(), (code as FencedCodeBlock)?.Info); return; }
        if (node is Footnote note) { AddFootnote(target, note, depth); return; }
        if (node is DefinitionItem definition)
        {
            var section = new Section { Margin = new Thickness(0, 0, 0, 8) };
            foreach (var child in definition)
            {
                if (child is DefinitionTerm term)
                {
                    var heading = new Paragraph { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 2, 0, 2) };
                    AddInline(heading.Inlines, term.Inline, depth + 1); section.Blocks.Add(heading);
                }
                else
                {
                    var body = new Section { Padding = new Thickness(16, 0, 0, 0) };
                    AddBlock(body.Blocks, child, depth + 1); section.Blocks.Add(body);
                }
            }
            target.Add(section); return;
        }
        if (node is ListBlock list)
        {
            var result = new System.Windows.Documents.List { MarkerStyle = TextMarkerStyle.Decimal, MarkerOffset = LayoutFontSize * .45,
                Padding = new Thickness(26, 0, 0, 0), Margin = new Thickness(0, 0, 0, 10) };
            if (!list.IsOrdered) StyleBulletList(result, list);
            var first = ReferenceEquals(RenderPiece?.Blocks.FirstOrDefault(), node) ? RenderPiece!.First : 0;
            var count = ReferenceEquals(RenderPiece?.Blocks.FirstOrDefault(), node) ? RenderPiece!.Count : int.MaxValue;
            if (list.IsOrdered && int.TryParse(list.OrderedStart, out var number))
                result.StartIndex = (int)Math.Clamp((long)number + first, 1, int.MaxValue);
            foreach (var item in list.Skip(first).Take(count).OfType<ListItemBlock>())
            {
                var li = new ListItem();
                // WPF sizes markers from the list and inherits body typography through each item.
                if (!list.IsOrdered) li.FontSize = LayoutFontSize;
                foreach (var child in item) AddBlock(li.Blocks, child, depth + 1);
                if (li.Blocks.Count == 0) li.Blocks.Add(new Paragraph()); result.ListItems.Add(li);
            }
            if (list.IsOrdered) SizeNumberedList(result);
            target.Add(result); return;
        }
        if (node is AlertBlock alert)
        {
            var (label, accent, background) = alert.Kind.ToString().ToUpperInvariant() switch
            {
                "TIP" => ("技巧", "Tone48624E", "Mint"),
                "IMPORTANT" => ("重要", "Accent", "Lilac"),
                "WARNING" => ("警告", "Tone836D58", "Yellow"),
                "CAUTION" => ("注意", "Danger", "Lilac"),
                _ => ("提示", "Accent", "AccentSoft")
            };
            var section = new Section { BorderBrush = Brush(accent), BorderThickness = new Thickness(3, 0, 0, 0),
                Background = Brush(background), Padding = new Thickness(12, 7, 10, 0), Margin = new Thickness(0, 5, 0, 10) };
            section.Blocks.Add(new Paragraph(new Run(label)) { Foreground = Brush(accent), FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 4) });
            foreach (var child in alert) AddBlock(section.Blocks, child, depth + 1);
            target.Add(section); return;
        }
        if (node is QuoteBlock quote)
        {
            var section = new Section { BorderBrush = Brush("Accent"), BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(16, 0, 0, 0), Foreground = Brush("Muted"), Margin = new Thickness(0, 8, 0, 14) };
            foreach (var child in quote) AddBlock(section.Blocks, child, depth + 1); target.Add(section); return;
        }
        if (node is ThematicBreakBlock) { target.Add(new Paragraph { BorderBrush = Brush("Line"), BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, 12, 0, 20), FontSize = 1 }); return; }
        if (node is LeafBlock leaf)
        {
            var p = new Paragraph { Margin = new Thickness(0, 0, 0, LayoutFontSize * .65) };
            if (node is HeadingBlock heading)
            {
                MarkdownHeadingStyle(p, heading.Level);
                if (RenderPiece is null)
                {
                    var anchor = ReaderDocuments.HeadingAnchor(ReaderDocuments.InlineText(heading.Inline));
                    var key = anchor;
                    for (var suffix = 1; !headings.TryAdd(key, p); suffix++) key = $"{anchor}-{suffix}";
                }
            }
            AddInline(p.Inlines, leaf.Inline, depth + 1); target.Add(p); return;
        }
        if (node is ContainerBlock container and not LinkReferenceDefinitionGroup) foreach (var child in container) AddBlock(target, child, depth + 1);
    }
    private void AddDisplayMath(BlockCollection target, MarkdownObject node, string source)
    {
        var raw = MathSource(node, "$$" + source + "$$");
        var image = Formula(source, raw, true, ++FormulaCount <= MathDelimiters.MaxFormulasPerRender, fontSize: LayoutFontSize,
            offset: node.Span.Start + (RenderPiece?.MathOffset ?? 0));
        if (image is null) { FormulaFallbackCount++; target.Add(new Paragraph(new Run(raw))); }
        else target.Add(new BlockUIContainer(FormulaScroller(image, true)));
    }

    private void AddInline(InlineCollection target, ContainerInline? container, int depth, bool allowLink = true)
    {
        if (container is null || depth > 32) return;
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal: target.Add(new Run(literal.Content.ToString())); break;
                case HtmlEntityInline entity: target.Add(new Run(entity.Transcoded.ToString())); break;
                case CodeInline code: target.Add(new Run(code.Content) { FontFamily = new FontFamily("Consolas"), FontSize = LayoutFontSize * .88, Background = Brush("AccentSoft") }); break;
                case LineBreakInline: target.Add(new LineBreak()); break;
                case MathInline math:
                    var raw = MathSource(math, "$" + math.Content + "$");
                    var formula = Formula(math.Content.ToString(), raw, math.DelimiterCount > 1, ++FormulaCount <= MathDelimiters.MaxFormulasPerRender,
                        displayStyle: InTable, fontSize: LayoutFontSize, offset: math.Span.Start + (RenderPiece?.MathOffset ?? 0));
                    if (formula is null) { FormulaFallbackCount++; target.Add(new Run(raw)); }
                    else
                    {
                        target.Add(new InlineUIContainer(FormulaScroller(formula, false)) { BaselineAlignment = BaselineAlignment.Center });
                    }
                    break;
                case FootnoteLink note:
                    if (note.IsBackLink) break;
                    if (!allowLink) { target.Add(new Run($"[{note.Footnote.Order}]")); break; }
                    var reference = FootnoteJump($"[{note.Footnote.Order}]", note.Footnote, false);
                    reference.BaselineAlignment = BaselineAlignment.Superscript;
                    reference.FontSize = LayoutFontSize * .76;
                    footnoteReferences.TryAdd(note.Footnote, reference);
                    target.Add(reference); break;
                case TaskList task: target.Add(new Run(task.Checked ? "☑ " : "☐ ")); break;
                case LinkInline link:
                    if (link.IsImage) { AddImage(target, link, allowLink); break; }
                    var hyperlink = Link(link.Url, allowLink);
                    AddInline(hyperlink.Inlines, link, depth + 1, hyperlink is not Hyperlink && allowLink); target.Add(hyperlink); break;
                case AutolinkInline auto:
                    var a = Link(auto.IsEmail ? "mailto:" + auto.Url : auto.Url, allowLink);
                    a.Inlines.Add(new Run(auto.Url)); target.Add(a); break;
                case EmphasisInline emphasis:
                    var span = new Span();
                    switch (emphasis.DelimiterChar)
                    {
                        case '~' when emphasis.DelimiterCount == 2:
                            span.TextDecorations = TextDecorations.Strikethrough; break;
                        case '~':
                            span.BaselineAlignment = BaselineAlignment.Subscript; span.FontSize = LayoutFontSize * .76; break;
                        case '^':
                            span.BaselineAlignment = BaselineAlignment.Superscript; span.FontSize = LayoutFontSize * .76; break;
                        case '+':
                            span.TextDecorations = TextDecorations.Underline; break;
                        case '=':
                            span.Background = Brush("AccentSoft"); break;
                        default:
                            span = emphasis.DelimiterCount >= 2 ? new Bold() : new Italic();
                            break;
                    }
                    AddInline(span.Inlines, emphasis, depth + 1, allowLink); target.Add(span); break;
                case ContainerInline child:
                    var other = new Span(); AddInline(other.Inlines, child, depth + 1, allowLink); target.Add(other); break;
            }
        }
    }
    private void AddFootnote(BlockCollection target, Footnote note, int depth)
    {
        var section = new Section { BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = Brush("Line"),
            Padding = new Thickness(0, 6, 0, 0), Margin = new Thickness(0, 8, 0, 0), FontSize = LayoutFontSize * .9 };
        var label = new Paragraph(new Run($"[{note.Order}] ")) { FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 3) };
        label.Inlines.Add(FootnoteJump("返回引用", note, true));
        section.Blocks.Add(label);
        footnoteTargets.TryAdd(note, label);
        foreach (var block in note) AddBlock(section.Blocks, block, depth + 1);
        target.Add(section);
    }

    private Hyperlink FootnoteJump(string label, Footnote note, bool back)
    {
        var link = new Hyperlink(new Run(label)) { Foreground = Brush("Accent") };
        link.Click += (_, _) => NavigateFootnote(note, back);
        return link;
    }

    protected virtual void NavigateFootnote(Footnote note, bool back)
    {
        if (back && footnoteReferences.TryGetValue(note, out var reference)) RevealText(reference.ContentStart);
        if (!back && footnoteTargets.TryGetValue(note, out var target)) RevealText(target.ContentStart);
    }

    private void RevealText(TextPointer target)
    {
        var rect = target.GetCharacterRect(LogicalDirection.Forward);
        if (rect.IsEmpty || Template.FindName("PART_ContentHost", this) is not FrameworkElement host) return;
        var bounds = host.TransformToAncestor(this).TransformBounds(rect);
        BringIntoView(bounds);
    }

    private void AddTable(BlockCollection target, MdTable table, int depth)
    {
        TableCount++;
        var result = new System.Windows.Documents.Table { CellSpacing = 0, BorderBrush = Brush("Line"), BorderThickness = new Thickness(1), Margin = new Thickness(0, 6, 0, 12) };
        foreach (var _ in table.ColumnDefinitions) result.Columns.Add(new TableColumn());
        var group = new TableRowGroup(); result.RowGroups.Add(group);
        var rows = table.Cast<Markdig.Extensions.Tables.TableRow>().AsEnumerable();
        if (RenderPiece is { First: > 0 } piece && ReferenceEquals(piece.Blocks.FirstOrDefault(), table)) rows = rows.Take(1).Concat(rows.Skip(piece.First).Take(piece.Count));
        foreach (var row in rows)
        {
            var r = new TableRow(); group.Rows.Add(r);
            if (row.IsHeader) { r.FontWeight = FontWeights.SemiBold; r.Background = Brush("TableHeader"); }
            var column = 0;
            foreach (var cell in row.OfType<Markdig.Extensions.Tables.TableCell>())
            {
                var c = new TableCell { Padding = new Thickness(9), BorderBrush = Brush("Line"), BorderThickness = new Thickness(0, 0, 1, 1), ColumnSpan = Math.Max(1, cell.ColumnSpan) };
                var previousColumns = tableColumns; tableColumns = result.Columns.Count;
                foreach (var child in cell) AddBlock(c.Blocks, child, depth + 1);
                tableColumns = previousColumns;
                c.TextAlignment = column < table.ColumnDefinitions.Count ? table.ColumnDefinitions[column].Alignment switch
                { Markdig.Extensions.Tables.TableColumnAlign.Center => TextAlignment.Center, Markdig.Extensions.Tables.TableColumnAlign.Right => TextAlignment.Right, _ => TextAlignment.Left } : TextAlignment.Left;
                r.Cells.Add(c); column++;
            }
        }
        target.Add(result);
    }
}
