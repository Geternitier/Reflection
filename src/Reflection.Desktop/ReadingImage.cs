using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Reflection.Desktop;

internal sealed class ReadingImage : Grid
{
    private readonly Image image = new()
    {
        Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly,
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
    };
    private readonly TextBlock fallback = new() { TextWrapping = TextWrapping.Wrap };
    private readonly string path;
    private readonly int decodeSize;
    internal BitmapSource? Bitmap => image.Source as BitmapSource;

    internal ReadingImage(string path, string alt, string placeholder, int decodeSize)
    {
        this.path = path;
        this.decodeSize = decodeSize;
        fallback.Text = placeholder;
        fallback.SetResourceReference(TextBlock.ForegroundProperty, "Muted");
        AutomationProperties.SetName(this, string.IsNullOrWhiteSpace(alt) ? "文档图片" : alt);
        Children.Add(fallback); Children.Add(image);
    }

    internal async Task LoadAsync(ImagePipeline pipeline, CancellationToken ct)
    {
        try
        {
            var bitmap = await pipeline.DisplayLocalAsync(path, decodeSize, ct);
            if (ct.IsCancellationRequested) return;
            image.Source = bitmap;
            fallback.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException or FormatException)
        {
            if (!ct.IsCancellationRequested) ToolTip = "图片无法显示：" + ex.Message;
        }
    }

    internal void Release()
    {
        image.Source = null;
        fallback.Visibility = Visibility.Visible;
        ToolTip = null;
    }
}
