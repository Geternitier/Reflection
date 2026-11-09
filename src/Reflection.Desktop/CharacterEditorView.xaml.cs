using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Reflection.Core;

namespace Reflection.Desktop;

public partial class CharacterEditorView : UserControl
{
    private ChatImage? image, avatar;
    private CancellationTokenSource? imageCancellation;
    private TaskCompletionSource? imageWorkCompleted;
    internal void CancelImageWork() { imageCancellation?.Cancel(); }
    internal async Task CancelImageWorkAsync()
    {
        var pending = imageWorkCompleted; CancelImageWork();
        if (pending is not null) await pending.Task;
    }
    internal CharacterCard? Original { get; private set; }
    internal CharacterCard? Result { get; private set; }
    internal ChatImage? DraftImage => image;
    internal ChatImage? DraftAvatar => avatar;
    public event EventHandler? SaveRequested;
    public event EventHandler? CancelRequested;
    public CharacterEditorView()
    {
        InitializeComponent();
        NameField.TextChanged += (_, _) => { InputValidation.SetHasError(NameField, false); ErrorText.Text = ""; };
        SystemField.TextChanged += (_, _) => { InputValidation.SetHasError(SystemField, false); ErrorText.Text = ""; };
        SizeChanged += (_, _) =>
        {
            var narrow = ActualWidth < 800;
            ImageColumn.Width = new GridLength(narrow ? 0 : 260);
            Grid.SetColumn(AvatarPanel, narrow ? 1 : 0);
            Grid.SetRow(IdentityFields, narrow ? 1 : 0);
            AvatarPanel.Margin = new Thickness(0, 0, narrow ? 0 : 28, narrow ? 24 : 0);
            AvatarDropZone.MaxWidth = narrow ? 320 : double.PositiveInfinity;
            AvatarDropZone.HorizontalAlignment = HorizontalAlignment.Left;
            AvatarDropZone.Width = narrow ? 260 : 232;
        };
    }
    internal void BeginEdit(CharacterCard? original)
    {
        CancelImageWork();
        Original = original; Result = null; ErrorText.Text = ""; SaveButton.IsEnabled = true;
        EditorTitle.Text = original is null ? "创建角色" : "编辑角色";
        NameField.Text = original?.Name ?? ""; DescriptionField.Text = original?.Description ?? "";
        SettingField.Text = original?.Setting ?? ""; GreetingField.Text = original?.Greeting ?? "";
        SystemField.Text = original?.SystemPrompt ?? ""; OpeningRoleField.SelectedIndex = original?.OpeningRole == "user" ? 1 : 0;
        image = original?.Image; avatar = original?.Avatar; RefreshImages(); FormScroll.ScrollToTop();
        InputValidation.SetHasError(NameField, false); InputValidation.SetHasError(SystemField, false);
    }
    private void RefreshImages()
    {
        AvatarPreview.Child = image is null ? null : new AssetImage { Picture = image, DecodeSize = 640, Stretch = Stretch.UniformToFill };
        CropCurrentButton.IsEnabled = image is not null;
        AvatarPlaceholder.Visibility = image is null ? Visibility.Visible : Visibility.Collapsed;
        ConversationAvatarPreview.Content = avatar ?? image;
        CropAvatarButton.Content = avatar is null ? "从封面裁剪" : "调整头像";
        CropAvatarButton.IsEnabled = avatar is not null || image is not null;
        RemoveAvatarButton.IsEnabled = avatar is not null;
    }
    internal void SetDropHighlight(bool active)
    {
        AvatarDropZone.SetResourceReference(Border.BorderBrushProperty, active ? "Accent" : "Line");
    }
    private async Task<bool> EditImageAsync(Func<MainWindow, CancellationToken, Task<ChatImage?>> work, bool forAvatar = false)
    {
        if (Window.GetWindow(this) is not MainWindow owner) return false;
        CancelImageWork();
        var cancel = new CancellationTokenSource(); imageCancellation = cancel;
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); imageWorkCompleted = completed;
        SaveButton.IsEnabled = false;
        try
        {
            var result = await work(owner, cancel.Token);
            cancel.Token.ThrowIfCancellationRequested();
            if (result is null) return false;
            if (forAvatar) avatar = result; else image = result;
            RefreshImages(); ErrorText.Text = ""; return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or FormatException)
        { if (!cancel.IsCancellationRequested) ErrorText.Text = ex.Message; return false; }
        finally
        {
            if (ReferenceEquals(imageCancellation, cancel)) { imageCancellation = null; imageWorkCompleted = null; SaveButton.IsEnabled = true; }
            completed.TrySetResult(); cancel.Dispose();
        }
    }
    internal Task<bool> ReceiveImageDropAsync(IDataObject data, bool forAvatar = false)
    {
        IReadOnlyList<ImageInput> inputs;
        try { inputs = ImageDrop.Capture(data, 1); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or NotSupportedException)
        { ErrorText.Text = ex.Message; return Task.FromResult(false); }
        if (inputs.Count == 0) { ErrorText.Text = "请拖入支持的图片文件。"; return Task.FromResult(false); }
        return EditImageAsync(async (owner, ct) =>
        {
            var original = await owner.VM.Images.ImportAsync(inputs[0], ct);
            return await CharacterImageCropper.ShowAsync(owner, original, ct, avatar: forAvatar);
        }, forAvatar);
    }
    internal bool TryBuildResult()
    {
        if (imageWorkCompleted is not null) { ErrorText.Text = "请等待图片处理完成后保存。"; return false; }
        Result = null; ErrorText.Text = ""; InputValidation.SetHasError(NameField, false); InputValidation.SetHasError(SystemField, false);
        if (string.IsNullOrWhiteSpace(NameField.Text)) { Fail(NameField, "请填写角色名称。"); return false; }
        if (SystemField.Text.Trim().Length > 20000) { Fail(SystemField, "系统提示词不能超过 20000 字。"); return false; }
        Result = new CharacterCard { Id = Original?.Id ?? Guid.NewGuid(), Name = NameField.Text.Trim(), Description = DescriptionField.Text.Trim(), Setting = SettingField.Text.Trim(), Greeting = GreetingField.Text.Trim(), OpeningRole = OpeningRoleField.SelectedIndex == 1 ? "user" : "assistant", SystemPrompt = SystemField.Text.Trim(), Image = image, Avatar = avatar };
        return true;
    }
    private void Fail(TextBox field, string message) { ErrorText.Text = message; InputValidation.SetHasError(field, true); field.BringIntoView(); field.Focus(); }
    private async void PickImage_Click(object sender, RoutedEventArgs e) => await PickImageAsync(forAvatar: false);
    private async void PickAvatar_Click(object sender, RoutedEventArgs e) => await PickImageAsync(forAvatar: true);
    private async Task PickImageAsync(bool forAvatar)
    {
        if (Window.GetWindow(this) is not MainWindow owner) return;
        var paths = await FileDialogs.PickAsync(owner, forAvatar ? "选择角色头像" : "选择角色封面", new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp" });
        if (paths is not null) await ReceiveImageDropAsync(new DataObject(DataFormats.FileDrop, paths), forAvatar);
    }
    private async void RecropImage_Click(object sender, RoutedEventArgs e)
    {
        if (image is not { } current) return;
        await EditImageAsync(async (owner, ct) =>
        {
            var original = await owner.VM.Images.NormalizeAsync(current, ct);
            return await CharacterImageCropper.ShowAsync(owner, original, ct);
        });
    }
    private async void RecropAvatar_Click(object sender, RoutedEventArgs e)
    {
        if ((avatar ?? image) is not { } current) return;
        var source = current.Snapshot();
        if (avatar is null) source.Crop = null;
        await EditImageAsync(async (owner, ct) =>
        {
            var original = await owner.VM.Images.NormalizeAsync(source, ct);
            return await CharacterImageCropper.ShowAsync(owner, original, ct, avatar: true);
        }, forAvatar: true);
    }
    private void RemoveImage_Click(object sender, RoutedEventArgs e) { CancelImageWork(); image = null; RefreshImages(); }
    private void RemoveAvatar_Click(object sender, RoutedEventArgs e) { CancelImageWork(); avatar = null; RefreshImages(); }
    private void Save_Click(object sender, RoutedEventArgs e) { if (TryBuildResult()) SaveRequested?.Invoke(this, EventArgs.Empty); }
    private void Cancel_Click(object sender, RoutedEventArgs e) => CancelRequested?.Invoke(this, EventArgs.Empty);
}
