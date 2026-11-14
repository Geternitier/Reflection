using System.Text.RegularExpressions;
using Markdig.Extensions.Mathematics;
using Markdig.Syntax;

namespace Reflection.Core;

// The renderer resolves numbering in source order, independently of realized viewports.
public sealed partial class ReaderFormulaIndex
{
    public sealed record Entry(int Offset, string Source, bool Display);
    private static long nextId;
    public long Id { get; } = Interlocked.Increment(ref nextId);
    public IReadOnlyList<Entry> Entries { get; }

    private ReaderFormulaIndex(IEnumerable<Entry> entries) => Entries = entries.OrderBy(entry => entry.Offset).ToArray();

    public static ReaderFormulaIndex? FromMarkdown(MarkdownDocument document)
    {
        var entries = new List<Entry>();
        foreach (var block in document.Descendants<MathBlock>())
        {
            if (block.ClosingFencedCharCount == 0) continue;
            var source = block.Lines.ToString();
            if (AffectsNumbering(source)) entries.Add(new(block.Span.Start, source, true));
        }
        foreach (var inline in document.Descendants<MathInline>())
        {
            var source = inline.Content.ToString();
            if (AffectsNumbering(source)) entries.Add(new(inline.Span.Start, source, inline.DelimiterCount == 2));
        }
        return entries.Count == 0 ? null : new(entries);
    }

    internal static ReaderFormulaIndex? FromText(string text, List<(int Start, int End)> ranges)
    {
        var entries = new List<Entry>();
        foreach (var range in ranges)
        {
            if (!MathDelimiters.TryRead(text, range.Start, range.End, out var math)) continue;
            var source = text[math.ContentStart..math.ContentEnd];
            if (AffectsNumbering(source)) entries.Add(new(range.Start, source, math.Display));
        }
        return entries.Count == 0 ? null : new(entries);
    }

    public static bool AffectsNumbering(string source) => NumberedCommand().IsMatch(source);

    [GeneratedRegex(@"\\(?:tag|label)(?![a-zA-Z])|\\begin\s*\{(?:equation|align|alignat|gather|multline)\}")]
    private static partial Regex NumberedCommand();
}
