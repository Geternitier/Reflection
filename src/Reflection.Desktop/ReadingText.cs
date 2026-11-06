using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Data;
using System.Windows.Media;
using Markdig.Extensions.Mathematics;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using Reflection.Core;
using MdTable = Markdig.Extensions.Tables.Table;
using WpfBlock = System.Windows.Documents.Block;

namespace Reflection.Desktop;

public sealed class ReadingText : MarkdownText
{
    public static readonly DependencyProperty PieceProperty = DependencyProperty.Register(nameof(Piece), typeof(ReadingPiece), typeof(ReadingText), new PropertyMetadata(null, Change));
    public static readonly DependencyProperty TextSizeProperty = DependencyProperty.Register(nameof(TextSize), typeof(double), typeof(ReadingText), new PropertyMetadata(18d, Change));
    public static readonly DependencyProperty LeadingProperty = DependencyProperty.Register(nameof(Leading), typeof(double), typeof(ReadingText), new PropertyMetadata(1.85d, Change));
    public static readonly DependencyProperty SerifProperty = DependencyProperty.Register(nameof(Serif), typeof(bool), typeof(ReadingText), new PropertyMetadata(true, Change));
    public ReadingPiece? Piece { get => (ReadingPiece?)GetValue(PieceProperty); set => SetValue(PieceProperty, value); }
    public double TextSize { get => (double)GetValue(TextSizeProperty); set => SetValue(TextSizeProperty, value); }
    public double Leading { get => (double)GetValue(LeadingProperty); set => SetValue(LeadingProperty, value); }
    public bool Serif { get => (bool)GetValue(SerifProperty); set => SetValue(SerifProperty, value); }
    public static readonly RoutedEvent LinkRequestedEvent = EventManager.RegisterRoutedEvent("LinkRequested", RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(ReadingText));
    public event RoutedEventHandler LinkRequested { add => AddHandler(LinkRequestedEvent, value); remove => RemoveHandler(LinkRequestedEvent, value); }
    internal string? RequestedLink { get; private set; }
    private ReadingPiece? renderedPiece;
    private (double, double, bool, int) renderedStyle;
    private int formulas;
    private bool inTable;
    internal int RenderedFormulaCount { get; private set; }
    private static void Change(DependencyObject obj, DependencyPropertyChangedEventArgs e) { var view = (ReadingText)obj; if (view.IsVisible) view.Render(); }
    private static Brush Color(string key) => (Brush)Application.Current.FindResource(key);
    internal override void Render()
    {
        var piece = Piece;
        var style = (TextSize, Leading, Serif, ThemeManager.Revision);
        if (piece is null || ReferenceEquals(piece, renderedPiece) && style == renderedStyle) return;
        renderedPiece = piece; renderedStyle = style; formulas = 0; RenderedFormulaCount = 0; ResetFormulaBudget();
        var doc = new FlowDocument { PagePadding = new Thickness(0), FontSize = TextSize,
            FontFamily = new FontFamily(Serif ? "Georgia, SimSun" : "Microsoft YaHei UI"), Foreground = Color("Ink"),
            LineHeight = TextSize * Leading, LineStackingStrategy = LineStackingStrategy.MaxHeight };
        if (piece.Text is { } text)
        {
            if (!piece.Code && piece.Heading == 0) AddPlainText(doc, text.TrimEnd('\r', '\n'));
            else
            {
                var paragraph = new Paragraph(new Run(text.TrimEnd('\r', '\n'))) { Margin = new Thickness(0, 0, 0, 12) };
                if (piece.Code) CodeStyle(paragraph);
                if (piece.Heading > 0) HeadingStyle(paragraph, piece.Heading);
                doc.Blocks.Add(paragraph);
            }
        }
        else foreach (var node in piece.Blocks) AddBlock(doc.Blocks, node, 0);
        Document = doc;
    }
    private ScrollViewer FormulaScroller(Image image, bool block)
    {
        var scroll = new ScrollViewer { Content = image, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = block ? new Thickness(0, 10, 0, 12) : new Thickness(0) };
        scroll.SetBinding(MaxWidthProperty, new Binding(nameof(ActualWidth)) { Source = this, Converter = new FormulaWidthConverter() });
        RenderedFormulaCount++; return scroll;
    }
    private void AddPlainText(FlowDocument doc, string text)
    {
        Paragraph? paragraph = null;
        Paragraph Current() { if (paragraph is null) { paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 12) }; doc.Blocks.Add(paragraph); } return paragraph; }
        foreach (var token in MessageMarkupTokenizer.Tokenize(text, recognizeTables: false))
        {
            if (token.Kind is not (MarkupKind.InlineMath or MarkupKind.DisplayMath)) { Current().Inlines.Add(new Run(token.Raw)); continue; }
            var block = token.Kind == MarkupKind.DisplayMath;
            var image = Formula(token.Text, block, ++formulas <= 64, fontSize: TextSize);
            if (image is null) { Current().Inlines.Add(new Run(token.Raw)); continue; }
            var scroll = FormulaScroller(image, block);
            if (block) { paragraph = null; doc.Blocks.Add(new BlockUIContainer(scroll)); }
            else Current().Inlines.Add(new InlineUIContainer(scroll) { BaselineAlignment = BaselineAlignment.Center });
        }
    }
    private void HeadingStyle(Paragraph p, int level)
    {
        p.FontFamily = new FontFamily("Microsoft YaHei UI"); p.FontSize = TextSize * (level == 1 ? 1.7 : level == 2 ? 1.4 : 1.15);
        p.FontWeight = FontWeights.SemiBold; p.Margin = new Thickness(0, TextSize * Leading * .55, 0, TextSize * Leading * .35); p.LineHeight = p.FontSize * Leading;
    }
    private void CodeStyle(Paragraph p)
    {
        p.FontFamily = new FontFamily("Cascadia Code, Consolas"); p.FontSize = TextSize * .82;
        p.LineHeight = TextSize * 1.45; p.Background = Color("Sidebar"); p.Padding = new Thickness(14); p.Margin = new Thickness(0, 4, 0, 12);
    }
    private void AddBlock(BlockCollection target, Markdig.Syntax.Block node, int depth)
    {
        if (depth > 24) { target.Add(new Paragraph(new Run("内容嵌套过深"))); return; }
        if (node is MathBlock math)
        {
            var source = math.Lines.ToString(); var image = Formula(source, true, ++formulas <= 64, fontSize: TextSize);
            if (image is null) target.Add(new Paragraph(new Run("$$" + source + "$$")));
            else
            {
                target.Add(new BlockUIContainer(FormulaScroller(image, true)));
            }
            return;
        }
        if (node is MdTable table) { AddTable(target, table, depth); return; }
        if (node is CodeBlock code) { var p = new Paragraph(new Run(code.Lines.ToString())); CodeStyle(p); target.Add(p); return; }
        if (node is ListBlock list)
        {
            var result = new System.Windows.Documents.List { MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc, Padding = new Thickness(26, 0, 0, 0), Margin = new Thickness(0, 0, 0, 10) };
            var first = ReferenceEquals(Piece?.Blocks.FirstOrDefault(), node) ? Piece.First : 0;
            var count = ReferenceEquals(Piece?.Blocks.FirstOrDefault(), node) ? Piece.Count : int.MaxValue;
            if (list.IsOrdered && int.TryParse(list.OrderedStart, out var number)) result.StartIndex = Math.Clamp(number + first, 1, int.MaxValue);
            foreach (var item in list.Skip(first).Take(count).OfType<ListItemBlock>())
            {
                var li = new ListItem(); foreach (var child in item) AddBlock(li.Blocks, child, depth + 1);
                if (li.Blocks.Count == 0) li.Blocks.Add(new Paragraph()); result.ListItems.Add(li);
            }
            target.Add(result); return;
        }
        if (node is QuoteBlock quote)
        {
            var section = new Section { BorderBrush = Color("Accent"), BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(16, 0, 0, 0), Foreground = Color("Muted"), Margin = new Thickness(0, 8, 0, 14) };
            foreach (var child in quote) AddBlock(section.Blocks, child, depth + 1); target.Add(section); return;
        }
        if (node is ThematicBreakBlock) { target.Add(new Paragraph { BorderBrush = Color("Line"), BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, 12, 0, 20), FontSize = 1 }); return; }
        if (node is LeafBlock leaf)
        {
            var p = new Paragraph { Margin = new Thickness(0, 0, 0, TextSize * .65) };
            if (node is HeadingBlock heading) HeadingStyle(p, heading.Level);
            AddInline(p.Inlines, leaf.Inline, depth + 1); target.Add(p); return;
        }
        if (node is ContainerBlock container) foreach (var child in container) AddBlock(target, child, depth + 1);
    }
    private void AddInline(InlineCollection target, ContainerInline? container, int depth)
    {
        if (container is null || depth > 32) return;
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline literal: target.Add(new Run(literal.Content.ToString())); break;
                case HtmlEntityInline entity: target.Add(new Run(entity.Transcoded.ToString())); break;
                case CodeInline code: target.Add(new Run(code.Content) { FontFamily = new FontFamily("Consolas"), FontSize = TextSize * .88, Background = Color("AccentSoft") }); break;
                case LineBreakInline: target.Add(new LineBreak()); break;
                case MathInline math:
                    var formula = Formula(math.Content.ToString(), math.DelimiterCount > 1, ++formulas <= 64, displayStyle: inTable, fontSize: TextSize);
                    if (formula is null) target.Add(new Run("$" + math.Content + "$"));
                    else
                    {
                        target.Add(new InlineUIContainer(FormulaScroller(formula, false)) { BaselineAlignment = BaselineAlignment.Center });
                    }
                    break;
                case TaskList task: target.Add(new Run(task.Checked ? "☑ " : "☐ ")); break;
                case LinkInline link:
                    if (link.IsImage) { target.Add(new Run("[图片：" + ReaderDocuments.InlineText(link) + "]") { Foreground = Color("Muted") }); break; }
                    var hyperlink = new Hyperlink { Foreground = Color("Accent"), ToolTip = link.Url };
                    AddInline(hyperlink.Inlines, link, depth + 1);
                    hyperlink.Click += (_, _) => { RequestedLink = link.Url; RaiseEvent(new RoutedEventArgs(LinkRequestedEvent, this)); };
                    target.Add(hyperlink); break;
                case AutolinkInline auto:
                    var a = new Hyperlink(new Run(auto.Url)) { Foreground = Color("Accent"), ToolTip = auto.Url };
                    a.Click += (_, _) => { RequestedLink = auto.IsEmail ? "mailto:" + auto.Url : auto.Url; RaiseEvent(new RoutedEventArgs(LinkRequestedEvent, this)); };
                    target.Add(a); break;
                case EmphasisInline emphasis:
                    var span = new Span();
                    if (emphasis.DelimiterChar == '~') span.TextDecorations = TextDecorations.Strikethrough;
                    else if (emphasis.DelimiterCount >= 2) span.FontWeight = FontWeights.Bold;
                    else span.FontStyle = FontStyles.Italic;
                    AddInline(span.Inlines, emphasis, depth + 1); target.Add(span); break;
                case ContainerInline child:
                    var other = new Span(); AddInline(other.Inlines, child, depth + 1); target.Add(other); break;
            }
        }
    }
    private void AddTable(BlockCollection target, MdTable table, int depth)
    {
        var result = new System.Windows.Documents.Table { CellSpacing = 0, BorderBrush = Color("Line"), BorderThickness = new Thickness(1), Margin = new Thickness(0, 6, 0, 12) };
        foreach (var _ in table.ColumnDefinitions) result.Columns.Add(new TableColumn());
        var group = new TableRowGroup(); result.RowGroups.Add(group);
        var rows = table.Cast<Markdig.Extensions.Tables.TableRow>().AsEnumerable();
        if (Piece is { First: > 0 } piece && ReferenceEquals(piece.Blocks.FirstOrDefault(), table)) rows = rows.Take(1).Concat(rows.Skip(piece.First).Take(piece.Count));
        foreach (var row in rows)
        {
            var r = new TableRow(); group.Rows.Add(r);
            if (row.IsHeader) { r.FontWeight = FontWeights.SemiBold; r.Background = Color("TableHeader"); }
            var column = 0;
            foreach (var cell in row.OfType<Markdig.Extensions.Tables.TableCell>())
            {
                var c = new TableCell { Padding = new Thickness(9), BorderBrush = Color("Line"), BorderThickness = new Thickness(0, 0, 1, 1), ColumnSpan = Math.Max(1, cell.ColumnSpan) };
                inTable = true;
                foreach (var child in cell) AddBlock(c.Blocks, child, depth + 1);
                inTable = false;
                c.TextAlignment = column < table.ColumnDefinitions.Count ? table.ColumnDefinitions[column].Alignment switch
                { Markdig.Extensions.Tables.TableColumnAlign.Center => TextAlignment.Center, Markdig.Extensions.Tables.TableColumnAlign.Right => TextAlignment.Right, _ => TextAlignment.Left } : TextAlignment.Left;
                r.Cells.Add(c); column++;
            }
        }
        target.Add(result);
    }
}
