using System.Text;
using System.Text.RegularExpressions;

namespace Reflection.Desktop;

internal enum MarkdownColumnAlignment { Left, Center, Right }
internal sealed record MarkdownTableData(string[] Headers, MarkdownColumnAlignment[] Alignments, IReadOnlyList<string[]> Rows);

internal static class MarkdownTableParser
{
    private static string Line(string source, int start, out int next)
    {
        var end = source.IndexOf('\n', start); if (end < 0) end = source.Length;
        next = end < source.Length ? end + 1 : end;
        return source[start..end].TrimEnd('\r');
    }
    internal static bool TryRead(string source, int start, out MarkdownTableData? table, out int end)
    {
        table = null; end = start;
        if (start >= source.Length) return false;
        var headerLine = Line(source, start, out var next);
        if (!headerLine.Contains('|') || next >= source.Length) return false;
        var delimiterLine = Line(source, next, out var afterDelimiter);
        var delimiters = SplitRow(delimiterLine, out var separators);
        var headers = SplitRow(headerLine, out var headerSeparators);
        if (separators == 0 || headerSeparators == 0 || headers.Length != delimiters.Length || headers.Length > 20 ||
            delimiters.Any(cell => !Regex.IsMatch(cell, @"^:?-{3,}:?$"))) return false;
        var alignments = delimiters.Select(cell => cell.StartsWith(':') && cell.EndsWith(':') ? MarkdownColumnAlignment.Center : cell.EndsWith(':') ? MarkdownColumnAlignment.Right : MarkdownColumnAlignment.Left).ToArray();
        var rows = new List<string[]>(); end = afterDelimiter;
        while (end < source.Length && rows.Count < 500)
        {
            var line = Line(source, end, out var afterRow);
            if (string.IsNullOrWhiteSpace(line)) break;
            var cells = SplitRow(line, out var count);
            if (count == 0 || line.TrimStart().StartsWith("```") || line.TrimStart().StartsWith("~~~")) break;
            rows.Add(Enumerable.Range(0, headers.Length).Select(i => i < cells.Length ? cells[i] : "").ToArray());
            end = afterRow;
        }
        table = new(headers, alignments, rows); return true;
    }
    internal static string[] SplitRow(string line, out int separators)
    {
        var cells = new List<string>(); var cell = new StringBuilder(); separators = 0;
        var trimmed = line.Trim();
        foreach (var token in MessageMarkupTokenizer.Tokenize(trimmed, recognizeTables: false))
        {
            if (token.Kind != MarkupKind.Text) { cell.Append(token.Raw); continue; }
            for (var i = 0; i < token.Text.Length; i++)
            {
                var c = token.Text[i];
                if (c == '\\' && i + 1 < token.Text.Length)
                {
                    if (token.Text[i + 1] == '|') { cell.Append('|'); i++; continue; }
                    if (token.Text[i + 1] == '\\') { cell.Append("\\\\"); i++; continue; }
                }
                if (c == '|') { cells.Add(cell.ToString().Trim()); cell.Clear(); separators++; }
                else cell.Append(c);
            }
        }
        cells.Add(cell.ToString().Trim());
        if (trimmed.StartsWith('|') && cells.Count > 1) cells.RemoveAt(0);
        if (trimmed.EndsWith('|') && cells.Count > 1 && cells[^1].Length == 0) cells.RemoveAt(cells.Count - 1);
        return cells.ToArray();
    }
}
