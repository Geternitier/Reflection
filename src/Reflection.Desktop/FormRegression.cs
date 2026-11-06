using System.IO;
using System.Windows;
using System.Windows.Controls;
using Reflection.Core;

namespace Reflection.Desktop;

internal static class FormRegression
{
    internal static async Task VerifyAsync(MainWindow owner, string output)
    {
        var fixture = owner.VM.Settings.Services.FirstOrDefault() ?? new ServiceConfig { Name = "本地服务", Protocol = ApiProtocol.OpenAI, BaseUrl = "http://127.0.0.1:8080" };
        var dialog = EditorDialog.CreateService(owner, fixture);
        var pending = owner.ShowOverlayAsync(dialog, dialog.ServiceName);
        try
        {
            await Task.Delay(150); dialog.UpdateLayout();
            foreach (var field in new[] { dialog.ServiceName!, dialog.ServiceAddress! })
            {
                field.ApplyTemplate();
                var host = (ScrollViewer)field.Template.FindName("PART_ContentHost", field);
                var glyph = field.GetRectFromCharacterIndex(0);
                if (field.IsReadOnly || !field.IsEnabled || host.ActualHeight < 32 || glyph.IsEmpty || glyph.Height < 12 || glyph.Bottom > field.ActualHeight)
                    throw new InvalidOperationException($"Text field is clipped or uneditable: {field.Text}; viewport={host.ActualHeight}; glyph={glyph}");
                field.SelectAll(); field.SelectedText = field.Text + "-edited";
                if (!field.Text.EndsWith("-edited")) throw new InvalidOperationException("Selection replacement failed");
            }
            dialog.ServiceName!.Text = "ninfer 测试连接";
            dialog.ServiceAddress!.Text = "not-an-api-address";
            if (dialog.TrySubmit() || dialog.ServiceResult is not null || string.IsNullOrWhiteSpace(dialog.ErrorText)) throw new InvalidOperationException("Invalid endpoint accepted");
            await Task.Delay(50); dialog.UpdateLayout(); App.CaptureElement(dialog, Path.Combine(output, "service-error.png"));
            dialog.ServiceAddress.Text = fixture.BaseUrl;
            if (!dialog.TrySubmit() || dialog.ServiceResult?.Name != "ninfer 测试连接") throw new InvalidOperationException("Valid service rejected");
            await Task.Delay(50); dialog.UpdateLayout(); App.CaptureElement(dialog, Path.Combine(output, "service-editor.png"));
            // Persist and reload through the production store, in the isolated smoke data directory.
            var configs = owner.VM.Settings.Services.ToList();
            try
            {
                owner.VM.Settings.Services.RemoveAll(x => x.Id == fixture.Id);
                owner.VM.Settings.Services.Add(dialog.ServiceResult);
                await owner.VM.Store.SaveSettingsAsync(owner.VM.Settings);
                var saved = await owner.VM.Store.LoadSettingsAsync();
                if (!saved.Services.Any(x => x.Name == "ninfer 测试连接" && x.BaseUrl == fixture.BaseUrl)) throw new InvalidOperationException("Service persistence failed");
            }
            finally { owner.VM.Settings.Services = configs; await owner.VM.Store.SaveSettingsAsync(owner.VM.Settings); }
        }
        finally { owner.CompleteOverlay(false); await pending; }
    }
}
