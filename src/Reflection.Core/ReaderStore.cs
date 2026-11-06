using System.Text.Json;

namespace Reflection.Core;

public sealed record ReadingPosition(int Offset = 0, double Fraction = 0);
public sealed class ReaderState
{
    public List<string> Sources { get; set; } = [];
    public string? LastPath { get; set; }
    public Dictionary<string, ReadingPosition> Positions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public double FontSize { get; set; } = 18;
    public double LineHeight { get; set; } = 1.85;
    public double Width { get; set; } = 760;
    public bool Serif { get; set; } = true;
    public bool KeepSnapshot { get; set; } = true;
    public bool FocusMode { get; set; }
}
public sealed record ReadingSnapshot(string Path, string Text, string Encoding);

public sealed class ReaderStore(string root)
{
    private readonly string directory = Path.Combine(root, "reader");
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly HashSet<string> recovered = [];
    public async Task<ReaderState> LoadAsync()
    {
        var state = await ReadAsync<ReaderState>("state.json") ?? new();
        state.Sources = (state.Sources ?? []).Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).Take(64).ToList();
        state.Positions = new((state.Positions ?? []).TakeLast(100).ToDictionary(x => x.Key, x => x.Value), StringComparer.OrdinalIgnoreCase);
        state.FontSize = double.IsFinite(state.FontSize) ? Math.Clamp(state.FontSize, 14, 28) : 18;
        state.LineHeight = double.IsFinite(state.LineHeight) ? Math.Clamp(state.LineHeight, 1.4, 2.4) : 1.85;
        state.Width = double.IsFinite(state.Width) ? Math.Clamp(state.Width, 520, 1040) : 760;
        return state;
    }
    public Task SaveAsync(ReaderState state) => WriteAsync("state.json", JsonSerializer.Serialize(state, LocalStore.Json));
    public Task<ReadingSnapshot?> LoadSnapshotAsync() => ReadAsync<ReadingSnapshot>("snapshot.json");
    public Task SaveSnapshotAsync(ReadingDocument doc) => doc.Text.Length <= 2_000_000
        ? WriteAsync("snapshot.json", JsonSerializer.Serialize(new ReadingSnapshot(doc.Path, doc.Text, doc.Encoding), LocalStore.Json)) : ClearSnapshotAsync();
    public async Task ClearSnapshotAsync()
    {
        await gate.WaitAsync();
        try { foreach (var suffix in new[] { "", ".bak" }) File.Delete(Path.Combine(directory, "snapshot.json" + suffix)); }
        finally { gate.Release(); }
    }
    private async Task<T?> ReadAsync<T>(string name)
    {
        foreach (var suffix in new[] { "", ".bak" })
            try
            {
                var value = JsonSerializer.Deserialize<T>(await File.ReadAllTextAsync(Path.Combine(directory, name + suffix)), LocalStore.Json);
                if (suffix.Length > 0) recovered.Add(name);
                return value;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return default;
    }
    private async Task WriteAsync(string name, string json)
    {
        await gate.WaitAsync();
        var temporary = Path.Combine(directory, name + "." + Guid.NewGuid() + ".tmp");
        try
        {
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, name);
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            await using (var writer = new StreamWriter(stream)) await writer.WriteAsync(json);
            if (recovered.Remove(name)) File.Move(temporary, path, true);
            else if (File.Exists(path)) File.Replace(temporary, path, path + ".bak"); else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); gate.Release(); }
    }
}
