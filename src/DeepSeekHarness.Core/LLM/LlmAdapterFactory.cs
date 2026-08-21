namespace DeepSeekHarness.Core.LLM;

using DeepSeekHarness.Core.Config;

/// <summary>LLM 适配器工厂(OpenAI 兼容端点,支持任意已配置厂商)。</summary>
public static class LlmAdapterFactory
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(5),
    };

    /// <summary>创建当前选中 provider 的适配器。</summary>
    public static ILlmAdapter Create(AppSettings settings)
        => CreateForProvider(settings, settings.ProviderId);

    /// <summary>创建指定 provider 的适配器(使用其配置的 BaseUrl 与 API Key)。</summary>
    public static ILlmAdapter CreateForProvider(AppSettings settings, string providerId)
    {
        var provider = settings.Providers.FirstOrDefault(p => p.Id == providerId);
        if (provider == null)
            return new DeepSeekAdapter(Http,
                () => settings.ResolveApiKey(providerId),
                settingsProvider: () => settings);

        return new DeepSeekAdapter(
            Http,
            () => settings.ResolveApiKey(providerId),
            () => string.IsNullOrWhiteSpace(provider.BaseUrl) ? DeepSeekAdapter.DefaultEndpoint : provider.BaseUrl,
            settingsProvider: () => settings);
    }
}
