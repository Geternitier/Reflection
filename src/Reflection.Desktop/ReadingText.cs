using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Data;
using System.Windows.Media;
using Markdig.Syntax;
using Markdig.Extensions.Footnotes;
using Markdig.Syntax.Inlines;
using Reflection.Core;

namespace Reflection.Desktop;

public sealed class ReadingText : MarkdownText
{
    private static readonly FontFamily SerifFont = new("Georgia, SimSun");
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
    internal int? RequestedFootnoteOffset { get; private set; }
    private ReadingPiece? renderedPiece;
    private (double, double, bool, int) renderedStyle;
    protected override FontFamily BodyFont => Serif ? SerifFont : SansSerifFont;
    protected override double LayoutFontSize => TextSize;
    protected override double LayoutLeading => Leading;
    protected override ReadingPiece? RenderPiece => Piece;
    private readonly List<ReadingImage> localImages = [];
    private CancellationTokenSource? imageLoad;
    internal int RenderedFormulaCount => FormulaCount - FormulaFallbackCount;
    public ReadingText()
    {
        Loaded += (_, _) => StartImages();
        Unloaded += (_, _) => StopImages();
        IsVisibleChanged += (_, _) => { if (IsVisible) StartImages(); else StopImages(); };
    }

    private void StartImages()
    {
        if (imageLoad is not null || localImages.Count == 0 || !IsLoaded || !IsVisible
            || Window.GetWindow(this) is not MainWindow owner) return;
        imageLoad = new CancellationTokenSource();
        foreach (var image in localImages) _ = image.LoadAsync(owner.VM.Images, imageLoad.Token);
    }

    private void StopImages()
    {
        if (imageLoad is null) return;
        imageLoad.Cancel(); imageLoad.Dispose(); imageLoad = null;
        foreach (var image in localImages) image.Release();
    }
    private static void Change(DependencyObject obj, DependencyPropertyChangedEventArgs e) { var view = (ReadingText)obj; if (view.IsVisible) view.Render(); }
    internal override void Render()
    {
        var piece = Piece;
        var style = (TextSize, Leading, Serif, ThemeManager.Revision);
        if (piece is null) { StopImages(); return; }
        if (!FormulaRenderInvalidated && ReferenceEquals(piece, renderedPiece) && style == renderedStyle) return;
        StopImages(); localImages.Clear();
        renderedPiece = piece; renderedStyle = style; ResetLayout(piece.MarkdownSource, piece.Formulas);
        var doc = new FlowDocument { PagePadding = new Thickness(0), FontSize = TextSize,
            FontFamily = BodyFont, Foreground = Brush("Ink"),
            LineHeight = TextSize * Leading, LineStackingStrategy = LineStackingStrategy.MaxHeight };
        if (piece.Text is { } text)
        {
            if (!piece.Code && piece.Heading == 0) AddPlainText(doc, text.TrimEnd('\r', '\n'), piece.Offset);
            else if (piece.Code) AddCode(doc.Blocks, text.EndsWith('\n') ? text[..^1].TrimEnd('\r') : text, piece.CodeLanguage, piece.CodeLine, allowDiagram: false);
            else
            {
                var paragraph = new Paragraph(new Run(text.TrimEnd('\r', '\n'))) { Margin = new Thickness(0, 0, 0, 12) };
                if (piece.Heading > 0)
                {
                    HeadingStyle(paragraph, 1.7);
                    var line = TextSize * Leading;
                    paragraph.Margin = new Thickness(0, line * .55, 0, line * .35);
                }
                doc.Blocks.Add(paragraph);
            }
        }
        else foreach (var node in piece.Blocks) AddBlock(doc.Blocks, node, 0);
        Document = doc;
        StartImages();
    }
    private void AddPlainText(FlowDocument doc, string text, int offset)
    {
        Paragraph? paragraph = null;
        Paragraph Current() { if (paragraph is null) { paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 12) }; doc.Blocks.Add(paragraph); } return paragraph; }
        foreach (var token in MessageMarkupTokenizer.Tokenize(text, recognizeTables: false))
        {
            var start = offset;
            offset += token.Raw.Length;
            if (token.Kind is not (MarkupKind.InlineMath or MarkupKind.DisplayMath)) { Current().Inlines.Add(new Run(token.Raw)); continue; }
            var block = token.Kind == MarkupKind.DisplayMath;
            var image = Formula(token.Text, token.Raw, block, ++FormulaCount <= MathDelimiters.MaxFormulasPerRender, fontSize: TextSize, offset: start);
            if (image is null) { FormulaFallbackCount++; Current().Inlines.Add(new Run(token.Raw)); continue; }
            var scroll = FormulaScroller(image, block);
            if (block) { paragraph = null; doc.Blocks.Add(new BlockUIContainer(scroll)); }
            else Current().Inlines.Add(new InlineUIContainer(scroll) { BaselineAlignment = BaselineAlignment.Center });
        }
    }
    protected override bool CanNavigateLink(string address) => true;

    protected override void NavigateLink(string address)
    {
        RequestedFootnoteOffset = null;
        RequestedLink = address;
        RaiseEvent(new RoutedEventArgs(LinkRequestedEvent, this));
    }

    protected override void NavigateFootnote(Footnote note, bool back)
    {
        RequestedLink = null;
        RequestedFootnoteOffset = back ? note.Links.FirstOrDefault(link => !link.IsBackLink)?.Span.Start : note.Span.Start;
        RaiseEvent(new RoutedEventArgs(LinkRequestedEvent, this));
    }

    protected override void AddImage(InlineCollection target, LinkInline link, bool allowLink)
    {
        var alt = ReaderDocuments.InlineText(link);
        var placeholder = ImagePlaceholder(link);
        var path = localImages.Count < 12 ? ReaderDocuments.ResolveImagePath(Piece?.SourcePath, link.Url) : null;
        if (path is null)
        {
            base.AddImage(target, link, allowLink);
            return;
        }
        var standalone = !InTable && link.Parent is { ParentBlock: ParagraphBlock } parent
            && ReferenceEquals(parent.FirstChild, link) && ReferenceEquals(parent.LastChild, link);
        var holder = new ReadingImage(path, alt, placeholder, standalone ? 1280 : 360)
        {
            MaxHeight = standalone ? 560 : InTable ? 110 : 160
        };
        if (standalone)
            holder.SetBinding(MaxWidthProperty, new Binding(nameof(ActualWidth)) { Source = this, Converter = new FormulaWidthConverter() });
        else holder.MaxWidth = InTable ? 140 : 240;
        localImages.Add(holder);
        target.Add(new InlineUIContainer(holder) { BaselineAlignment = standalone ? BaselineAlignment.Baseline : BaselineAlignment.Center });
    }
}
