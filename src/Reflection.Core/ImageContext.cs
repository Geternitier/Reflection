namespace Reflection.Core;

public sealed record ImageContextSelection(IReadOnlyList<ChatMessage> Messages, int OmittedImages, int IncludedImages, long EncodedCharacters)
{
    public string Notice => OmittedImages > 0 ? $"本轮携带 {IncludedImages} 张图片；较早的 {OmittedImages} 张图片及所在轮次未纳入上下文，历史记录仍保留。" : "";
}

public static class ImageContext
{
    public const int MaxImages = 16;
    public const long MaxEncodedCharacters = 12_000_000;
    public static ImageContextSelection Select(IEnumerable<ChatMessage> messages, string system, int characterBudget)
    {
        if (system.Length >= characterBudget) throw new ArgumentException("角色提示词超过上下文预算，请缩短提示词或调高预算。");
        var eligible = messages.Where(x => !x.Incomplete && !x.Stopped && !x.HasError && InferenceClient.HasPayload(x)).ToArray();
        var turns = new List<List<ChatMessage>>();
        foreach (var message in eligible)
        {
            if (message.Role == "user" || message.Opening || turns.Count == 0) turns.Add([]);
            turns[^1].Add(message);
        }
        var selected = new List<ChatMessage>();
        var remaining = characterBudget - system.Length;
        var count = 0; long bytes = 0;
        for (var i = turns.Count - 1; i >= 0; i--)
        {
            var turn = turns[i];
            if (turn[0].Role != "user" && !(turn[0].Opening && turn[0].Role == "assistant")) continue;
            var weight = turn.Sum(InferenceClient.MessageWeight);
            var pictures = turn.Where(x => x.Role == "user").SelectMany(x => x.Images).ToArray();
            if (weight > remaining || count + pictures.Length > MaxImages)
            {
                if (selected.Count == 0)
                    throw new ArgumentException(weight > remaining ? "最新一轮消息超过上下文预算，请缩短消息。" : "本次图片超过请求预算，请减少图片（最多 16 张、12 MB 编码数据）。");
                break;
            }
            var size = pictures.Sum(SizeOf);
            if (bytes + size > MaxEncodedCharacters)
            {
                if (selected.Count == 0) throw new ArgumentException("本次图片超过请求预算，请减少图片（最多 16 张、12 MB 编码数据）。");
                break;
            }
            selected.InsertRange(0, turn); remaining -= weight; count += pictures.Length; bytes += size;
        }
        var total = eligible.Where(x => x.Role == "user").Sum(x => x.Images.Count);
        return new(selected, total - count, count, bytes);
    }
    private static long SizeOf(ChatImage image)
    {
        if (!string.IsNullOrEmpty(image.Data)) return image.Data.Length;
        if (image.AssetId is null) throw new IOException("图片没有可发送的数据。");
        return image.Store?.EncodedSize(image) ?? throw new IOException("图片未绑定工作区。");
    }
    public static async Task<ChatMessage[]> MaterializeAsync(ImageContextSelection selection, CancellationToken ct = default)
    {
        var result = new List<ChatMessage>();
        foreach (var message in selection.Messages)
        {
            ct.ThrowIfCancellationRequested();
            var copy = new ChatMessage { Role = message.Role, Content = message.Content, Opening = message.Opening, CreatedAt = message.CreatedAt };
            if (message.Role == "user")
                foreach (var image in message.Images)
                {
                    var data = image.Data;
                    if (string.IsNullOrEmpty(data))
                        data = await (image.Store ?? throw new IOException("图片未绑定工作区。")).Base64Async(image.AssetId ?? throw new IOException("图片资产缺失。"), ct).ConfigureAwait(false);
                    copy.Images.Add(new() { Mime = image.Mime, Data = data });
                }
            result.Add(copy);
        }
        return result.ToArray();
    }
}
