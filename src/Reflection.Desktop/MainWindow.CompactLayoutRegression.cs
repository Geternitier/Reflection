using System.IO;
using System.Windows;
using System.Windows.Automation;
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
        await VerifyComposerMetricsAsync(output);
        var tools = new[] { WorkspaceModeButton, ThemeToggle, SettingsNav };
        if (tools.Any(b => b.Parent != SidebarTools || b.Content is not Geometry) || SidebarTools.Orientation != Orientation.Horizontal)
            throw new InvalidOperationException("Sidebar tools were not three icons in one row");
        if (WelcomeContent.Children.Count != 1 || WelcomeContent.Children[0] != WelcomeHeadline)
            throw new InvalidOperationException("New chat retained welcome text or prompt buttons");
        if (Composer.ToolTip is not null)
            throw new InvalidOperationException("Composer text box retained a hover tooltip");
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
        var shortUserMessage = new ChatMessage { Role = "user", Content = "好" };
        var longUserMessage = new ChatMessage { Role = "user", Content = new string('长', 72) };
        VM.Current.Messages.Add(shortUserMessage);
        VM.Current.Messages.Add(longUserMessage);
        VM.Current.Messages.Add(new ChatMessage { Role = "assistant", Content = "助手消息保留原有操作。" });
        await VM.SaveCurrentAsync(); ShowPage("chat"); SetSidebar(true); await Task.Delay(220); UpdateLayout();
        var historyRows = Descendants<Grid>(HistoryList).Where(g => g.Name == "HistoryRow").ToArray();
        if (historyRows.Length == 0 || historyRows.Any(r => r.ActualHeight > 34)) throw new InvalidOperationException("History rows were not compact single lines");
        if (Descendants<StackPanel>(HistoryList).Where(p => p.Name == "HistoryActions").Any(p => p.Visibility != Visibility.Collapsed))
            throw new InvalidOperationException("History actions appeared without hover");
        if (Descendants<TextBlock>(HistoryList).Any(t => System.Windows.Data.BindingOperations.GetBinding(t, TextBlock.TextProperty)?.Path?.Path == "Preview"))
            throw new InvalidOperationException("History still showed message previews");
        var userActions = Descendants<StackPanel>(MessagesList).Single(p => p.Name == "UserActions" && ReferenceEquals(p.DataContext, shortUserMessage));
        if (userActions.Visibility != Visibility.Hidden || userActions.HorizontalAlignment != HorizontalAlignment.Right)
            throw new InvalidOperationException("User actions did not remain hidden at the right edge");
        var bubble = Descendants<Border>(MessagesList).Single(b => b.Name == "Bubble" && ReferenceEquals(b.DataContext, shortUserMessage));
        var wideBubble = Descendants<Border>(MessagesList).Single(b => b.Name == "Bubble" && ReferenceEquals(b.DataContext, longUserMessage));
        if (bubble.ActualWidth is < 40 or > 96 || wideBubble.ActualWidth < 480)
            throw new InvalidOperationException($"User message bubbles did not size to their text: short {bubble.ActualWidth}, long {wideBubble.ActualWidth}");
        var area = (FrameworkElement)VisualTreeHelper.GetParent(bubble);
        if (userActions.TransformToAncestor(area).Transform(new Point()).Y < bubble.ActualHeight)
            throw new InvalidOperationException("User actions covered the message body");
        App.Capture(this, Path.Combine(output, "compact-chat.png"));
        ShowPage("reader"); await ReaderPage.InitializeAsync(); await Task.Delay(220); UpdateLayout();
        if (!ReaderHeaderHost.IsVisible || ReaderHeaderHost.ActualHeight > 34.5 || !ReferenceEquals(ReaderHeaderHost.Content, ReaderPage.HeaderToolbar))
            throw new InvalidOperationException("Reader tools did not share the compact page header");
        var readerActions = ((DockPanel)ReaderPage.HeaderToolbar).Children.OfType<StackPanel>().Single().Children
            .OfType<Button>().Select(AutomationProperties.GetName);
        if (!readerActions.SequenceEqual(["字号与排版", "目录"]))
            throw new InvalidOperationException("Reader header did not reduce its right side to typography and outline");
        var pageTop = ReaderPage.TranslatePoint(new Point(), WorkspaceRoot).Y;
        if (pageTop > 77) throw new InvalidOperationException("Reader retained the extra toolbar row");
        App.Capture(this, Path.Combine(output, "compact-reader-header.png"));
        ShowPage("cards"); await Task.Delay(220); UpdateLayout();
        App.Capture(this, Path.Combine(output, "compact-character-header.png"));
        await VM.NewChatAsync(VM.Characters.First());
    }

    private async Task VerifyComposerMetricsAsync(string output)
    {
        if (VM.SelectedModel is null) return;
        await VM.NewChatAsync();
        VM.Draft = "token metrics fixture";
        var generating = VM.SendAsync();
        await WaitOverlayStateAsync(() => VM.Busy);
        var chatId = VM.Current.Id;
        await VM.NewChatAsync();
        await generating;
        if (VM.Current.Id == chatId || TokenSpeedReadout.Text != "— tok/s" || ContextUsageRing.Value != 0)
            throw new InvalidOperationException("Background token metrics leaked into the active conversation");
        await VM.OpenAsync(VM.Conversations.Single(chat => chat.Id == chatId));
        var hint = AutomationProperties.GetName(ContextButton);
        if (TokenSpeedReadout.Text != "24 tok/s" || ContextUsageRing.Value != .25 || ContextButton.ActualWidth > 32
            || ContextButton.ToolTip is not string tooltip || !tooltip.Contains(.25.ToString("P1"))
            || !hint.Contains(1024.ToString("N0")) || !hint.Contains(4096.ToString("N0")))
            throw new InvalidOperationException("Token metrics did not display the completed request's measurements");
        var originalWidth = Width;
        var originalTheme = VM.Settings.Theme;
        try
        {
            UpdateLayout();
            App.Capture(this, Path.Combine(output, "composer-metrics.png"));
            Width = MinWidth; SetSidebar(false); ThemeManager.Apply("dark"); UpdateLayout();
            var picker = ModelPicker.TranslatePoint(new Point(), ComposerPanel);
            var speed = TokenSpeedReadout.TranslatePoint(new Point(), ComposerPanel);
            var context = ContextButton.TranslatePoint(new Point(), ComposerPanel);
            if (speed.X < picker.X + ModelPicker.ActualWidth || context.X < speed.X + TokenSpeedReadout.ActualWidth
                || context.X + ContextButton.ActualWidth > ComposerPanel.ActualWidth
                || Math.Abs(speed.Y + TokenSpeedReadout.ActualHeight / 2 - context.Y - ContextButton.ActualHeight / 2) > 1)
                throw new InvalidOperationException("Token metrics overlapped composer controls at the minimum width");
            App.Capture(this, Path.Combine(output, "composer-metrics-narrow-dark.png"));
            var dialog = await OpenContextEditorAsync();
            var field = Descendants<TextBox>(dialog).Single();
            if (field.Text.Length != 0 || !Equals(field.Tag, "模型默认")) throw new InvalidOperationException("New conversation did not inherit the model context");
            field.Text = "0";
            if (dialog.TrySubmit()) throw new InvalidOperationException("Invalid context length was accepted");
            field.Text = "8192";
            App.Capture(this, Path.Combine(output, "context-window-narrow-dark.png"));
            ConfirmContextEditor(dialog);
            await WaitOverlayStateAsync(() => VM.Current.ContextWindowOverride == 8192 && VM.Idle);
            await WaitOverlayStateAsync(() => !OverlayOpen);
        }
        finally { Width = originalWidth; SetSidebar(true); ThemeManager.Apply(originalTheme); }
        VM.Draft = "token metrics fixture override";
        await VM.SendAsync();
        if (VM.Current.Messages.Last().HasError || (await VM.Store.LoadConversationAsync(chatId)).ContextWindowOverride != 8192)
            throw new InvalidOperationException("Context override was not sent or persisted");
        var restore = await OpenContextEditorAsync();
        Descendants<TextBox>(restore).Single().Text = "";
        ConfirmContextEditor(restore);
        await WaitOverlayStateAsync(() => VM.Current.ContextWindowOverride is null && VM.Idle);
        await VM.NewChatAsync();
    }
    private async Task<EditorDialog> OpenContextEditorAsync()
    {
        ContextButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitOverlayStateAsync(() => OverlayOpen && OverlayContent.Child is EditorDialog);
        return (EditorDialog)OverlayContent.Child;
    }
    private void ConfirmContextEditor(EditorDialog dialog) => Descendants<Button>(dialog)
        .Single(button => AutomationProperties.GetAutomationId(button) == "SaveDialog").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
}
