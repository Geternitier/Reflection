using System.Windows;
using System.Windows.Controls;
using Reflection.Core;

namespace Reflection.Desktop;

public sealed class AssetImage : Image
{
    public static readonly DependencyProperty PictureProperty = DependencyProperty.Register(nameof(Picture), typeof(ChatImage), typeof(AssetImage), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty DecodeSizeProperty = DependencyProperty.Register(nameof(DecodeSize), typeof(int), typeof(AssetImage), new PropertyMetadata(360, Changed));
    public ChatImage? Picture { get => (ChatImage?)GetValue(PictureProperty); set => SetValue(PictureProperty, value); }
    public int DecodeSize { get => (int)GetValue(DecodeSizeProperty); set => SetValue(DecodeSizeProperty, value); }
    private CancellationTokenSource? pending;
    public AssetImage()
    {
        Loaded += (_, _) => Refresh();
        Unloaded += (_, _) => Cancel();
        IsVisibleChanged += (_, _) => { if (IsVisible) Refresh(); else Cancel(); };
    }
    private static void Changed(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((AssetImage)d).Refresh();
    private void Cancel() { pending?.Cancel(); pending?.Dispose(); pending = null; Source = null; }
    private async void Refresh()
    {
        Cancel();
        if (!IsLoaded || !IsVisible || Picture is not { } picture || Window.GetWindow(this) is not MainWindow owner) return;
        var pipeline = owner.VM.Images;
        var request = new CancellationTokenSource(); pending = request;
        try
        {
            var source = await pipeline.DisplayAsync(picture.Snapshot(), DecodeSize, false, request.Token);
            if (ReferenceEquals(pending, request)) { Source = source; ToolTip = null; }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is System.IO.IOException or FormatException or ArgumentException or InvalidOperationException or NotSupportedException or UnauthorizedAccessException)
        { if (ReferenceEquals(pending, request)) ToolTip = "图片无法显示：" + ex.Message; }
    }
}
