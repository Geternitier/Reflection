using System.Windows;
using System.Windows.Controls;
using Reflection.Core;

namespace Reflection.Desktop;

internal static class InlineSelection
{
    internal static Task<object?> ShowAsync(MainWindow owner, FrameworkElement anchor, IEnumerable<object> items, object? selected, Func<object, string> label)
        => owner.ShowChoicesAsync(anchor, items, selected, label);
    internal static async Task OpenAsync(ComboBox combo)
    {
        if (Window.GetWindow(combo) is not MainWindow owner || owner.ChoiceOpen) return;
        var result = await ShowAsync(owner, combo, combo.Items.Cast<object>().ToArray(), combo.SelectedItem, item => item switch
        {
            ModelOption model => model.Label,
            ComboBoxItem option => option.Content?.ToString() ?? "",
            ApiProtocol.Ollama => "Ollama",
            ApiProtocol.OpenAI => "OpenAI 兼容",
            _ => item.ToString() ?? ""
        });
        if (result is ModelOption modelResult) result = combo.Items.OfType<ModelOption>().FirstOrDefault(model => model.Service.Id == modelResult.Service.Id && model.Model == modelResult.Model);
        if (result is not null) combo.SetCurrentValue(ComboBox.SelectedItemProperty, result);
    }
}
