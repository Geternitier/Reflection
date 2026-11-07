using System.IO;
using Reflection.Core;

namespace Reflection.Desktop;

public sealed partial class MainViewModel
{
    private readonly List<(CancellationTokenSource Cancel, Task<bool> Task)> imageImports = [];
    private int reservedImages;
    internal int AvailableImageSlots => Math.Max(0, 4 - Current.Attachments.Count - reservedImages);
    internal Task<bool> ImportImagesAsync(IReadOnlyList<ImageInput> inputs)
    {
        var selected = inputs.Take(AvailableImageSlots).ToArray();
        if (selected.Length == 0) { Status = "一次最多 4 张图片"; return Task.FromResult(false); }
        var chat = Current;
        var cancel = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        reservedImages += selected.Length;
        var remainingReservations = selected.Length;
        var task = ImportAsync(); imageImports.Add((cancel, task));
        _ = ObserveAsync(); return task;
        async Task ObserveAsync()
        {
            try { await task; }
            finally { imageImports.RemoveAll(x => ReferenceEquals(x.Cancel, cancel)); cancel.Dispose(); }
        }
        async Task<bool> ImportAsync()
        {
            var success = true;
            try
            {
                Status = "正在处理图片…";
                foreach (var input in selected)
                {
                    var image = await Images.ImportAsync(input, cancel.Token);
                    cancel.Token.ThrowIfCancellationRequested();
                    if (!ReferenceEquals(Current, chat)) return false;
                    reservedImages--; remainingReservations--;
                    chat.Attachments.Add(image);
                }
                Status = "图片已加入，可移除后发送";
            }
            catch (OperationCanceledException) { success = false; }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException or InvalidOperationException or FormatException)
            { if (ReferenceEquals(Current, chat)) Status = "图片处理失败：" + ex.Message; success = false; }
            finally { reservedImages -= remainingReservations; }
            return success;
        }
    }
    internal async Task<bool> WaitImageImportsAsync()
    {
        var success = true;
        while (imageImports.Count > 0)
        {
            var pending = imageImports.ToArray();
            success &= (await Task.WhenAll(pending.Select(x => x.Task))).All(x => x);
            foreach (var item in pending) imageImports.RemoveAll(x => ReferenceEquals(x.Cancel, item.Cancel));
        }
        return success;
    }
    internal async Task CancelImageImportsAsync()
    {
        var pending = imageImports.ToArray();
        foreach (var item in pending) item.Cancel.Cancel();
        await Task.WhenAll(pending.Select(x => x.Task));
    }
    internal async Task CollectImagesAsync()
    {
        try { await Store.CollectImageAssetsAsync(); }
        catch (Exception ex) when (ex is System.IO.IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            Status = "图片资产清理已跳过：" + ex.Message;
            System.Diagnostics.Trace.TraceWarning(Status);
        }
    }
    private IProgress<ImageContextSelection> ImageProgress(GenerationRun run) => new Progress<ImageContextSelection>(selection =>
    {
        run.ImageNotice = selection.Notice;
        if (selection.Notice.Length > 0 && runs.ContainsKey(run.Chat.Id)) SetRunStatus(run, run.Status);
    });
}
