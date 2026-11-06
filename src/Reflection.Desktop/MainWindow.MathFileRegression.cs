using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Reflection.Core;

namespace Reflection.Desktop;

public partial class MainWindow
{
    internal async Task VerifyMathAndFilesAsync(string output)
    {
        const string stokesSource = @"用户公式：分析$\oint_L P\mathrm dx+Q\mathrm dy+R\mathrm dz=\iint(\frac{\partial R}{\partial y}-\frac{\partial Q}{\partial z})\mathrm dy\mathrm dz+(\frac{\partial P}{\partial z}-\frac{\partial R}{\partial x})\mathrm dz\mathrm dx+(\frac{\partial Q}{\partial x}-\frac{\partial P}{\partial y})\mathrm dx\mathrm dy$
### 斯托克斯公式
$$\oint_L \mathbf{F} \cdot d\mathbf{r} = \iint_S (\nabla \times \mathbf{F}) \cdot d\mathbf{S}$$
其中 $\mathbf{F} = (P,Q,R)$。";
        var stokes = new MarkdownText { Source = stokesSource };
        stokes.Render();
        if (stokes.FormulaCount != 3 || stokes.FormulaFallbackCount != 0) throw new InvalidOperationException("Stokes formula: " + stokes.FormulaCount + ": " + stokes.FormulaError);
        var stokesTask = ShowOverlayAsync(new ScrollViewer { Content = stokes, Width = 950, Height = 400, Padding = new Thickness(24) });
        await Task.Delay(100); UpdateLayout();
        App.Capture(this, Path.Combine(output, "stokes-formulas.png"));
        CompleteOverlay(false); await stokesTask;
        var windows = Application.Current.Windows.Count;
        const string source = "### 公式渲染\n行内分式 \\(\\frac{a}{b}\\) 与平方根 \\(\\sqrt{x^2+1}\\)。\n\\[\\int_0^1 x^2 dx = \\frac{1}{3}\\]\n$$\\begin{pmatrix}1&2\\\\3&4\\end{pmatrix}$$\n```tex\n$$code$$\n```\n无效公式 \\(\\unknowncommand{x}\\)\n未闭合 \\(a+b";
        var math = new MarkdownText { Source = source, FontFamily = FontFamily };
        math.Render();
        if (math.FormulaCount != 5 || math.FormulaFallbackCount != 1 || math.Source != source)
            throw new InvalidOperationException($"Unexpected formula rendering: {math.FormulaCount}, fallbacks={math.FormulaFallbackCount}");
        var pendingMath = ShowOverlayAsync(new ScrollViewer { Content = math, Width = 840, Height = 620 });
        await Task.Delay(100); UpdateLayout();
        App.Capture(this, Path.Combine(output, "math-rendering.png"));
        foreach (var dpi in new[] { 96, 144, 192 }) App.CaptureElement(math, Path.Combine(output, $"math-{dpi}.png"), dpi);
        CompleteOverlay(false); await pendingMath;
        var unsafeMath = new MarkdownText { Source = "\\(\\newcommand{\\x}{x}\\) \\(" + new string('x', 4100) + "\\)" };
        unsafeMath.Render();
        if (unsafeMath.FormulaFallbackCount != 2) throw new InvalidOperationException("Formula limits did not preserve source fallback");

        var folder = Path.Combine(output, "file-dialog-fixtures"); Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "sample.json");
        var exportedCard = Path.Combine(folder, "exported-card.json");
        var exportedChat = Path.Combine(folder, "formula-chat.md");
        try
        {
            FileDialogs.SelectForRegression = request => null;
            if (await FileDialogs.PickAsync(this, "打开文档", [".md"]) is not null || OverlayOpen)
                throw new InvalidOperationException("Cancelled system file request changed the workspace");
            FileDialogs.SelectForRegression = request => request.Directory ? [folder] : null;
            if ((await FileDialogs.PickAsync(this, "选择文件夹", [], directory: true))?.Single() != folder)
                throw new InvalidOperationException("Folder selection result was not forwarded");
            await File.WriteAllTextAsync(path, CharacterExchange.Export(new CharacterCard { Name = "文件流程回归", SystemPrompt = "系统文件导入" }));
            FileDialogs.SelectForRegression = request =>
            {
                if (request.Title != "导入角色卡" || request.Save || !request.Extensions.Contains(".json")) throw new InvalidOperationException("Import dialog configuration failed");
                return [path];
            };
            ImportCard_Click(this, new RoutedEventArgs());
            await WaitOverlayStateAsync(() => VM.DetailCard?.Name == "文件流程回归");
            FileDialogs.SelectForRegression = request =>
            {
                if (!request.Save || request.Suggested != "character.json") throw new InvalidOperationException("Save dialog configuration failed");
                return [exportedCard];
            };
            ExportDetail_Click(this, new RoutedEventArgs());
            await WaitOverlayStateAsync(() => File.Exists(exportedCard));
            if (CharacterExchange.Import(await File.ReadAllTextAsync(exportedCard)).Name != "文件流程回归") throw new InvalidOperationException("Card file round trip failed");
            await VM.NewChatAsync(VM.Characters.First()); ShowPage("chat");
            VM.Current.Messages.Add(new ChatMessage { Role = "assistant", Content = source, Thinking = "公式排版回归" });
            await VM.SaveCurrentAsync(); SetSidebar(true); await Task.Delay(100); UpdateLayout();
            App.Capture(this, Path.Combine(output, "math-conversation.png"));
            FileDialogs.SelectForRegression = request => [exportedChat];
            ExportChat_Click(this, new RoutedEventArgs());
            await WaitOverlayStateAsync(() => File.Exists(exportedChat));
            if (!(await File.ReadAllTextAsync(exportedChat)).Contains(source)) throw new InvalidOperationException("Markdown export modified formula source");
            var chatId = VM.Current.Id; await VM.NewChatAsync(); await VM.OpenAsync(VM.Conversations.First(c => c.Id == chatId));
            if (VM.Current.Messages.Last().Content != source) throw new InvalidOperationException("Formula source changed on reload");
        }
        finally { FileDialogs.SelectForRegression = null; }
        var editor = EditorDialog.CreateService(this, VM.Settings.Services.FirstOrDefault());
        var pendingEditor = ShowOverlayAsync(editor, editor.ServiceName);
        var selected = InlineSelection.OpenAsync(editor.ServiceProtocol!); await Task.Delay(50);
        if (Application.Current.Windows.Count != windows || OverlayContent.Child != editor || !ChoiceOpen) throw new InvalidOperationException("Nested selection escaped the editor");
        CloseChoices(); await selected;
        CompleteOverlay(false); await pendingEditor;
        await VM.NewChatAsync(VM.Characters.First());
    }
}
