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
}
