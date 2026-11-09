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
    }
}
