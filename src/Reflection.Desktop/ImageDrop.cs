using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using Reflection.Core;

namespace Reflection.Desktop;

internal static class ImageDrop
{
    internal static bool IsImageFile(string path) => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp";
    internal static bool CanRead(IDataObject data) =>
        data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files && files.Any(IsImageFile)
        || data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is BitmapSource;

    internal static IEnumerable<ChatImage> Read(IDataObject data)
    {
        if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files)
        {
            foreach (var path in files.Where(IsImageFile)) yield return ImageCodec.FromFile(path);
        }
        else if (data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
            yield return ImageCodec.Encode(bitmap);
    }
}
