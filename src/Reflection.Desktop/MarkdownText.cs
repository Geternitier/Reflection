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

// Markdown uses native document elements. Streaming repaints are throttled.
public partial class MarkdownText : RichTextBox
{
    static MarkdownText()
    {
        // The engine already implements the ring accent as \ocirc; expose its LaTeX name.
        CSharpMath.Atom.LaTeXSettings.CommandSymbols.Add(@"\mathring", new CSharpMath.Atom.Atoms.Accent("\u030A"));
    }
    public static readonly DependencyProperty SourceProperty = DependencyProperty.Register(nameof(Source), typeof(string), typeof(MarkdownText), new PropertyMetadata("", Changed));
    public string Source { get => (string)GetValue(SourceProperty); set => SetValue(SourceProperty, value); }
    public static readonly DependencyProperty IsStreamingProperty = DependencyProperty.Register(nameof(IsStreaming), typeof(bool), typeof(MarkdownText), new PropertyMetadata(false, StreamingChanged));
    public static readonly DependencyProperty FitShortLineProperty = DependencyProperty.Register(nameof(FitShortLine), typeof(bool), typeof(MarkdownText),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public bool IsStreaming { get => (bool)GetValue(IsStreamingProperty); set => SetValue(IsStreamingProperty, value); }
    public bool FitShortLine { get => (bool)GetValue(FitShortLineProperty); set => SetValue(FitShortLineProperty, value); }
    private readonly DispatcherTimer repaint = new() { Interval = TimeSpan.FromMilliseconds(120) };
    internal int FormulaCount { get; private protected set; }
    internal int FormulaFallbackCount { get; private protected set; }
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
        SetResourceReference(SelectionBrushProperty, "ToneE2DCCE");
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
    protected override Size MeasureOverride(Size constraint)
    {
        if (FitShortLine && Source is { Length: > 0 and <= 128 } text && text.AsSpan().IndexOfAny('\r', '\n') < 0
            && Document.Blocks.Count == 1 && Document.Blocks.FirstBlock is Paragraph paragraph
            && MeasureInlineWidth(paragraph.Inlines, VisualTreeHelper.GetDpi(this).PixelsPerDip) is { } width)
        {
            static double Horizontal(Thickness inset) =>
                (double.IsFinite(inset.Left) ? inset.Left : 0) + (double.IsFinite(inset.Right) ? inset.Right : 0);
            // Allow a small rounding reserve so a glyph at the right edge does not wrap.
            var desired = Math.Ceiling(width + Horizontal(paragraph.Margin) + Horizontal(paragraph.Padding) + Horizontal(paragraph.BorderThickness)) + 2;
            constraint.Width = Math.Min(constraint.Width, desired);
        }
        return base.MeasureOverride(constraint);
    }

    private static double? MeasureInlineWidth(InlineCollection inlines, double pixelsPerDip)
    {
        var width = 0d;
        foreach (var inline in inlines)
        {
            if (inline is Span span && MeasureInlineWidth(span.Inlines, pixelsPerDip) is { } spanWidth)
                width += spanWidth;
            else if (inline is Run run)
            {
                if (run.Text.Length == 0) continue;
                var typeface = new Typeface(run.FontFamily, run.FontStyle, run.FontWeight, run.FontStretch);
                width += new FormattedText(run.Text, CultureInfo.CurrentUICulture, run.FlowDirection,
                    typeface, run.FontSize, run.Foreground, pixelsPerDip).WidthIncludingTrailingWhitespace;
            }
            else return null;
        }
        return width;
    }

    internal virtual void Render()
    {
        if (rendered == Source && renderedTheme == ThemeManager.Revision) return;
        var document = new FlowDocument { PagePadding = new Thickness(0), FontSize = LayoutFontSize,
            FontFamily = BodyFont, Foreground = Brush("Ink"), LineStackingStrategy = LineStackingStrategy.MaxHeight };
        ResetLayout(Source);
        foreach (var block in Reflection.Core.ReaderDocuments.ParseMarkdown(Source)) AddBlock(document.Blocks, block, 0);
        Document = document; rendered = Source; renderedTheme = ThemeManager.Revision;
    }

    private static SKColor ToSkiaColor(Color color) => new(color.R, color.G, color.B, color.A);
    protected static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);
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
