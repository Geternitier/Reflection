using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Reflection.Core;

namespace Reflection.Desktop;

internal sealed record ImageInput(string? Path, BitmapSource? Bitmap);

// One STA worker owns all WPF decoding/rendering. Shared jobs outlive an individual waiter.
public sealed class ImagePipeline : IAsyncDisposable, IDisposable
{
    private readonly ImageAssetStore assets;
    private readonly BlockingCollection<Action> queue = new(32);
    private readonly CancellationTokenSource lifetime = new();
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object gate = new();
    private readonly Dictionary<string, Task<object>> pending = [];
    private readonly LinkedList<(string Key, BitmapSource Image)> bitmaps = new();
    private sealed class LegacyIdentity { public string Key { get; } = Guid.NewGuid().ToString("N"); }
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<string, LegacyIdentity> legacyKeys = new();
    private long pixels;
    private long retainedInputBytes;
    private bool disposed;
    private bool resourcesDisposed;
    internal int GeneratedVariants { get; private set; }
    internal int DecodedBitmaps { get; private set; }
    internal Action? BeforeDecodeForRegression { get; set; }
    internal int PendingCount { get { lock (gate) return pending.Count; } }
    public ImagePipeline(ImageAssetStore assets)
    {
        this.assets = assets;
        var thread = new Thread(() =>
        {
            try
            {
                while (queue.TryTake(out var action, Timeout.Infinite)) { action(); action = null; }
            }
            finally { stopped.TrySetResult(); }
        }) { IsBackground = true, Name = "Reflection images" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
    }
    private Task<T> RunAsync<T>(string key, Func<T> work, CancellationToken ct, long inputBytes = 0)
    {
        Task<object> task;
        lock (gate)
        {
            if (disposed) return Task.FromCanceled<T>(new CancellationToken(true));
            if (!pending.TryGetValue(key, out task!))
            {
                if (retainedInputBytes + inputBytes > 96_000_000)
                    return Task.FromException<T>(new InvalidOperationException("待处理图片占用过大，请等待当前图片完成后重试。"));
                var completion = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
                task = completion.Task;
                // Observe failures even when every interested view has been unloaded/cancelled.
                _ = task.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
                pending.Add(key, task);
                retainedInputBytes += inputBytes;
                if (!queue.TryAdd(() =>
                {
                    try
                    {
                        lifetime.Token.ThrowIfCancellationRequested();
                        var result = work();
                        lifetime.Token.ThrowIfCancellationRequested();
                        completion.TrySetResult(result!);
                    }
                    catch (OperationCanceledException) { completion.TrySetCanceled(); }
                    catch (Exception ex) { completion.TrySetException(ex); }
                    finally { work = null!; lock (gate) { pending.Remove(key); retainedInputBytes -= inputBytes; } }
                }))
                {
                    pending.Remove(key);
                    retainedInputBytes -= inputBytes;
                    completion.TrySetException(new InvalidOperationException("图片处理队列已满，请稍后重试。"));
                }
            }
        }
        return AwaitAsync();
        async Task<T> AwaitAsync() => (T)await task.WaitAsync(ct).ConfigureAwait(false);
    }
    internal async Task<ChatImage> ImportAsync(ImageInput input, CancellationToken ct)
    {
        if (input.Path is { } path)
        {
            var file = new FileInfo(path);
            var key = $"file:{Path.GetFullPath(path)}:{file.Length}:{file.LastWriteTimeUtc.Ticks}";
            return (await RunAsync(key, () => ImportBytes(ReadFile(path)), ct)).Snapshot();
        }
        var bitmap = input.Bitmap ?? throw new ArgumentException("没有可读取的图片。");
        return await RunAsync("bitmap:" + Guid.NewGuid(), () =>
        {
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream(); encoder.Save(stream);
            if (stream.Length > ImageAssetStore.MaxFileBytes) throw new ArgumentException("剪贴板图片超过 20 MB，请先缩小图片。");
            return ImportBytes(stream.ToArray());
        }, ct, (long)bitmap.PixelWidth * bitmap.PixelHeight * Math.Max(4, (bitmap.Format.BitsPerPixel + 7) / 8));
    }
    private static byte[] ReadFile(string path)
    {
        using var stream = File.OpenRead(path);
        if (stream.Length is 0 or > ImageAssetStore.MaxFileBytes) throw new ArgumentException("请选择小于 20 MB 的图片。");
        var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes); return bytes;
    }
    private string Put(byte[] bytes) => assets.PutAsync(bytes, lifetime.Token).GetAwaiter().GetResult();
    private byte[] Read(string id) => assets.ReadAsync(id, lifetime.Token).GetAwaiter().GetResult();
    private ChatImage ImportBytes(byte[] bytes)
    {
        var original = Put(bytes);
        return Variant(original, null, bytes);
    }
    private ChatImage Variant(string original, ImageCrop? crop, byte[]? raw = null, bool rebuild = false, bool squareCrop = false)
    {
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("image-v1:1280:360:jpeg86:png:" + original + ":" + JsonSerializer.Serialize(crop) + (squareCrop ? ":square" : ""))));
        var variants = Path.Combine(Path.GetDirectoryName(assets.DirectoryPath)!, "variants");
        var path = Path.Combine(variants, key + ".json");
        if (!rebuild && File.Exists(path))
        {
            try
            {
                var cached = JsonSerializer.Deserialize<ChatImage>(File.ReadAllText(path), LocalStore.Json);
                if (cached is not null && cached.OriginalAssetId == original && cached.Crop == crop && cached.SquareCrop == squareCrop)
                { assets.Attach(cached); return cached; }
            }
            catch (Exception ex) when (ex is JsonException or IOException or ArgumentException)
            { System.Diagnostics.Trace.WriteLine("Rebuilding image variant: " + ex.Message); }
        }
        using var stream = new MemoryStream(raw ?? Read(original));
        BeforeDecodeForRegression?.Invoke();
        var decoded = ImageCodec.Decode(stream, 2560); DecodedBitmaps++;
        BitmapSource source = decoded;
        if (crop is not null)
        {
            if (!ValidCrop(crop)) throw new ArgumentException("裁剪范围无效。");
            var x = Math.Min(decoded.PixelWidth - 1, (int)Math.Floor(crop.X * decoded.PixelWidth));
            var y = Math.Min(decoded.PixelHeight - 1, (int)Math.Floor(crop.Y * decoded.PixelHeight));
            var width = Math.Max(1, Math.Min(decoded.PixelWidth - x, (int)Math.Round(crop.Width * decoded.PixelWidth)));
            var height = Math.Max(1, Math.Min(decoded.PixelHeight - y, (int)Math.Round(crop.Height * decoded.PixelHeight)));
            var cropped = new CroppedBitmap(decoded, new Int32Rect(x, y, width, height));
            // Preserve the requested aspect ratio after rounding normalized crop coordinates.
            var horizontal = squareCrop ? 1 : 3;
            var vertical = squareCrop ? 1 : 4;
            var unit = Math.Max(1, Math.Min(1280 / vertical, Math.Min(width / horizontal, height / vertical)));
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen()) dc.DrawImage(cropped, new Rect(0, 0, unit * horizontal, unit * vertical));
            var target = new RenderTargetBitmap(unit * horizontal, unit * vertical, 96, 96, PixelFormats.Pbgra32); target.Render(visual); source = target;
        }
        // JPEG stays photographic; other formats retain transparency and sharp screenshot edges.
        var originalBytes = raw ?? Read(original);
        var png = !(originalBytes.Length >= 2 && originalBytes[0] == 0xff && originalBytes[1] == 0xd8);
        var main = ImageCodec.Encode(source, 1280, png);
        var thumbnail = ImageCodec.Encode(source, 360, png);
        var image = new ChatImage
        {
            Mime = main.Mime, AssetId = Put(Convert.FromBase64String(main.Data)),
            OriginalAssetId = original, ThumbnailAssetId = Put(Convert.FromBase64String(thumbnail.Data)),
            Crop = crop, SquareCrop = squareCrop, EncodedLength = main.Data.Length, Store = assets
        };
        Directory.CreateDirectory(variants);
        var temp = path + "." + Guid.NewGuid() + ".tmp";
        try { File.WriteAllText(temp, JsonSerializer.Serialize(image, LocalStore.Json)); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        GeneratedVariants++; return image;
    }
    private static bool ValidCrop(ImageCrop crop) =>
        double.IsFinite(crop.X) && double.IsFinite(crop.Y) && double.IsFinite(crop.Width) && double.IsFinite(crop.Height)
        && crop.X >= 0 && crop.Y >= 0 && crop.Width > 0 && crop.Height > 0 && crop.X + crop.Width <= 1.000001 && crop.Y + crop.Height <= 1.000001;
    internal Task<ChatImage> CropAsync(ChatImage image, ImageCrop crop, CancellationToken ct, bool squareCrop = false) =>
        RunAsync("crop:" + (image.OriginalAssetId ?? image.AssetId) + ":" + JsonSerializer.Serialize(crop) + ":" + squareCrop,
            () => Variant(image.OriginalAssetId ?? image.AssetId ?? throw new IOException("图片原图缺失。"), crop, squareCrop: squareCrop), ct);
    internal async Task<ChatImage> NormalizeAsync(ChatImage image, CancellationToken ct)
    {
        image = image.Snapshot();
        if (image.Store is { } origin && !string.Equals(origin.DirectoryPath, assets.DirectoryPath, StringComparison.OrdinalIgnoreCase)) throw new IOException("不能跨工作区读取图片。");
        if (!string.IsNullOrEmpty(image.Data))
            return await RunAsync("legacy:" + legacyKeys.GetValue(image.Data, _ => new()).Key, () => ImportBytes(Convert.FromBase64String(image.Data)), ct);
        return await RunAsync("normalize:" + image.AssetId, () => { assets.Attach(image); return image.Snapshot(); }, ct);
    }
    internal Task<BitmapSource> DisplayAsync(ChatImage image, int limit, bool original, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return Task.FromCanceled<BitmapSource>(ct);
        if (image.Store is { } origin && !string.Equals(origin.DirectoryPath, assets.DirectoryPath, StringComparison.OrdinalIgnoreCase)) throw new IOException("不能跨工作区读取图片。");
        var id = original ? image.OriginalAssetId ?? image.AssetId : limit <= 360 ? image.ThumbnailAssetId ?? image.AssetId : image.AssetId;
        var key = $"view:{id ?? legacyKeys.GetValue(image.Data, _ => new()).Key}:{limit}:{original}";
        if (FindBitmap(key) is { } cached) return Task.FromResult(cached);
        return RunAsync(key, () =>
        {
            if (image.Store is { } origin && !string.Equals(origin.DirectoryPath, assets.DirectoryPath, StringComparison.OrdinalIgnoreCase)) throw new IOException("不能跨工作区读取图片。");
            byte[] bytes;
            try { bytes = id is null ? Convert.FromBase64String(image.Data) : Read(id); }
            catch (IOException) when (!original && limit <= 360 && image.ThumbnailAssetId is not null && image.OriginalAssetId is not null)
            {
                var rebuilt = Variant(image.OriginalAssetId, image.Crop, rebuild: true, squareCrop: image.SquareCrop);
                bytes = Read(rebuilt.ThumbnailAssetId!);
            }
            return DecodeCached(key, bytes, limit);
        }, ct);
    }

    internal Task<BitmapSource> DisplayLocalAsync(string path, int limit, CancellationToken ct) =>
        RunAsync($"reader:{path}:{limit}", () =>
        {
            var root = Path.GetPathRoot(path);
            if (root is not { Length: 3 } || root[1] != ':' || new DriveInfo(root).DriveType is not
                (DriveType.Fixed or DriveType.Removable or DriveType.CDRom or DriveType.Ram))
                throw new IOException("图片引用需要指向本机磁盘。");
            var current = root;
            foreach (var part in path[root.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            {
                current = Path.Combine(current, part);
                if ((File.GetAttributes(current) & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                    throw new IOException("图片路径包含重解析项。");
            }
            var file = new FileInfo(path);
            var key = $"reader:{path}:{limit}:{file.Length}:{file.LastWriteTimeUtc.Ticks}";
            return FindBitmap(key) ?? DecodeCached(key, ReadFile(path), limit);
        }, ct);

    private BitmapSource? FindBitmap(string key)
    {
        lock (gate)
        {
            var node = bitmaps.First;
            while (node is not null)
            {
                if (node.Value.Key == key) { bitmaps.Remove(node); bitmaps.AddFirst(node); return node.Value.Image; }
                node = node.Next;
            }
            return null;
        }
    }

    private BitmapSource DecodeCached(string key, byte[] bytes, int limit)
    {
        using var stream = new MemoryStream(bytes);
        var bitmap = ImageCodec.Decode(stream, limit); DecodedBitmaps++;
        lock (gate)
        {
            if (!disposed)
            {
                bitmaps.AddFirst((key, bitmap)); pixels += (long)bitmap.PixelWidth * bitmap.PixelHeight;
                while (bitmaps.Count > 32 || pixels > 12_000_000)
                { var last = bitmaps.Last!.Value; pixels -= (long)last.Image.PixelWidth * last.Image.PixelHeight; bitmaps.RemoveLast(); }
            }
        }
        return bitmap;
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true; lifetime.Cancel(); queue.CompleteAdding(); bitmaps.Clear(); pixels = 0; assets.ClearCache();
        }
    }
    internal async Task DrainAsync()
    {
        while (PendingCount > 0) await Task.Delay(15);
    }
    public async ValueTask DisposeAsync()
    {
        Dispose(); await stopped.Task.ConfigureAwait(false);
        lock (gate)
        {
            if (resourcesDisposed) return;
            resourcesDisposed = true; lifetime.Dispose(); queue.Dispose();
        }
    }
}
