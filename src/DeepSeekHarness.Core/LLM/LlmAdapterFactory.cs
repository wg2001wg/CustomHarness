namespace DeepSeekHarness.Core.LLM;

using DeepSeekHarness.Core.Config;

/// <summary>LLM 适配器工厂。</summary>
public static class LlmAdapterFactory
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(5),
    };

    /// <summary>创建适配器(当前仅 DeepSeek 官方,OpenAI 兼容)。</summary>
    public static ILlmAdapter Create(AppSettings settings)
        => CreateForProvider(settings, settings.ProviderId);

    /// <summary>创建自定义 provider 的适配器。</summary>
    public static ILlmAdapter CreateForProvider(AppSettings settings, string providerId)
    {
        var provider = settings.Providers.FirstOrDefault(p => p.Id == providerId);
        if (provider == null) return new DeepSeekAdapter(Http, () => settings.ResolveApiKey(settings.ProviderId));
        return new DeepSeekAdapter(Http, () => settings.ResolveApiKey(providerId));
    }
}
