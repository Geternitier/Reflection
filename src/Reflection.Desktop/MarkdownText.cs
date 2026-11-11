using Reflection.Core;
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
    private CancellationTokenSource? formulaWork;
    internal int PendingFormulaCount { get; private set; }
    protected bool FormulaRenderInvalidated { get; private set; }

    static MarkdownText()
    {
        // The engine already implements the ring accent as \ocirc; expose its LaTeX name.
        CSharpMath.Atom.LaTeXSettings.CommandSymbols.Add(@"\mathring", new CSharpMath.Atom.Atoms.Accent("\u030A"));
        var symbols = CSharpMath.Atom.LaTeXSettings.CommandSymbols;
        symbols.Add(@"\lt", new CSharpMath.Atom.Atoms.Relation("<"));
        symbols.Add(@"\gt", new CSharpMath.Atom.Atoms.Relation(">"));
        var delimiters = CSharpMath.Atom.LaTeXSettings.BoundaryDelimiters;
        delimiters.Add(@"\lt", new CSharpMath.Atom.Boundary("\u2329"));
        delimiters.Add(@"\gt", new CSharpMath.Atom.Boundary("\u232A"));
        delimiters.Add(@"\lvert", @"\rvert", new CSharpMath.Atom.Boundary("|"));
        delimiters.Add(@"\lVert", @"\rVert", new CSharpMath.Atom.Boundary("\u2016"));
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
        Unloaded += (_, _) => { ThemeManager.Changed -= OnThemeChanged; repaint.Stop(); CancelFormulaWork(); };
        IsVisibleChanged += (_, _) => { if (IsVisible) Render(); else CancelFormulaWork(); };
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
        if (!FormulaRenderInvalidated && rendered == Source && renderedTheme == ThemeManager.Revision) return;
        var document = new FlowDocument { PagePadding = new Thickness(0), FontSize = LayoutFontSize,
            FontFamily = BodyFont, Foreground = Brush("Ink"), LineStackingStrategy = LineStackingStrategy.MaxHeight };
        var parsed = ReaderDocuments.ParseMarkdown(Source);
        ResetLayout(Source, ReaderFormulaIndex.AffectsNumbering(Source) ? ReaderFormulaIndex.FromMarkdown(parsed) : null);
        foreach (var block in parsed) AddBlock(document.Blocks, block, 0);
        Document = document; rendered = Source; renderedTheme = ThemeManager.Revision;
    }

    private static SKColor ToSkiaColor(Color color) => new(color.R, color.G, color.B, color.A);
    protected static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);
    private static Image FormulaImage(BitmapSource image, double width, double height, string source)
    {
        var formula = new Image { Source = image, Width = width, Height = height, Stretch = Stretch.None, Focusable = false, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(formula, "LaTeX：" + source); return formula;
    }
    private void CancelFormulaWork()
    {
        if (formulaWork is null) return;
        formulaWork.Cancel();
        formulaWork.Dispose();
        formulaWork = null;
        FormulaRenderInvalidated = PendingFormulaCount > 0;
        PendingFormulaCount = 0;
    }

    protected void ResetFormulaBudget()
    {
        CancelFormulaWork();
        FormulaRenderInvalidated = false;
        formulaPixels = 0;
        FormulaError = "";
    }

    protected FrameworkElement? Formula(string source, string raw, bool block, bool withinBudget,
        bool displayStyle = false, double fontSize = 14, int offset = 0)
    {
        source = source.Trim();
        if (!withinBudget || !FormulaSource.WithinLimits(source)) return null;
        var display = block || displayStyle;
        var contextual = FormulaCompatibility.HasReferences(source) || ReaderFormulaIndex.AffectsNumbering(source);
        var index = contextual ? formulaIndex : null;
        var key = new FormulaRasterCache.Key(source, block, display, fontSize, ThemeManager.Revision,
            index?.Id ?? 0, contextual ? offset : 0);
        if (FormulaRasterCache.TryGet(key, out var cached)) return UseRaster(cached, source);
        var color = ((SolidColorBrush)Application.Current.FindResource("Ink")).Color;
        var size = block ? fontSize + 1 : fontSize;
        string? error = null;
        if (!contextual)
        {
            try
            {
                var painter = new MathPainter
                {
                    LaTeX = source, FontSize = (float)size,
                    LineStyle = display ? CSharpMath.Atom.LineStyle.Display : CSharpMath.Atom.LineStyle.Text,
                    TextColor = ToSkiaColor(color), DisplayErrorInline = false
                };
                error = painter.ErrorMessage;
                if (string.IsNullOrEmpty(error))
                {
                    var bounds = painter.Measure();
                    var width = Math.Ceiling(bounds.Width + 6);
                    var height = Math.Ceiling(bounds.Height + 6);
                    if (!FormulaSource.ValidSurface(width, height) || formulaPixels + width * height * 4 > 12_000_000) return null;
                    var raster = FormulaSource.Rasterize(width, height,
                        canvas => painter.Draw(canvas, 3 - bounds.Left, 3 - bounds.Top));
                    FormulaRasterCache.Add(key, raster);
                    return UseRaster(raster, source);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { error = ex.Message; }
        }
        if (!contextual && !FormulaCompatibility.Supports(source))
        {
            FormulaError += source + ": " + error + "\n";
            return null;
        }

        // Keep the original text visible while the offline renderer works.
        var host = new ContentControl
        {
            Focusable = false,
            Content = new TextBlock { Text = raw, TextWrapping = TextWrapping.Wrap }
        };
        var token = (formulaWork ??= new CancellationTokenSource()).Token;
        PendingFormulaCount++;
        _ = CompleteFormulaAsync(host, key, source, display, size, color, index, offset, token);
        return host;
    }

    private Image? UseRaster(FormulaRasterCache.Raster raster, string source)
    {
        if (formulaPixels + raster.Pixels > 12_000_000) return null;
        formulaPixels += raster.Pixels;
        return FormulaImage(raster.Image, raster.Width, raster.Height, source);
    }

    private async Task CompleteFormulaAsync(ContentControl host, FormulaRasterCache.Key key, string source, bool display,
        double size, Color color, ReaderFormulaIndex? index, int offset, CancellationToken token)
    {
        var (raster, error) = await FormulaCompatibility.RenderAsync(source, display, size, color, index, offset, token);
        if (token.IsCancellationRequested) return;
        PendingFormulaCount--;
        if (raster is not null && UseRaster(raster, source) is { } image)
        {
            FormulaRasterCache.Add(key, raster);
            host.Content = image;
        }
        else
        {
            FormulaFallbackCount++;
            if (error is not null) FormulaError += source + ": " + error + "\n";
        }
    }
}

internal sealed class FormulaWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is double width ? Math.Max(48, (width - 24) / (parameter is int columns ? columns : 1) - (parameter is int ? 24 : 0)) : 600d;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
