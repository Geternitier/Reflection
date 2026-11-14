namespace Reflection.Core;

public static class MathDelimiters
{
    public const int MaxFormulasPerRender = 64;
    public const int MaxFormulaLength = 4096;
    public readonly record struct Match(int ContentStart, int ContentEnd, int End, bool Display);

    public static bool TryRead(string text, int start, int end, out Match match)
    {
        match = default;
        if (start + 1 >= end || IsEscaped(text, start)) return false;
        var opening = text[start];
        var next = text[start + 1];
        if (opening == '\\' && TryReadStandaloneCommand(text, start, end, out match)) return true;
        var explicitDelimiter = opening == '\\' && next is '(' or '[';
        if (!explicitDelimiter && (opening != '$' || start > 0 && text[start - 1] == '$')) return false;
        var display = next == '[' && explicitDelimiter || next == '$' && opening == '$';
        var singleDollar = !explicitDelimiter && !display;
        var contentStart = start + (singleDollar ? 1 : 2);
        if (contentStart >= end) return false;
        var first = contentStart;
        while (first < end && text[first] is ' ' or '\t') first++;
        if (first >= end) return false;
        var closing = explicitDelimiter ? display ? "\\]" : "\\)" : display ? "$$" : "$";
        var numericStart = singleDollar && char.IsDigit(text[first]);
        // Recognize a bounded range of oversized formulas so they take the explicit
        // source fallback path. Only formulas within MaxFormulaLength are kept whole.
        var limit = Math.Min(end, contentStart + 2 * MaxFormulaLength + closing.Length);
        var groupDepth = 0;
        for (var position = contentStart; position < limit; position++)
        {
            var character = text[position];
            if ((!display && character is '\r' or '\n') || character == '`') return false;
            // Keep Chinese prose from joining a currency amount to an unrelated dollar sign.
            // TeX groups can contain text even when the expression starts with a number.
            if (numericStart && character is '{' or '}' && !IsEscaped(text, position))
                groupDepth = character == '{' ? groupDepth + 1 : Math.Max(0, groupDepth - 1);
            if (numericStart && groupDepth == 0 && character > 127 && !IsGreek(character) &&
                (char.IsLetter(character) || char.IsPunctuation(character))) return false;
            if (position + closing.Length > end || !text.AsSpan(position, closing.Length).SequenceEqual(closing) ||
                IsEscaped(text, position)) continue;
            if (position <= first) return false;
            if (singleDollar && (first != contentStart || char.IsWhiteSpace(text[position - 1])) &&
                !LooksLikePaddedMath(text.AsSpan(first, position - first).TrimEnd())) return false;
            var after = position + closing.Length;
            if (singleDollar && after < end &&
                (text[after] == '$' || numericStart && char.IsDigit(text[after]))) return false;
            match = new Match(contentStart, position, after, display);
            return true;
        }
        return false;
    }

    private static bool LooksLikePaddedMath(ReadOnlySpan<char> body)
    {
        if (body.Length == 1 || body.IndexOfAny(@"\{}_^+=-*/<>|".AsSpan()) >= 0) return true;
        foreach (var character in body)
            if (!char.IsAsciiDigit(character) && !IsGreek(character) && character != '.') return false;
        return !body.IsEmpty;
    }

    private static bool TryReadStandaloneCommand(string text, int start, int end, out Match match)
    {
        match = default;
        var remaining = text.AsSpan(start, Math.Min(end - start, MaxFormulaLength));
        if (remaining.StartsWith(@"\ref{", StringComparison.Ordinal) || remaining.StartsWith(@"\eqref{", StringComparison.Ordinal))
        {
            var close = remaining.IndexOf('}');
            if (close < 0 || remaining[..close].IndexOfAny('\r', '\n') >= 0) return false;
            match = new Match(start, start + close + 1, start + close + 1, false);
            return true;
        }
        if (!remaining.StartsWith(@"\begin{", StringComparison.Ordinal)) return false;
        var nameEnd = remaining.IndexOf('}');
        if (nameEnd < 0) return false;
        var name = remaining[7..nameEnd];
        if (name is not ("equation" or "equation*" or "align" or "align*" or "alignat" or "alignat*" or
            "gather" or "gather*" or "multline" or "multline*")) return false;
        var closing = @"\end{" + name.ToString() + "}";
        var bodyStart = nameEnd + 1;
        var bodyEnd = remaining[bodyStart..].IndexOf(closing, StringComparison.Ordinal);
        if (bodyEnd < 0) return false;
        var after = start + bodyStart + bodyEnd + closing.Length;
        match = new Match(start, after, after, true);
        return true;
    }

    internal static List<(int Start, int End)> FindRanges(string text)
    {
        var ranges = new List<(int, int)>();
        for (var index = 0; index < text.Length;)
        {
            if ((text[index] is '`' or '~') && !IsEscaped(text, index))
            {
                var count = 1;
                while (index + count < text.Length && text[index + count] == text[index]) count++;
                if (text[index] == '`' || count >= 3)
                {
                    var fence = new string(text[index], count);
                    var close = text.IndexOf(fence, index + count, StringComparison.Ordinal);
                    while (close >= 0 && (text[close - 1] == text[index] ||
                        close + count < text.Length && text[close + count] == text[index]))
                        close = text.IndexOf(fence, close + count, StringComparison.Ordinal);
                    if (close >= 0) { index = close + count; continue; }
                }
                index += count;
                continue;
            }
            if ((text[index] is '$' or '\\') && TryRead(text, index, text.Length, out var match))
            {
                if (match.ContentEnd - match.ContentStart <= MaxFormulaLength) ranges.Add((index, match.End));
                index = match.End;
                continue;
            }
            index++;
        }
        return ranges;
    }

    internal static bool ContainsOffset(IReadOnlyList<(int Start, int End)> ranges, ref int index, int offset)
    {
        while (index < ranges.Count && ranges[index].End <= offset) index++;
        return index < ranges.Count && ranges[index].Start < offset;
    }

    internal static bool IsStandalone(string text, int delimiter)
    {
        for (var index = delimiter - 1; index >= 0 && text[index] is not ('\r' or '\n'); index--)
            if (text[index] is not (' ' or '\t')) return false;
        for (var index = delimiter + 2; index < text.Length && text[index] is not ('\r' or '\n'); index++)
            if (text[index] is not (' ' or '\t')) return false;
        return true;
    }

    private static bool IsGreek(char character) =>
        character is >= '\u0370' and <= '\u03FF' or >= '\u1F00' and <= '\u1FFF';

    private static bool IsEscaped(string text, int position)
    {
        var slashes = 0;
        while (position > 0 && text[--position] == '\\') slashes++;
        return slashes % 2 != 0;
    }
}
