using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using Reflection.Core;

namespace Reflection.Desktop;

public partial class MarkdownText
{
    private bool TryAddDiagram(Section section, string source, string? language)
    {
        var diagram = MarkdownDiagrams.Parse(source, language);
        var canvas = diagram switch
        {
            FlowDiagram flow => DrawFlow(flow),
            SequenceDiagram sequence => DrawSequence(sequence),
            _ => null
        };
        if (canvas is null) return false;
        AutomationProperties.SetName(canvas, diagram is FlowDiagram ? "流程图" : "时序图");
        var scroll = new ScrollViewer { Content = canvas, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 460 };
        scroll.SetBinding(MaxWidthProperty, new Binding(nameof(ActualWidth)) { Source = this, Converter = new FormulaWidthConverter() });
        section.Blocks.Add(new BlockUIContainer(scroll));
        return true;
    }

    private static Canvas? DrawFlow(FlowDiagram diagram)
    {
        var levels = diagram.Nodes.ToDictionary(node => node.Id, _ => 0);
        var incoming = diagram.Nodes.ToDictionary(node => node.Id, _ => 0);
        foreach (var edge in diagram.Edges) incoming[edge.To]++;
        var ready = new Queue<string>(diagram.Nodes.Select(node => node.Id).Where(id => incoming[id] == 0));
        var visited = 0;
        while (ready.TryDequeue(out var id))
        {
            visited++;
            foreach (var edge in diagram.Edges.Where(edge => edge.From == id))
            {
                levels[edge.To] = Math.Max(levels[edge.To], levels[id] + 1);
                if (--incoming[edge.To] == 0) ready.Enqueue(edge.To);
            }
        }
        if (visited != levels.Count) return null;
        var horizontal = diagram.Direction is "LR" or "RL";
        var reverse = diagram.Direction is "BT" or "RL";
        const double width = 122, height = 54, gapX = 56, gapY = 48, padding = 10;
        var last = levels.Values.Max();
        var groups = diagram.Nodes.GroupBy(node => levels[node.Id]).ToArray();
        var breadth = groups.Max(group => group.Count());
        var columns = horizontal ? last + 1 : breadth;
        var rows = horizontal ? breadth : last + 1;
        var canvas = new Canvas { Width = padding * 2 + columns * (width + gapX) - gapX,
            Height = padding * 2 + rows * (height + gapY) - gapY };
        var bounds = new Dictionary<string, Rect>();
        foreach (var group in groups)
        {
            var rank = reverse ? last - group.Key : group.Key;
            var index = (breadth - group.Count()) / 2d;
            foreach (var node in group)
            {
                var x = horizontal ? rank : index;
                var y = horizontal ? index : rank;
                bounds[node.Id] = new Rect(padding + x * (width + gapX), padding + y * (height + gapY), width, height);
                index++;
            }
        }
        foreach (var edge in diagram.Edges)
        {
            var from = bounds[edge.From]; var to = bounds[edge.To];
            var a = horizontal ? new Point(reverse ? from.Left : from.Right, from.Top + height / 2)
                : new Point(from.Left + width / 2, reverse ? from.Top : from.Bottom);
            var b = horizontal ? new Point(reverse ? to.Right : to.Left, to.Top + height / 2)
                : new Point(to.Left + width / 2, reverse ? to.Bottom : to.Top);
            DrawArrow(canvas, a, b, edge.Arrow != "---", edge.Arrow == "-.->", edge.Arrow == "==>" ? 2.5 : 1.4);
            if (edge.Label.Length > 0)
                Put(canvas, DiagramLabel(edge.Label, 96, 30), (a.X + b.X) / 2 + (horizontal ? -48 : 4),
                    (a.Y + b.Y) / 2 - (horizontal ? 30 : 15));
        }
        foreach (var node in diagram.Nodes) DrawNode(canvas, node, bounds[node.Id]);
        return canvas;
    }

    private static Canvas DrawSequence(SequenceDiagram diagram)
    {
        const double width = 122, height = 36, step = 164, row = 48, padding = 10;
        var diagramHeight = padding * 2 + height + row * diagram.Messages.Count;
        var canvas = new Canvas { Width = padding * 2 + step * (diagram.Participants.Count - 1) + width + 42, Height = diagramHeight };
        var xById = new Dictionary<string, double>();
        for (var index = 0; index < diagram.Participants.Count; index++)
        {
            var node = diagram.Participants[index];
            var left = padding + index * step;
            var x = left + width / 2;
            xById[node.Id] = x;
            DrawLine(canvas, new(x, padding + height), new(x, diagramHeight - padding / 2), Brush("Muted"), 1, true);
            DrawNode(canvas, node, new Rect(left, padding, width, height));
        }
        for (var index = 0; index < diagram.Messages.Count; index++)
        {
            var message = diagram.Messages[index];
            var x1 = xById[message.From]; var x2 = xById[message.To];
            var y = padding + height + (index + .73) * row;
            var dashed = message.Arrow.Contains("--", StringComparison.Ordinal);
            if (x1 == x2)
            {
                DrawLine(canvas, new(x1, y - 12), new(x1 + 32, y - 12), Brush("Accent"), 1.4, dashed);
                DrawLine(canvas, new(x1 + 32, y - 12), new(x1 + 32, y + 4), Brush("Accent"), 1.4, dashed);
                DrawArrow(canvas, new(x1 + 32, y + 4), new(x1, y + 4), true, dashed);
            }
            else DrawArrow(canvas, new(x1, y), new(x2, y), true, dashed);
            if (message.Label.Length > 0)
            {
                var labelWidth = x1 == x2 ? 100 : Math.Min(300, Math.Abs(x1 - x2) - 8);
                Put(canvas, DiagramLabel(message.Label, labelWidth, 26, Brush("Sidebar")),
                    x1 == x2 ? x1 + 4 : (x1 + x2 - labelWidth) / 2, y - (x1 == x2 ? 36 : 26));
            }
        }
        return canvas;
    }

    private static void DrawNode(Canvas canvas, DiagramNode node, Rect bounds)
    {
        var background = Brush("Panel");
        var stroke = Brush("Accent");
        if (node.Shape == "diamond")
        {
            var w = bounds.Width; var h = bounds.Height;
            Put(canvas, new Polygon { Fill = background, Stroke = stroke, StrokeThickness = 1.25,
                Points = new PointCollection([new(w / 2, 0), new(w, h / 2), new(w / 2, h), new(0, h / 2)]) }, bounds.X, bounds.Y);
            Put(canvas, DiagramLabel(node.Label, w * .64, h), bounds.X + w * .18, bounds.Y);
        }
        else
        {
            Put(canvas, new Border { Width = bounds.Width, Height = bounds.Height, Background = background,
                BorderBrush = stroke, BorderThickness = new Thickness(1.25), CornerRadius = new CornerRadius(node.Shape == "round" ? 17 : 5),
                Child = DiagramLabel(node.Label, bounds.Width - 8, bounds.Height - 4) }, bounds.X, bounds.Y);
        }
    }

    private static Grid DiagramLabel(string label, double width, double height, Brush? background = null) =>
        new() { Width = width, Height = height, Background = background, Children =
        {
            new TextBlock { Text = label, FontFamily = SansSerifFont, FontSize = 12, Foreground = Brush("Ink"),
                TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis,
                TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, ToolTip = label }
        } };

    private static void DrawArrow(Canvas canvas, Point from, Point to, bool head, bool dashed, double thickness = 1.4)
    {
        var brush = Brush("Accent");
        DrawLine(canvas, from, to, brush, thickness, dashed);
        var direction = from - to;
        if (!head || direction.Length < 8) return;
        direction.Normalize();
        var normal = new Vector(-direction.Y, direction.X);
        canvas.Children.Add(new Polygon { Fill = brush,
            Points = new PointCollection([to, to + direction * 8 + normal * 3.5, to + direction * 8 - normal * 3.5]) });
    }

    private static void DrawLine(Canvas canvas, Point from, Point to, Brush brush, double width, bool dashed) =>
        canvas.Children.Add(new Line { X1 = from.X, Y1 = from.Y, X2 = to.X, Y2 = to.Y, Stroke = brush, StrokeThickness = width,
            StrokeDashArray = dashed ? new DoubleCollection([4, 3]) : null });

    private static void Put(Canvas canvas, UIElement item, double x, double y)
    {
        Canvas.SetLeft(item, x); Canvas.SetTop(item, y);
        canvas.Children.Add(item);
    }
}
