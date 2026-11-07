using System.Security.Cryptography;
using System.Text.Json;

namespace Reflection.Core;

// Assets are immutable and scoped to one workspace. Only content hashes become filenames.
public sealed class ImageAssetStore
{
    public const int MaxFileBytes = 20_000_000;
    private readonly string directory;
    private readonly object cacheGate = new();
    private readonly LinkedList<(string Id, string Data)> encoded = new();
    private long cachedCharacters;
    private readonly SemaphoreSlim encodingGate = new(1, 1);
    private readonly Dictionary<string, (long Length, long Stamp)> validated = [];
    public string DirectoryPath => directory;
    public ImageAssetStore(string root) => directory = Path.Combine(Path.GetFullPath(root), "assets", "images");
    public static bool ValidId(string? id) => id is { Length: 64 } && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    public string PathFor(string id) => ValidId(id) ? Path.Combine(directory, id + ".bin") : throw new IOException("图片资产标识无效。");
    public async Task<string> PutAsync(byte[] bytes, CancellationToken ct = default)
    {
        if (bytes.Length is 0 or > MaxFileBytes) throw new ArgumentException("图片文件必须为 1 字节至 20 MB。");
        var id = Convert.ToHexStringLower(SHA256.HashData(bytes));
        Directory.CreateDirectory(directory);
        var path = PathFor(id);
        if (File.Exists(path))
        {
            try
            {
                var existing = await ReadAsync(id, ct).ConfigureAwait(false);
                if (existing.AsSpan().SequenceEqual(bytes)) return id;
            }
            catch (IOException ex) { System.Diagnostics.Trace.WriteLine("Repairing content-addressed asset: " + ex.Message); }
        }
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temp, bytes, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            // Any concurrent writer for this ID has the same verified content.
            File.Move(temp, path, true);
            RememberValidated(id);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return id;
    }
    public async Task<byte[]> ReadAsync(string id, CancellationToken ct = default)
    {
        var path = PathFor(id);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
        if (stream.Length is 0 or > MaxFileBytes) throw new IOException("图片资产尺寸无效。");
        var bytes = new byte[(int)stream.Length];
        await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != id) throw new IOException("图片资产已损坏，请检查备份。");
        RememberValidated(id);
        return bytes;
    }
    private void RememberValidated(string id)
    {
        var file = new FileInfo(PathFor(id));
        lock (cacheGate)
        {
            if (validated.Count >= 512) validated.Clear();
            validated[id] = (file.Length, file.LastWriteTimeUtc.Ticks);
        }
    }
    private void Validate(string id)
    {
        var file = new FileInfo(PathFor(id));
        if (!file.Exists || file.Length is 0 or > MaxFileBytes) throw new IOException("图片资产缺失或尺寸无效，请检查备份。");
        lock (cacheGate)
            if (validated.TryGetValue(id, out var stamp) && stamp == (file.Length, file.LastWriteTimeUtc.Ticks)) return;
        // Called on the store/pipeline worker, never by a WPF binding converter.
        var bytes = File.ReadAllBytes(file.FullName);
        if (bytes.Length > MaxFileBytes || Convert.ToHexStringLower(SHA256.HashData(bytes)) != id) throw new IOException("图片资产已损坏，请检查备份。");
        RememberValidated(id);
    }
    public long EncodedSize(ChatImage image)
    {
        if (!string.IsNullOrEmpty(image.Data)) return image.Data.Length;
        if (image.AssetId is null) return 0;
        Validate(image.AssetId);
        var size = new FileInfo(PathFor(image.AssetId)).Length;
        if (size is 0 or > MaxFileBytes) throw new IOException("图片资产尺寸无效。");
        return (size + 2) / 3 * 4;
    }
    public async Task<string> Base64Async(string id, CancellationToken ct = default)
    {
        await Task.Run(() => Validate(id), ct).ConfigureAwait(false);
        if (Cached() is { } existing) return existing;
        await encodingGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Cached() is { } shared) return shared;
            var data = Convert.ToBase64String(await ReadAsync(id, ct).ConfigureAwait(false));
            lock (cacheGate)
            {
                if (data.Length <= 12_000_000)
                {
                    encoded.AddFirst((id, data)); cachedCharacters += data.Length;
                    while (encoded.Count > 16 || cachedCharacters > 12_000_000)
                    { cachedCharacters -= encoded.Last!.Value.Data.Length; encoded.RemoveLast(); }
                }
            }
            return data;
        }
        finally { encodingGate.Release(); }
        string? Cached()
        {
            lock (cacheGate)
            {
                var node = encoded.First;
                while (node is not null)
                {
                    if (node.Value.Id == id) { encoded.Remove(node); encoded.AddFirst(node); return node.Value.Data; }
                    node = node.Next;
                }
                return null;
            }
        }
    }
    public async Task<ChatImage> ExternalizeAsync(ChatImage source, CancellationToken ct = default)
    {
        var image = source.Snapshot();
        if (!string.IsNullOrEmpty(image.Data))
        {
            if (image.Data.Length > (MaxFileBytes + 2L) / 3 * 4) throw new ArgumentException("图片编码数据过大。");
            image.AssetId = await PutAsync(Convert.FromBase64String(image.Data), ct).ConfigureAwait(false);
            image.Data = "";
        }
        foreach (var id in References(image))
        {
            if (image.Store is { } origin && !string.Equals(origin.DirectoryPath, directory, StringComparison.OrdinalIgnoreCase))
                throw new IOException("不能跨工作区引用图片资产。");
            ValidateReference(image, id);
        }
        image.Store = this; image.EncodedLength = EncodedSize(image);
        return image;
    }
    public static IEnumerable<string> References(ChatImage image) => new[] { image.AssetId, image.OriginalAssetId, image.ThumbnailAssetId }.OfType<string>().Distinct();
    public void Attach(ChatImage image)
    {
        image.Store = this;
        if (string.IsNullOrEmpty(image.Data) && image.AssetId is null) throw new IOException("图片没有有效内容或资产引用。");
        if (!string.IsNullOrEmpty(image.Data))
        {
            if (image.Data.Length > (MaxFileBytes + 2L) / 3 * 4) throw new IOException("旧图片编码数据过大。");
            try { _ = Convert.FromBase64String(image.Data); }
            catch (FormatException ex) { throw new IOException("旧图片编码损坏，请检查备份。", ex); }
        }
        foreach (var id in References(image))
            ValidateReference(image, id);
        image.EncodedLength = EncodedSize(image);
    }
    private void ValidateReference(ChatImage image, string id)
    {
        if (id == image.ThumbnailAssetId && id != image.AssetId && id != image.OriginalAssetId)
        {
            if (!ValidId(id)) throw new IOException("图片资产标识无效。");
            // A thumbnail is replaceable. Its original and model image remain required roots.
            return;
        }
        Validate(id);
    }
    public void ClearCache() { lock (cacheGate) { encoded.Clear(); validated.Clear(); cachedCharacters = 0; } }

    // Call only while the owning workspace is quiescent. Backups remain GC roots.
    public async Task<int> CollectUnusedAsync(string root, CancellationToken ct = default)
    {
        var workspace = Path.GetFullPath(root);
        if (!string.Equals(Path.Combine(workspace, "assets", "images"), directory, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("工作区不匹配。");
        if (!Directory.Exists(directory)) return 0;
        var referenced = new HashSet<string>();
        var files = new[] { "characters.json", "characters.json.bak" }.Select(x => Path.Combine(workspace, x))
            .Concat(Directory.EnumerateFiles(Path.Combine(workspace, "chats"), "*.json*", SearchOption.TopDirectoryOnly)
                .Where(x => x.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || x.EndsWith(".json.bak", StringComparison.OrdinalIgnoreCase)));
        foreach (var file in files.Where(File.Exists))
        {
            ct.ThrowIfCancellationRequested();
            // A corrupt primary with a valid backup is recoverable, but GC cannot prove its references.
            // Abort the entire collection rather than delete potentially recoverable assets.
            await using var stream = File.OpenRead(file);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            Visit(json.RootElement);
        }
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*.bin"))
        {
            ct.ThrowIfCancellationRequested();
            var id = Path.GetFileNameWithoutExtension(file);
            if (!ValidId(id) || referenced.Contains(id)) continue;
            File.Delete(PathFor(id)); count++;
        }
        var variants = Path.Combine(Path.GetDirectoryName(directory)!, "variants");
        if (Directory.Exists(variants))
            foreach (var file in Directory.EnumerateFiles(variants, "*.json"))
            {
                if (!ValidId(Path.GetFileNameWithoutExtension(file))) continue;
                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(file, ct).ConfigureAwait(false));
                var image = document.RootElement.Deserialize<ChatImage>(LocalStore.Json);
                if (image is not null && References(image).Any(id => !referenced.Contains(id))) File.Delete(file);
            }
        ClearCache(); return count;
        void Visit(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Object)
                foreach (var property in node.EnumerateObject())
                {
                    if (property.Name is "assetId" or "originalAssetId" or "thumbnailAssetId" && property.Value.ValueKind == JsonValueKind.String)
                    {
                        var id = property.Value.GetString();
                        if (!ValidId(id)) throw new IOException("图片资产标识无效，已取消清理。");
                        referenced.Add(id!);
                    }
                    else Visit(property.Value);
                }
            else if (node.ValueKind == JsonValueKind.Array) foreach (var child in node.EnumerateArray()) Visit(child);
        }
    }
}
