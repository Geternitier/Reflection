using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Input;
using Reflection.Core;

namespace Reflection.Desktop;

public partial class MainWindow
{
    internal void VerifyStartupChrome()
    {
        UpdateLayout();
        if (currentPage != "cards" || CardsRoot.HasAnimatedProperties || CardsRoot.Opacity != 1 || PageLabel.Visibility != Visibility.Visible || PageLabel.Text != "角色")
            throw new InvalidOperationException("Startup repainted or hid its character header");
        if (TopBar.ActualHeight > 44.5 || !CardsHeaderActions.IsVisible || ConnectionBadge.IsVisible)
            throw new InvalidOperationException("Character controls did not share the compact header");
        var theme = ThemeManager.Revision; ThemeManager.Apply(VM.Settings.Theme);
        if (theme != ThemeManager.Revision) throw new InvalidOperationException("Applying the same startup theme caused another repaint");
    }
    internal async Task VerifyCompactLayoutAsync(string output)
    {
        ShowPage("chat"); SetSidebar(true); UpdateLayout();
        var tools = new[] { WorkspaceModeButton, ThemeToggle, SettingsNav };
        if (tools.Any(b => b.Parent != SidebarTools || b.Content is not Geometry) || SidebarTools.Orientation != Orientation.Horizontal)
            throw new InvalidOperationException("Sidebar tools were not three icons in one row");
        if (WelcomeContent.Children.Count != 1 || WelcomeContent.Children[0] != WelcomeHeadline)
            throw new InvalidOperationException("New chat retained welcome text or prompt buttons");
        VM.Draft = "第一行\n" + string.Concat(Enumerable.Repeat("自动换行测试", 70)); UpdateLayout();
        var lastLine = Composer.LineCount - 1;
        if (lastLine < 2) throw new InvalidOperationException("Composer regression did not wrap its last logical line");
        Composer.CaretIndex = Composer.GetCharacterIndexFromLineIndex(lastLine);
        var down = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this), 0, Key.Down) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        Composer_KeyDown(Composer, down);
        if (!down.Handled || Composer.CaretIndex != Composer.Text.Length)
            throw new InvalidOperationException("Down on the last wrapped line did not reach the end");
        Composer.CaretIndex = 1;
        down = new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this), 0, Key.Down) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
        Composer_KeyDown(Composer, down);
        if (down.Handled || Composer.CaretIndex != 1) throw new InvalidOperationException("Down navigation was intercepted above the last line");
        VM.Draft = "";
        await VM.NewChatAsync(); VM.Current.Title = "紧凑列表验证";
        VM.Current.Messages.Add(new ChatMessage { Role = "user", Content = "这是一条用户消息。编辑和复制仅在悬停时出现在右下角。" });
        VM.Current.Messages.Add(new ChatMessage { Role = "assistant", Content = "助手消息保留原有操作。" });
        await VM.SaveCurrentAsync(); ShowPage("chat"); SetSidebar(true); await Task.Delay(220); UpdateLayout();
        var historyRows = Descendants<Grid>(HistoryList).Where(g => g.Name == "HistoryRow").ToArray();
        if (historyRows.Length == 0 || historyRows.Any(r => r.ActualHeight > 34)) throw new InvalidOperationException("History rows were not compact single lines");
        if (Descendants<StackPanel>(HistoryList).Where(p => p.Name == "HistoryActions").Any(p => p.Visibility != Visibility.Collapsed))
            throw new InvalidOperationException("History actions appeared without hover");
        if (Descendants<TextBlock>(HistoryList).Any(t => System.Windows.Data.BindingOperations.GetBinding(t, TextBlock.TextProperty)?.Path?.Path == "Preview"))
            throw new InvalidOperationException("History still showed message previews");
        var userActions = Descendants<StackPanel>(MessagesList).Single(p => p.Name == "UserActions" && p.DataContext is ChatMessage { Role: "user" });
        if (userActions.Visibility != Visibility.Hidden || userActions.HorizontalAlignment != HorizontalAlignment.Right)
            throw new InvalidOperationException("User actions did not remain hidden at the right edge");
        var bubble = Descendants<Border>(MessagesList).Single(b => b.Name == "Bubble" && b.DataContext is ChatMessage { Role: "user" });
        var area = (FrameworkElement)VisualTreeHelper.GetParent(bubble);
        if (userActions.TransformToAncestor(area).Transform(new Point()).Y < bubble.ActualHeight)
            throw new InvalidOperationException("User actions covered the message body");
        App.Capture(this, Path.Combine(output, "compact-chat.png"));
        ShowPage("reader"); await ReaderPage.InitializeAsync(); await Task.Delay(220); UpdateLayout();
        if (!ReaderHeaderHost.IsVisible || ReaderHeaderHost.ActualHeight > 34.5 || !ReferenceEquals(ReaderHeaderHost.Content, ReaderPage.HeaderToolbar))
            throw new InvalidOperationException("Reader tools did not share the compact page header");
        var pageTop = ReaderPage.TranslatePoint(new Point(), WorkspaceRoot).Y;
        if (pageTop > 77) throw new InvalidOperationException("Reader retained the extra toolbar row");
        App.Capture(this, Path.Combine(output, "compact-reader-header.png"));
        ShowPage("cards"); await Task.Delay(220); UpdateLayout();
        App.Capture(this, Path.Combine(output, "compact-character-header.png"));
        await VM.NewChatAsync(VM.Characters.First());
    }
}
