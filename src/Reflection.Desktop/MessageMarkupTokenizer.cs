using Reflection.Core;

namespace Reflection.Desktop;

internal enum MarkupKind { Text, InlineCode, CodeBlock, InlineMath, DisplayMath, Table }
internal sealed record MarkupToken(MarkupKind Kind, string Text, string Raw);

// Code is consumed before math, and raw text is retained for every token.
internal static class MessageMarkupTokenizer
{
    internal static IReadOnlyList<MarkupToken> Tokenize(string source, bool recognizeTables = true)
    {
        var result = new List<MarkupToken>();
        var plain = 0; var i = 0;
        void Emit(int end, MarkupKind kind, int contentStart, int contentEnd)
        {
            if (i > plain) result.Add(new(MarkupKind.Text, source[plain..i], source[plain..i]));
            result.Add(new(kind, source[contentStart..contentEnd], source[i..end]));
            i = end; plain = i;
        }
        while (i < source.Length)
        {
            var c = source[i];
            // Fences only start at the beginning of a line (up to three spaces).
            var lineStart = i == 0 || source[i - 1] == '\n';
            if (lineStart)
            {
                var marker = i;
                while (marker < source.Length && marker - i < 3 && source[marker] == ' ') marker++;
                if (marker < source.Length && source[marker] is '`' or '~')
                {
                    var count = 0; while (marker + count < source.Length && source[marker + count] == source[marker]) count++;
                    if (count >= 3)
                    {
                        var firstEnd = source.IndexOf('\n', marker + count);
                        var end = firstEnd < 0 ? source.Length : firstEnd + 1;
                        var bodyStart = end; var bodyEnd = source.Length;
                        while (end < source.Length)
                        {
                            var eol = source.IndexOf('\n', end); if (eol < 0) eol = source.Length;
                            var line = source[end..eol].Trim();
                            if (line.Length >= count && line.All(x => x == source[marker])) { bodyEnd = end; end = eol < source.Length ? eol + 1 : eol; break; }
                            end = eol < source.Length ? eol + 1 : eol;
                        }
                        Emit(end, MarkupKind.CodeBlock, bodyStart, bodyEnd); continue;
                    }
                }
            }
            if (lineStart && recognizeTables && MarkdownTableParser.TryRead(source, i, out _, out var tableEnd))
            { Emit(tableEnd, MarkupKind.Table, i, tableEnd); continue; }
            var slashes = 0; for (var k = i - 1; k >= 0 && source[k] == '\\'; k--) slashes++;
            if (slashes % 2 == 1) { i++; continue; }
            if (c == '`')
            {
                var count = 1; while (i + count < source.Length && source[i + count] == '`') count++;
                var delimiter = new string('`', count); var end = source.IndexOf(delimiter, i + count, StringComparison.Ordinal);
                if (end >= 0) { Emit(end + count, MarkupKind.InlineCode, i + count, end); continue; }
                i += count; continue;
            }
            if (c is '$' or '\\' && MathDelimiters.TryRead(source, i, source.Length, out var math))
            {
                Emit(math.End, math.Display ? MarkupKind.DisplayMath : MarkupKind.InlineMath, math.ContentStart, math.ContentEnd);
                continue;
            }
            i++;
        }
        if (plain < source.Length) result.Add(new(MarkupKind.Text, source[plain..], source[plain..]));
        return result;
    }
}
