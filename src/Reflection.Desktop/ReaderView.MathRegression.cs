using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media.Imaging;
using Reflection.Core;

namespace Reflection.Desktop;

public partial class ReaderView
{
    internal async Task VerifyMathCompatibilityAsync()
    {
        string[] samples =
        [
            @"\lt", @"\gt", @"a\not< b", @"\lvert x\rvert", @"\lVert x\rVert",
            @"\left\lt x\right\gt", @"\left\lvert x\right\rvert", @"\left\lVert x\right\rVert",
            @"\left(a\middle|b\right)", @"\big(x\big)", @"\bigl(x\bigr)", @"\Bigl(x\Bigr)",
            @"\biggl(x\biggr)", @"\Biggl(x\Biggr)", @"\dfrac{a}{b}", @"\tfrac{a}{b}", @"\cfrac{a}{b}",
            @"\genfrac{(}{)}{0pt}{}{a}{b}", @"\dbinom{n}{k}", @"\tbinom{n}{k}",
            @"\overset{a}{=}", @"\underset{a}{=}", @"\stackrel{a}{=}", @"\sum_{\substack{i=1\\j=2}}x_{ij}",
            @"\xrightarrow{f}", @"\xleftarrow{f}", @"\overbrace{x}^{n}", @"\underbrace{x}_{n}", @"\boxed{x}",
            @"\cancel{x}", @"\bcancel{x}", @"\xcancel{x}", @"\boldsymbol{\alpha}", @"\mathscr{F}",
            @"\operatorname*{argmax}_{x}", @"\mathop{\mathrm{lim}}\limits_{x\to0}",
            @"x\pmod{n}", @"x\bmod n", @"x\mod n",
            @"\begin{align}a&=b\\c&=d\end{align}", @"\begin{align*}a&=b\\c&=d\end{align*}",
            @"\begin{alignat}{2}a&=b&c&=d\end{alignat}", @"\begin{alignedat}{2}a&=b&c&=d\end{alignedat}",
            @"\begin{gathered}a=b\\c=d\end{gathered}", @"\begin{multline}a+b\\+c\end{multline}",
            @"\begin{equation}a=b\end{equation}", @"\begin{smallmatrix}a&b\\c&d\end{smallmatrix}",
            @"\begin{dcases}x&x>0\\-x&x<0\end{dcases}", @"\ce{SO4^2-}", @"\ce{2H2 + O2 -> 2H2O}",
            @"\SI{9.8}{m/s^2}", @"\qty{1e3}{\kilo\gram\per\second\squared}", @"\qty(\frac{1}{2})",
            @"\bra{\psi}", @"\ket{\psi}", @"\dv{f}{x}", @"\pdv[2]{f}{x}", @"2π", @"2α",
            @"\idotsint f", @"\unicode{8721}", @"\coloneqq", @"\vcentcolon", @"\smash{\frac{a}{b}}",
            @"x+\phantom{x}", @"x+\hphantom{x}", @"x+\vphantom{x}",
            @"\overrightarrow{AB}", @"\overleftrightarrow{AB}", @"\underleftarrow{AB}",
            @"\mathclap{x}", @"\sideset{_a^b}{_c^d}\sum", @"\pu{9.8 m/s^2}", @"\braket{\phi|\psi}", @"\abs{x}"
        ];
        foreach (var extension in new[] { "md", "txt" })
        {
            foreach (var formula in samples)
            {
                var views = await RenderMathFixtureAsync(extension, "$$\n" + formula + "\n$$");
                if (views.Sum(view => view.RenderedFormulaCount) != 1 ||
                    views.SelectMany(FormulaBitmaps).SingleOrDefault() is not { } image || !HasInk(image))
                    throw new InvalidOperationException($"{extension} formula {formula}: {string.Join(", ", views.Select(view => view.FormulaError))}");
            }

            var delimiters = await RenderMathFixtureAsync(extension, @"$ x+y $ $x$2 $2π$ \( x+y \) \[ x+y \] $$ x+y $$");
            if (delimiters.Sum(view => view.RenderedFormulaCount) != 6)
                throw new InvalidOperationException("A supported formula boundary did not render.");

            var dfrac = await RenderMathFixtureAsync(extension, @"$$\dfrac{a}{b}$$");
            var tfrac = await RenderMathFixtureAsync(extension, @"$$\tfrac{a}{b}$$");
            if (dfrac.SelectMany(FormulaBitmaps).Single().PixelHeight <= tfrac.SelectMany(FormulaBitmaps).Single().PixelHeight)
                throw new InvalidOperationException("Display and text fraction styles were identical.");
            var arrow = await RenderMathFixtureAsync(extension, @"$$\xrightarrow{f}$$");
            var wideArrow = await RenderMathFixtureAsync(extension, @"$$\xrightarrow{abcdef}$$");
            if (arrow.SelectMany(FormulaBitmaps).Single().PixelWidth >= wideArrow.SelectMany(FormulaBitmaps).Single().PixelWidth)
                throw new InvalidOperationException("A labeled arrow did not stretch to its annotation.");

            var unclosed = await RenderMathFixtureAsync(extension, "$$\nx+y");
            if (unclosed.Sum(view => view.RenderedFormulaCount) != 0)
                throw new InvalidOperationException("An unclosed block was typeset.");

            var small = await ReferenceWidthAsync(extension, "7");
            var large = await ReferenceWidthAsync(extension, "12345");
            if (large <= small) throw new InvalidOperationException("Equation references reused a different document's cached number.");

            var source = new string('a', ReaderDocuments.PieceCharacters - 2) + " $x+1$ " +
                new string('b', ReaderDocuments.PieceCharacters + 20);
            if ((await RenderMathFixtureAsync(extension, source)).Sum(view => view.RenderedFormulaCount) != 1)
                throw new InvalidOperationException("A formula was lost at a virtualized piece boundary.");

            var rejected = await RenderMathFixtureAsync(extension, @"$\newcommand{\x}{x}$ $\includegraphics{a.png}$ $\eqref{missing}$");
            if (rejected.Sum(view => view.FormulaFallbackCount) != 3)
                throw new InvalidOperationException("Restricted formulas or unresolved references did not fall back.");
        }
    }

    private static IEnumerable<BitmapSource> FormulaBitmaps(ReadingText view)
    {
        foreach (var block in view.Document.Blocks)
        {
            if (block is BlockUIContainer { Child: ScrollViewer display } && FormulaBitmap(display) is { } displayImage)
                yield return displayImage;
            if (block is not Paragraph paragraph) continue;
            foreach (var inline in paragraph.Inlines.OfType<InlineUIContainer>())
                if (inline.Child is ScrollViewer scroll && FormulaBitmap(scroll) is { } inlineImage) yield return inlineImage;
        }
    }

    private static BitmapSource? FormulaBitmap(ScrollViewer scroll) =>
        (scroll.Content is ContentControl host ? host.Content as Image : scroll.Content as Image)?.Source as BitmapSource;

    private static bool HasInk(BitmapSource source)
    {
        var stride = source.PixelWidth * 4;
        var pixels = new byte[stride * source.PixelHeight];
        source.CopyPixels(pixels, stride, 0);
        for (var index = 3; index < pixels.Length; index += 4)
            if (pixels[index] != 0) return true;
        return false;
    }

    private static async Task<int> ReferenceWidthAsync(string extension, string number)
    {
        var views = await RenderMathFixtureAsync(extension, "\\eqref{eq:x}\n\n" +
            string.Concat(Enumerable.Repeat("段落分隔。\n\n", 450)) +
            "\n\n$$\nx=1\\tag{" + number + "}\\label{eq:x}\n$$");
        var line = (Paragraph)views[0].Document.Blocks.FirstBlock!;
        var scroll = (ScrollViewer)((InlineUIContainer)line.Inlines.FirstInline!).Child;
        return FormulaBitmap(scroll)?.PixelWidth
            ?? throw new InvalidOperationException("The forward equation reference did not resolve: " + views[0].FormulaError);
    }

    private static async Task<ReadingText[]> RenderMathFixtureAsync(string extension, string source)
    {
        var parsed = ReaderDocuments.Parse("formulas." + extension, source);
        var views = parsed.Pieces.Select(piece => new ReadingText { Piece = piece }).ToArray();
        foreach (var view in views) view.Render();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        while (views.Any(view => view.PendingFormulaCount > 0)) await Task.Delay(15, timeout.Token);
        return views;
    }
}
