using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Reflection.Core;

namespace Reflection.Desktop;

internal static class ImageCodec
{
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
    public static ChatImage Encode(BitmapSource source, int limit = 1280, bool png = false)
    {
        var scale = Math.Min(1, (double)limit / Math.Max(source.PixelWidth, source.PixelHeight));
        var width = Math.Max(1, (int)Math.Round(source.PixelWidth * scale));
        var height = Math.Max(1, (int)Math.Round(source.PixelHeight * scale));
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            if (!png) context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x11)), null, new Rect(0, 0, width, height));
            context.DrawImage(source, new Rect(0, 0, width, height));
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        BitmapEncoder encoder = png ? new PngBitmapEncoder() : new JpegBitmapEncoder { QualityLevel = 86 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        var data = Convert.ToBase64String(stream.ToArray());
        if (data.Length > 1_500_000)
        {
            if (png && limit > 480) return Encode(source, (int)(limit * .8) / 4 * 4, true);
            throw new ArgumentException("图片压缩后仍然过大。");
        }
        return new() { Mime = png ? "image/png" : "image/jpeg", Data = data };
    }
}
