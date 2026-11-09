using Reflection.Core;

namespace Reflection.Desktop;

public sealed partial class MainViewModel
{
    private readonly record struct MetricsSource(Guid Service, string Address, ApiProtocol Protocol, string Model)
    {
        public MetricsSource(ModelOption model) : this(model.Service.Id, model.Service.BaseUrl, model.Service.Protocol, model.Model) { }
    }

    private readonly Dictionary<Guid, (MetricsSource Source, GenerationMetrics Metrics)> lastMetrics = [];

    private GenerationMetrics? CurrentMetrics
    {
        get
        {
            if (CurrentRun?.Activity == "chat" || SelectedModel is not { } selected
                || !lastMetrics.TryGetValue(Current.Id, out var entry)) return null;
            return entry.Source == new MetricsSource(selected) ? entry.Metrics : null;
        }
    }

    public string TokenSpeed => CurrentMetrics is { TokensPerSecond: { } rate } metrics
        ? $"{(metrics.EstimatedSpeed ? "≈" : "")}{Compact(rate)} tok/s" : "— tok/s";

    public double ContextFraction => CurrentMetrics is { ContextTokens: { } tokens, ContextLimit: { } limit }
        ? Math.Clamp((double)tokens / limit, 0, 1) : 0;

    public string ContextRingHint => CurrentMetrics is { ContextTokens: { } tokens, ContextLimit: { } limit }
        ? $"上下文占用 {(double)tokens / limit:P1}\n{ContextUsageHint}" : ContextUsageHint;

    public string TokenSpeedHint => CurrentMetrics is { TokensPerSecond: { } rate } metrics
        ? metrics.EstimatedSpeed ? $"最近一次成功回复约 {rate:N1} token/秒。按服务报告的输出 token 与请求总耗时计算，含等待时间。"
            : $"最近一次成功回复的生成速度为 {rate:N1} token/秒，由推理服务报告。"
        : "生成速度。回复结束并收到服务统计后显示。";

    public string ContextUsageHint => CurrentMetrics is { ContextTokens: { } tokens } metrics
        ? metrics.ContextLimit is { } limit ? $"最近一次成功回复的输入和输出共 {tokens:N0} token，服务报告的上下文容量为 {limit:N0} token。"
            : $"最近一次成功回复的输入和输出共 {tokens:N0} token，服务未报告上下文容量。"
        : "上下文占用。回复结束并收到服务统计后显示。";

    public string ContextControlName => $"修改当前对话的上下文长度。{ContextRingHint}";

    public async Task SetContextWindowOverrideAsync(int? value)
    {
        var chat = Current;
        await navigation.WaitAsync();
        try
        {
            var previous = chat.ContextWindowOverride;
            if (!ReferenceEquals(Current, chat) || !Idle || previous == value) return;
            loading = true; Raise(nameof(Idle));
            chat.ContextWindowOverride = value;
            try
            {
                if (!IsUnused() || Conversations.Any(summary => summary.Id == chat.Id)) await SaveCurrentAsync();
            }
            catch
            {
                chat.ContextWindowOverride = previous;
                throw;
            }
            finally { loading = false; Raise(nameof(Idle)); }
        }
        finally { navigation.Release(); }
    }

    private static string Compact(double value) => value >= 1000 ? $"{value / 1000:0.#}k" : $"{value:0.#}";

    private void NotifyMetrics()
    {
        Raise(nameof(TokenSpeed)); Raise(nameof(ContextFraction)); Raise(nameof(ContextRingHint));
        Raise(nameof(TokenSpeedHint)); Raise(nameof(ContextUsageHint)); Raise(nameof(ContextControlName));
    }
}
