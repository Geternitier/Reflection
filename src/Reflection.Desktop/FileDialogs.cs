using System.IO;
using Microsoft.Win32;

namespace Reflection.Desktop;

internal sealed record FileDialogRequest(string Title, string[] Extensions, bool Save = false, bool Multiple = false, string? Suggested = null, string? Initial = null, bool Directory = false);

// The OS owns navigation, validation, overwrite confirmation and dialog accessibility.
internal static class FileDialogs
{
    internal static Func<FileDialogRequest, string[]?>? SelectForRegression { get; set; }
    internal static Task<string[]?> PickAsync(MainWindow owner, string title, string[] extensions, bool save = false, bool multiple = false, string? suggested = null, string? initial = null, bool directory = false)
    {
        owner.StopAutoScroll();
        var request = new FileDialogRequest(title, extensions, save, multiple, suggested, initial, directory);
        if (SelectForRegression is { } select) return Task.FromResult(select(request));
        return Task.FromResult(Show(owner, request));
    }
    internal static string[]? Show(MainWindow owner, FileDialogRequest request)
    {
        var initial = request.Initial is { } folder && System.IO.Directory.Exists(folder) ? folder : "";
        if (request.Directory)
        {
            var dialog = new OpenFolderDialog { Title = request.Title, InitialDirectory = initial, Multiselect = request.Multiple };
            return dialog.ShowDialog(owner) == true ? dialog.FolderNames : null;
        }
        var patterns = string.Join(';', request.Extensions.Select(e => "*" + e));
        var filter = patterns.Length == 0 ? "所有文件|*.*" : $"支持的文件 ({patterns})|{patterns}";
        if (request.Save)
        {
            var dialog = new SaveFileDialog { Title = request.Title, Filter = filter, FileName = request.Suggested ?? "", InitialDirectory = initial,
                DefaultExt = request.Extensions.FirstOrDefault() ?? "", AddExtension = true, OverwritePrompt = true, CheckPathExists = true, RestoreDirectory = true };
            return dialog.ShowDialog(owner) == true ? [dialog.FileName] : null;
        }
        var open = new OpenFileDialog { Title = request.Title, Filter = filter, InitialDirectory = initial, Multiselect = request.Multiple,
            CheckFileExists = true, CheckPathExists = true, RestoreDirectory = true };
        return open.ShowDialog(owner) == true ? open.FileNames : null;
    }
}
