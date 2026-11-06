using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Reflection.Desktop;

// Stable row metrics and bounded realization, including very large character libraries.
public sealed class GalleryPanel : VirtualizingPanel, IScrollInfo
{
    private const double Gap = 18;
    private int columns = 1;
    private double cardWidth, rowHeight, offset, extent, viewport, width;
    public GalleryPanel() => ClipToBounds = true;
    protected override Size MeasureOverride(Size available)
    {
        width = double.IsFinite(available.Width) ? available.Width : 800;
        viewport = double.IsFinite(available.Height) ? available.Height : 600;
        var owner = ItemsControl.GetItemsOwner(this);
        if (owner is null) return new Size(width, viewport);
        var previousColumns = columns; var previousHeight = rowHeight;
        columns = Math.Max(1, (int)((width + Gap) / (240 + Gap)));
        cardWidth = Math.Max(1, (width - Gap * (columns - 1)) / columns);
        rowHeight = Math.Max(160, (cardWidth - 20) * 4 / 3 + 154);
        if (previousHeight > 0 && previousColumns != columns)
            offset = Math.Floor(offset / previousHeight) * previousColumns / columns * rowHeight;
        extent = Math.Max(0, Math.Ceiling(owner.Items.Count / (double)columns) * rowHeight - Gap);
        offset = Math.Clamp(offset, 0, Math.Max(0, extent - viewport));
        ScrollOwner?.InvalidateScrollInfo();
        var first = Math.Max(0, ((int)(offset / rowHeight) - 1) * columns);
        var last = Math.Min(owner.Items.Count - 1, ((int)((offset + viewport) / rowHeight) + 2) * columns - 1);
        _ = InternalChildren; // Initializing the children also connects the WPF generator.
        var generator = ItemContainerGenerator;
        for (var i = InternalChildren.Count - 1; i >= 0; i--)
        {
            var position = new GeneratorPosition(i, 0);
            var index = generator.IndexFromGeneratorPosition(position);
            if (index >= first && index <= last) continue;
            if (generator is IRecyclingItemContainerGenerator recycling) recycling.Recycle(position, 1); else generator.Remove(position, 1);
            RemoveInternalChildRange(i, 1);
        }
        var start = generator.GeneratorPositionFromIndex(first);
        var childIndex = start.Offset == 0 ? start.Index : start.Index + 1;
        using (generator.StartAt(start, GeneratorDirection.Forward, true))
            for (var index = first; index <= last; index++, childIndex++)
            {
                var child = (UIElement)generator.GenerateNext(out var created);
                if (created || childIndex >= InternalChildren.Count || !ReferenceEquals(InternalChildren[childIndex], child))
                {
                    var previousIndex = InternalChildren.IndexOf(child);
                    if (previousIndex >= 0) RemoveInternalChildRange(previousIndex, 1);
                    if (childIndex >= InternalChildren.Count) AddInternalChild(child); else InsertInternalChild(childIndex, child);
                    generator.PrepareItemContainer(child);
                }
                child.Measure(new Size(cardWidth, rowHeight - Gap));
            }
        return new Size(width, viewport);
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        for (var i = 0; i < InternalChildren.Count; i++)
        {
            var index = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            InternalChildren[i].Arrange(new Rect(index % columns * (cardWidth + Gap), index / columns * rowHeight - offset, cardWidth, rowHeight - Gap));
        }
        return finalSize;
    }
    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        base.OnItemsChanged(sender, args);
        if (args.Action is NotifyCollectionChangedAction.Remove or NotifyCollectionChangedAction.Replace or NotifyCollectionChangedAction.Move && args.ItemUICount > 0)
            RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
        InvalidateMeasure();
    }
    protected override void BringIndexIntoView(int index) => SetVerticalOffset(index / columns * rowHeight);
    public ScrollViewer? ScrollOwner { get; set; }
    public bool CanVerticallyScroll { get; set; }
    public bool CanHorizontallyScroll { get; set; }
    public double ExtentHeight => extent;
    public double ExtentWidth => width;
    public double ViewportHeight => viewport;
    public double ViewportWidth => width;
    public double VerticalOffset => offset;
    public double HorizontalOffset => 0;
    internal int ColumnCount => columns;
    internal string Metrics => $"width={width:F1}, viewport={viewport:F1}, extent={extent:F1}, offset={offset:F1}, columns={columns}, row={rowHeight:F1}, children={InternalChildren.Count}; " + string.Join("; ", InternalChildren.Cast<UIElement>().Select((c, i) => $"index={ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0))}, size={c.RenderSize}, at={VisualTreeHelper.GetOffset(c)}"));
    public void SetVerticalOffset(double value) { offset = Math.Clamp(value, 0, Math.Max(0, extent - viewport)); InvalidateMeasure(); ScrollOwner?.InvalidateScrollInfo(); }
    public void SetHorizontalOffset(double value) { }
    public void LineUp() => SetVerticalOffset(offset - 22);
    public void LineDown() => SetVerticalOffset(offset + 22);
    public void MouseWheelUp() => SetVerticalOffset(offset - 66);
    public void MouseWheelDown() => SetVerticalOffset(offset + 66);
    public void PageUp() => SetVerticalOffset(offset - viewport * .9);
    public void PageDown() => SetVerticalOffset(offset + viewport * .9);
    public void LineLeft() { } public void LineRight() { } public void MouseWheelLeft() { } public void MouseWheelRight() { } public void PageLeft() { } public void PageRight() { }
    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        var point = visual.TransformToAncestor(this).Transform(rectangle.TopLeft);
        if (point.Y < 0) SetVerticalOffset(offset + point.Y);
        else if (point.Y + rectangle.Height > viewport) SetVerticalOffset(offset + point.Y + rectangle.Height - viewport);
        return rectangle;
    }
}
