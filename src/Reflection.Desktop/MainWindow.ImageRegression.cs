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
    internal async Task<Func<Task>> PrepareClosingImageImportAsync(string output)
    {
        var fixture = Path.Combine(output, "closing-image-card.json");
        var source = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
            new byte[] { 11, 31, 71, 255, 22, 42, 82, 255, 33, 53, 93, 255, 44, 64, 104, 255 }, 8);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source));
        using var stream = new MemoryStream(); encoder.Save(stream);
        await File.WriteAllTextAsync(fixture, CharacterExchange.Export(new() { Name = "关闭期间不得导入", Image = new() { Mime = "image/png", Data = Convert.ToBase64String(stream.ToArray()) } }));
        var count = VM.Characters.Count;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new ManualResetEventSlim(); var pipeline = VM.Images;
        pipeline.BeforeDecodeForRegression = () =>
        {
            started.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new InvalidOperationException("Closing image import did not release its worker");
        };
        try
        {
            FileDialogs.SelectForRegression = _ => [fixture];
            ImportCard_Click(this, new RoutedEventArgs());
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch { release.Set(); pipeline.BeforeDecodeForRegression = null; throw; }
        finally { FileDialogs.SelectForRegression = null; }
        var pending = characterSaveCompleted!.Task;
        var released = false;
        return async () =>
        {
            if (released) return;
            released = true; release.Set();
            try
            {
                await pending.WaitAsync(TimeSpan.FromSeconds(5));
                await pipeline.DrainAsync();
                if (OverlayOpen || VM.Characters.Count != count) throw new InvalidOperationException("Closing allowed a late image crop overlay or committed an unfinished import");
            }
            finally { pipeline.BeforeDecodeForRegression = null; release.Dispose(); }
        };
    }
    internal async Task VerifyImageAssetsAsync(string output)
    {
        var path = Path.Combine(output, "asset-original.png"); WriteFixture(path, 1);
        var pipeline = VM.Images;
        var before = pipeline.GeneratedVariants;
        var first = pipeline.ImportAsync(new(path, null), CancellationToken.None);
        var second = pipeline.ImportAsync(new(path, null), CancellationToken.None);
        var image = await first; var duplicate = await second;
        if (image.Data != "" || image.AssetId != duplicate.AssetId || image.OriginalAssetId is null || image.ThumbnailAssetId is null || pipeline.GeneratedVariants != before + 1)
            throw new InvalidOperationException("Concurrent imports failed to reuse generated image assets");
        var original = await pipeline.DisplayAsync(image, 2560, true, CancellationToken.None);
        if (!original.IsFrozen || original.PixelWidth != 1600 || original.PixelHeight != 800)
            throw new InvalidOperationException("Image import discarded or resized the original");
        var decodes = pipeline.DecodedBitmaps;
        var thumbnailA = pipeline.DisplayAsync(image, 360, false, CancellationToken.None);
        var thumbnailB = pipeline.DisplayAsync(image, 360, false, CancellationToken.None);
        if (!ReferenceEquals(await thumbnailA, await thumbnailB) || pipeline.DecodedBitmaps != decodes + 1)
            throw new InvalidOperationException("Concurrent thumbnail requests did not share a frozen decode");
        var pixels = new FormatConvertedBitmap(await thumbnailA, PixelFormats.Bgra32, null, 0); var sample = new byte[4];
        pixels.CopyPixels(new Int32Rect(2, 2, 1, 1), sample, 4, 0);
        if (image.Mime != "image/png" || sample[3] != 0) throw new InvalidOperationException("PNG transparency was not preserved");
        await using (var reopened = new ImagePipeline(VM.Store.Images))
        {
            var reused = await reopened.ImportAsync(new(path, null), CancellationToken.None);
            if (reused.AssetId != image.AssetId || reopened.GeneratedVariants != 0 || reopened.DecodedBitmaps != 0)
                throw new InvalidOperationException("Derived image assets were not reusable across pipeline lifetimes");
        }
        await File.WriteAllBytesAsync(VM.Store.Images.PathFor(image.ThumbnailAssetId!), [1, 2, 3]);
        await using (var repair = new ImagePipeline(VM.Store.Images))
        {
            var rebuilt = await repair.DisplayAsync(image, 360, false, CancellationToken.None);
            if (!rebuilt.IsFrozen || repair.GeneratedVariants != 1) throw new InvalidOperationException("A corrupt thumbnail could not be rebuilt from the original");
        }
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel();
            var canceled = pipeline.DisplayAsync(image, 500, false, cancel.Token);
            var interested = pipeline.DisplayAsync(image, 500, false, CancellationToken.None);
            try { await canceled; throw new InvalidOperationException("Canceled image view completed successfully"); }
            catch (OperationCanceledException) { }
            if (!(await interested).IsFrozen) throw new InvalidOperationException("A canceled image consumer canceled another consumer");
        }

        var crop = await pipeline.CropAsync(image, new(0, 0, .375, 1), CancellationToken.None);
        var card = new CharacterCard { Name = "原图与裁剪回归", Image = crop };
        BeginCharacterEdit(card);
        CharacterEditor.CropCurrentButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitOverlayStateAsync(() => OverlayOpen && OverlayContent.Child is CharacterImageCropper);
        var cropper = (CharacterImageCropper)OverlayContent.Child;
        if (Math.Abs(cropper.Surface.Selection.Width - 600) > 1) throw new InvalidOperationException("Recropping did not restore the original source selection");
        cropper.Surface.Select(new Rect(1000, 0, 600, 800));
        cropper.ConfirmButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await WaitOverlayStateAsync(() => !OverlayOpen && CharacterEditor.DraftImage?.AssetId != crop.AssetId);
        var recropped = CharacterEditor.DraftImage!;
        var right = await pipeline.DisplayAsync(recropped, 1280, false, CancellationToken.None);
        new FormatConvertedBitmap(right, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(right.PixelWidth / 2, right.PixelHeight / 2, 1, 1), sample, 4, 0);
        if (recropped.OriginalAssetId != image.OriginalAssetId || sample[0] < 240 || sample[2] > 20 || right.PixelWidth * 4 != right.PixelHeight * 3)
            throw new InvalidOperationException("Recropping could not recover the area outside the previous crop");
        CharacterEditor.BeginEdit(null); ShowPage("cards");

        await VM.NewChatAsync(VM.Characters.First()); ShowPage("chat");
        VM.Current.Attachments.Clear();
        VM.Current.Messages.Add(new() { Content = "再次引用原图", Images = [image.Snapshot()] });
        await VM.SaveCurrentAsync(); UpdateLayout();
        await WaitOverlayStateAsync(() => Descendants<Button>(MessagesList).Any(b => b.DataContext is ChatImage && b.ContextMenu is not null));
        var imageButton = Descendants<Button>(MessagesList).First(b => b.DataContext is ChatImage && b.ContextMenu is not null);
        var menu = imageButton.ContextMenu!;
        var oldTheme = ThemeManager.IsDark ? "dark" : "light";
        try
        {
            foreach (var theme in new[] { "light", "dark" })
            {
                ThemeManager.Apply(theme); menu.PlacementTarget = imageButton; menu.IsOpen = true;
                await WaitOverlayStateAsync(() => menu.IsOpen && menu.ActualWidth > 0);
                await Task.Delay(60); menu.UpdateLayout();
                if (menu.DataContext is not ChatImage selected || selected.AssetId != image.AssetId || ((SolidColorBrush)menu.Background).Color != ((SolidColorBrush)FindResource("Panel")).Color)
                    throw new InvalidOperationException("Image reuse menu lost its picture binding or theme");
                ((MenuItem)menu.Items[0]).Focus();
                var capture = Path.Combine(output, "image-menu-" + theme + ".png");
                App.CaptureElement(menu, capture, 96);
                using (var rendered = File.OpenRead(capture))
                {
                    var menuBitmap = new FormatConvertedBitmap(ImageCodec.Decode(rendered, 500), PixelFormats.Bgra32, null, 0);
                    menuBitmap.CopyPixels(new Int32Rect(2, menuBitmap.PixelHeight / 2, 1, 1), sample, 4, 0);
                    var expected = ((SolidColorBrush)FindResource("Panel")).Color;
                    if (sample[0] != expected.B || sample[1] != expected.G || sample[2] != expected.R)
                        throw new InvalidOperationException("Rendered image menu did not follow " + theme + " theme");
                }
                menu.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(menu)!, Environment.TickCount, Key.Escape) { RoutedEvent = Keyboard.KeyDownEvent });
                await WaitOverlayStateAsync(() => !menu.IsOpen);
            }
        }
        finally { menu.IsOpen = false; ThemeManager.Apply(oldTheme); }
        ((MenuItem)menu.Items[0]).RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        await WaitOverlayStateAsync(() => VM.Current.Attachments.Count == 1);
        if (VM.Current.Attachments[0].AssetId != image.AssetId) throw new InvalidOperationException("Reusing a historical image did not reuse its asset");
        VM.Current.Attachments.Clear();

        // Block the worker after intake to verify cancellation and responsiveness deterministically.
        await VerifyCanceledImageTargetAsync(Path.Combine(output, "asset-navigation.png"), 2, false);
        await VerifyCanceledImageTargetAsync(Path.Combine(output, "asset-editor-cancel.png"), 3, true);
        await pipeline.DrainAsync();
        if (VM.SelectedModel is not null)
        {
            await VM.NewChatAsync(VM.Characters.First());
            for (var i = 0; i < 17; i++)
            {
                VM.Current.Messages.Add(new() { Content = "图片轮次 " + i, Images = [image.Snapshot()] });
                VM.Current.Messages.Add(new() { Role = "assistant", Content = "图片回复 " + i });
            }
            VM.Draft = "图片上下文预算回归"; await VM.SendAsync();
            if (VM.Current.Messages.Last().HasError || !VM.Status.Contains("较早的 1 张图片") || VM.Current.Messages.Sum(x => x.Images.Count) != 17)
                throw new InvalidOperationException("Image context trimming failed to report its boundary or changed persisted history");
        }
        await VM.NewChatAsync(VM.Characters.First());
        ShowPage("cards");

        async Task VerifyCanceledImageTargetAsync(string fixture, byte marker, bool editor)
        {
            WriteFixture(fixture, marker);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var release = new ManualResetEventSlim();
            var uiThread = Environment.CurrentManagedThreadId;
            pipeline.BeforeDecodeForRegression = () =>
            {
                if (Environment.CurrentManagedThreadId == uiThread || Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
                    throw new InvalidOperationException("Image processing ran on the UI thread or outside STA");
                started.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new InvalidOperationException("Image worker regression did not release");
            };
            Task<bool>? pending = null;
            try
            {
                if (editor)
                {
                    BeginCharacterEdit(null);
                    pending = CharacterEditor.ReceiveImageDropAsync(new DataObject(DataFormats.FileDrop, new[] { fixture }));
                }
                else
                {
                    await VM.NewChatAsync(VM.Characters.First()); ShowPage("chat");
                    VM.Draft = "保留的旧会话草稿"; await VM.SaveCurrentAsync();
                    pending = ReceiveComposerDrop(new DataObject(DataFormats.FileDrop, new[] { fixture }));
                }
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await Dispatcher.InvokeAsync(() => UpdateLayout()); // Must remain runnable while decoding is blocked.
                if (editor)
                {
                    CharacterEditor.BeginEdit(card);
                    if (await pending) throw new InvalidOperationException("Canceled editor image work reported success");
                    if (CharacterEditor.DraftImage != card.Image || OverlayOpen) throw new InvalidOperationException("Canceled image work overwrote a different editor draft");
                    CharacterEditor.BeginEdit(null); ShowPage("cards");
                }
                else
                {
                    var oldId = VM.Current.Id;
                    await VM.NewChatAsync(VM.Characters.First());
                    if (await pending || VM.Current.Id == oldId || VM.Current.Attachments.Count != 0 || VM.AvailableImageSlots != 4)
                        throw new InvalidOperationException("Pending image crossed conversation navigation or retained its reservation");
                    var saved = await VM.Store.LoadConversationAsync(oldId);
                    if (saved.Draft != "保留的旧会话草稿" || saved.Attachments.Count != 0) throw new InvalidOperationException("Image cancellation lost the previous draft");
                }
            }
            finally
            {
                release.Set(); pipeline.BeforeDecodeForRegression = null;
                if (pending is not null) await pending;
                await pipeline.DrainAsync();
            }
        }
        static void WriteFixture(string target, byte marker)
        {
            var bytes = new byte[1600 * 800 * 4];
            for (var y = 0; y < 800; y++) for (var x = 0; x < 1600; x++)
            {
                var index = (y * 1600 + x) * 4;
                bytes[index] = x >= 800 ? (byte)255 : (byte)0;
                bytes[index + 2] = x < 800 ? (byte)255 : (byte)0;
                bytes[index + 3] = x < 100 && y < 100 ? (byte)0 : (byte)255;
            }
            bytes[^3] = marker;
            var bitmap = BitmapSource.Create(1600, 800, 96, 96, PixelFormats.Bgra32, null, bytes, 6400);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(target); encoder.Save(stream);
        }
    }
}
