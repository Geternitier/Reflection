using Markdig;
using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Reflection.Core;

internal sealed class WikiLinkExtension : IMarkdownExtension
{
    public void Setup(MarkdownPipelineBuilder pipeline) => pipeline.InlineParsers.Insert(0, new WikiLinkParser());
    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer) { }

    private sealed class WikiLinkParser : InlineParser
    {
        public WikiLinkParser() => OpeningCharacters = ['['];

        public override bool Match(InlineProcessor processor, ref StringSlice slice)
        {
            var start = slice.Start;
            var text = slice.Text;
            if (start + 3 > slice.End || text[start + 1] != '[') return false;
            var limit = Math.Min(slice.End - 1, start + 512);
            for (var end = start + 2; end <= limit; end++)
            {
                if (text[end] is '\r' or '\n' or '[') return false;
                if (text[end] != ']' || text[end + 1] != ']') continue;
                var content = text.AsSpan(start + 2, end - start - 2);
                var separator = content.IndexOf('|');
                var address = (separator < 0 ? content : content[..separator]).Trim();
                if (address.IsEmpty || address.Contains(':') || address.Contains('\\') || address.StartsWith("/")) return false;
                var label = separator < 0 ? address : content[(separator + 1)..].Trim();
                var target = address.ToString();
                var anchor = target.IndexOf('#');
                var path = anchor < 0 ? target : target[..anchor];
                if (path.Length > 0 && !ReaderDocuments.Supported(path))
                    target = path + ".md" + (anchor < 0 ? "" : target[anchor..]);
                var link = new LinkInline(target, "")
                {
                    Span = new SourceSpan(processor.GetSourcePosition(start, out var line, out var column),
                        processor.GetSourcePosition(end + 1)), Line = line, Column = column, IsClosed = true
                };
                link.AppendChild(new LiteralInline(label.IsEmpty ? address.ToString() : label.ToString()));
                processor.Inline = link;
                slice.Start = end + 2;
                return true;
            }
            return false;
        }
    }
}
