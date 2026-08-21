namespace DeepSeekHarness.Core.Config;

/// <summary>
/// 网上主流大模型厂商的预设目录(OpenAI 兼容端点)。
/// 用户在设置里可从这些厂商一键添加,再选择具体模型进行配置。
/// </summary>
public static class KnownProviders
{
    public sealed record ProviderTemplate(
        string Id,
        string Name,
        string BaseUrl,
        string ApiKeyEnv,
        string[] Models);

    /// <summary>预设厂商目录(Order 表示展示顺序)。</summary>
    public static readonly IReadOnlyList<ProviderTemplate> All = new List<ProviderTemplate>
    {
        new("openai", "OpenAI", "https://api.openai.com/v1", "OPENAI_API_KEY",
            new[] { "gpt-5", "gpt-4o", "gpt-4o-mini", "o3", "o4-mini" }),
        new("anthropic", "Anthropic Claude", "https://api.anthropic.com/v1", "ANTHROPIC_API_KEY",
            new[] { "claude-opus-4-1", "claude-sonnet-4-5", "claude-haiku-4-5", "claude-3-7-sonnet" }),
        new("google", "Google Gemini", "https://generativelanguage.googleapis.com/v1beta", "GEMINI_API_KEY",
            new[] { "gemini-2.5-pro", "gemini-2.5-flash", "gemini-2.0-flash", "gemini-1.5-pro" }),
        new("deepseek", "DeepSeek 深度求索", "https://api.deepseek.com/v1", "DEEPSEEK_API_KEY",
            new[] { "deepseek-chat", "deepseek-reasoner", "deepseek-v4", "deepseek-v4-flash" }),
        new("qwen", "阿里通义千问", "https://dashscope.aliyuncs.com/compatible-mode/v1", "DASHSCOPE_API_KEY",
            new[] { "qwen-max", "qwen-plus", "qwen-turbo", "qwen-long" }),
        new("moonshot", "Moonshot Kimi", "https://api.moonshot.cn/v1", "MOONSHOT_API_KEY",
            new[] { "kimi-k2", "moonshot-v1-8k", "moonshot-v1-32k", "moonshot-v1-128k" }),
        new("zhipu", "智谱 GLM", "https://open.bigmodel.cn/api/paas/v4", "ZHIPU_API_KEY",
            new[] { "glm-4-plus", "glm-4-air", "glm-4-flash", "glm-4-long" }),
        new("doubao", "字节豆包(火山引擎)", "https://ark.cn-beijing.volces.com/api/v3", "ARK_API_KEY",
            new[] { "doubao-pro-32k", "doubao-pro-128k", "doubao-lite-32k", "doubao-lite-128k" }),
        new("baidu", "百度文心", "https://qianfan.baidubce.com/v2", "QIANFAN_API_KEY",
            new[] { "ernie-4.0-turbo-8k", "ernie-4.0-8k", "ernie-3.5-8k", "ernie-speed-8k" }),
        new("mistral", "Mistral AI", "https://api.mistral.ai/v1", "MISTRAL_API_KEY",
            new[] { "mistral-large-latest", "mistral-medium-latest", "mistral-small-latest", "open-mistral-nemo" }),
        new("xai", "xAI Grok", "https://api.x.ai/v1", "XAI_API_KEY",
            new[] { "grok-4", "grok-3", "grok-3-mini", "grok-2-latest" }),
        new("groq", "Groq", "https://api.groq.com/openai/v1", "GROQ_API_KEY",
            new[] { "llama-3.3-70b-versatile", "llama-3.1-8b-instant", "mixtral-8x7b-32768", "gemma2-9b-it" }),
        new("cohere", "Cohere", "https://api.cohere.com/v2", "COHERE_API_KEY",
            new[] { "command-r-plus", "command-r", "command-a" }),
        new("ollama", "Ollama(本地)", "http://localhost:11434/v1", "OLLAMA_API_KEY",
            new[] { "llama3.3", "qwen2.5", "mistral", "deepseek-r1" }),
        new("lmstudio", "LM Studio(本地)", "http://localhost:1234/v1", "LMSTUDIO_API_KEY",
            new[] { "local-model" }),
        new("vllm", "vLLM 自建服务", "http://localhost:8000/v1", "VLLM_API_KEY",
            new[] { "qwen2.5-72b-instruct", "llama-3.1-70b-instruct" }),
        new("openrouter", "OpenRouter(聚合)", "https://openrouter.ai/api/v1", "OPENROUTER_API_KEY",
            new[] { "anthropic/claude-3.5-sonnet", "openai/gpt-4o", "google/gemini-2.0-flash", "deepseek/deepseek-chat" }),
    };

    /// <summary>查找厂商模板,找不到返回 null。</summary>
    public static ProviderTemplate? Find(string id)
        => All.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>把厂商模板转为可加入设置的 ProviderConfig。</summary>
    public static AppSettings.ProviderConfig ToConfig(ProviderTemplate t, string? apiKey = null)
    {
        var cfg = new AppSettings.ProviderConfig
        {
            Id = t.Id,
            Name = t.Name,
            BaseUrl = t.BaseUrl,
            ApiKeyEnv = t.ApiKeyEnv,
            ApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey.Trim(),
        };
        foreach (var m in t.Models)
        {
            cfg.Models.Add(new AppSettings.ModelConfig
            {
                Id = m,
                Name = m,
                Default = false,
                Thinking = true,
            });
        }
        if (cfg.Models.Count > 0) cfg.Models[0].Default = true;
        return cfg;
    }
}
