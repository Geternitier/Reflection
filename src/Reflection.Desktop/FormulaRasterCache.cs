using System.Windows.Media.Imaging;

namespace Reflection.Desktop;

// Shared frozen rasters survive container recycling. A strict LRU bounds retained pixels.
internal static class FormulaRasterCache
{
    internal readonly record struct Key(string Source, bool Block, bool Display, double Size, int Theme, long NumberingScope = 0, int Offset = 0);
    internal sealed record Raster(BitmapSource Image, double Width, double Height)
    { public long Pixels => (long)Image.PixelWidth * Image.PixelHeight; }
    private static readonly Dictionary<Key, LinkedListNode<(Key Key, Raster Raster)>> entries = [];
    private static readonly LinkedList<(Key Key, Raster Raster)> recent = new();
    private static long pixels;
    internal static void Clear() { entries.Clear(); recent.Clear(); pixels = 0; }
    internal static bool TryGet(Key key, out Raster raster)
    {
        if (!entries.TryGetValue(key, out var node)) { raster = null!; return false; }
        recent.Remove(node); recent.AddFirst(node); raster = node.Value.Raster; return true;
    }
    internal static void Add(Key key, Raster raster)
    {
        if (raster.Pixels > 6_000_000 || entries.ContainsKey(key)) return;
        while (recent.Last is { } tail && (pixels + raster.Pixels > 6_000_000 || entries.Count >= 256))
        { pixels -= tail.Value.Raster.Pixels; entries.Remove(tail.Value.Key); recent.RemoveLast(); }
        entries.Add(key, recent.AddFirst((key, raster))); pixels += raster.Pixels;
    }
}
