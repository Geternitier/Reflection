using CSharpMath.Atom;
using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Reflection.Core;
using SkiaSharp;

namespace Reflection.Desktop;

internal static partial class FormulaSource
{
    internal static bool WithinLimits(string source)
    {
        if (source.Length is 0 or > MathDelimiters.MaxFormulaLength || RestrictedCommand().IsMatch(source)) return false;
        var depth = 0;
        var columns = 0;
        for (var index = 0; index < source.Length; index++)
        {
            var character = source[index];
            if (character == '\\' && index + 1 < source.Length && !char.IsAsciiLetter(source[index + 1]))
            {
                index++;
                continue;
            }
            if (character == '{' && ++depth > 64 || character == '&' && ++columns > 128 ||
                character == '}' && --depth < 0) return false;
        }
        return depth == 0;
    }

    // CSharpMath 0.5.1 includes these suffixes in the command lookup. Separate
    // them from recognized control words when the combined command is unknown.
    internal static string ForCSharpMath(string source) => CommandWithSuffix().Replace(source, match =>
    {
        if (!match.Groups["word"].Success) return match.Value; // Escaped control symbol.
        var word = match.ValueSpan;
        var combined = source.AsSpan(match.Index, match.Length + 1);
        return LaTeXSettings.Commands.TryLookup(combined).Error is not null &&
            LaTeXSettings.Commands.TryLookup(word).Error is null ? match.Value + " " : match.Value;
    });

    internal static bool ValidSurface(double width, double height) => double.IsFinite(width) && double.IsFinite(height)
        && width is >= 1 and <= 12000 && height is >= 1 and <= 1200 && width * height * 4 <= 4_000_000;

    internal static FormulaRasterCache.Raster Rasterize(double width, double height, Action<SKCanvas> draw)
    {
        const int scale = 2;
        using var bitmap = new SKBitmap(new SKImageInfo((int)width * scale, (int)height * scale,
            SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(scale);
        draw(canvas);
        canvas.Flush();
        var image = BitmapSource.Create(bitmap.Width, bitmap.Height, 96 * scale, 96 * scale,
            PixelFormats.Pbgra32, null, bitmap.GetPixels(), bitmap.ByteCount, bitmap.RowBytes);
        image.Freeze();
        return new(image, width, height);
    }

    [GeneratedRegex(@"\\[^a-zA-Z@]|(?<word>\\[a-zA-Z@]+)(?=[=*'])")]
    private static partial Regex CommandWithSuffix();

    [GeneratedRegex(@"\\(?:def|gdef|edef|xdef|let|futurelet|newcommand|renewcommand|providecommand|newenvironment|renewenvironment|DeclareMathOperator|DeclarePairedDelimiter|DeclarePairedDelimiterX|DeclarePairedDelimiterXPP|newtagform|renewtagform|usetagform|input|include|includegraphics|href|url|require|color|textcolor|definecolor|rule|hspace|vspace|kern|mkern|raisebox|resizebox|scalebox)(?![a-zA-Z])")]
    private static partial Regex RestrictedCommand();
}
