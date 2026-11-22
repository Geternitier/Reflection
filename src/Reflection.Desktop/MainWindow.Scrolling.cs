using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Reflection.Desktop;

public partial class MainWindow
{
    private readonly Stopwatch autoScrollClock = new();
    private TimeSpan? autoScrollRenderingTime;
    private bool autoScrollRendering;
    private double autoScrollPosition, autoScrollRequested;
    private ScrollViewer? autoScrollViewer;
    private FrameworkElement? autoScrollSurface;
    private Point autoScrollOrigin;
    private Cursor? priorAutoCursor;
    internal bool AutoScrollActive => autoScrollViewer is not null;
    private const double BottomTolerance = 1;
    private bool followPending;
    private void RequestChatFollow()
    {
        if (followPending || !ChatPage.IsVisible || !followOutput || VM.Current.Messages.Count == 0) return;
        followPending = true;
        Dispatcher.InvokeAsync(() =>
        {
            followPending = false;
            if (MessagesList.IsVisible && followOutput && SmoothScroll.Viewer(MessagesList) is { } viewer
                && viewer.ScrollableHeight - viewer.VerticalOffset > BottomTolerance)
                viewer.ScrollToBottom();
        }, DispatcherPriority.Background);
    }

    private void Messages_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        // Nested text and formula viewers also raise ScrollChanged. Only the message viewport can follow.
        if (!MessagesList.IsVisible || e.OriginalSource is not ScrollViewer viewer
            || !ReferenceEquals(viewer.TemplatedParent, MessagesList) || autoScrollSurface == MessagesList) return;
        var atBottom = viewer.ScrollableHeight - viewer.VerticalOffset <= BottomTolerance;
        if (followOutput)
        {
            if (!atBottom) RequestChatFollow();
        }
        else if (atBottom && e.VerticalChange > 0 && e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0)
            followOutput = true;
    }

    private void Messages_ScrollBarScroll(object sender, ScrollEventArgs e)
    {
        if (e.OriginalSource is ScrollBar { Orientation: Orientation.Vertical, TemplatedParent: ScrollViewer viewer }
            && ReferenceEquals(viewer.TemplatedParent, MessagesList))
            followOutput = viewer.ScrollableHeight - e.NewValue <= BottomTolerance;
    }

    private void InitializeScrolling()
    {
        PreviewMouseDown += (_, e) => { if (AutoScrollActive) { StopAutoScroll(); e.Handled = true; } };
        PreviewMouseWheel += (_, _) => StopAutoScroll();
        Deactivated += (_, _) => StopAutoScroll();
        Closed += (_, _) => StopAutoScroll();
        MessagesList.IsVisibleChanged += (_, _) => RequestChatFollow();
        MessagesList.AddHandler(ScrollBar.ScrollEvent, new ScrollEventHandler(Messages_ScrollBarScroll), true);
        MessagesList.AddHandler(Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler((_, e) =>
        {
            if (e.Delta > 0 && e.Handled && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) == 0
                && SmoothScroll.Viewer(MessagesList) is { ScrollableHeight: > 0 }) followOutput = false;
        }), true);
        MessagesList.PreviewKeyDown += (_, e) =>
        {
            if ((e.Key == Key.PageUp && Keyboard.Modifiers == ModifierKeys.None
                || e.Key == Key.Home && Keyboard.Modifiers == ModifierKeys.Control)
                && SmoothScroll.Viewer(MessagesList) is { ScrollableHeight: > 0 }) followOutput = false;
        };
        ReaderPage.DocumentList.PreviewMouseDown += Messages_MouseDown;
        VM.PropertyChanged += WorkspacePropertyChanged;
    }
    private void WorkspacePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(VM.Current)) return;
        StopAutoScroll();
        SmoothScroll.Stop(MessagesList);
        followOutput = true;
        RequestChatFollow();
    }
    private void Messages_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle || OverlayOpen || ChoiceOpen) return;
        BeginAutoScroll(e.GetPosition(currentPage == "reader" ? ReaderPage.DocumentList : MessagesList)); e.Handled = true;
    }
    internal void BeginAutoScroll(Point origin, bool startTimer = true)
    {
        StopAutoScroll();
        var surface = currentPage == "reader" ? ReaderPage.DocumentList : MessagesList;
        SmoothScroll.Stop(surface);
        var viewer = FindChild<ScrollViewer>(surface);
        if (viewer is null || viewer.ScrollableHeight <= 0) return;
        autoScrollViewer = viewer; autoScrollSurface = surface; autoScrollOrigin = origin;
        if (surface == MessagesList) followOutput = false;
        var point = surface.TranslatePoint(origin, WorkspaceRoot);
        Canvas.SetLeft(AutoScrollLayer.Children[0], point.X - 14); Canvas.SetTop(AutoScrollLayer.Children[0], point.Y - 14);
        AutoScrollLayer.Visibility = Visibility.Visible;
        priorAutoCursor = Mouse.OverrideCursor; Mouse.OverrideCursor = Cursors.ScrollNS;
        autoScrollPosition = autoScrollRequested = viewer.VerticalOffset;
        autoScrollRenderingTime = null; autoScrollClock.Restart();
        if (startTimer) { autoScrollRendering = true; CompositionTarget.Rendering += RenderAutoScroll; }
    }
    private void RenderAutoScroll(object? sender, EventArgs e)
    {
        if (autoScrollSurface?.IsVisible != true || OverlayOpen || ChoiceOpen || autoScrollViewer?.ScrollableHeight <= 0) { StopAutoScroll(); return; }
        if (e is RenderingEventArgs frame)
        {
            if (autoScrollRenderingTime == frame.RenderingTime) return;
            autoScrollRenderingTime = frame.RenderingTime;
        }
        var elapsed = Math.Min(.05, autoScrollClock.Elapsed.TotalSeconds); autoScrollClock.Restart();
        StepAutoScroll(Mouse.GetPosition(autoScrollSurface), elapsed);
    }
    internal void StepAutoScroll(Point point, double seconds)
    {
        if (autoScrollViewer is not { } viewer) return;
        var distance = point.Y - autoScrollOrigin.Y;
        var speed = Math.Sign(distance) * Math.Min(1200, Math.Max(0, Math.Abs(distance) - 12) * 5);
        // Preserve subpixel motion, but present text on physical pixel boundaries.
        // Correct for virtualized content rebasing its scroll coordinates.
        autoScrollPosition += viewer.VerticalOffset - autoScrollRequested;
        autoScrollPosition = Math.Clamp(autoScrollPosition + speed * Math.Max(0, seconds), 0, viewer.ScrollableHeight);
        var scale = VisualTreeHelper.GetDpi(viewer).DpiScaleY;
        autoScrollRequested = Math.Clamp(Math.Round(autoScrollPosition * scale) / scale, 0, viewer.ScrollableHeight);
        viewer.ScrollToVerticalOffset(autoScrollRequested);
    }
    internal void StopAutoScroll()
    {
        if (autoScrollRendering) CompositionTarget.Rendering -= RenderAutoScroll;
        autoScrollRendering = false; autoScrollRenderingTime = null; autoScrollClock.Stop();
        if (autoScrollViewer is not { } viewer) return;
        var surface = autoScrollSurface; autoScrollSurface = null;
        autoScrollViewer = null; AutoScrollLayer.Visibility = Visibility.Collapsed;
        Mouse.OverrideCursor = priorAutoCursor; priorAutoCursor = null;
        if (surface == MessagesList) followOutput = viewer.ScrollableHeight - viewer.VerticalOffset <= BottomTolerance;
    }
}
