using System.Text.Json;
using Reflection.Core;

internal static class ImageRegressions
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        await test("Image history trims whole turns instead of blocking later text", () =>
        {
            var history = new List<ChatMessage>();
            for (var i = 0; i < 20; i++)
            {
                history.Add(new() { Content = "question " + i, Images = [new() { Data = "QQ==" }] });
                history.Add(new() { Role = "assistant", Content = "answer " + i });
            }
            history.Add(new() { Content = "next text question" });
            var selected = ImageContext.Select(history, "", 24000);
            Check(selected.IncludedImages == 16 && selected.OmittedImages == 4 && selected.Messages.Count == 33);
            Check(selected.Messages[0].Content == "question 4" && selected.Messages[^1] == history[^1]);
            Check(history.Count == 41 && history.Sum(x => x.Images.Count) == 20);
            return Task.CompletedTask;
        });
        await test("Encoded image and text budgets retain complete recent turns", async () =>
        {
            var history = Enumerable.Range(0, 9).Select(i => new ChatMessage { Content = "q" + i, Images = [new() { Data = new string('A', 1_500_000) }] }).ToArray();
            var selected = ImageContext.Select(history, "", 1000);
            Check(selected.IncludedImages == 8 && selected.OmittedImages == 1 && selected.EncodedCharacters == 12_000_000);
            await Throws<ArgumentException>(() => Task.FromResult(ImageContext.Select([new() { Images = [new() { Data = new string('A', 12_000_001) }] }], "", 1000)));
            var text = ImageContext.Select([new() { Content = new string('x', 80) }, new() { Role = "assistant", Content = "answer" }, new() { Content = "latest" }], "", 30);
            Check(text.Messages.Count == 1 && text.Messages[0].Content == "latest");
        });
        await test("Omitted image turns are not read and cannot leave orphaned answers", () =>
        {
            var old = new ChatMessage { Content = "missing old image", Images = [new() { AssetId = new string('a', 64) }] };
            var history = new[] { old, new() { Role = "assistant", Content = "old answer" } }
                .Concat(Enumerable.Range(0, 16).Select(i => new ChatMessage { Content = "q" + i, Images = [new() { Data = "QQ==" }] }));
            var selected = ImageContext.Select(history, "", 24000);
            Check(selected.IncludedImages == 16 && selected.OmittedImages == 1 && !selected.Messages.Contains(old) && selected.Messages.All(x => x.Content != "old answer"));
            return Task.CompletedTask;
        });
        await test("Image assets deduplicate within a workspace and reject cross-workspace references", async () =>
        {
            using var temp = new Workspace();
            var a = new LocalStore(Path.Combine(temp.Root, "daily"));
            var b = new LocalStore(Path.Combine(temp.Root, "private"));
            var image = await a.Images.ExternalizeAsync(new() { Data = "QQ==" });
            var again = await a.Images.ExternalizeAsync(new() { Data = "QQ==" });
            Check(image.AssetId == again.AssetId && Directory.GetFiles(a.Images.DirectoryPath, "*.bin").Length == 1);
            await Throws<IOException>(() => b.Images.ExternalizeAsync(image));
            var other = await b.Images.ExternalizeAsync(new() { Data = "QQ==" });
            Check(other.AssetId == image.AssetId && File.Exists(b.Images.PathFor(other.AssetId!)));
            File.Delete(a.Images.PathFor(image.AssetId!));
            Check(await b.Images.Base64Async(other.AssetId!) == "QQ==");
            await Throws<IOException>(() => Task.FromResult(a.Images.PathFor("../private")));
        });
        await test("Legacy embedded images migrate on save without mutating live data or its backup", async () =>
        {
            using var temp = new Workspace(); var store = new LocalStore(temp.Root);
            var chat = new Conversation
            {
                Attachments = [new() { Data = "QQ==" }],
                CharacterSnapshot = new() { Image = new() { Data = "QQ==" } },
                Messages = [new() { Content = "old", Images = [new() { Data = "QQ==" }] }]
            };
            var path = Path.Combine(temp.Root, "chats", chat.Id + ".json");
            var legacy = JsonSerializer.Serialize(chat, LocalStore.Json); await File.WriteAllTextAsync(path, legacy);
            await store.SaveConversationAsync(chat);
            Check(chat.Messages[0].Images[0].Data == "QQ==" && chat.Messages[0].Images[0].AssetId is null);
            Check(!(await File.ReadAllTextAsync(path)).Contains("QQ==") && await File.ReadAllTextAsync(path + ".bak") == legacy);
            var loaded = await store.LoadConversationAsync(chat.Id);
            Check(loaded.Attachments[0].Data == "" && loaded.Attachments[0].AssetId == loaded.Messages[0].Images[0].AssetId);
            Check(loaded.CharacterSnapshot!.Image!.AssetId == loaded.Attachments[0].AssetId);
            Check(await store.Images.Base64Async(loaded.Attachments[0].AssetId!) == "QQ==");
            Check(Directory.GetFiles(store.Images.DirectoryPath, "*.bin").Length == 1);
        });
        await test("Referenced images materialize as inline payloads in both protocols", async () =>
        {
            using var temp = new Workspace(); var store = new LocalStore(temp.Root);
            var image = await store.Images.ExternalizeAsync(new() { Data = "QQ==", Mime = "image/png" });
            image.OriginalAssetId = await store.Images.PutAsync([0, 1, 2]);
            var messages = new[] { new ChatMessage { Content = "inspect", Images = [image, image.Snapshot()] } };
            foreach (var protocol in new[] { ApiProtocol.Ollama, ApiProtocol.OpenAI })
            {
                var body = await InferenceClient.CreateBodyAsync(new(new() { Protocol = protocol }, "m"), messages, "", new());
                Check(body.Contains("QQ==") && !body.Contains("assetId") && !body.Contains(image.AssetId!));
                Check(!body.Contains("AAEC"), "Original source bytes leaked into the model request");
                using var json = JsonDocument.Parse(body);
                var message = json.RootElement.GetProperty("messages")[0];
                Check(protocol == ApiProtocol.Ollama ? message.GetProperty("images").GetArrayLength() == 2 : message.GetProperty("content").GetArrayLength() == 3);
            }
            Check(image.Data == "", "Sending mutated a stored image reference");
            store.Images.ClearCache();
            var encoded = await Task.WhenAll(store.Images.Base64Async(image.AssetId!), store.Images.Base64Async(image.AssetId!));
            Check(ReferenceEquals(encoded[0], encoded[1]), "Concurrent request preparation repeated Base64 encoding");
            var exported = CharacterExchange.Import(await CharacterExchange.ExportAsync(new() { Name = "portable", Image = image }));
            Check(exported.Image!.Data == "QQ==" && exported.Image.AssetId is null);
        });
        await test("Asset request cancellation occurs before HTTP dispatch", async () =>
        {
            using var temp = new Workspace(); var store = new LocalStore(temp.Root);
            var image = await store.Images.ExternalizeAsync(new() { Data = "QQ==" });
            using var cancel = new CancellationTokenSource(); cancel.Cancel();
            await Throws<OperationCanceledException>(() => InferenceClient.CreateBodyAsync(new(new(), "m"), [new() { Images = [image] }], "", new(), ct: cancel.Token));
            await Throws<OperationCanceledException>(() => store.Images.PutAsync([7, 8, 9], cancel.Token));
            Check(Directory.GetFiles(store.Images.DirectoryPath, "*.bin").Length == 1);
        });
        await test("Missing or corrupt assets recover the valid backup without overwriting it", async () =>
        {
            foreach (var corrupt in new[] { false, true })
            {
                using var temp = new Workspace(); var store = new LocalStore(temp.Root);
                var chat = new Conversation { Messages = [new() { Content = "valid", Images = [new() { Data = "QQ==" }] }] };
                await store.SaveConversationAsync(chat);
                chat.Messages[0].Images[0] = new() { Data = "Qg==" }; chat.Messages[0].Content = "new";
                await store.SaveConversationAsync(chat);
                var path = Path.Combine(temp.Root, "chats", chat.Id + ".json");
                var backup = await File.ReadAllTextAsync(path + ".bak");
                var broken = JsonSerializer.Deserialize<Conversation>(await File.ReadAllTextAsync(path), LocalStore.Json)!.Messages[0].Images[0].AssetId!;
                if (corrupt) await File.WriteAllBytesAsync(store.Images.PathFor(broken), [3, 4]); else File.Delete(store.Images.PathFor(broken));
                var restored = await store.LoadConversationAsync(chat.Id);
                Check(restored.Messages[0].Content == "valid" && store.RecoveryNotice is not null);
                await store.SaveConversationAsync(restored);
                Check(await File.ReadAllTextAsync(path + ".bak") == backup);
                Check(await store.Images.Base64Async(restored.Messages[0].Images[0].AssetId!) == "QQ==");
            }
        });
        await test("Asset cleanup preserves backups, drafts, portraits and shared conversation references", async () =>
        {
            using var temp = new Workspace(); var store = new LocalStore(temp.Root);
            var image = await store.Images.ExternalizeAsync(new() { Data = "QQ==" });
            image.OriginalAssetId = await store.Images.PutAsync([1, 2]); image.ThumbnailAssetId = await store.Images.PutAsync([3, 4]);
            var card = new CharacterCard { Image = image };
            await store.SaveCharactersAsync([card]); await store.SaveCharactersAsync([]); // portrait survives through backup
            var a = new Conversation { Attachments = [image.Snapshot()] };
            var b = new Conversation { Messages = [new() { Images = [image.Snapshot()] }] };
            await store.SaveConversationAsync(a); await store.SaveConversationAsync(b);
            var orphan = await store.Images.PutAsync([9, 9]);
            await store.DeleteConversationAsync(a.Id);
            Check(await store.CollectImageAssetsAsync() == 1 && !File.Exists(store.Images.PathFor(orphan)));
            Check(ImageAssetStore.References(image).All(id => File.Exists(store.Images.PathFor(id))));
            await store.DeleteConversationAsync(b.Id); await store.SaveCharactersAsync([]);
            Check(await store.CollectImageAssetsAsync() == 3);
        });
        await test("Corrupt JSON aborts asset cleanup before any deletion", async () =>
        {
            using var temp = new Workspace(); var store = new LocalStore(temp.Root);
            var orphan = await store.Images.PutAsync([9, 9]);
            await File.WriteAllTextAsync(Path.Combine(temp.Root, "characters.json"), "{broken");
            await Throws<JsonException>(() => store.CollectImageAssetsAsync());
            Check(File.Exists(store.Images.PathFor(orphan)));
        });
        await test("Failed JSON commit leaves old references valid and orphan assets reclaimable", async () =>
        {
            using var temp = new Workspace(); var store = new LocalStore(temp.Root);
            var card = new CharacterCard { Image = new() { Data = "QQ==" } };
            await store.SaveCharactersAsync([card]); var path = Path.Combine(temp.Root, "characters.json");
            var before = await File.ReadAllTextAsync(path);
            card.Image = new() { Data = "Qg==" };
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                await Throws<IOException>(() => store.SaveCharactersAsync([card]));
            Check(await File.ReadAllTextAsync(path) == before);
            Check(await store.CollectImageAssetsAsync() == 1);
            Check(await store.Images.Base64Async((await store.LoadCharactersAsync())[0].Image!.AssetId!) == "QQ==");
        });
    }
    private static void Check(bool value, string message = "Image regression assertion failed") { if (!value) throw new Exception(message); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private sealed class Workspace : IDisposable
    {
        private readonly string artifacts = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "artifacts"));
        public string Root { get; }
        public Workspace() { Root = Path.Combine(artifacts, "Reflection-image-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(Root); }
        public void Dispose()
        {
            if (!Path.GetFullPath(Root).StartsWith(artifacts + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Unexpected test cleanup path");
            Directory.Delete(Root, true);
        }
    }
}
