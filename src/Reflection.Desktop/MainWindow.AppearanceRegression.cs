using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Reflection.Core;

namespace Reflection.Desktop;

public partial class MainWindow
{
    internal static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T found) yield return found;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    internal async Task VerifyAppearanceAndTablesAsync(string output)
    {
        const string source = "### 斯托克斯公式的三个分量\n正文与行内公式 $x+y=z$ 使用协调的字号。\n\n| 项 | 表达式 | 对应方向 |\n|---|:---:|---:|\n| 第一项 | $\\frac{\\partial R}{\\partial y}-\\frac{\\partial Q}{\\partial z}$ | $x$ 方向（$dy\\,dz$） |\n| 第二项 | $\\frac{\\partial P}{\\partial z}-\\frac{\\partial R}{\\partial x}$ | $y$ 方向（$dz\\,dx$） |\n| 第三项 | $\\frac{\\partial Q}{\\partial x}-\\frac{\\partial P}{\\partial y}$ | $z$ 方向（$dx\\,dy$） |\n\n公式块：\n$$E = mc^2$$\n\n`a|b` 保持为代码。";
        await VM.NewChatAsync(VM.Characters.First());
        VM.Current.Messages.Add(new ChatMessage { Role = "user", Content = "请用表格整理这个公式。" });
        VM.Current.Messages.Add(new ChatMessage { Role = "assistant", Content = source, Thinking = "按三个方向整理，并核对表格中的分式。" });
        await VM.SaveCurrentAsync(); ShowPage("chat"); SetSidebar(true);
        await Task.Delay(220); UpdateLayout();
        var markdown = Descendants<MarkdownText>(MessagesList).Single(view => view.Source == source);
        if (markdown.TableCount != 1 || markdown.FormulaFallbackCount != 0)
            throw new InvalidOperationException("Formula table failed to render: " + markdown.FormulaError);
        var table = markdown.Document.Blocks.OfType<Table>().Single();
        if (table.Columns.Count != 3 || table.RowGroups[0].Rows.Count != 4) throw new InvalidOperationException("Table dimensions changed");
        if (Descendants<TextBlock>(MessagesList).Any(label => label.Name == "Speaker")) throw new InvalidOperationException("Message speaker labels remain visible");
        var chatId = VM.Current.Id; VM.Draft = "主题切换保留草稿";
        App.Capture(this, Path.Combine(output, "table-light.png"));
        var lightInk = ((SolidColorBrush)markdown.Document.Foreground).Color;
        await VM.ToggleThemeAsync(); await Task.Delay(100); UpdateLayout();
        if (!ThemeManager.IsDark || (await VM.Store.LoadSettingsAsync()).Theme != "dark" || VM.Current.Id != chatId || VM.Draft != "主题切换保留草稿")
            throw new InvalidOperationException("Theme switch failed to persist or changed conversation state");
        if (((SolidColorBrush)markdown.Document.Foreground).Color == lightInk || markdown.TableCount != 1 || markdown.FormulaFallbackCount != 0)
            throw new InvalidOperationException("Existing formulas and tables did not update to the dark theme");
        if (((SolidColorBrush)HistoryList.Foreground).Color != ((SolidColorBrush)FindResource("Ink")).Color) throw new InvalidOperationException("History text did not follow the dark palette");
        App.Capture(this, Path.Combine(output, "table-dark.png"));
        ShowPage("cards"); await Task.Delay(220); App.Capture(this, Path.Combine(output, "cards-dark.png"));
        ShowPage("settings"); await Task.Delay(220); App.Capture(this, Path.Combine(output, "settings-dark.png"));
        Width = 720; Height = 620; ShowPage("chat"); await Task.Delay(220); UpdateLayout();
        App.Capture(this, Path.Combine(output, "table-dark-minimum.png"));
        Width = 1440; Height = 940;
        await VM.ToggleThemeAsync(); await Task.Delay(100); UpdateLayout();
        if (ThemeManager.IsDark || (await VM.Store.LoadSettingsAsync()).Theme != "light") throw new InvalidOperationException("Light theme did not restore");
        if (WorkspaceRoot.Clip is not RectangleGeometry { RadiusX: > 0 }) throw new InvalidOperationException("Rounded window clip is missing");
        await VM.NewChatAsync(VM.Characters.First());
    }
}
