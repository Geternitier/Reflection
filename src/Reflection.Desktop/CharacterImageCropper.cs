using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Reflection.Core;

namespace Reflection.Desktop;

internal sealed class CharacterImageCropper : UserControl
{
    internal CropSurface Surface { get; }
    internal ChatImage? Result { get; private set; }
    internal Button ConfirmButton { get; }
    internal CharacterImageCropper(MainWindow owner, BitmapSource image, ChatImage? original = null, CancellationToken cancellationToken = default)
    {
        Width = 780; Height = 640; Surface = new CropSurface(image);
        if (original?.Crop is { } crop) Surface.Select(new Rect(crop.X * image.PixelWidth, crop.Y * image.PixelHeight, crop.Width * image.PixelWidth, crop.Height * image.PixelHeight));
        var cancelWork = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var retired = false;
        Unloaded += (_, _) => { if (!retired) { retired = true; cancelWork.Cancel(); cancelWork.Dispose(); } };
        var pipeline = owner.VM.Images;
        var captured = image.IsFrozen ? image : image.CloneCurrentValue();
        if (!captured.IsFrozen) captured.Freeze();
        var panel = new DockPanel { Margin = new Thickness(24) }; Content = panel;
        var heading = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        heading.Children.Add(new TextBlock { Text = "裁剪角色图片", FontSize = 20 });
        var hint = new TextBlock { Text = "拖动选框调整位置，拖动角点或滚轮调整大小；框外拖动可重新选取。", FontSize = 12, Margin = new Thickness(0, 8, 0, 0) };
        hint.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); heading.Children.Add(hint);
        DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
        var footer = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        var cancel = new Button { Content = "取消", Margin = new Thickness(0, 0, 10, 0) };
        cancel.Click += (_, _) => owner.CompleteOverlay(false); buttons.Children.Add(cancel);
        ConfirmButton = new Button { Content = "使用裁剪", Style = (Style)owner.FindResource("Primary") };
        ConfirmButton.Click += async (_, _) =>
        {
            ConfirmButton.IsEnabled = false;
            var selection = Surface.Selection;
            var crop = new ImageCrop(selection.X / image.PixelWidth, selection.Y / image.PixelHeight, selection.Width / image.PixelWidth, selection.Height / image.PixelHeight);
            try
            {
                var source = original ?? await pipeline.ImportAsync(new(null, captured), cancelWork.Token);
                var result = await pipeline.CropAsync(source, crop, cancelWork.Token);
                if (!cancelWork.IsCancellationRequested && owner.IsOverlayContent(this))
                { Result = result; owner.CompleteOverlay(true); }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or InvalidOperationException)
            { hint.Text = ex.Message; hint.SetResourceReference(TextBlock.ForegroundProperty, "Danger"); }
            finally { ConfirmButton.IsEnabled = true; }
        };
        buttons.Children.Add(ConfirmButton); DockPanel.SetDock(buttons, Dock.Right); footer.Children.Add(buttons);
        footer.Children.Add(new TextBlock { Text = "图片比例 3:4", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
        DockPanel.SetDock(footer, Dock.Bottom); panel.Children.Add(footer);
        panel.Children.Add(Surface);
    }
    internal static async Task<ChatImage?> ShowAsync(MainWindow owner, ChatImage original, CancellationToken ct = default)
    {
        var source = await owner.VM.Images.DisplayAsync(original, 2560, true, ct);
        ct.ThrowIfCancellationRequested();
        var cropper = new CharacterImageCropper(owner, source, original, ct);
        using var registration = ct.Register(() => owner.Dispatcher.BeginInvoke(() =>
        {
            if (owner.IsOverlayContent(cropper)) owner.CompleteOverlay(false);
        }));
        return await owner.ShowOverlayAsync(cropper, cropper.Surface) ? cropper.Result : null;
    }

}

internal sealed class CropSurface : FrameworkElement
{
    private readonly BitmapSource source;
    private Rect displayed;
    private Point start;
    private Rect previous;
    private int handle = -1;
    private bool moving;
    internal Rect Selection { get; private set; }
    internal CropSurface(BitmapSource image)
    {
        source = image; Focusable = true; Cursor = Cursors.Cross;
        System.Windows.Automation.AutomationProperties.SetName(this, "角色图片裁剪区域");
        Reset(); SizeChanged += (_, _) => InvalidateVisual();
    }
    private void Reset()
    {
        var height = Math.Min(source.PixelHeight, source.PixelWidth * 4d / 3);
        Selection = new Rect((source.PixelWidth - height * .75) / 2, (source.PixelHeight - height) / 2, height * .75, height);
        InvalidateVisual();
    }
    internal void Select(Rect desired)
    {
        var maximum = Math.Min(source.PixelHeight, source.PixelWidth / .75);
        var height = Math.Clamp(desired.Height, Math.Min(4, maximum), maximum);
        var width = height * .75;
        Selection = new Rect(Math.Clamp(desired.X, 0, source.PixelWidth - width), Math.Clamp(desired.Y, 0, source.PixelHeight - height), width, height);
        InvalidateVisual();
    }
    private Point Pixels(Point point) => new((point.X - displayed.X) * source.PixelWidth / Math.Max(1, displayed.Width), (point.Y - displayed.Y) * source.PixelHeight / Math.Max(1, displayed.Height));
    private Rect Screen(Rect selection) => new(displayed.X + selection.X * displayed.Width / source.PixelWidth, displayed.Y + selection.Y * displayed.Height / source.PixelHeight, selection.Width * displayed.Width / source.PixelWidth, selection.Height * displayed.Height / source.PixelHeight);
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var scale = Math.Min(ActualWidth / source.PixelWidth, ActualHeight / source.PixelHeight);
        displayed = new Rect((ActualWidth - source.PixelWidth * scale) / 2, (ActualHeight - source.PixelHeight * scale) / 2, source.PixelWidth * scale, source.PixelHeight * scale);
        dc.DrawRectangle((Brush)Application.Current.FindResource("Sidebar"), null, new Rect(RenderSize));
        dc.DrawImage(source, displayed);
        var crop = Screen(Selection);
        var shade = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(displayed), new RectangleGeometry(crop));
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(140, 0, 0, 0)), null, shade);
        var pen = new Pen(Brushes.White, 1.5);
        dc.DrawRectangle(null, pen, crop);
        dc.PushClip(new RectangleGeometry(crop));
        for (var i = 1; i < 3; i++)
        {
            var grid = new Pen(new SolidColorBrush(Color.FromArgb(100, 255, 255, 255)), 1);
            dc.DrawLine(grid, new Point(crop.Left + crop.Width * i / 3, crop.Top), new Point(crop.Left + crop.Width * i / 3, crop.Bottom));
            dc.DrawLine(grid, new Point(crop.Left, crop.Top + crop.Height * i / 3), new Point(crop.Right, crop.Top + crop.Height * i / 3));
        }
        dc.Pop();
        foreach (var corner in new[] { crop.TopLeft, crop.TopRight, crop.BottomRight, crop.BottomLeft })
            dc.DrawRoundedRectangle(Brushes.White, null, new Rect(corner.X - 4, corner.Y - 4, 8, 8), 2, 2);
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e); Focus();
        var point = e.GetPosition(this);
        if (!displayed.Contains(point)) return;
        var corners = new[] { Screen(Selection).TopLeft, Screen(Selection).TopRight, Screen(Selection).BottomRight, Screen(Selection).BottomLeft };
        handle = Array.FindIndex(corners, corner => (corner - point).Length <= 12);
        start = Pixels(point); previous = Selection; moving = handle < 0 && Selection.Contains(start);
        CaptureMouse(); e.Handled = true;
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e); if (!IsMouseCaptured) return;
        var point = Pixels(e.GetPosition(this));
        if (moving) Select(new Rect(previous.TopLeft + (point - start), previous.Size));
        else
        {
            var anchor = handle switch { 0 => previous.BottomRight, 1 => previous.BottomLeft, 2 => previous.TopLeft, 3 => previous.TopRight, _ => start };
            var dx = point.X - anchor.X; var dy = point.Y - anchor.Y;
            var maxWidth = dx >= 0 ? source.PixelWidth - anchor.X : anchor.X;
            var maxHeight = dy >= 0 ? source.PixelHeight - anchor.Y : anchor.Y;
            var height = Math.Min(Math.Max(Math.Abs(dy), Math.Abs(dx) / .75), Math.Min(maxHeight, maxWidth / .75));
            if (height > 0) Select(new Rect(dx >= 0 ? anchor.X : anchor.X - height * .75, dy >= 0 ? anchor.Y : anchor.Y - height, height * .75, height));
        }
        e.Handled = true;
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) { base.OnMouseLeftButtonUp(e); if (IsMouseCaptured) { ReleaseMouseCapture(); e.Handled = true; } }
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e); var factor = e.Delta > 0 ? .9 : 1.1;
        var height = Selection.Height * factor;
        Select(new Rect(Selection.X + (Selection.Width - height * .75) / 2, Selection.Y + (Selection.Height - height) / 2, height * .75, height)); e.Handled = true;
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e); var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
        var delta = e.Key switch { Key.Left => new Vector(-step, 0), Key.Right => new Vector(step, 0), Key.Up => new Vector(0, -step), Key.Down => new Vector(0, step), _ => new Vector() };
        if (delta.Length > 0) { Select(new Rect(Selection.TopLeft + delta, Selection.Size)); e.Handled = true; }
    }
}
