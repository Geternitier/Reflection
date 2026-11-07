using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Reflection.Desktop;

internal static class ImageDrop
{
    internal static bool IsImageFile(string path) => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp";
    internal static bool CanRead(IDataObject data) =>
        data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files && files.Any(IsImageFile)
        || data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is BitmapSource;

    internal static IReadOnlyList<ImageInput> Capture(IDataObject data, int maximum)
    {
        if (maximum <= 0) return [];
        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files)
        {
            var paths = files.Where(IsImageFile).Take(maximum).Select(path => new ImageInput(path, null)).ToArray();
            if (paths.Length > 0) return paths;
        }
        if (data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is BitmapSource source)
        {
            var pixels = (long)source.PixelWidth * source.PixelHeight;
            if (pixels > 24_000_000 || pixels * Math.Max(4, (source.Format.BitsPerPixel + 7) / 8) > 96_000_000)
                throw new ArgumentException("剪贴板图片尺寸过大，请保存为文件后选择。");
            var frozen = source.IsFrozen ? source : source.CloneCurrentValue();
            if (!frozen.IsFrozen) frozen.Freeze();
            return [new(null, frozen)];
        }
        return [];
    }
}
