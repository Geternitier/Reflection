using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace Reflection.Desktop;

public partial class MainWindow
{
    internal async Task VerifyMarkdownExtensionsAsync(string output)
    {
        const string source = """
            ##### 五级标题

            ###### 六级标题

            公式 $x^2$，H~2~O 与 m^2^，~~删除~~。[^note]

            12. 一级
                - 二级
                  - [x] 三级任务

            术语
            : 释义

            > [!TIP]
            > 保留正文和 [普通链接](https://example.invalid)。

            ![远程图片](https://example.invalid/picture.png)

            ```cs
            var answer = 42;
            ```

            ```mermaid
            flowchart LR
            A[开始] --> B{检查} -->|通过| C(完成)
            ```

            ```plantuml
            @startuml
            Alice -> Bob: 请求
            Bob --> Alice: 响应
            @enduml
            ```

            ```plantuml
            @startuml
            !include https://example.invalid/external
            @enduml
            ```

            [^note]: 脚注正文。
            """;
        var markdown = new MarkdownText { Source = source };
        var area = new ScrollViewer { Content = markdown, Width = 620, Height = 620 };
        var pending = ShowOverlayAsync(area);
        try
        {
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.ContextIdle);
            var blocks = markdown.Document.Blocks.ToArray();
            var body = new TextRange(markdown.Document.ContentStart, markdown.Document.ContentEnd).Text;
            if (((Paragraph)blocks[0]).FontSize <= ((Paragraph)blocks[1]).FontSize
                || !body.Contains("术语") || !body.Contains("释义") || !body.Contains("技巧") || body.Contains("[!TIP]")
                || !body.Contains("☑") || !body.Contains("[图片：远程图片]") || !body.Contains("!include https://example.invalid/external"))
                throw new InvalidOperationException("Extended native Markdown did not preserve hierarchy, task markers or fallback");
            var variants = ((Paragraph)blocks[2]).Inlines.OfType<Span>().ToArray();
            if (!variants.Any(span => span.BaselineAlignment == BaselineAlignment.Subscript)
                || !variants.Any(span => span is not Hyperlink && span.BaselineAlignment == BaselineAlignment.Superscript)
                || !variants.Any(span => span.TextDecorations == TextDecorations.Strikethrough))
                throw new InvalidOperationException("Superscript, subscript or strike-through was not styled");
            var lists = blocks.OfType<System.Windows.Documents.List>().ToArray();
            if (lists.Single().StartIndex != 12 || lists[0].ListItems.FirstListItem.Blocks.OfType<System.Windows.Documents.List>().Count() != 1)
                throw new InvalidOperationException("Native ordered and nested lists were not rendered");
            var code = blocks.OfType<Section>().SelectMany(section => section.Blocks.OfType<System.Windows.Documents.List>()).First();
            var keyword = ((Paragraph)code.ListItems.FirstListItem.Blocks.FirstBlock).Inlines.OfType<Run>().First();
            if (keyword.Text != "var" || keyword.Foreground != FindResource("AccentHover"))
                throw new InvalidOperationException("Code keyword or line-number layout was not rendered");
            var diagrams = blocks.OfType<Section>().SelectMany(section => section.Blocks.OfType<BlockUIContainer>())
                .Select(block => (block.Child as ScrollViewer)?.Content as Canvas).OfType<Canvas>().ToArray();
            if (!diagrams.Select(AutomationProperties.GetName).SequenceEqual(["流程图", "时序图"])
                || diagrams.Any(canvas => canvas.Children.Count < 6))
                throw new InvalidOperationException("Native Mermaid or PlantUML preview was not rendered");
            var footnote = variants.OfType<Hyperlink>().Single(link => new TextRange(link.ContentStart, link.ContentEnd).Text == "[1]");
            footnote.RaiseEvent(new RoutedEventArgs(Hyperlink.ClickEvent));
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.ContextIdle);
            if (area.VerticalOffset <= 0) throw new InvalidOperationException($"Footnote jump did not scroll the message (extent {area.ExtentHeight}, viewport {area.ViewportHeight})");
            area.ScrollToTop();
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.ContextIdle);
            App.Capture(this, Path.Combine(output, "markdown-extensions.png"));
            await VM.ToggleThemeAsync();
            await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.ContextIdle);
            if (markdown.Document.Foreground != FindResource("Ink")) throw new InvalidOperationException("Extended Markdown theme did not refresh");
            App.Capture(this, Path.Combine(output, "markdown-extensions-dark.png"));
            await VM.ToggleThemeAsync();
        }
        finally { CompleteOverlay(false); await pending; }
    }
}
