using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Reflection.Core;

namespace Reflection.Desktop;

internal sealed class EditorDialog : UserControl
{
    private readonly StackPanel fields = new() { Margin = new Thickness(28, 0, 28, 20) };
    private readonly TextBlock error = new() { Foreground = new SolidColorBrush(Color.FromRgb(173, 52, 59)), Visibility = Visibility.Collapsed, Margin = new Thickness(0,0,0,14) };
    private readonly List<TextBox> textFields = [];
    private readonly StackPanel footer = new() { Margin = new Thickness(28,18,28,20) };
    private Func<bool>? validate;
    private readonly MainWindow owner;
    internal TextBox? ServiceName { get; private set; }
    internal TextBox? ServiceAddress { get; private set; }
    internal ComboBox? ServiceProtocol { get; private set; }
    internal ServiceConfig? ServiceResult { get; private set; }
    internal string ErrorText => error.Text;

    private EditorDialog(MainWindow owner, string title)
    {
        this.owner = owner; Width = 580;
        error.SetResourceReference(TextBlock.ForegroundProperty, "Danger");
        AutomationProperties.SetName(this, title);
        var layout = new DockPanel(); layout.SetResourceReference(BackgroundProperty, "Panel");
        var header = new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(24,24,24,12) };
        DockPanel.SetDock(header, Dock.Top); layout.Children.Add(header);
        var footerPanel = new Border { Background = (Brush)FindResource("Sidebar"), BorderBrush = (Brush)FindResource("Line"), BorderThickness = new Thickness(0,1,0,0), Child = footer };
        DockPanel.SetDock(footerPanel, Dock.Bottom); layout.Children.Add(footerPanel);
        layout.Children.Add(new ScrollViewer { Content = fields, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        Content = layout;
        Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (textFields.FirstOrDefault() is { } first) { first.Focus(); Keyboard.Focus(first); first.SelectAll(); }
        });
    }
    private void Hint(string text) => fields.Children.Add(new TextBlock { Text = text, Foreground = (Brush)FindResource("Muted"), FontSize = 13, LineHeight = 21, Margin = new Thickness(0,0,0,10) });
    private TextBox Field(string label, string value, int height = 44, string? id = null)
    {
        var box = new TextBox
        {
            Text = value, MinHeight = 44, AcceptsReturn = height > 44,
            TextWrapping = height > 44 ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalScrollBarVisibility = height > 44 ? ScrollBarVisibility.Auto : ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalContentAlignment = height > 44 ? VerticalAlignment.Top : VerticalAlignment.Center,
            Padding = new Thickness(12,8,12,8)
        };
        if (height > 44) box.Height = height;
        AutomationProperties.SetName(box, label);
        if (id is not null) AutomationProperties.SetAutomationId(box, id);
        fields.Children.Add(new Label { Content = label, Target = box, Padding = new Thickness(0), Margin = new Thickness(0,14,0,8), FontWeight = FontWeights.Medium });
        fields.Children.Add(box); textFields.Add(box);
        box.TextChanged += (_, _) => { error.Visibility = Visibility.Collapsed; InputValidation.SetHasError(box, false); };
        return box;
    }
    private void Prepare(Func<bool> validation, string action = "保存")
    {
        validate = validation; footer.Children.Add(error);
        AutomationProperties.SetLiveSetting(error, AutomationLiveSetting.Assertive);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "取消", MinWidth = 84, Margin = new Thickness(0,0,10,0) };
        cancel.Click += (_, _) => owner.CompleteOverlay(false);
        var save = new Button { Content = action, Style = (Style)FindResource("Primary"), MinWidth = 112, IsDefault = true };
        AutomationProperties.SetAutomationId(save, "SaveDialog");
        save.Click += (_, _) => { if (TrySubmit()) owner.CompleteOverlay(true); };
        buttons.Children.Add(cancel); buttons.Children.Add(save); footer.Children.Add(buttons);
    }
    internal bool TrySubmit()
    {
        try { return validate?.Invoke() == true; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        { error.Text = ex.Message; error.Visibility = Visibility.Visible; return false; }
    }
    private Task<bool> ShowEditorAsync(Func<bool> validation, string action = "保存") { Prepare(validation, action); return ShowPreparedAsync(); }
    private Task<bool> ShowPreparedAsync() => owner.ShowOverlayAsync(this, textFields.FirstOrDefault());
    private void Invalid(TextBox field, string message)
    {
        InputValidation.SetHasError(field, true); field.Focus(); field.SelectAll();
        throw new ArgumentException(message);
    }
    public static async Task<string?> EditMessageAsync(MainWindow owner, ChatMessage message)
    {
        var resend = message.Role == "user" && !message.Opening;
        var d = new EditorDialog(owner, resend ? "编辑并重新发送" : "编辑消息");
        d.Hint(message.Opening ? "只改开场白的文字。" : resend ? "保存后从这里重新回答，后面的内容会被替换。" : "只改这条回复的文字，后面的对话会保留。");
        var text = d.Field("消息内容", message.Content, 220);
        return await d.ShowEditorAsync(() => { if (string.IsNullOrWhiteSpace(text.Text) && message.Images.Count == 0) d.Invalid(text, "消息不能为空。"); return true; }, resend ? "保存并重答" : "保存") ? text.Text.Trim() : null;
    }
    internal static EditorDialog CreateService(MainWindow owner, ServiceConfig? original)
    {
        var d = new EditorDialog(owner, original is null ? "添加本地服务" : "编辑服务连接");
        d.Hint("填写已启动服务的地址。保存后会检测连接并获取模型，离线时也可以保存。");
        var name = d.ServiceName = d.Field("服务名称", original?.Name ?? "ninfer", id: "ServiceName");
        name.Tag = "例如 Ollama、ninfer、strata";
        var address = d.ServiceAddress = d.Field("API 地址", original?.BaseUrl ?? "http://127.0.0.1:8080", id: "ServiceAddress");
        address.Tag = "http://127.0.0.1:端口";
        fieldsHint(d, "使用本机地址；OpenAI 接口可包含 /v1。", 7);
        var protocol = d.ServiceProtocol = new ComboBox { ItemsSource = Enum.GetValues<ApiProtocol>(), SelectedItem = original?.Protocol ?? ApiProtocol.OpenAI, MinHeight = 44 };
        AutomationProperties.SetName(protocol, "接口协议"); AutomationProperties.SetAutomationId(protocol, "ServiceProtocol");
        d.fields.Children.Add(new Label { Content = "接口协议", Target = protocol, Padding = new Thickness(0), Margin = new Thickness(0,18,0,8), FontWeight = FontWeights.Medium });
        d.fields.Children.Add(protocol);
        fieldsHint(d, "Ollama 选择原生协议；ninfer、strata 选择其支持的兼容协议。", 7);
        var enabled = new CheckBox { Content = "启用并自动发现模型", IsChecked = original?.Enabled ?? true, Margin = new Thickness(0,22,0,0), Padding = new Thickness(6,0,0,0) };
        d.fields.Children.Add(enabled);
        d.Prepare(() =>
        {
            if (string.IsNullOrWhiteSpace(name.Text)) d.Invalid(name, "请填写服务名称，方便切换时识别。");
            var service = new ServiceConfig { Id = original?.Id ?? Guid.NewGuid(), Name = name.Text.Trim(), BaseUrl = address.Text.Trim().TrimEnd('/'), Protocol = (ApiProtocol)protocol.SelectedItem, Enabled = enabled.IsChecked == true };
            try { InferenceClient.Endpoint(service, service.Protocol == ApiProtocol.Ollama ? "api/tags" : "v1/models"); }
            catch (ArgumentException) { d.Invalid(address, "地址无效。请使用本机 HTTP 地址，例如 http://127.0.0.1:8080。"); }
            d.ServiceResult = service; return true;
        }, "保存并检测");
        return d;
    }
    private static void fieldsHint(EditorDialog d, string text, int top) => d.fields.Children.Add(new TextBlock { Text = text, FontSize = 12, Foreground = (Brush)d.FindResource("Muted"), Margin = new Thickness(0,top,0,0), LineHeight = 19 });
    public static async Task<ServiceConfig?> ServiceAsync(MainWindow owner, ServiceConfig? original)
    {
        var d = CreateService(owner, original); return await d.ShowPreparedAsync() ? d.ServiceResult : null;
    }
}

public static class InputValidation
{
    public static readonly DependencyProperty HasErrorProperty = DependencyProperty.RegisterAttached("HasError", typeof(bool), typeof(InputValidation), new PropertyMetadata(false));
    public static bool GetHasError(DependencyObject obj) => (bool)obj.GetValue(HasErrorProperty);
    public static void SetHasError(DependencyObject obj, bool value) => obj.SetValue(HasErrorProperty, value);
}
