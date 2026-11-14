using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml;
using System.Xml.Linq;
using Jint;
using Reflection.Core;
using SkiaSharp;
using Svg.Skia;

namespace Reflection.Desktop;

internal static partial class FormulaCompatibility
{
    private const int MaxSvgCharacters = 1_000_000;
    private static readonly SemaphoreSlim worker = new(1);
    private static readonly Lazy<Engine> engine = new(CreateEngine, LazyThreadSafetyMode.PublicationOnly);
    private static readonly ConditionalWeakTable<ReaderFormulaIndex, Numbering> documentNumbers = new();
    private static readonly string[] ChineseLanguage = ["zh-Hans"];
    private static int queued;

    internal static bool ContainsHan(string source)
    {
        foreach (var rune in source.EnumerateRunes())
            if (IsHan(rune.Value)) return true;
        return false;
    }

    private static bool IsHan(int value) => value is
        0x3005 or 0x3007 or >= 0x3400 and <= 0x4DBF or >= 0x4E00 and <= 0x9FFF or
        >= 0xF900 and <= 0xFAFF or >= 0x20000 and <= 0x2FA1F or >= 0x30000 and <= 0x323AF;

    private static bool IsChineseGlyph(int value) => IsHan(value) ||
        value is >= 0x3000 and <= 0x303F or >= 0xFF01 and <= 0xFF60;

    internal static bool HasReferences(string source) => References().IsMatch(source);

    internal static bool Supports(string source) => CompatibleCommand().IsMatch(source);

    internal static Task<(FormulaRasterCache.Raster? Raster, string? Error)> RenderAsync(string source, bool display,
        double fontSize, Color color, ReaderFormulaIndex? index, int offset, CancellationToken token)
    {
        if (Interlocked.Increment(ref queued) > 256)
        {
            Interlocked.Decrement(ref queued);
            return Task.FromResult<(FormulaRasterCache.Raster?, string?)>((null, "Formula queue is full."));
        }

        return Task.Run<(FormulaRasterCache.Raster? Raster, string? Error)>(async () =>
        {
            try
            {
                await worker.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    var context = index is null ? null : documentNumbers.GetValue(index, key => BuildNumbering(key, token));
                    token.ThrowIfCancellationRequested();
                    var start = context?.Offsets.GetValueOrDefault(offset) ?? 0;
                    var labels = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
                    foreach (Match reference in References().Matches(source))
                    {
                        var name = reference.Groups[1].Value;
                        if (context is null || !context.Labels.TryGetValue(name, out var label))
                            return (null, "Unresolved equation reference: " + name);
                        labels[name] = label;
                    }
                    var svg = engine.Value.Invoke("renderFormula", source, display, start, JsonSerializer.Serialize(labels),
                        $"#{color.R:X2}{color.G:X2}{color.B:X2}").AsString();
                    return (Rasterize(svg, fontSize), null);
                }
                finally { worker.Release(); }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return (null, null); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return (null, ex.Message); }
            finally { Interlocked.Decrement(ref queued); }
        });
    }

    private static Engine CreateEngine()
    {
        using var stream = typeof(FormulaCompatibility).Assembly.GetManifestResourceStream("Reflection.MathJax.js")
            ?? throw new InvalidOperationException("The offline formula engine is missing.");
        using var reader = new StreamReader(stream);
        var interpreter = new Engine(options => options.TimeoutInterval(TimeSpan.FromSeconds(2))
            .LimitMemory(128_000_000).LimitRecursion(384).MaxStatements(2_000_000));
        try { interpreter.Execute(reader.ReadToEnd()); return interpreter; }
        catch { interpreter.Dispose(); throw; }
    }

    private sealed record Numbering(Dictionary<int, int> Offsets, Dictionary<string, JsonElement> Labels);

    private static Numbering BuildNumbering(ReaderFormulaIndex index, CancellationToken token)
    {
        var offsets = new Dictionary<int, int>();
        var labels = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var counter = 0;
        foreach (var entry in index.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (!FormulaSource.WithinLimits(entry.Source)) continue;
            offsets[entry.Offset] = counter;
            try
            {
                var result = engine.Value.Invoke("indexFormula", entry.Source, entry.Display, counter).AsString();
                using var metadata = JsonDocument.Parse(result);
                counter = metadata.RootElement.GetProperty("Counter").GetInt32();
                foreach (var label in metadata.RootElement.GetProperty("Labels").EnumerateObject())
                    labels.TryAdd(label.Name, label.Value.Clone());
            }
            catch (JintException) { /* The view reports the error on the individual formula. */ }
        }
        return new Numbering(offsets, labels);
    }

    private static FormulaRasterCache.Raster? Rasterize(string svg, double fontSize)
    {
        if (svg.Length > MaxSvgCharacters) return null;
        using var reader = XmlReader.Create(new StringReader(svg), new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = MaxSvgCharacters
        });
        var root = XDocument.Load(reader).Root ?? throw new InvalidDataException("Empty formula image.");
        foreach (var element in root.DescendantsAndSelf())
        {
            // MathJax links are local equation references. The bitmap only needs their contents.
            if (element.Name.LocalName == "a")
            {
                element.Name = element.Name.Namespace + "g";
                element.Attributes().Where(attribute => attribute.Name.LocalName == "href").Remove();
            }
            if (element.Name.NamespaceName != "http://www.w3.org/2000/svg" ||
                element.Name.LocalName is not ("svg" or "g" or "path" or "rect" or "line" or "polygon" or
                    "polyline" or "circle" or "ellipse" or "text" or "title") ||
                element.Attributes().Any(attribute => attribute.Name.LocalName is "href" or "src" ||
                    attribute.Value.Contains("url(", StringComparison.OrdinalIgnoreCase)))
                return null;
        }
        var scale = fontSize / 16;
        if (!double.TryParse((string?)root.Attribute("width"), CultureInfo.InvariantCulture, out var sourceWidth) ||
            !double.TryParse((string?)root.Attribute("height"), CultureInfo.InvariantCulture, out var sourceHeight))
            return null;
        var width = Math.Ceiling(sourceWidth * scale + 6);
        var height = Math.Ceiling(sourceHeight * scale + 6);
        if (!FormulaSource.ValidSurface(width, height)) return null;
        if (!ApplyChineseFonts(root)) return null;
        using var renderer = new SKSvg();
        var picture = renderer.FromSvg(root.ToString(SaveOptions.DisableFormatting));
        if (picture is null) return null;
        return FormulaSource.Rasterize(width, height, canvas =>
        {
            canvas.Translate(3, 3);
            canvas.Scale((float)scale);
            canvas.DrawPicture(picture);
        });
    }

    private static bool ApplyChineseFonts(XElement root)
    {
        // MathJax positions each glyph missing from its math font as a separate SVG text node.
        // Resolve those glyphs against installed fonts before handing the SVG to Svg.Skia.
        var families = new Dictionary<int, string>();
        foreach (var text in root.Descendants(root.Name.Namespace + "text"))
        {
            var value = text.Value;
            if (value.Length == 0 || !Rune.TryGetRuneAt(value, 0, out var rune) || rune.Utf16SequenceLength != value.Length ||
                !IsChineseGlyph(rune.Value)) continue;
            if (!families.TryGetValue(rune.Value, out var family))
            {
                using var typeface = SKFontManager.Default.MatchCharacter("Microsoft YaHei", ChineseLanguage, rune.Value);
                if (typeface is null) return false;
                families[rune.Value] = family = typeface.FamilyName;
            }
            text.SetAttributeValue("font-family", family);
        }
        return true;
    }

    [GeneratedRegex(@"\\(?:lt|gt|lvert|rvert|lVert|rVert|not|middle|[bB]ig{1,2}[lrm]?|dfrac|tfrac|cfrac|genfrac|dbinom|tbinom|idotsint|unicode|mathscr|boldsymbol|overset|underset|stackrel|xrightarrow|xleftarrow|overbrace|underbrace|overrightarrow|overleftrightarrow|underleftarrow|cancel|bcancel|xcancel|boxed|operatorname|tag|label|ref|eqref|ce|pu|SI|qty|bra|ket|braket|abs|dv|pdv|coloneqq|vcentcolon|substack|smash|phantom|hphantom|vphantom|mathop|pmod|mod|bmod|mathclap|sideset)(?![a-zA-Z])|\\begin\s*\{(?:equation\*?|align\*?|alignat\*?|alignedat|gather\*?|gathered|multline\*?|smallmatrix|dcases)\}")]
    private static partial Regex CompatibleCommand();

    [GeneratedRegex(@"(?<!\\)\\(?:eqref|ref)\s*\{([^{}]+)\}")]
    private static partial Regex References();
}
