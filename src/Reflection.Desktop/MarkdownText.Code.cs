using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Reflection.Desktop;

public partial class MarkdownText
{
    private static readonly FontFamily CodeFont = new("Cascadia Code, Consolas");
    private static readonly Regex CStyleTokens = CodeTokens("""(?<comment>//.*|/\*.*?(?:\*/|$))|""");
    private static readonly Regex HashTokens = CodeTokens("(?<comment>#.*)|");
    private static readonly Regex SqlTokens = CodeTokens("(?<comment>--.*)|");
    private static readonly Regex JsonTokens = CodeTokens("");

    private static Regex CodeTokens(string comments) => new(comments +
        """(?<string>"(?:\\.|[^"\\])*"|'(?:\\.|[^'\\])*')|(?<number>\b(?:0[xX][0-9a-fA-F]+|\d+(?:\.\d+)?)\b)|(?<word>\b[A-Za-z_]\w*\b)""",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly HashSet<string> CodeKeywords = new((
        "abstract async await as bool break case catch class const continue def default do elif else enum except export extends false False fi finally float fn for foreach from function if import in int interface internal is let match namespace new None null object override pass private protected public raise return self static string struct super switch then this throw true True try type typeof using var void while with yield"
        ).Split(' '), StringComparer.Ordinal);
    private static readonly HashSet<string> SqlKeywords = new((
        "add all alter and as asc begin between by case count create cross delete desc distinct drop else end exists from full group having in inner insert into is join left like limit not null on or order outer over right select set table then union update values view when where with"
        ).Split(' '), StringComparer.OrdinalIgnoreCase);

    protected void AddCode(BlockCollection target, string source, string? language, int firstLine = 1, bool allowDiagram = true)
    {
        var fontSize = LayoutFontSize * .82;
        var section = new Section { FontFamily = CodeFont, FontSize = fontSize,
            LineHeight = LayoutFontSize * 1.45, Background = Brush("Sidebar"), Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 4, 0, 12) };
        if (allowDiagram && TryAddDiagram(section, source, language)) { target.Add(section); return; }
        var syntax = language?.ToLowerInvariant() switch
        {
            "c" or "cpp" or "c++" or "cs" or "csharp" or "java" or "js" or "javascript" or "ts" or "typescript" or
                "go" or "rs" or "rust" or "swift" or "kt" or "kotlin" => "c",
            "py" or "python" or "sh" or "bash" or "powershell" or "ps1" or "rb" or "ruby" or "yml" or "yaml" => "hash",
            "json" or "jsonc" => "json",
            "sql" => "sql",
            _ => null
        };
        var lines = source.ReplaceLineEndings("\n").Split('\n', 301);
        var count = Math.Min(300, lines.Length);
        var numbers = new List { MarkerStyle = TextMarkerStyle.Decimal, StartIndex = firstLine, MarkerOffset = fontSize * .5,
            Padding = new Thickness((Math.Min((long)int.MaxValue, (long)firstLine + count - 1)
                .ToString(CultureInfo.InvariantCulture).Length + 2) * fontSize * .7 + 3, 0, 0, 0), Margin = new Thickness(0) };
        for (var index = 0; index < count; index++)
        {
            var line = new Paragraph { Margin = new Thickness(0), Foreground = Brush("Ink") };
            AddCodeLine(line, lines[index], syntax);
            numbers.ListItems.Add(new ListItem(line));
        }
        section.Blocks.Add(numbers);
        if (lines.Length > count) section.Blocks.Add(new Paragraph(new Run(lines[count])) { Margin = new Thickness(0) });
        target.Add(section);
    }

    private static void AddCodeLine(Paragraph line, string source, string? syntax)
    {
        if (syntax is null || source.Length > 4096) { line.Inlines.Add(new Run(source)); return; }
        var position = 0;
        var tokens = syntax switch { "c" => CStyleTokens, "hash" => HashTokens, "sql" => SqlTokens, _ => JsonTokens };
        foreach (Match token in tokens.Matches(source))
        {
            var color = token.Groups["comment"].Success ? "Muted"
                : token.Groups["string"].Success ? "Tone48624E"
                : token.Groups["number"].Success ? "Tone836D58"
                : (syntax == "sql" ? SqlKeywords : CodeKeywords).Contains(token.Value) ? "AccentHover" : null;
            if (color is null) continue;
            if (position < token.Index) line.Inlines.Add(new Run(source[position..token.Index]));
            line.Inlines.Add(new Run(token.Value) { Foreground = Brush(color) });
            position = token.Index + token.Length;
        }
        if (position < source.Length || position == 0) line.Inlines.Add(new Run(source[position..]));
    }
}
