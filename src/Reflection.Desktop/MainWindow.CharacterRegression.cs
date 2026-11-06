using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Reflection.Desktop;

public partial class MainWindow
{
    internal async Task VerifyCharacterFlowAsync(string output)
    {
        async Task<bool> CropAsync(IDataObject data, bool accept = true)
        {
            var pending = CharacterEditor.ReceiveImageDropAsync(data);
            await WaitOverlayStateAsync(() => OverlayOpen && OverlayContent.Child is CharacterImageCropper);
            if (accept) ((CharacterImageCropper)OverlayContent.Child).ConfirmButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            else CompleteOverlay(false);
            return await pending;
        }
        var drawing = new DrawingVisual();
        using (var context = drawing.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(191, 224, 207)), null, new Rect(0, 0, 240, 180));
            context.DrawEllipse(new SolidColorBrush(Color.FromRgb(237, 130, 109)), null, new Point(120, 90), 54, 54);
        }
        var bitmap = new RenderTargetBitmap(240, 180, 96, 96, PixelFormats.Pbgra32); bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var imagePath = Path.Combine(output, "drop-fixture.png"); using (var stream = File.Create(imagePath)) encoder.Save(stream);
        var fileDrop = new DataObject(DataFormats.FileDrop, new[] { imagePath });
        var bitmapDrop = new DataObject(DataFormats.Bitmap, bitmap);
        if (!ImageDrop.CanRead(fileDrop) || !ImageDrop.CanRead(bitmapDrop) || ImageDrop.CanRead(new DataObject(DataFormats.Text, "text")))
            throw new InvalidOperationException("Image drag formats were classified incorrectly");

        ShowPage("chat"); VM.Current.Attachments.Clear(); VM.Draft = "keep this draft";
        if (!ReceiveComposerDrop(fileDrop) || !ReceiveComposerDrop(bitmapDrop) || VM.Current.Attachments.Count != 2 || VM.Draft != "keep this draft")
            throw new InvalidOperationException("Composer image drop replaced text or lost images");
        ReceiveComposerDrop(fileDrop); ReceiveComposerDrop(fileDrop);
        if (ReceiveComposerDrop(fileDrop) || VM.Current.Attachments.Count != 4)
            throw new InvalidOperationException("Image drop exceeded the attachment limit");
        await Task.Delay(260); UpdateLayout(); App.Capture(this, Path.Combine(output, "composer-drop.png"));
        VM.Current.Attachments.Clear(); VM.Draft = "";

        var count = VM.Characters.Count; var windows = Application.Current.Windows.Count;
        CreateCard_Click(this, new RoutedEventArgs());
        if (currentPage != "editor" || Application.Current.Windows.Count != windows)
            throw new InvalidOperationException("Character creation opened a separate window");
        if (await SaveCharacterAsync() || string.IsNullOrWhiteSpace(CharacterEditor.ErrorText.Text) || VM.Characters.Count != count)
            throw new InvalidOperationException("Character name validation failed");
        CharacterEditor.NameField.Text = "拖放测试角色";
        CharacterEditor.DescriptionField.Text = "图片拖放与角色保存回归";
        CharacterEditor.SettingField.Text = "测试设定";
        CharacterEditor.GreetingField.Text = "测试开场白";
        CharacterEditor.SystemField.Text = "测试系统提示词";
        if (!await CropAsync(fileDrop) || CharacterEditor.DraftImage is null)
            throw new InvalidOperationException("Inline avatar drop failed");
        var previousImage = CharacterEditor.DraftImage;
        var badPath = Path.Combine(output, "invalid-image.png"); await File.WriteAllTextAsync(badPath, "invalid image");
        if (await CharacterEditor.ReceiveImageDropAsync(new DataObject(DataFormats.FileDrop, new[] { badPath })) || CharacterEditor.DraftImage != previousImage)
            throw new InvalidOperationException("Invalid image replaced the avatar draft");
        CharacterEditor.ErrorText.Text = "";
        await Task.Delay(260); UpdateLayout(); App.Capture(this, Path.Combine(output, "character-create.png"));
        var snapshot = VM.Current.CharacterSnapshot; var characterId = VM.Current.CharacterId;
        if (!await SaveCharacterAsync() || VM.Characters.Count != count + 1 || currentPage != "detail")
            throw new InvalidOperationException("Inline character creation failed");
        var created = VM.DetailCard!;
        var saved = (await VM.Store.LoadCharactersAsync()).FirstOrDefault(x => x.Id == created.Id);
        if (saved?.Image is null || saved.Setting != "测试设定" || saved.Greeting != "测试开场白" || saved.SystemPrompt != "测试系统提示词")
            throw new InvalidOperationException("Inline character fields or image were not persisted");
        if (VM.Current.CharacterSnapshot != snapshot || VM.Current.CharacterId != characterId)
            throw new InvalidOperationException("Library save changed the current chat character");
        EditDetail_Click(this, new RoutedEventArgs());
        CharacterEditor.NameField.Text = "should be discarded";
        await CropAsync(bitmapDrop);
        CancelCharacter_Requested(this, EventArgs.Empty);
        if (created.Name != "拖放测试角色" || currentPage != "detail" || VM.DetailCard != created)
            throw new InvalidOperationException("Cancel changed the library character");
        EditDetail_Click(this, new RoutedEventArgs());
        CharacterEditor.DescriptionField.Text = "已编辑";
        if (!await SaveCharacterAsync() || VM.DetailCard?.Id != created.Id || VM.DetailCard.Description != "已编辑" || VM.Characters.Count != count + 1)
            throw new InvalidOperationException("Editing duplicated the character or failed to save");

        var originalWidth = Width; var originalHeight = Height;
        Width = 720; Height = 620;
        await Task.Delay(260); UpdateLayout(); App.Capture(this, Path.Combine(output, "minimum-character-detail.png"));
        BeginCharacterEdit(VM.DetailCard);
        await Task.Delay(260); UpdateLayout(); App.Capture(this, Path.Combine(output, "minimum-character-editor.png"));
        if (CharacterEditor.SaveButton.ActualWidth < 60 || CharacterEditor.NameField.ActualWidth < 200)
            throw new InvalidOperationException("Compact character editor has clipped controls");
        CancelCharacter_Requested(this, EventArgs.Empty);
        Width = originalWidth; Height = originalHeight;
        VM.Characters.Remove(VM.Characters.First(x => x.Id == created.Id)); await VM.SaveCharactersAsync();
        VM.DetailCard = VM.Characters.First(); VM.Status = "";
        ShowPage("cards"); await Task.Delay(100);
    }
}
