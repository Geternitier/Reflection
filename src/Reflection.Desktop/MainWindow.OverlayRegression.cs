using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Reflection.Core;

namespace Reflection.Desktop;

public partial class MainWindow
{
    private async Task WaitOverlayStateAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(20);
        if (!condition()) throw new InvalidOperationException("Overlay action did not finish");
    }
    private void SendOverlayEscape()
    {
        RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(this)!, Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
    }
    internal async Task VerifyOverlaysAsync(string output)
    {
        var windowCount = Application.Current.Windows.Count;
        await VM.NewChatAsync(VM.Characters.First()); VM.Current.Title = "删除确认测试"; VM.Draft = "保留草稿"; await VM.SaveCurrentAsync();
        ShowPage("chat"); Composer.Focus(); var id = VM.Current.Id;
        DeleteChat_Click(this, new RoutedEventArgs());
        await WaitOverlayStateAsync(() => OverlayOpen);
        await Task.Delay(100); UpdateLayout();
        if (Application.Current.Windows.Count != windowCount || OverlayContent.Child is not InlineNotice notice || notice.CancelButton is null || !notice.CancelButton.IsDefault || notice.PrimaryButton.IsDefault)
            throw new InvalidOperationException("Delete confirmation is not an inline safe-default dialog");
        if (KeyboardNavigation.GetTabNavigation(OverlayLayer) != KeyboardNavigationMode.Cycle || WorkspaceRoot.Children.OfType<UIElement>().Any(child => child != OverlayLayer && child != ChoiceLayer && child.IsEnabled))
            throw new InvalidOperationException("Overlay does not isolate background input");
        App.Capture(this, Path.Combine(output, "delete-chat-overlay.png"));
        SendOverlayEscape(); await Task.Delay(50);
        if (OverlayOpen || VM.Current.Id != id || VM.Draft != "保留草稿" || !Composer.IsEnabled)
            throw new InvalidOperationException("Escape deleted data or left the background disabled");

        DeleteChat_Click(this, new RoutedEventArgs()); await WaitOverlayStateAsync(() => OverlayOpen);
        ((InlineNotice)OverlayContent.Child).PrimaryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitOverlayStateAsync(() => !OverlayOpen && VM.Current.Id != id);
        if (VM.Conversations.Any(item => item.Id == id)) throw new InvalidOperationException("Confirmed conversation deletion did not run");

        VM.Current.Title = "历史删除测试"; VM.Draft = "历史草稿"; await VM.SaveCurrentAsync();
        var history = VM.Conversations.First(item => item.Id == VM.Current.Id);
        await VM.NewChatAsync(VM.Characters.First());
        var historyButton = new Button { DataContext = history };
        DeleteHistory_Down(historyButton, new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
        await WaitOverlayStateAsync(() => OverlayOpen);
        ((InlineNotice)OverlayContent.Child).CloseButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(30);
        if (!VM.Conversations.Any(item => item.Id == history.Id)) throw new InvalidOperationException("Closing a history confirmation deleted its data");
        DeleteHistory_Down(historyButton, new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.PreviewMouseDownEvent });
        await WaitOverlayStateAsync(() => OverlayOpen);
        ((InlineNotice)OverlayContent.Child).PrimaryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitOverlayStateAsync(() => !OverlayOpen && !VM.Conversations.Any(item => item.Id == history.Id));

        var card = new CharacterCard { Name = "删除角色测试", Description = "测试删除确认" };
        VM.Characters.Add(card); await VM.SaveCharactersAsync(); VM.DetailCard = card; ShowPage("detail");
        DeleteDetail_Click(this, new RoutedEventArgs()); await WaitOverlayStateAsync(() => OverlayOpen);
        UpdateLayout(); App.Capture(this, Path.Combine(output, "delete-character-overlay.png"));
        ((InlineNotice)OverlayContent.Child).CancelButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(30);
        if (!VM.Characters.Contains(card)) throw new InvalidOperationException("Cancel removed a character");
        DeleteDetail_Click(this, new RoutedEventArgs()); await WaitOverlayStateAsync(() => OverlayOpen);
        ((InlineNotice)OverlayContent.Child).PrimaryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitOverlayStateAsync(() => !OverlayOpen && !VM.Characters.Contains(card) && currentPage == "cards");

        var message = new ChatMessage { Role = "user", Content = "原消息" };
        var edit = EditorDialog.EditMessageAsync(this, message);
        await WaitOverlayStateAsync(() => OverlayOpen); UpdateLayout();
        var text = FindChild<TextBox>(OverlayContent.Child)!; text.Text = "修改后的消息";
        UpdateLayout();
        App.Capture(this, Path.Combine(output, "message-editor-overlay.png"));
        CompleteOverlay(false);
        if (await edit is not null || message.Content != "原消息") throw new InvalidOperationException("Message cancel changed content");

        var serviceToRemove = new ServiceConfig { Name = "移除确认测试", Enabled = false };
        VM.Settings.Services.Add(serviceToRemove); RefreshSettings(); ShowPage("settings");
        var serviceButton = new Button { DataContext = serviceToRemove };
        DeleteService_Click(serviceButton, new RoutedEventArgs()); await WaitOverlayStateAsync(() => OverlayOpen);
        ((InlineNotice)OverlayContent.Child).CancelButton!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Task.Delay(30);
        if (!VM.Settings.Services.Contains(serviceToRemove)) throw new InvalidOperationException("Cancel removed a service");
        DeleteService_Click(serviceButton, new RoutedEventArgs()); await WaitOverlayStateAsync(() => OverlayOpen);
        ((InlineNotice)OverlayContent.Child).PrimaryButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitOverlayStateAsync(() => !OverlayOpen && !VM.Settings.Services.Contains(serviceToRemove));

        var originalWidth = Width; var originalHeight = Height; Width = 720; Height = 620;
        var confirm = ConfirmAsync("删除对话", "确认窗口在最小尺寸下应保留所有操作。");
        await Task.Delay(100); UpdateLayout();
        if (OverlayContent.ActualWidth > ActualWidth || OverlayContent.ActualHeight > ActualHeight)
            throw new InvalidOperationException("Overlay extends outside the application");
        App.Capture(this, Path.Combine(output, "minimum-confirm-overlay.png"));
        OverlayBackdrop_Down(this, new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left) { RoutedEvent = Mouse.MouseDownEvent });
        if (await confirm) throw new InvalidOperationException("Backdrop accepted a destructive action");

        var service = EditorDialog.CreateService(this, VM.Settings.Services.FirstOrDefault());
        var serviceTask = ShowOverlayAsync(service, service.ServiceName);
        await Task.Delay(100); UpdateLayout();
        if (service.ServiceName!.ActualWidth < 200 || service.ActualHeight > ActualHeight - 80)
            throw new InvalidOperationException("Compact inline service form is clipped");
        App.Capture(this, Path.Combine(output, "minimum-service-overlay.png"));
        CompleteOverlay(false); await serviceTask;

        var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 109, 130, 237, 255 }, 4);
        var imageTask = ShowImageAsync(bitmap); await Task.Delay(100); UpdateLayout();
        if (Application.Current.Windows.Count != windowCount) throw new InvalidOperationException("Image preview opened another window");
        App.Capture(this, Path.Combine(output, "image-preview-overlay.png"));
        SendOverlayEscape(); if (await imageTask) throw new InvalidOperationException("Image dismissal returned an affirmative result");
        Width = originalWidth; Height = originalHeight;
        await VM.NewChatAsync(VM.Characters.First()); VM.Status = ""; ShowPage("cards");
    }
}
