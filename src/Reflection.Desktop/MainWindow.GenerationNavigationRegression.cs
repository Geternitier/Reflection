using System.IO;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using Reflection.Core;

namespace Reflection.Desktop;

public partial class MainWindow
{
    internal async Task VerifyGenerationNavigationAsync(string output)
    {
        if (VM.SelectedModel is null) return;
        await VM.NewChatAsync(VM.Characters.First());
        VM.Current.Messages.Add(new ChatMessage { Role = "user", Content = "历史导航目标" });
        VM.Draft = "目标会话草稿"; await VM.SaveCurrentAsync();
        var targetId = VM.Current.Id;
        await VM.NewChatAsync(VM.Characters.First()); ShowPage("chat"); SetSidebar(true);
        VM.Draft = "navigation slow A";
        var runA = VM.SendAsync();
        await WaitOverlayStateAsync(() => VM.Busy && VM.Current.Messages.Any(m => m.Incomplete));
        var chatA = VM.Current;
        await Task.Delay(100);
        var summaryA = VM.Conversations.First(c => c.Id == chatA.Id);
        var order = VM.Conversations.Select(c => c.Id).ToArray();
        var resets = 0;
        NotifyCollectionChangedEventHandler monitor = (_, e) => { if (e.Action is NotifyCollectionChangedAction.Reset or NotifyCollectionChangedAction.Remove or NotifyCollectionChangedAction.Move) resets++; };
        VM.Conversations.CollectionChanged += monitor;
        try
        {
            if (!HistoryList.IsEnabled || !NewChatButton.IsEnabled) throw new InvalidOperationException("Generation locked sidebar");
            HistoryList.SelectedItem = VM.Conversations.First(c => c.Id == targetId);
            await WaitOverlayStateAsync(() => VM.Current.Id == targetId);
            if (!VM.AnyBusy || VM.Busy || runA.IsCompleted || chatA.Messages.Last().Stopped || VM.Draft != "目标会话草稿")
                throw new InvalidOperationException("Navigation interrupted or leaked the background generation");
            if (resets != 0 || !order.SequenceEqual(VM.Conversations.Select(c => c.Id)) || !ReferenceEquals(summaryA, VM.Conversations.First(c => c.Id == chatA.Id)))
                throw new InvalidOperationException("Navigation rebuilt or reordered history");
            VM.Search = "历史导航目标"; VM.Search = "";
            App.Capture(this, Path.Combine(output, "background-generation-navigation.png"));
            VM.Draft = "navigation slow B"; var runB = VM.SendAsync(); var chatB = VM.Current;
            await WaitOverlayStateAsync(() => VM.Busy);
            await VM.OpenAsync(summaryA);
            if (!VM.Busy || !ReferenceEquals(VM.Current, chatA)) throw new InvalidOperationException("Reopening a generating chat lost its live instance");
            VM.Stop(); await runA;
            if (!chatA.Messages.Last().Stopped || !VM.AnyBusy || runB.IsCompleted) throw new InvalidOperationException("Stop affected another conversation");
            await VM.OpenAsync(VM.Conversations.First(c => c.Id == chatB.Id));
            NewChatButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await WaitOverlayStateAsync(() => VM.Current.Id != chatB.Id);
            var empty = VM.Current;
            VM.Draft = "新会话独立草稿";
            await runB;
            if (chatB.Messages.Last() is not { Stopped: false, Incomplete: false, HasError: false } || VM.Draft != "新会话独立草稿" || VM.Busy || VM.AnyBusy)
                throw new InvalidOperationException("Background generation did not complete independently");
            if (empty.Messages.Any(m => m.Content.Contains("你好")) || resets != 0) throw new InvalidOperationException("Background output changed the active chat or rebuilt history");
            var savedA = await VM.Store.LoadConversationAsync(chatA.Id); var savedB = await VM.Store.LoadConversationAsync(chatB.Id);
            if (!savedA.Messages.Last().Stopped || savedB.Messages.Last().Stopped || !savedB.Messages.Last().Content.Contains("你好"))
                throw new InvalidOperationException("Background conversations were not persisted separately");

            // Background draft assistance must not overwrite the active conversation's composer.
            VM.Draft = "navigation slow help"; var helperChat = VM.Current;
            var help = VM.HelpDraftAsync(); await Task.Delay(100);
            await VM.OpenAsync(VM.Conversations.First(c => c.Id == chatA.Id));
            VM.Draft = "保持当前草稿";
            await help;
            if (VM.Draft != "保持当前草稿" || !helperChat.Draft.Contains("你好")) throw new InvalidOperationException("Background drafting overwrote another composer");
            if (!order.SequenceEqual(VM.Conversations.Where(c => order.Contains(c.Id)).Select(c => c.Id))) throw new InvalidOperationException("Background completion reordered history");
        }
        finally { VM.Conversations.CollectionChanged -= monitor; await VM.StopAllAsync(); }
        await VM.NewChatAsync(VM.Characters.First());
    }
}
