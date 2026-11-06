using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Reflection.Desktop;

public partial class MainWindow
{
    internal async Task VerifyPortraitsAndHistoryAsync(string output)
    {
        ShowPage("cards"); SetSidebar(true); await Task.Delay(220); UpdateLayout();
        var portraits = Descendants<PortraitFrame>(CharacterGallery).ToArray();
        if (portraits.Length == 0 || portraits.Any(frame => Math.Abs(frame.ActualWidth / frame.ActualHeight - .75) > .002))
            throw new InvalidOperationException("Gallery images are not displayed as 3:4 portraits");
        if (!PageLabel.IsVisible || PageLabel.Text != "角色" || Descendants<TextBlock>(CardsCanvas).Any(t => t.Text == "角色")) throw new InvalidOperationException("Character title did not use the shared header");
        App.Capture(this, Path.Combine(output, "portrait-gallery.png"));
        var pixels = new byte[120 * 80 * 4];
        for (var y = 0; y < 80; y++) for (var x = 0; x < 120; x++)
        {
            var index = (y * 120 + x) * 4;
            pixels[index] = x < 60 ? (byte)0 : (byte)255; pixels[index + 2] = x < 60 ? (byte)255 : (byte)0; pixels[index + 3] = 255;
        }
        var bitmap = BitmapSource.Create(120, 80, 96, 96, PixelFormats.Bgra32, null, pixels, 480);
        var cropper = new CharacterImageCropper(this, bitmap);
        var pending = ShowOverlayAsync(cropper, cropper.Surface); await Task.Delay(100); UpdateLayout();
        cropper.Surface.Select(new Rect(0, 0, 60, 80)); await Task.Delay(50); UpdateLayout();
        App.Capture(this, Path.Combine(output, "character-crop.png"));
        cropper.ConfirmButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        if (!await pending || cropper.Result is null) throw new InvalidOperationException("Crop confirmation did not return an image");
        using var stream = new MemoryStream(Convert.FromBase64String(cropper.Result.Data));
        var result = ImageCodec.Decode(stream, 1280);
        if (result.PixelWidth * 4 != result.PixelHeight * 3) throw new InvalidOperationException("Cropped image dimensions lost 3:4 ratio");
        var converted = new FormatConvertedBitmap(result, PixelFormats.Bgra32, null, 0); var sample = new byte[4];
        converted.CopyPixels(new Int32Rect(result.PixelWidth / 2, result.PixelHeight / 2, 1, 1), sample, 4, 0);
        if (sample[2] < 200 || sample[0] > 30) throw new InvalidOperationException("Cropping did not use the selected region");
        BeginCharacterEdit(null);
        var before = CharacterEditor.DraftImage;
        var importing = CharacterEditor.ReceiveImageDropAsync(new DataObject(DataFormats.Bitmap, bitmap));
        await WaitOverlayStateAsync(() => OverlayOpen && OverlayContent.Child is CharacterImageCropper);
        CompleteOverlay(false);
        if (await importing || CharacterEditor.DraftImage != before) throw new InvalidOperationException("Cancel cropping changed the editor image");
        CancelCharacter_Requested(this, EventArgs.Empty);
        await VM.NewChatAsync(VM.Characters.First());
        VM.Current.Messages.Add(new Reflection.Core.ChatMessage { Role = "user", Content = "保留原会话主题" });
        VM.Current.Title = "主题仍可搜索"; await VM.SaveCurrentAsync();
        var summary = VM.Conversations.First(item => item.Id == VM.Current.Id);
        if (summary.DisplayTitle != VM.Current.CharacterSnapshot?.Name || summary.Title != "主题仍可搜索") throw new InvalidOperationException("Character list name replaced the original conversation topic");
        ShowPage("chat");
        await VM.ToggleHistoryAsync(); UpdateLayout();
        if (HistorySection.IsVisible || (await VM.Store.LoadSettingsAsync()).HistoryExpanded) throw new InvalidOperationException("History collapse did not hide and persist");
        await Task.Delay(220); UpdateLayout(); App.Capture(this, Path.Combine(output, "history-collapsed.png"));
        await VM.ToggleHistoryAsync(); UpdateLayout();
        if (!HistorySection.IsVisible) throw new InvalidOperationException("History could not expand again");
        var scroll = new ScrollBar { Style = (Style)FindResource(typeof(ScrollBar)), Minimum = 0, Maximum = 1000, ViewportSize = 4000, Height = 600, Width = 6, Orientation = Orientation.Vertical };
        scroll.ApplyTemplate(); scroll.Measure(new Size(6, 600)); scroll.Arrange(new Rect(0, 0, 6, 600)); scroll.UpdateLayout();
        var track = (Track)scroll.Template.FindName("PART_Track", scroll);
        if (track.Thumb.ActualHeight > 98 || track.Thumb.ActualHeight < 20) throw new InvalidOperationException("Scrollbar thumb did not remain compact");
        scroll.Value = 1000; scroll.UpdateLayout();
        if (track.Value != 1000) throw new InvalidOperationException("Compact scrollbar changed its scroll range");
        var w = Width; var h = Height; Width = 720; Height = 620;
        var small = new CharacterImageCropper(this, bitmap); var smallPending = ShowOverlayAsync(small, small.Surface);
        await Task.Delay(100); UpdateLayout();
        if (small.ConfirmButton.ActualHeight < 25 || small.ActualHeight > ActualHeight - 80) throw new InvalidOperationException("Compact cropper clipped its actions");
        App.Capture(this, Path.Combine(output, "character-crop-minimum.png"));
        CompleteOverlay(false); await smallPending; Width = w; Height = h;
        await VM.NewChatAsync(VM.Characters.First());
    }
}
