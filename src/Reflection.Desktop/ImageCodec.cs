using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Reflection.Core;

namespace Reflection.Desktop;

internal static class ImageCodec
{
    public static ChatImage FromFile(string path)
    {
        if (new FileInfo(path).Length > 20_000_000) throw new ArgumentException("请选择小于 20 MB 的图片。");
        using var stream = File.OpenRead(path);
        return Encode(Decode(stream, 1280));
    }
    public static ChatImage FromBase64(ChatImage image)
    {
        using var stream = new MemoryStream(Convert.FromBase64String(image.Data));
        return Encode(Decode(stream, 1280));
    }
    internal static BitmapImage Decode(Stream stream, int limit)
    {
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnDemand).Frames[0];
        var width = frame.PixelWidth; var height = frame.PixelHeight;
        if ((long)width * height > 80_000_000 || width > 32768 || height > 32768)
            throw new ArgumentException("图片尺寸过大，请先缩小图片。");
        stream.Position = 0;
        var bitmap = new BitmapImage();
        bitmap.BeginInit(); bitmap.StreamSource = stream; bitmap.CacheOption = BitmapCacheOption.OnLoad;
        if (width >= height) bitmap.DecodePixelWidth = Math.Min(limit, width);
        else bitmap.DecodePixelHeight = Math.Min(limit, height);
        bitmap.EndInit(); bitmap.Freeze(); return bitmap;
    }
    public static ChatImage Encode(BitmapSource source)
    {
        var scale = Math.Min(1, 1280d / Math.Max(source.PixelWidth, source.PixelHeight));
        var width = Math.Max(1, (int)Math.Round(source.PixelWidth * scale));
        var height = Math.Max(1, (int)Math.Round(source.PixelHeight * scale));
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x11)), null, new Rect(0, 0, width, height));
            context.DrawImage(source, new Rect(0, 0, width, height));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new JpegBitmapEncoder { QualityLevel = 86 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        var data = Convert.ToBase64String(stream.ToArray());
        if (data.Length > 1_500_000) throw new ArgumentException("图片压缩后仍然过大。");
        return new() { Mime = "image/jpeg", Data = data };
    }
}
