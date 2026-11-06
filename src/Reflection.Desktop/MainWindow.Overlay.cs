using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Reflection.Desktop;

public partial class MainWindow
{
    private TaskCompletionSource<bool>? overlayCompletion;
    private IInputElement? overlayPreviousFocus;
    private readonly List<(UIElement Element, bool Enabled)> overlayBackground = [];
    private readonly Stack<(FrameworkElement View, TaskCompletionSource<bool> Completion, IInputElement? Focus)> overlayStack = new();
    internal bool OverlayOpen => overlayCompletion is not null;

    internal Task<bool> ShowOverlayAsync(FrameworkElement view, IInputElement? initialFocus = null)
    {
        StopAutoScroll(); CloseChoices();
        if (overlayCompletion is not null && OverlayContent.Child is FrameworkElement previousView)
            overlayStack.Push((previousView, overlayCompletion, Keyboard.FocusedElement));
        overlayCompletion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = overlayCompletion;
        if (overlayStack.Count == 0) overlayPreviousFocus = Keyboard.FocusedElement;
        if (overlayStack.Count == 0) foreach (UIElement child in WorkspaceRoot.Children)
        {
            if (child == OverlayLayer || child == ChoiceLayer) continue;
            overlayBackground.Add((child, child.IsEnabled)); child.IsEnabled = false;
        }
        OverlayContent.Child = view;
        OverlayLayer.Visibility = Visibility.Visible;
        UpdateOverlaySize(); UpdateLayout();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (overlayCompletion != completion) return;
            if (initialFocus is UIElement element && element.IsEnabled && element.IsVisible) element.Focus();
            else view.MoveFocus(new TraversalRequest(FocusNavigationDirection.First));
        });
        return completion.Task;
    }
    internal void CompleteOverlay(bool accepted)
    {
        CloseChoices();
        if (overlayCompletion is not { } completion) return;
        if (overlayStack.TryPop(out var parent))
        {
            OverlayContent.Child = parent.View; overlayCompletion = parent.Completion;
            UpdateOverlaySize(); UpdateLayout();
            if (parent.Focus is UIElement focused && focused.IsEnabled && focused.IsVisible) focused.Focus();
            completion.TrySetResult(accepted); return;
        }
        overlayCompletion = null;
        OverlayContent.Child = null; OverlayLayer.Visibility = Visibility.Collapsed;
        foreach (var (element, enabled) in overlayBackground) element.IsEnabled = enabled;
        overlayBackground.Clear();
        if (overlayPreviousFocus is UIElement previous && previous.IsEnabled && previous.IsVisible) previous.Focus();
        else if (ChatPage.IsVisible && Composer.IsEnabled) Composer.Focus();
        else CardsNav.Focus();
        overlayPreviousFocus = null;
        completion.TrySetResult(accepted);
    }
    private void UpdateOverlaySize()
    {
        OverlayContent.MaxWidth = Math.Max(280, ActualWidth - 64);
        OverlayContent.MaxHeight = Math.Max(160, ActualHeight - 100);
        if (OverlayContent.Child is FrameworkElement view)
        {
            view.MaxWidth = OverlayContent.MaxWidth;
            view.MaxHeight = OverlayContent.MaxHeight;
        }
    }
    private void OverlayBackdrop_Down(object sender, MouseButtonEventArgs e) { CompleteOverlay(false); e.Handled = true; }
    private bool HandleOverlayKey(KeyEventArgs e)
    {
        if (!OverlayOpen) return false;
        if (e.Key == Key.Escape) { CompleteOverlay(false); e.Handled = true; }
        else if (e.Key == Key.N && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) e.Handled = true;
        return true;
    }
    internal async Task<bool> ConfirmAsync(string title, string message, string action = "删除")
    {
        var notice = new InlineNotice(title, message, confirmation: true, action: action, dangerous: true);
        notice.Completed += (_, accepted) => CompleteOverlay(accepted);
        return await ShowOverlayAsync(notice, notice.CancelButton);
    }
    internal async Task ShowNoticeAsync(string title, string message)
    {
        var notice = new InlineNotice(title, message);
        notice.Completed += (_, accepted) => CompleteOverlay(accepted);
        await ShowOverlayAsync(notice, notice.PrimaryButton);
    }
    internal Task<bool> ShowImageAsync(ImageSource source)
    {
        var panel = new DockPanel { Width = 900, Height = 640, Margin = new Thickness(20), LastChildFill = true };
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
        var close = new Button { Content = FindResource("IconClose"), Style = (Style)FindResource("IconButton"), ToolTip = "关闭图片" };
        System.Windows.Automation.AutomationProperties.SetName(close, "关闭图片");
        close.Click += (_, _) => CompleteOverlay(false); DockPanel.SetDock(close, Dock.Right); header.Children.Add(close);
        header.Children.Add(new TextBlock { Text = "图片", FontSize = 18, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        DockPanel.SetDock(header, Dock.Top); panel.Children.Add(header);
        panel.Children.Add(new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = new Image { Source = source, Stretch = Stretch.Uniform } });
        return ShowOverlayAsync(panel, close);
    }
}
