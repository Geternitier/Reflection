using Markdig;
using Markdig.Extensions.Mathematics;
using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Renderers;
using Markdig.Syntax;

namespace Reflection.Core;

// Math boundaries follow TeX delimiters rather than Western word boundaries.
internal sealed class ReaderMathExtension : IMarkdownExtension
{
    public void Setup(MarkdownPipelineBuilder pipeline)
    {
        var standard = pipeline.InlineParsers.Find<MathInlineParser>();
        if (standard is not null) pipeline.InlineParsers.Remove(standard);
        pipeline.InlineParsers.Insert(0, new AdjacentMathParser());
    }
    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer) { }
    private sealed class AdjacentMathParser : InlineParser
    {
        public AdjacentMathParser() => OpeningCharacters = ['$'];
        public override bool Match(InlineProcessor processor, ref StringSlice slice)
        {
            var text = slice.Text; var start = slice.Start;
            if (start > 0 && text[start - 1] == '$' || start >= slice.End) return false;
            var count = text[start + 1] == '$' ? 2 : 1;
            var content = start + count;
            if (content > slice.End || char.IsWhiteSpace(text[content])) return false;
            for (var end = content; end <= slice.End; end++)
            {
                if (text[end] is '\r' or '\n' or '`') return false;
                // A currency amount must not capture later prose/math as its closing pair.
                if (count == 1 && char.IsDigit(text[content]) && text[end] > 127 && (char.IsLetter(text[end]) || char.IsPunctuation(text[end]))) return false;
                if (text[end] != '$') continue;
                var escapes = 0; for (var i = end - 1; i >= content && text[i] == '\\'; i--) escapes++;
                if (escapes % 2 != 0) continue;
                if (count == 2 && (end == slice.End || text[end + 1] != '$')) continue;
                if (end == content || char.IsWhiteSpace(text[end - 1])) return false;
                var after = end + count;
                if (count == 1 && after <= slice.End && (text[after] == '$' || char.IsDigit(text[after]))) return false;
                processor.Inline = new MathInline
                {
                    Content = new StringSlice(text, content, end - 1), Delimiter = '$', DelimiterCount = count,
                    Span = new SourceSpan(processor.GetSourcePosition(start, out var line, out var column), processor.GetSourcePosition(after - 1)),
                    Line = line, Column = column
                };
                slice.Start = after; return true;
            }
            return false;
        }
    }
}
