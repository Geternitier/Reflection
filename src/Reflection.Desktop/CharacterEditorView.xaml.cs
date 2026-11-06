using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Reflection.Core;

namespace Reflection.Desktop;

public partial class CharacterEditorView : UserControl
{
    private ChatImage? image;
    internal CharacterCard? Original { get; private set; }
    internal CharacterCard? Result { get; private set; }
    internal ChatImage? DraftImage => image;
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
        Original = original; Result = null; ErrorText.Text = ""; SaveButton.IsEnabled = true;
        EditorTitle.Text = original is null ? "创建角色" : "编辑角色";
        NameField.Text = original?.Name ?? ""; DescriptionField.Text = original?.Description ?? "";
        SettingField.Text = original?.Setting ?? ""; GreetingField.Text = original?.Greeting ?? "";
        SystemField.Text = original?.SystemPrompt ?? ""; OpeningRoleField.SelectedIndex = original?.OpeningRole == "user" ? 1 : 0;
        image = original?.Image; RefreshImage(); FormScroll.ScrollToTop();
        InputValidation.SetHasError(NameField, false); InputValidation.SetHasError(SystemField, false);
    }
    private void RefreshImage()
    {
        var source = image is null ? null : new Base64ImageConverter().Convert(image, typeof(ImageSource), "640", CultureInfo.InvariantCulture) as ImageSource;
        AvatarPreview.Background = source is null ? null : new ImageBrush(source) { Stretch = Stretch.UniformToFill };
        CropCurrentButton.IsEnabled = image is not null;
        AvatarPlaceholder.Visibility = image is null ? Visibility.Visible : Visibility.Collapsed;
    }
    internal void SetDropHighlight(bool active)
    {
        AvatarDropZone.SetResourceReference(Border.BorderBrushProperty, active ? "Accent" : "Line");
    }
    internal async Task<bool> ReceiveImageDropAsync(IDataObject data)
    {
        try
        {
            BitmapSource? source = null;
            if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files && files.FirstOrDefault(ImageDrop.IsImageFile) is { } path)
            {
                if (new FileInfo(path).Length > 20_000_000) throw new ArgumentException("请选择小于 20 MB 的图片。");
                using var stream = File.OpenRead(path); source = ImageCodec.Decode(stream, 2560);
            }
            else if (data.GetDataPresent(DataFormats.Bitmap) && data.GetData(DataFormats.Bitmap) is BitmapSource bitmap)
            {
                // Reuse the existing size validation/normalization for external bitmap sources.
                var normalized = ImageCodec.Encode(bitmap);
                using var stream = new MemoryStream(Convert.FromBase64String(normalized.Data)); source = ImageCodec.Decode(stream, 2560);
            }
            if (source is null) { ErrorText.Text = "请拖入支持的图片文件。"; return false; }
            if (Window.GetWindow(this) is not MainWindow owner) return false;
            var cropped = await CharacterImageCropper.ShowAsync(owner, source);
            if (cropped is null) return false;
            image = cropped; RefreshImage(); ErrorText.Text = ""; return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or FormatException)
        { ErrorText.Text = ex.Message; return false; }
    }
    internal bool TryBuildResult()
    {
        Result = null; ErrorText.Text = ""; InputValidation.SetHasError(NameField, false); InputValidation.SetHasError(SystemField, false);
        if (string.IsNullOrWhiteSpace(NameField.Text)) { Fail(NameField, "请填写角色名称。"); return false; }
        if (SystemField.Text.Trim().Length > 20000) { Fail(SystemField, "系统提示词不能超过 20000 字。"); return false; }
        Result = new CharacterCard { Id = Original?.Id ?? Guid.NewGuid(), Name = NameField.Text.Trim(), Description = DescriptionField.Text.Trim(), Setting = SettingField.Text.Trim(), Greeting = GreetingField.Text.Trim(), OpeningRole = OpeningRoleField.SelectedIndex == 1 ? "user" : "assistant", SystemPrompt = SystemField.Text.Trim(), Image = image };
        return true;
    }
    private void Fail(TextBox field, string message) { ErrorText.Text = message; InputValidation.SetHasError(field, true); field.BringIntoView(); field.Focus(); }
    private async void PickImage_Click(object sender, RoutedEventArgs e)
    {
        if (Window.GetWindow(this) is not MainWindow owner) return;
        var paths = await FileDialogs.PickAsync(owner, "选择角色图片", new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp" });
        if (paths is not null) await ReceiveImageDropAsync(new DataObject(DataFormats.FileDrop, paths));
    }
    private async void RecropImage_Click(object sender, RoutedEventArgs e)
    {
        if (image is null || Window.GetWindow(this) is not MainWindow owner) return;
        try
        {
            using var stream = new MemoryStream(Convert.FromBase64String(image.Data));
            var cropped = await CharacterImageCropper.ShowAsync(owner, ImageCodec.Decode(stream, 2560));
            if (cropped is not null) { image = cropped; RefreshImage(); ErrorText.Text = ""; }
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or InvalidOperationException or FormatException)
        { ErrorText.Text = ex.Message; }
    }
    private void RemoveImage_Click(object sender, RoutedEventArgs e) { image = null; RefreshImage(); }
    private void Save_Click(object sender, RoutedEventArgs e) { if (TryBuildResult()) SaveRequested?.Invoke(this, EventArgs.Empty); }
    private void Cancel_Click(object sender, RoutedEventArgs e) => CancelRequested?.Invoke(this, EventArgs.Empty);
}
