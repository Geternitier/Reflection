using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using Reflection.Core;

namespace Reflection.Desktop;

public partial class MainWindow
{
    internal async Task VerifyScrollingAndStreamingAsync(string output)
    {
        var extra = Enumerable.Range(0, 24).Select(i => new CharacterCard { Name = "滚动验证 " + i }).ToArray();
        try
        {
            foreach (var card in extra) VM.Characters.Add(card);
            ShowPage("cards"); await Task.Delay(120); UpdateLayout();
            CardsPage.ScrollToTop(); UpdateLayout();
            var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
            CharacterGallery.RaiseEvent(wheel); UpdateLayout();
            if (!wheel.Handled || CardsPage.VerticalOffset <= 0) throw new InvalidOperationException("Character page did not scroll from nested gallery wheel");
        }
        finally { foreach (var card in extra) VM.Characters.Remove(card); }
        await VM.NewChatAsync(VM.Characters.First()); ShowPage("chat");
        VM.Current.Messages.Add(new ChatMessage { Role = "assistant", Content = string.Join("\n\n", Enumerable.Range(0, 100).Select(i => "滚动测试正文 " + i)) });
        await Task.Delay(150); UpdateLayout();
        var viewer = FindChild<ScrollViewer>(MessagesList)!;
        viewer.ScrollToTop(); UpdateLayout();
        var origin = new Point(100, 100); BeginAutoScroll(origin, false);
        StepAutoScroll(new Point(100, 200), .5); UpdateLayout();
        if (!AutoScrollActive || viewer.VerticalOffset <= 0 || AutoScrollLayer.Visibility != Visibility.Visible) throw new InvalidOperationException("Middle-click auto scroll did not move the conversation");
        var down = viewer.VerticalOffset;
        StepAutoScroll(new Point(100, 0), .1); UpdateLayout();
        if (viewer.VerticalOffset >= down) throw new InvalidOperationException("Auto scroll did not reverse direction");
        App.Capture(this, Path.Combine(output, "middle-auto-scroll.png"));
        RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this)!, Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
        if (AutoScrollActive || AutoScrollLayer.IsVisible) throw new InvalidOperationException("Esc did not stop auto scroll");
        BeginAutoScroll(origin, false); ShowPage("cards");
        if (AutoScrollActive) throw new InvalidOperationException("Page navigation kept auto scroll active");
        await VerifyChatTailAsync();
        await VM.NewChatAsync(VM.Characters.First()); ShowPage("chat");
        if (VM.SelectedModel is null) return;
        VM.Draft = "streaming lifecycle"; var run = VM.SendAsync();
        await WaitOverlayStateAsync(() => VM.Current.Messages.LastOrDefault()?.IsThinking == true);
        var answer = VM.Current.Messages.Last();
        await WaitOverlayStateAsync(() => Descendants<Expander>(MessagesList).Any(e => ReferenceEquals(e.DataContext, answer) && e.IsExpanded));
        App.Capture(this, Path.Combine(output, "streaming-thinking.png"));
        await WaitOverlayStateAsync(() => answer.Content.Contains("斜体") && !answer.IsThinking);
        await WaitOverlayStateAsync(() => Descendants<MarkdownText>(MessagesList).Any(m => ReferenceEquals(m.DataContext, answer) && m.Document.Blocks.OfType<Paragraph>().Any(p => p.Inlines.OfType<Bold>().Any()) && m.Document.Blocks.OfType<Paragraph>().Any(p => p.Inlines.OfType<Italic>().Any())));
        var markdown = Descendants<MarkdownText>(MessagesList).First(m => ReferenceEquals(m.DataContext, answer));
        var thinking = Descendants<Expander>(MessagesList).First(e => ReferenceEquals(e.DataContext, answer));
        if (!answer.Incomplete || thinking.IsExpanded || !markdown.Document.Blocks.OfType<Paragraph>().Any(p => p.FontSize > 14)) throw new InvalidOperationException("Streaming formatting waited for completion or thinking stayed open");
        App.Capture(this, Path.Combine(output, "streaming-markdown.png"));
        await run;
        thinking.IsExpanded = true;
        if (!thinking.IsExpanded || answer.IsThinking) throw new InvalidOperationException("Completed thinking cannot be manually expanded");
        VM.Draft = "streaming lifecycle stop"; var stopped = VM.SendAsync();
        await WaitOverlayStateAsync(() => VM.Current.Messages.Last().IsThinking);
        var interrupted = VM.Current.Messages.Last(); VM.Stop(); await stopped;
        if (interrupted.IsThinking || !interrupted.Stopped || string.IsNullOrEmpty(interrupted.Thinking)) throw new InvalidOperationException("Stopping did not close and preserve thinking");
        await VM.NewChatAsync(VM.Characters.First());
    }

    private async Task VerifyChatTailAsync()
    {
        await VM.NewChatAsync(); ShowPage("chat");
        var answer = new ChatMessage { Role = "assistant", Incomplete = true, Content = "开始生成" };
        VM.Current.Messages.Add(answer);
        UpdateLayout();
        var viewer = SmoothScroll.Viewer(MessagesList)!;
        bool AtBottom() => viewer.ScrollableHeight > 0 && viewer.ScrollableHeight - viewer.VerticalOffset <= 1;
        async Task GrowAsync(string label)
        {
            answer.Content += string.Concat(Enumerable.Range(1, 45).Select(i => $"\n\n{label} {i}"));
            await WaitOverlayStateAsync(() => Descendants<MarkdownText>(MessagesList).Any(text => ReferenceEquals(text.DataContext, answer)
                && new TextRange(text.Document.ContentStart, text.Document.ContentEnd).Text.Contains(label)));
        }

        await GrowAsync("首次增长");
        await Task.Delay(180);
        if (!AtBottom()) throw new InvalidOperationException($"First streaming growth did not reach the bottom: {viewer.VerticalOffset}/{viewer.ScrollableHeight}, follow={followOutput}, viewer owner={viewer.TemplatedParent?.GetType().Name}");
        var firstBottom = viewer.VerticalOffset;
        await GrowAsync("继续增长");
        await WaitOverlayStateAsync(() => AtBottom() && viewer.VerticalOffset > firstBottom);
        var bottomBeforeWheel = viewer.VerticalOffset;

        // A high-resolution wheel increment can be smaller than the old 48-pixel sticking area.
        var wheel = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, 48) { RoutedEvent = Mouse.PreviewMouseWheelEvent };
        MessagesList.RaiseEvent(wheel);
        await WaitOverlayStateAsync(() => viewer.VerticalOffset < bottomBeforeWheel - 1);
        var readingOffset = viewer.VerticalOffset;
        await GrowAsync("阅读历史时继续生成");
        await Task.Delay(180);
        if (Math.Abs(viewer.VerticalOffset - readingOffset) > 1 || AtBottom())
            throw new InvalidOperationException("Streaming pulled the reader away from the position selected with the wheel");

        viewer.ScrollToBottom();
        await WaitOverlayStateAsync(AtBottom);
        await GrowAsync("回到底部以后");
        await WaitOverlayStateAsync(AtBottom);
    }
}
