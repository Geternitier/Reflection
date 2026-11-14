using Markdig;
using Markdig.Extensions.Mathematics;
using Markdig.Helpers;
using Markdig.Parsers;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Reflection.Core;

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
        public AdjacentMathParser() => OpeningCharacters = ['$', '\\'];

        public override bool Match(InlineProcessor processor, ref StringSlice slice)
        {
            var text = slice.Text;
            var start = slice.Start;
            if (!MathDelimiters.TryRead(text, start, slice.End + 1, out var range))
            {
                // Keep an incomplete explicit delimiter visible while content streams in.
                if (text[start] != '\\' || start == slice.End || text[start + 1] is not ('(' or '[')) return false;
                processor.Inline = new LiteralInline(new StringSlice(text, start, start + 1));
                slice.Start += 2;
                return true;
            }

            processor.Inline = new MathInline
            {
                Content = new StringSlice(text, range.ContentStart, range.ContentEnd - 1),
                Delimiter = '$',
                DelimiterCount = range.Display ? 2 : 1,
                Span = new SourceSpan(processor.GetSourcePosition(start, out var line, out var column),
                    processor.GetSourcePosition(range.End - 1)),
                Line = line,
                Column = column
            };
            slice.Start = range.End;
            return true;
        }
    }
}
