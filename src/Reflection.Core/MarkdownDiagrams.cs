using System.Text.RegularExpressions;

namespace Reflection.Core;

public abstract record MarkdownDiagram;
public sealed record DiagramNode(string Id, string Label, string Shape);
public sealed record DiagramEdge(string From, string To, string Label, string Arrow);
public sealed record FlowDiagram(string Direction, IReadOnlyList<DiagramNode> Nodes, IReadOnlyList<DiagramEdge> Edges) : MarkdownDiagram;
public sealed record SequenceDiagram(IReadOnlyList<DiagramNode> Participants, IReadOnlyList<DiagramEdge> Messages) : MarkdownDiagram;

public static partial class MarkdownDiagrams
{
    private const string Identifier = @"[\p{L}_][\p{L}\p{N}_]{0,59}";

    public static MarkdownDiagram? Parse(string source, string? language)
    {
        var kind = language?.ToLowerInvariant();
        if (kind is not ("mermaid" or "plantuml" or "puml") || source.Length > 6000) return null;
        var lines = source.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length is < 2 or > 45 || lines.Any(line => line.Length > 300)) return null;
        var mermaid = kind == "mermaid";
        if (mermaid && FlowHeader().Match(lines[0]) is { Success: true } header)
            return ParseFlow(lines.Skip(1), header.Groups[1].Value);
        if (mermaid && lines[0] == "sequenceDiagram") return ParseSequence(lines.Skip(1), true);
        if (!mermaid && lines[0] == "@startuml" && lines[^1] == "@enduml")
            return ParseSequence(lines.Skip(1).SkipLast(1), false);
        return null;
    }

    private static FlowDiagram? ParseFlow(IEnumerable<string> lines, string direction)
    {
        var nodes = new Dictionary<string, DiagramNode>(StringComparer.Ordinal);
        var edges = new List<DiagramEdge>();
        foreach (var line in lines)
        {
            if (line.StartsWith("%%") && !line.StartsWith("%%{")) continue;
            var content = line.TrimEnd(';');
            var from = ReadNode(FlowNode().Match(content), nodes);
            if (from is null) return null;
            var position = from.Value.End;
            while (position < content.Length)
            {
                var arrow = FlowArrow().Match(content, position);
                if (!arrow.Success) return null;
                var to = ReadNode(FlowNode().Match(content, arrow.Index + arrow.Length), nodes);
                if (to is null || edges.Count == 36 || to.Value.Id == from.Value.Id) return null;
                edges.Add(new(from.Value.Id, to.Value.Id, arrow.Groups["label"].Value.Trim(), arrow.Groups["arrow"].Value));
                from = to;
                position = to.Value.End;
            }
            if (nodes.Count > 20) return null;
        }
        return nodes.Count == 0 ? null : new(direction, nodes.Values.ToArray(), edges);
    }

    private static (string Id, int End)? ReadNode(Match match, Dictionary<string, DiagramNode> nodes)
    {
        if (!match.Success) return null;
        var id = match.Groups["id"].Value;
        if (id is "end" or "subgraph" or "direction" or "classDef" or "class" or "click" or "style" or "linkStyle") return null;
        var shape = match.Groups["round"].Success ? "round" : match.Groups["diamond"].Success ? "diamond" : "box";
        var label = match.Groups["label"];
        if (label.Success || !nodes.ContainsKey(id)) nodes[id] = new(id, label.Success ? label.Value.Trim('"') : id, shape);
        return (id, match.Index + match.Length);
    }

    private static SequenceDiagram? ParseSequence(IEnumerable<string> lines, bool mermaid)
    {
        var nodes = new Dictionary<string, DiagramNode>(StringComparer.Ordinal);
        var messages = new List<DiagramEdge>();
        foreach (var line in lines)
        {
            if (mermaid ? line.StartsWith("%%") && !line.StartsWith("%%{") : line.StartsWith('\'')) continue;
            var participant = (mermaid ? MermaidParticipant() : PlantParticipant()).Match(line);
            if (participant.Success)
            {
                var id = participant.Groups["id"].Value;
                var label = participant.Groups["label"];
                nodes[id] = new(id, label.Success ? label.Value.Trim('"') : id, "box");
            }
            else
            {
                var message = (mermaid ? MermaidMessage() : PlantMessage()).Match(line);
                if (!message.Success) return null;
                var from = message.Groups["from"].Value;
                var to = message.Groups["to"].Value;
                var arrow = message.Groups["arrow"].Value;
                nodes.TryAdd(from, new(from, from, "box"));
                nodes.TryAdd(to, new(to, to, "box"));
                if (arrow[0] == '<') (from, to) = (to, from);
                messages.Add(new(from, to, message.Groups["label"].Value.Trim(), arrow));
            }
            if (nodes.Count > 8 || messages.Count > 36) return null;
        }
        return messages.Count == 0 ? null : new(nodes.Values.ToArray(), messages);
    }

    [GeneratedRegex(@"^(?:flowchart|graph)\s+(TD|TB|BT|LR|RL);?$")]
    private static partial Regex FlowHeader();
    [GeneratedRegex(@"\G\s*(?<id>" + Identifier + @")(?:\[(?<label>[^\[\]]{0,80})\]|(?<round>\((?<label>[^()]{0,80})\))|(?<diamond>\{(?<label>[^{}]{0,80})\}))?\s*")]
    private static partial Regex FlowNode();
    [GeneratedRegex(@"\G(?<arrow>-->|---|-\.->|==>)\s*(?:\|(?<label>[^|]{0,60})\|)?")]
    private static partial Regex FlowArrow();
    [GeneratedRegex(@"^(?:participant|actor)\s+(?<id>" + Identifier + @")(?:\s+as\s+(?<label>[^:]{1,60}))?$")]
    private static partial Regex MermaidParticipant();
    [GeneratedRegex("""^(?:participant|actor)\s+(?:"(?<label>[^"]{1,60})"\s+as\s+)?(?<id>""" + Identifier + ")$")]
    private static partial Regex PlantParticipant();
    [GeneratedRegex(@"^(?<from>" + Identifier + @")\s*(?<arrow>--?>>)\s*(?<to>" + Identifier + @")\s*:\s*(?<label>.{0,100})$")]
    private static partial Regex MermaidMessage();
    [GeneratedRegex(@"^(?<from>" + Identifier + @")\s*(?<arrow>--?>|<--?)\s*(?<to>" + Identifier + @")(?:\s*:\s*(?<label>.{0,100}))?$")]
    private static partial Regex PlantMessage();
}
