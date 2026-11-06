using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
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
    private bool followPending;
    private void RequestChatFollow()
    {
        if (followPending || !ChatPage.IsVisible || !followOutput || VM.Current.Messages.Count == 0) return;
        followPending = true;
        Dispatcher.InvokeAsync(() =>
        {
            followPending = false;
            if (ChatPage.IsVisible && followOutput && VM.Current.Messages.Count > 0) MessagesList.ScrollIntoView(VM.Current.Messages.Last());
        }, DispatcherPriority.Background);
    }

    private void InitializeScrolling()
    {
        PreviewMouseDown += (_, e) => { if (AutoScrollActive) { StopAutoScroll(); e.Handled = true; } };
        PreviewMouseWheel += (_, _) => StopAutoScroll();
        Deactivated += (_, _) => StopAutoScroll();
        Closed += (_, _) => StopAutoScroll();
        ReaderPage.DocumentList.PreviewMouseDown += Messages_MouseDown;
        VM.PropertyChanged += WorkspacePropertyChanged;
    }
    private void WorkspacePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) { if (e.PropertyName == nameof(VM.Current)) StopAutoScroll(); }
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
        if (surface == MessagesList) followOutput = viewer.VerticalOffset >= viewer.ScrollableHeight - 48;
    }
}
