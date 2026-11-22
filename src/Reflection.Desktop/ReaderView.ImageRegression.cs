using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Reflection.Desktop;

public partial class ReaderView
{
    internal async Task VerifyLocalImagesAsync(string output)
    {
        var fixture = Path.Combine(Owner.VM.Store.Root, "reader-images");
        var figures = Directory.CreateDirectory(Path.Combine(fixture, "figures"));
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRectangle(Brushes.LightSteelBlue, null, new Rect(0, 0, 720, 280));
            context.DrawEllipse(Brushes.IndianRed, null, new Point(360, 140), 100, 100);
        }
        var bitmap = new RenderTargetBitmap(720, 280, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(drawing);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bitmap));
        using (var file = File.Create(Path.Combine(figures.FullName, "test image.png"))) png.Save(file);
        var path = Path.Combine(fixture, "图片引用.md");
        await File.WriteAllTextAsync(path, """
            # Markdown 图片引用

            ![本地大图](figures/test%20image.png)

            行内引用 ![参考式图片][figure] 后面的文字。

            ![找不到的图](figures/missing.png)

            ![远程占位](https://example.invalid/image.png)

            [figure]: <figures/test image.png>
            """);
        var variants = Owner.VM.Images.GeneratedVariants;
        await OpenDocumentAsync(path); UpdateLayout();
        ReadingImage[] images = [];
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(5))
        {
            images = MainWindow.Descendants<ReadingImage>(Pages).ToArray();
            if (images.Count(image => image.Bitmap is not null) == 2 && images.Any(image => image.ToolTip is not null)) break;
            await Task.Delay(20);
        }
        if (images.Length != 3 || images.Count(image => image.Bitmap is { IsFrozen: true }) != 2
            || images.Single(image => AutomationProperties.GetName(image) == "找不到的图").ToolTip is null
            || Owner.VM.Images.GeneratedVariants != variants)
            throw new InvalidOperationException("Markdown images did not decode locally with a fallback: " +
                string.Join("; ", images.Select(image => $"{AutomationProperties.GetName(image)} loaded={image.IsLoaded} bitmap={image.Bitmap is not null} error={image.ToolTip}")));
        var documentText = string.Concat(MainWindow.Descendants<ReadingText>(Pages).Select(view =>
            new TextRange(view.Document.ContentStart, view.Document.ContentEnd).Text));
        if (!documentText.Contains("[图片：远程占位]") || !documentText.Contains("后面的文字"))
            throw new InvalidOperationException("Markdown image rendering lost surrounding text or the remote placeholder");
        var large = images.Single(image => AutomationProperties.GetName(image) == "本地大图");
        var inline = images.Single(image => AutomationProperties.GetName(image) == "参考式图片");
        UpdateLayout();
        if (large.ActualWidth <= 300 || inline.ActualWidth is <= 0 or > 240)
            throw new InvalidOperationException("Standalone and inline Markdown images did not use their layout limits");
        App.Capture(Owner, Path.Combine(output, "reader-local-images.png"));
        var width = Owner.Width;
        try
        {
            Owner.Width = Owner.MinWidth; UpdateLayout();
            if (large.ActualWidth <= 0 || large.ActualWidth >= 600 || large.ActualWidth >= Pages.ActualWidth)
                throw new InvalidOperationException("Markdown image did not fit the narrow reading column");
            App.Capture(Owner, Path.Combine(output, "reader-local-images-narrow.png"));
        }
        finally { Owner.Width = width; }
        var second = Path.Combine(fixture, "另一篇.md");
        await File.WriteAllTextAsync(second, "# 另一篇文档\n\n只有正文。");
        await OpenDocumentAsync(second); UpdateLayout();
        if (images.Any(image => image.Bitmap is not null))
            throw new InvalidOperationException("Unloaded reader images retained their display bitmaps");

        await VerifySvgImagesAsync(figures.FullName, output);
    }

    private async Task VerifySvgImagesAsync(string folder, string output)
    {
        await File.WriteAllTextAsync(Path.Combine(folder, "图 形.SVG"), """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="10 20 240 120">
              <style>.shape { fill: #e04020; }</style>
              <defs><path id="shape" class="shape" d="M20 30 H240 V130 H20 Z"/></defs>
              <use href="#shape"/>
            </svg>
            """);
        await File.WriteAllTextAsync(Path.Combine(folder, "invalid.svg"), "<svg");
        await File.WriteAllTextAsync(Path.Combine(folder, "external.svg"), """
            <svg xmlns="http://www.w3.org/2000/svg" width="240" height="120">
              <image href="https://example.invalid/external.png" width="240" height="120"/>
            </svg>
            """);
        await File.WriteAllTextAsync(Path.Combine(folder, "dtd.svg"), """
            <!DOCTYPE svg SYSTEM "https://example.invalid/external.dtd">
            <svg xmlns="http://www.w3.org/2000/svg" width="240" height="120"/>
            """);
        var path = Path.Combine(folder, "svg.md");
        await File.WriteAllTextAsync(path, """
            ![流程图](%E5%9B%BE%20%E5%BD%A2.SVG)

            ![损坏的图](invalid.svg) ![含外部资源的图](external.svg) ![含 DTD 的图](dtd.svg)

            ![远程 SVG](https://example.invalid/diagram.svg)
            """);
        await OpenDocumentAsync(path);
        UpdateLayout();
        var images = MainWindow.Descendants<ReadingImage>(Pages).ToArray();
        var svg = images.Single(image => AutomationProperties.GetName(image) == "流程图");
        var failures = images.Where(image => image != svg).ToArray();
        var timeout = Stopwatch.StartNew();
        while (timeout.Elapsed < TimeSpan.FromSeconds(5) && (svg.Bitmap is null || failures.Any(image => image.ToolTip is null)))
            await Task.Delay(20);
        if (svg.Bitmap is not { IsFrozen: true, PixelWidth: 240, PixelHeight: 120 } bitmap || failures.Length != 3
            || failures.Any(image => image.Bitmap is not null || image.ToolTip is null))
            throw new InvalidOperationException("SVG rendering or fallback failed: " + svg.ToolTip);

        var pixel = new byte[4];
        bitmap.CopyPixels(new Int32Rect(120, 60, 1, 1), pixel, 4, 0);
        if (!pixel.SequenceEqual(new byte[] { 0x20, 0x40, 0xe0, 0xff }))
            throw new InvalidOperationException("SVG local reference, CSS fill or channel order failed");
        bitmap.CopyPixels(new Int32Rect(0, 0, 1, 1), pixel, 4, 0);
        if (pixel[3] != 0) throw new InvalidOperationException("SVG transparency or viewBox offset was lost");
        var text = string.Concat(MainWindow.Descendants<ReadingText>(Pages).Select(view =>
            new TextRange(view.Document.ContentStart, view.Document.ContentEnd).Text));
        if (!text.Contains("[图片：远程 SVG]")) throw new InvalidOperationException("Remote SVG fallback was lost");
        App.Capture(Owner, Path.Combine(output, "reader-svg.png"));

        var width = Owner.Width;
        var theme = ThemeManager.IsDark ? "dark" : "light";
        try
        {
            Owner.Width = Owner.MinWidth;
            ThemeManager.Apply("dark");
            UpdateLayout();
            timeout.Restart();
            do
            {
                await Task.Delay(20);
                svg = MainWindow.Descendants<ReadingImage>(Pages).Single(image => AutomationProperties.GetName(image) == "流程图");
            } while (svg.Bitmap is null && timeout.Elapsed < TimeSpan.FromSeconds(5));
            UpdateLayout();
            if (!ReferenceEquals(svg.Bitmap, bitmap) || svg.ActualWidth <= 0 || svg.ActualWidth >= Pages.ActualWidth)
                throw new InvalidOperationException("SVG did not reuse its bitmap or fit the narrow dark reading column");
            App.Capture(Owner, Path.Combine(output, "reader-svg-dark-narrow.png"));
        }
        finally
        {
            ThemeManager.Apply(theme);
            Owner.Width = width;
        }

        var cycle = Path.Combine(folder, "cycle.svg");
        await File.WriteAllTextAsync(cycle, """
            <svg xmlns="http://www.w3.org/2000/svg" width="240" height="120">
              <g id="cycle"><use href="#cycle"/></g>
            </svg>
            """);
        try { await Owner.VM.Images.DisplayLocalAsync(cycle, 360, CancellationToken.None); }
        catch (IOException ex) when (ex.Message.Contains("循环")) { return; }
        throw new InvalidOperationException("Recursive SVG reference was accepted");
    }
}
