using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Reflection.Core;

namespace Reflection.Desktop;

public static class SmoothScroll
{
    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached("Enabled", typeof(bool), typeof(SmoothScroll), new PropertyMetadata(false, Changed));
    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached("State", typeof(Motion), typeof(SmoothScroll));
    public static bool GetEnabled(DependencyObject obj) => (bool)obj.GetValue(EnabledProperty);
    public static void SetEnabled(DependencyObject obj, bool value) => obj.SetValue(EnabledProperty, value);
    private static void Changed(DependencyObject obj, DependencyPropertyChangedEventArgs args)
    {
        if (obj is not FrameworkElement element) return;
        if (element.GetValue(StateProperty) is Motion old) old.Detach();
        element.SetValue(StateProperty, (bool)args.NewValue ? new Motion(element) : null);
    }
    internal static void Stop(FrameworkElement element) { if (element.GetValue(StateProperty) is Motion motion) motion.Stop(); }
    internal sealed record Metrics(int Frames, double MedianInterval, double P95Interval, double MaximumInterval, double MaximumCallback);
    internal static Metrics Diagnostics(FrameworkElement element) => element.GetValue(StateProperty) is Motion motion ? motion.Diagnostics() : new(0, 0, 0, 0, 0);
    internal static ScrollViewer? Viewer(DependencyObject root)
    {
        if (root is ScrollViewer scroll) return scroll;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) if (Viewer(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }
    private sealed class Motion
    {
        private readonly FrameworkElement owner;
        private ScrollViewer? viewer;
        private readonly Stopwatch clock = new();
        private readonly ScrollDynamics dynamics = new();
        private readonly Queue<double> intervals = new();
        private double previousTime, lastRequested, maximumCallback;
        private TimeSpan? renderingTime;
        private bool active, pendingDirect;
        public Motion(FrameworkElement owner)
        {
            this.owner = owner;
            owner.PreviewMouseWheel += Wheel; owner.PreviewMouseDown += Interrupt; owner.PreviewKeyDown += Key;
            owner.Unloaded += Unloaded; owner.IsVisibleChanged += Visibility;
        }
        public Metrics Diagnostics()
        {
            var samples = intervals.Order().ToArray();
            return samples.Length == 0 ? new(0, 0, 0, 0, maximumCallback) : new(samples.Length, samples[samples.Length / 2], samples[Math.Min(samples.Length - 1, (int)(samples.Length * .95))], samples[^1], maximumCallback);
        }
        public void Detach()
        {
            Stop(); owner.PreviewMouseWheel -= Wheel; owner.PreviewMouseDown -= Interrupt; owner.PreviewKeyDown -= Key;
            owner.Unloaded -= Unloaded; owner.IsVisibleChanged -= Visibility;
            DisconnectViewer();
        }
        public void Stop()
        {
            if (active) CompositionTarget.Rendering -= Frame;
            active = pendingDirect = false; clock.Stop(); renderingTime = null;
        }
        private void DisconnectViewer() { if (viewer is not null) viewer.ScrollChanged -= ScrollChanged; viewer = null; }
        private void Interrupt(object sender, MouseButtonEventArgs e) => Stop();
        private void Key(object sender, KeyEventArgs e) => Stop();
        private void Unloaded(object sender, RoutedEventArgs e) { Stop(); DisconnectViewer(); }
        private void Visibility(object sender, DependencyPropertyChangedEventArgs e) { if (!(bool)e.NewValue) Stop(); }
        private void ScrollChanged(object sender, ScrollChangedEventArgs e)
        {
            if (viewer is null || !ReferenceEquals(e.OriginalSource, viewer)) return;
            if (active && e.ExtentHeightChange != 0)
                dynamics.Shift(viewer.VerticalOffset - lastRequested, viewer.ScrollableHeight);
            if (pendingDirect && (e.ExtentHeightChange != 0 || Math.Abs(viewer.VerticalOffset - lastRequested) < .5)) { pendingDirect = false; lastRequested = viewer.VerticalOffset; }
        }
        private void Wheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Handled || e.Delta == 0 || Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
            if (viewer is null) { viewer = Viewer(owner); if (viewer is not null) viewer.ScrollChanged += ScrollChanged; }
            if (viewer is null || viewer.ScrollableHeight <= 0) return;
            var lines = SystemParameters.WheelScrollLines; if (lines == 0) return;
            var delta = -e.Delta / 120d * (lines < 0 ? viewer.ViewportHeight * .9 : lines * 22);
            if (!active) dynamics.Reset(pendingDirect ? lastRequested : viewer.VerticalOffset);
            dynamics.Add(delta, viewer.ScrollableHeight); e.Handled = true;
            if (!SystemParameters.ClientAreaAnimation || Math.Abs(e.Delta) < 120)
            {
                var target = dynamics.Target; Stop(); dynamics.Reset(target);
                pendingDirect = true; lastRequested = target; viewer.ScrollToVerticalOffset(target); return;
            }
            if (!active)
            {
                pendingDirect = false; previousTime = 0; renderingTime = null; clock.Restart();
                lastRequested = dynamics.Step(1d / 120); viewer.ScrollToVerticalOffset(lastRequested);
                active = true; CompositionTarget.Rendering += Frame;
            }
        }
        private void Frame(object? sender, EventArgs e)
        {
            if (viewer is null || !owner.IsVisible) { Stop(); return; }
            if (e is RenderingEventArgs tick)
            { if (renderingTime == tick.RenderingTime) return; renderingTime = tick.RenderingTime; }
            var start = Stopwatch.GetTimestamp(); var now = clock.Elapsed.TotalSeconds;
            var elapsed = now - previousTime; previousTime = now;
            if (elapsed > 0) { intervals.Enqueue(elapsed * 1000); if (intervals.Count > 240) intervals.Dequeue(); }
            dynamics.Shift(0, viewer.ScrollableHeight);
            lastRequested = dynamics.Step(Math.Min(.05, elapsed)); viewer.ScrollToVerticalOffset(lastRequested);
            maximumCallback = Math.Max(maximumCallback, Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            if (dynamics.Settled) Stop();
        }
    }
}
