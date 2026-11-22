using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using SkiaSharp;
using Svg;
using Svg.Skia;

namespace Reflection.Desktop;

internal static class SvgImageCodec
{
    internal const int MaxFileBytes = 2 * 1024 * 1024;
    private const int MaxElements = 10_000;
    private const int MaxDepth = 64;
    private static readonly string[] ReferenceAttributes =
        ["href", "fill", "stroke", "color", "stop-color", "flood-color", "clip-path", "mask", "filter",
            "marker", "marker-start", "marker-mid", "marker-end"];

    internal static BitmapSource Decode(Stream stream, int limit)
    {
        if (stream.Length > MaxFileBytes) throw new IOException("SVG 图片超过 2 MiB。");
        try
        {
            ValidateXml(stream);
            stream.Position = 0;
            using var reader = CreateReader(stream);
            var document = SvgDocument.Open<SvgDocument>(reader);
            ValidateReferences(document);
            // Explicit dimensions avoid the renderer including the viewBox origin in the image size.
            if (!document.ContainsAttribute("width") && document.ViewBox.Width > 0)
                document.Width = document.ViewBox.Width;
            if (!document.ContainsAttribute("height") && document.ViewBox.Height > 0)
                document.Height = document.ViewBox.Height;

            using var renderer = new SKSvg();
            var picture = renderer.FromSvgDocument(document) ?? throw new IOException("SVG 没有可显示的图形。");
            var bounds = picture.CullRect;
            if (!float.IsFinite(bounds.Width) || !float.IsFinite(bounds.Height) || bounds.Width <= 0 || bounds.Height <= 0)
                throw new IOException("SVG 图片尺寸无效。");

            var scale = Math.Min(1d, limit / (double)Math.Max(bounds.Width, bounds.Height));
            var width = Math.Max(1, (int)Math.Round(bounds.Width * scale));
            var height = Math.Max(1, (int)Math.Round(bounds.Height * scale));
            using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Transparent);
            canvas.Scale((float)scale);
            canvas.Translate(-bounds.Left, -bounds.Top);
            canvas.DrawPicture(picture);
            canvas.Flush();
            var result = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null,
                bitmap.GetPixels(), bitmap.ByteCount, bitmap.RowBytes);
            result.Freeze();
            return result;
        }
        catch (Exception ex) when (ex is XmlException or InvalidDataException or OverflowException or IndexOutOfRangeException)
        {
            throw new IOException("SVG 图片格式无效。", ex);
        }
    }

    private static XmlReader CreateReader(Stream stream) => XmlReader.Create(stream, new XmlReaderSettings
    {
        DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxFileBytes,
        IgnoreProcessingInstructions = true
    });

    private static void ValidateXml(Stream stream)
    {
        using var reader = CreateReader(stream);
        const string svgNamespace = "http://www.w3.org/2000/svg";
        if (reader.MoveToContent() != XmlNodeType.Element || reader.LocalName != "svg" || reader.NamespaceURI != svgNamespace)
            throw new IOException("文件缺少 SVG 根元素。");
        var count = 0;
        do
        {
            if (reader.NodeType != XmlNodeType.Element) continue;
            if (++count > MaxElements || reader.Depth >= MaxDepth)
                throw new IOException("SVG 图形结构过于复杂。");
            // Svg.Skia loads image and filter-image resources itself, including nested SVGs.
            if (reader.LocalName is "image" or "feImage" or "foreignObject" or "script")
                throw new IOException("SVG 包含嵌入图片、HTML 或脚本。");
            if (reader.HasAttributes)
            {
                while (reader.MoveToNextAttribute())
                {
                    if (reader.LocalName == "href" && !reader.Value.Trim().StartsWith('#'))
                        throw new IOException("SVG 只能引用图内资源。");
                }
                reader.MoveToElement();
            }
        } while (reader.Read());
    }

    private static void ValidateReferences(SvgDocument document)
    {
        var active = new HashSet<SvgElement>();
        var costs = new Dictionary<SvgElement, (int Nodes, int Depth)>();
        Measure(document, 1);

        // Count expanded references as well as XML nodes. Shared definitions can expand many times.
        (int Nodes, int Depth) Measure(SvgElement element, int depth)
        {
            if (depth > MaxDepth || !active.Add(element))
                throw new IOException("SVG 引用嵌套过深或形成循环。");
            if (!costs.TryGetValue(element, out var cost))
            {
                cost = (1, 1);
                foreach (var child in element.Children) Add(child);
                foreach (var name in ReferenceAttributes)
                {
                    if (!element.TryGetAttribute(name, out var value)) continue;
                    value = value.Trim();
                    var end = value.IndexOf(')');
                    if (value.StartsWith("url(", StringComparison.OrdinalIgnoreCase) && end > 4)
                        value = value[4..end].Trim().Trim('\'', '"');
                    else if (name != "href") continue;

                    if (!value.StartsWith('#')) throw new IOException("SVG 只能引用图内资源。");
                    if (document.GetElementById(value) is { } target) Add(target);
                }
                costs[element] = cost;
            }
            active.Remove(element);
            if (depth + cost.Depth - 1 > MaxDepth) throw new IOException("SVG 引用嵌套过深。");
            return cost;

            void Add(SvgElement child)
            {
                var childCost = Measure(child, depth + 1);
                cost = (cost.Nodes + childCost.Nodes, Math.Max(cost.Depth, 1 + childCost.Depth));
                if (cost.Nodes > MaxElements) throw new IOException("SVG 图形结构过于复杂。");
            }
        }
    }
}
