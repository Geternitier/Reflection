using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using CSharpMath.SkiaSharp;
using SkiaSharp;
using System.Windows.Media.Imaging;
using System.Windows.Data;
using System.Globalization;
using System.Windows.Threading;

namespace Reflection.Desktop;

// No HTML, external navigation or executable TeX. Streaming repaints are throttled.
public class MarkdownText : RichTextBox
{
    static MarkdownText()
    {
        // The engine already implements the ring accent as \ocirc; expose its LaTeX name.
        CSharpMath.Atom.LaTeXSettings.CommandSymbols.Add(@"\mathring", new CSharpMath.Atom.Atoms.Accent("\u030A"));
    }
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(nameof(Source), typeof(string), typeof(MarkdownText), new PropertyMetadata("", Changed));
    public string Source { get => (string)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public static readonly DependencyProperty IsStreamingProperty = DependencyProperty.Register(nameof(IsStreaming), typeof(bool), typeof(MarkdownText), new PropertyMetadata(false, StreamingChanged));
    public bool IsStreaming { get => (bool)GetValue(IsStreamingProperty); set => SetValue(IsStreamingProperty, value); }
    private readonly DispatcherTimer repaint = new() { Interval = TimeSpan.FromMilliseconds(120) };
    internal int FormulaCount { get; private set; }
    internal int FormulaFallbackCount { get; private set; }
    internal string FormulaError { get; private set; } = "";
    private long formulaPixels;
    private string? rendered;
    private int renderedTheme = -1;
    internal int TableCount { get; private set; }
    public MarkdownText()
    {
        repaint.Tick += (_, _) => { repaint.Stop(); if (IsVisible) Render(); };
        IsReadOnly = true; IsDocumentEnabled = true; BorderThickness = new Thickness(0); Background = Brushes.Transparent;
        ContextMenu = null; Padding = new Thickness(0);
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled; HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        Loaded += (_, _) => { ThemeManager.Changed += OnThemeChanged; Render(); };
        Unloaded += (_, _) => { ThemeManager.Changed -= OnThemeChanged; repaint.Stop(); };
        IsVisibleChanged += (_, _) => { if (IsVisible) Render(); };
    }
    private void OnThemeChanged(object? sender, EventArgs e) { rendered = null; if (IsVisible) Render(); }
    private static void Changed(DependencyObject obj, DependencyPropertyChangedEventArgs e)
    {
        var view = (MarkdownText)obj; if (!view.IsVisible) return;
        if (view.IsStreaming) { if (!view.repaint.IsEnabled) view.repaint.Start(); }
        else view.Render();
    }
    private static void StreamingChanged(DependencyObject obj, DependencyPropertyChangedEventArgs e)
    {
        var view = (MarkdownText)obj;
        if (!(bool)e.NewValue) { view.repaint.Stop(); if (view.IsVisible) view.Render(); }
    }
    internal virtual void Render()
    {
        if (rendered == Source && renderedTheme == ThemeManager.Revision) return;
        var document = new FlowDocument { PagePadding = new Thickness(0), FontSize = 14, FontFamily = FontFamily, Foreground = (Brush)Application.Current.FindResource("Ink") };
        TableCount = 0; FormulaCount = 0; FormulaFallbackCount = 0; FormulaError = ""; formulaPixels = 0;
        Paragraph? current = null;
        Paragraph Paragraph() { if (current is null) { current = new Paragraph { Margin = new Thickness(0, 0, 0, 10), LineStackingStrategy = LineStackingStrategy.MaxHeight }; document.Blocks.Add(current); } return current; }
        void Text(string text)
        {
            var lines = text.ReplaceLineEndings("\n").Split('\n');
            for (var i = 0; i < lines.Length; i++)
            {
                if (i > 0) current = null;
                if (lines[i].Length == 0) continue;
                var content = lines[i]; var newLine = current is null; var paragraph = Paragraph();
                var heading = newLine ? Regex.Match(content, @"^(#{1,4})\s+(.+)$") : Match.Empty;
                if (heading.Success) { content = heading.Groups[2].Value; paragraph.FontSize = 23 - heading.Groups[1].Length * 2; paragraph.FontWeight = FontWeights.SemiBold; paragraph.Margin = new Thickness(0, 12, 0, 8); }
                else if (newLine && (content.StartsWith("- ") || content.StartsWith("* "))) content = "•  " + content[2..];
                else if (newLine && content.StartsWith("> ")) { content = content[2..]; paragraph.BorderThickness = new Thickness(3, 0, 0, 0); paragraph.BorderBrush = (Brush)Application.Current.FindResource("Accent"); paragraph.Padding = new Thickness(12, 0, 0, 0); }
                var position = 0;
                foreach (Match match in Regex.Matches(content, @"\*\*[^*]+\*\*|__[^_]+__|(?<!\*)\*(?![\s*])(?:[^*\n]*?[^\s*])\*(?!\*)|(?<!\w)_[^_\n]+_(?!\w)"))
                {
                    paragraph.Inlines.Add(new Run(content[position..match.Index]));
                    paragraph.Inlines.Add(match.Value.StartsWith("**") || match.Value.StartsWith("__") ? (Inline)new Bold(new Run(match.Value[2..^2])) : new Italic(new Run(match.Value[1..^1]))); position = match.Index + match.Length;
                }
                paragraph.Inlines.Add(new Run(content[position..]));
            }
        }
        foreach (var token in MessageMarkupTokenizer.Tokenize(Source))
        {
            if (token.Kind == MarkupKind.Table && MarkdownTableParser.TryRead(token.Raw, 0, out var table, out _) && table is not null)
            { current = null; document.Blocks.Add(BuildTable(table)); TableCount++; continue; }
            if (token.Kind == MarkupKind.Text) { Text(token.Text); continue; }
            if (token.Kind == MarkupKind.CodeBlock)
            {
                current = null;
                document.Blocks.Add(new Paragraph(new Run(token.Text.TrimEnd('\r', '\n'))) { FontFamily = new FontFamily("Cascadia Code, Consolas"), FontSize = 13, Background = (Brush)Application.Current.FindResource("Sidebar"), Padding = new Thickness(12), Margin = new Thickness(0, 8, 0, 14) }); continue;
            }
            if (token.Kind == MarkupKind.InlineCode)
            { Paragraph().Inlines.Add(new Run(token.Text) { FontFamily = new FontFamily("Consolas"), Background = (Brush)Application.Current.FindResource("AccentSoft") }); continue; }
            var block = token.Kind == MarkupKind.DisplayMath;
            var formula = Formula(token.Text, block, ++FormulaCount <= 64);
            if (formula is null) { FormulaFallbackCount++; Text(token.Raw); continue; }
            if (block)
            {
                current = null;
                var scroll = new ScrollViewer { Content = formula, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(8, 10, 8, 14), MaxHeight = 600 };
                scroll.SetBinding(MaxWidthProperty, new Binding(nameof(ActualWidth)) { Source = this, Converter = new FormulaWidthConverter() });
                document.Blocks.Add(new BlockUIContainer(scroll) { Margin = new Thickness(0, 6, 0, 14) });
            }
            else
            {
                var inline = new ScrollViewer { Content = formula, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
                inline.SetBinding(MaxWidthProperty, new Binding(nameof(ActualWidth)) { Source = this, Converter = new FormulaWidthConverter() });
                Paragraph().Inlines.Add(new InlineUIContainer(inline) { BaselineAlignment = BaselineAlignment.Center });
            }
        }
        Document = document; rendered = Source; renderedTheme = ThemeManager.Revision;
    }
    private static SKColor ToSkiaColor(Color color) => new(color.R, color.G, color.B, color.A);
    private static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);
    private Table BuildTable(MarkdownTableData data)
    {
        var table = new Table { CellSpacing = 0, BorderBrush = Brush("Line"), BorderThickness = new Thickness(1), Margin = new Thickness(0, 8, 0, 16) };
        for (var i = 0; i < data.Headers.Length; i++) table.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
        var group = new TableRowGroup(); table.RowGroups.Add(group);
        void Row(string[] values, bool header)
        {
            var row = new TableRow(); group.Rows.Add(row);
            if (header) { row.Background = Brush("TableHeader"); row.FontWeight = FontWeights.SemiBold; }
            for (var column = 0; column < values.Length; column++)
            {
                var paragraph = new Paragraph { Margin = new Thickness(0), LineStackingStrategy = LineStackingStrategy.MaxHeight,
                    TextAlignment = data.Alignments[column] switch { MarkdownColumnAlignment.Center => TextAlignment.Center, MarkdownColumnAlignment.Right => TextAlignment.Right, _ => TextAlignment.Left } };
                AppendCell(paragraph, values[column], data.Headers.Length);
                row.Cells.Add(new TableCell(paragraph) { Padding = new Thickness(10, 9, 10, 9), BorderBrush = Brush("Line"), BorderThickness = new Thickness(0, 0, column < values.Length - 1 ? 1 : 0, 1) });
            }
        }
        Row(data.Headers, true); foreach (var row in data.Rows) Row(row, false);
        foreach (var cell in group.Rows.Last().Cells) cell.BorderThickness = new Thickness(0, 0, cell.BorderThickness.Right, 0);
        return table;
    }
    private void AppendCell(Paragraph paragraph, string text, int columns)
    {
        void Plain(string content)
        {
            var position = 0;
            foreach (Match bold in Regex.Matches(content, @"\*\*[^*]+\*\*|__[^_]+__|(?<!\*)\*(?![\s*])(?:[^*\n]*?[^\s*])\*(?!\*)|(?<!\w)_[^_\n]+_(?!\w)"))
            { paragraph.Inlines.Add(new Run(content[position..bold.Index])); paragraph.Inlines.Add(bold.Value.StartsWith("**") || bold.Value.StartsWith("__") ? (Inline)new Bold(new Run(bold.Value[2..^2])) : new Italic(new Run(bold.Value[1..^1]))); position = bold.Index + bold.Length; }
            paragraph.Inlines.Add(new Run(content[position..]));
        }
        foreach (var token in MessageMarkupTokenizer.Tokenize(text, recognizeTables: false))
        {
            if (token.Kind == MarkupKind.InlineCode) { paragraph.Inlines.Add(new Run(token.Text) { FontFamily = new FontFamily("Consolas"), FontSize = 13, Background = Brush("AccentSoft") }); continue; }
            if (token.Kind is MarkupKind.InlineMath or MarkupKind.DisplayMath)
            {
                var formula = Formula(token.Text, false, ++FormulaCount <= 64, displayStyle: true);
                if (formula is null) { FormulaFallbackCount++; Plain(token.Raw); continue; }
                var scroll = new ScrollViewer { Content = formula, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
                scroll.SetBinding(MaxWidthProperty, new Binding(nameof(ActualWidth)) { Source = this, Converter = new FormulaWidthConverter(), ConverterParameter = columns });
                paragraph.Inlines.Add(new InlineUIContainer(scroll) { BaselineAlignment = BaselineAlignment.Center });
            }
            else Plain(token.Raw);
        }
    }
    private static Image FormulaImage(BitmapSource image, double width, double height, string source)
    {
        var formula = new Image { Source = image, Width = width, Height = height, Stretch = Stretch.None, Focusable = false, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(formula, "LaTeX：" + source); return formula;
    }
    protected void ResetFormulaBudget() { formulaPixels = 0; FormulaError = ""; }
    protected Image? Formula(string source, bool block, bool withinBudget, bool displayStyle = false, double fontSize = 14)
    {
        if (!withinBudget || source.Length > 4096 || source.Length == 0) return null;
        var key = new FormulaRasterCache.Key(source, block, block || displayStyle, fontSize, ThemeManager.Revision);
        if (FormulaRasterCache.TryGet(key, out var cached))
        {
            var cost = (long)(cached.Width * cached.Height * 4);
            if (formulaPixels + cost > 12_000_000) return null;
            formulaPixels += cost; return FormulaImage(cached.Image, cached.Width, cached.Height, source);
        }
        var depth = 0;
        foreach (var c in source) { if (c == '{' && ++depth > 64) return null; if (c == '}') depth--; }
        // Do not accept user macros, external resources, arbitrary dimensions or huge arrays.
        if (Regex.IsMatch(source, @"\\(?:def|gdef|edef|xdef|newcommand|renewcommand|input|include|includegraphics|href|url|color|textcolor|definecolor|rule|hspace|vspace|kern|mkern|raisebox|resizebox|scalebox)\b") || source.Count(c => c == '&') > 128) return null;
        try
        {
            var painter = new MathPainter
            {
                LaTeX = source, FontSize = (float)(block ? fontSize + 1 : fontSize),
                LineStyle = block || displayStyle ? CSharpMath.Atom.LineStyle.Display : CSharpMath.Atom.LineStyle.Text,
                TextColor = ToSkiaColor(((SolidColorBrush)Application.Current.FindResource("Ink")).Color), DisplayErrorInline = false
            };
            if (painter.ErrorMessage is { Length: > 0 } error) { FormulaError += source + ": " + error + "\n"; return null; }
            var bounds = painter.Measure();
            var width = Math.Ceiling(bounds.Width + 6); var height = Math.Ceiling(bounds.Height + 6);
            if (!double.IsFinite(width) || !double.IsFinite(height) || width < 1 || height < 1 || width > 12000 || height > 1200) return null;
            // Two device pixels per DIP; bound both a single surface and the whole message.
            const int scale = 2;
            var pixels = (long)(width * scale) * (long)(height * scale);
            if (pixels > 4_000_000 || formulaPixels + pixels > 12_000_000) return null;
            using var bitmap = new SKBitmap(new SKImageInfo((int)width * scale, (int)height * scale, SKColorType.Bgra8888, SKAlphaType.Premul));
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Transparent); canvas.Scale(scale);
            painter.Draw(canvas, 3 - bounds.Left, 3 - bounds.Top); canvas.Flush();
            var image = BitmapSource.Create(bitmap.Width, bitmap.Height, 96 * scale, 96 * scale, PixelFormats.Pbgra32, null, bitmap.GetPixels(), bitmap.ByteCount, bitmap.RowBytes);
            image.Freeze(); formulaPixels += pixels;
            FormulaRasterCache.Add(key, new(image, width, height));
            return FormulaImage(image, width, height, source);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { FormulaError += ex.Message + "\n"; return null; }
    }
}

internal sealed class FormulaWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is double width ? Math.Max(48, (width - 24) / (parameter is int columns ? columns : 1) - (parameter is int ? 24 : 0)) : 600d;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
