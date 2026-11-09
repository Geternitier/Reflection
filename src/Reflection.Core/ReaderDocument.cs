using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.Footnotes;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Reflection.Core;

public sealed record ReadingHeading(string Title, int Level, int Piece, string Anchor);
public sealed record ReadingPiece(int Offset, int Length, IReadOnlyList<Block> Blocks, string? Text = null, bool Code = false, int Heading = 0, int First = 0, int Count = int.MaxValue, string? SourcePath = null, string? MarkdownSource = null, string? CodeLanguage = null, int CodeLine = 1);
public sealed record ReadingDocument(string Path, string Text, string Encoding, IReadOnlyList<ReadingPiece> Pieces, IReadOnlyList<ReadingHeading> Outline);

// Parsing creates data only; the dispatcher materializes just the nearby pieces.
public static partial class ReaderDocuments
{
    public const int MaxBytes = 20 * 1024 * 1024;
    public const int PieceCharacters = 2400;
    public static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables().UseEmphasisExtras().UseAutoLinks().UseTaskLists().UseMathematics()
        .UseFootnotes().UseDefinitionLists().UseAlertBlocks().UsePreciseSourceLocation()
        .Use<ReaderMathExtension>().Use<WikiLinkExtension>().DisableHtml().Build();
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    static ReaderDocuments() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    public static bool Supported(string path) => Path.GetExtension(path).ToLowerInvariant() is ".md" or ".markdown" or ".txt";

    public static async Task<ReadingDocument> OpenAsync(string path, CancellationToken token)
    {
        path = Path.GetFullPath(path);
        if (!Supported(path)) throw new InvalidDataException("请选择 Markdown 或 TXT 文件。");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, true);
        if (stream.Length > MaxBytes) throw new InvalidDataException("文件超过 20 MB，请拆分后阅读。");
        using var memory = new MemoryStream((int)stream.Length);
        var buffer = new byte[65536]; int read;
        while ((read = await stream.ReadAsync(buffer, token)) > 0)
        {
            if (memory.Length + read > MaxBytes) throw new InvalidDataException("文件超过 20 MB，请拆分后阅读。");
            memory.Write(buffer, 0, read);
        }
        return await Task.Run(() =>
        {
            var bytes = memory.ToArray(); string text, encoding;
            if (bytes.AsSpan().StartsWith(new byte[] { 0xff, 0xfe })) { text = Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2); encoding = "UTF-16 LE"; }
            else if (bytes.AsSpan().StartsWith(new byte[] { 0xfe, 0xff })) { text = Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2); encoding = "UTF-16 BE"; }
            else try { text = StrictUtf8.GetString(bytes).TrimStart('\uFEFF'); encoding = "UTF-8"; }
                catch (DecoderFallbackException) { text = Encoding.GetEncoding("GB18030").GetString(bytes); encoding = "GB18030"; }
            return Parse(path, text, encoding, token);
        }, token);
    }

    public static MarkdownDocument ParseMarkdown(string source) => Markdown.Parse(NormalizeMath(source), Pipeline);

    public static ReadingDocument Parse(string path, string text, string encoding = "UTF-8", CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var pieces = new List<ReadingPiece>(); var headings = new List<ReadingHeading>();
        if (Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase))
        {
            var offset = 0; var pending = new StringBuilder(); var start = 0;
            void Flush() { if (pending.Length == 0) return; pieces.Add(new(start, pending.Length, [], pending.ToString())); pending.Clear(); }
            foreach (var line in text.Split('\n'))
            {
                token.ThrowIfCancellationRequested();
                if (Chapter().IsMatch(line.Trim()) && line.Trim().Length <= 100)
                {
                    Flush(); headings.Add(new(line.Trim(), 1, pieces.Count, "chapter-" + offset));
                    pieces.Add(new(offset, line.Length, [], line.Trim(), Heading: 1)); offset += line.Length + 1; start = offset; continue;
                }
                for (var index = 0; index < line.Length;)
                {
                    if (pending.Length == 0) start = offset + index;
                    var take = Math.Min(PieceCharacters - pending.Length, line.Length - index);
                    if (take > 1 && index + take < line.Length && char.IsHighSurrogate(line[index + take - 1])) take--;
                    pending.Append(line, index, take); index += take;
                    if (pending.Length >= PieceCharacters - 1) Flush();
                }
                pending.Append('\n'); offset += line.Length + 1;
                if (pending.Length >= PieceCharacters || pending.Length >= 1200 && string.IsNullOrWhiteSpace(line)) Flush();
            }
            Flush();
        }
        else
        {
            var markdown = ParseMarkdown(text);
            var group = new List<Block>(); var start = 0; var end = 0;
            void Flush() { if (group.Count == 0) return; pieces.Add(new(start, Math.Max(1, end - start + 1), group.ToArray(), SourcePath: path, MarkdownSource: text)); group.Clear(); }
            // Footnote definitions remain at their source locations in the virtualized reader.
            var blocks = markdown.Where(block => block is not LinkReferenceDefinitionGroup and not FootnoteGroup);
            if (markdown.OfType<FootnoteGroup>().FirstOrDefault() is { } notes)
                blocks = blocks.Concat(notes.OfType<Footnote>()).OrderBy(block => block.Span.Start);
            foreach (var block in blocks)
            {
                token.ThrowIfCancellationRequested();
                if (block is HeadingBlock heading)
                {
                    Flush(); var title = InlineText(heading.Inline);
                    var anchor = HeadingAnchor(title);
                    var duplicate = headings.Count(h => h.Anchor == anchor || h.Anchor.StartsWith(anchor + "-", StringComparison.Ordinal));
                    headings.Add(new(title, heading.Level, pieces.Count, duplicate == 0 ? anchor : anchor + "-" + duplicate));
                }
                if (block is Footnote)
                {
                    Flush();
                    pieces.Add(new(block.Span.Start, Math.Max(1, block.Span.Length), [block], SourcePath: path, MarkdownSource: text));
                    continue;
                }
                if (group.Count > 0 && block.Span.End - start > PieceCharacters) Flush();
                if (block is CodeBlock code && (block.Span.Length > PieceCharacters || code.Lines.Count > 80))
                {
                    Flush(); var content = code.Lines.ToString(); var line = 1;
                    for (var pos = 0; pos < content.Length;)
                    {
                        var take = Math.Min(PieceCharacters, content.Length - pos);
                        if (pos + take < content.Length && char.IsHighSurrogate(content[pos + take - 1])) take--;
                        // Bound the number of native list items in each numbered code piece.
                        var scan = pos;
                        for (var row = 0; row < 80 && scan < pos + take; row++)
                        {
                            var next = content.IndexOf('\n', scan, pos + take - scan);
                            if (next < 0) break;
                            scan = next + 1;
                            if (row == 79) take = scan - pos;
                        }
                        var newline = content.LastIndexOf('\n', pos + take - 1, take);
                        if (pos + take < content.Length && newline >= pos) take = newline - pos + 1;
                        var chunk = content.Substring(pos, take);
                        pieces.Add(new(block.Span.Start + pos, take, [], chunk, true, CodeLanguage: (code as FencedCodeBlock)?.Info, CodeLine: line));
                        line += chunk.Count(c => c == '\n'); pos += take;
                    }
                    continue;
                }
                if (block is Markdig.Extensions.Tables.Table table && table.Count > 25)
                {
                    Flush();
                    for (var row = 1; row < table.Count; row += 24)
                        pieces.Add(new(table[row].Span.Start, Math.Max(1, table[Math.Min(table.Count - 1, row + 23)].Span.End - table[row].Span.Start + 1), [table], First: row, Count: 24, SourcePath: path, MarkdownSource: text));
                    continue;
                }
                if (block is ListBlock list && list.Count > 24)
                {
                    Flush();
                    for (var row = 0; row < list.Count; row += 24)
                        pieces.Add(new(list[row].Span.Start, Math.Max(1, list[Math.Min(list.Count - 1, row + 23)].Span.End - list[row].Span.Start + 1), [list], First: row, Count: 24, SourcePath: path, MarkdownSource: text));
                    continue;
                }
                if (block is ParagraphBlock && block.Span.Length > PieceCharacters * 2)
                {
                    Flush(); var raw = text.Substring(block.Span.Start, Math.Min(block.Span.Length, text.Length - block.Span.Start));
                    for (var pos = 0; pos < raw.Length;)
                    {
                        var take = Math.Min(PieceCharacters, raw.Length - pos);
                        if (pos + take < raw.Length && char.IsHighSurrogate(raw[pos + take - 1])) take--;
                        var chunk = raw.Substring(pos, take);
                        pieces.Add(new(block.Span.Start + pos, take, ParseMarkdown(chunk).ToArray(), SourcePath: path, MarkdownSource: chunk)); pos += take;
                    }
                    continue;
                }
                if (group.Count == 0) start = block.Span.Start;
                group.Add(block); end = block.Span.End;
            }
            Flush();
        }
        if (pieces.Count == 0) pieces.Add(new(0, 0, [], "这是一篇空文档。"));
        return new(path, text, encoding, pieces, headings);
    }
    public static string HeadingAnchor(string title) =>
        Regex.Replace(title.ToLowerInvariant(), @"[^\p{L}\p{N}\s_-]", "").Trim().Replace(' ', '-');

    public static string InlineText(ContainerInline? inline)
    {
        if (inline is null) return "";
        var result = new StringBuilder();
        foreach (var child in inline)
            if (child is LiteralInline literal) result.Append(literal.Content);
            else if (child is CodeInline code) result.Append(code.Content);
            else if (child is ContainerInline container) result.Append(InlineText(container));
        return result.ToString();
    }

    public static string? ResolveImagePath(string? sourcePath, string? reference)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(reference)) return null;
        try
        {
            var suffix = reference.AsSpan().IndexOfAny('#', '?');
            var path = suffix < 0 ? reference : reference[..suffix];
            if (path.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(path, UriKind.Absolute, out var uri) || !uri.IsFile || uri.IsUnc || uri.Host.Length != 0) return null;
                path = uri.LocalPath;
            }
            else path = Uri.UnescapeDataString(path);

            if (path.Length == 0 || path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)) return null;
            var colon = path.IndexOf(':');
            if (colon >= 0 && (colon != 1 || !char.IsAsciiLetter(path[0]) || !Path.IsPathFullyQualified(path) || path.IndexOf(':', 2) >= 0))
                return null;
            if (Path.IsPathRooted(path) && !Path.IsPathFullyQualified(path)) return null;
            var full = Path.GetFullPath(path, Path.GetDirectoryName(Path.GetFullPath(sourcePath))!);
            var root = Path.GetPathRoot(full);
            return root is { Length: 3 } && root[1] == ':' && Path.GetExtension(full).ToLowerInvariant() is
                ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".tif" or ".tiff" or ".webp" ? full : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException) { return null; }
    }
    // Equal-length substitutions preserve source offsets used by reading anchors.
    private static string NormalizeMath(string text)
    {
        var chars = text.ToCharArray(); var code = false; char fence = '\0'; var ticks = 0;
        for (var i = 0; i < chars.Length - 1; i++)
        {
            if (chars[i] is '`' or '~')
            {
                var count = 1; while (i + count < chars.Length && chars[i + count] == chars[i]) count++;
                if (chars[i] == '~' && count < 3) continue;
                if (!code) { code = true; fence = chars[i]; ticks = count; }
                else if (chars[i] == fence && count == ticks) code = false;
                i += count - 1; continue;
            }
            if (code || chars[i] != '\\' || (i > 0 && chars[i - 1] == '\\')) continue;
            if (chars[i + 1] is not ('(' or '[')) continue;
            var inline = chars[i + 1] == '(';
            var closing = inline ? "\\)" : "\\]";
            var end = text.IndexOf(closing, i + 2, StringComparison.Ordinal);
            while (end > 0 && text[end - 1] == '\\') end = text.IndexOf(closing, end + 2, StringComparison.Ordinal);
            if (end < 0 || inline && text.AsSpan(i + 2, end - i - 2).Contains('\n')) continue;
            chars[i] = inline ? ' ' : '$'; chars[i + 1] = '$';
            chars[end] = '$'; chars[end + 1] = inline ? ' ' : '$'; i = end + 1;
        }
        return new string(chars);
    }
    [GeneratedRegex(@"^(?:第[〇零一二三四五六七八九十百千万两\d]+[章节卷部篇回集]|序章|序言|前言|楔子|尾声|后记|终章|附录|chapter\s+\d+|part\s+\d+)(?:\s|[、:：.．]|$|[^\x00-\x7F])", RegexOptions.IgnoreCase)]
    private static partial Regex Chapter();
}
