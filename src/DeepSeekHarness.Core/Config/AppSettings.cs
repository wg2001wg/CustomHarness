using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeepSeekHarness.Core.Config;

using DeepSeekHarness.Core.Tools;

/// <summary>
/// 应用设置(对齐参考项目 settings.yaml + credentials)。
/// 持久化到 $DSH_HOME/settings.json。
/// </summary>
public sealed class AppSettings
{
    public string Version { get; set; } = "1.0";

    /// <summary>LLM provider 列表。</summary>
    public List<ProviderConfig> Providers { get; set; } = new()
    {
        new ProviderConfig
        {
            Id = "deepseek-official",
            Name = "DeepSeek 官方",
            BaseUrl = "https://api.deepseek.com/v1",
            ApiKeyEnv = "DEEPSEEK_API_KEY",
            Models =
            {
                new ModelConfig { Id = "deepseek-v4-flash", Name = "DeepSeek V4 Flash(快速)", Default = true, Thinking = true },
                new ModelConfig { Id = "deepseek-v4", Name = "DeepSeek V4(最强)", Thinking = true },
                new ModelConfig { Id = "deepseek-v4-mini", Name = "DeepSeek V4 Mini(轻量)", Thinking = false },
            },
        },
    };

    /// <summary>选中的 provider + model。</summary>
    public string ProviderId { get; set; } = "deepseek-official";
    public string ModelId { get; set; } = "deepseek-v4-flash";

    /// <summary>推理强度: low | medium | high。</summary>
    public string ReasoningEffort { get; set; } = "medium";

    /// <summary>工作区目录。</summary>
    public string Workspace { get; set; } = Directory.GetCurrentDirectory();

    /// <summary>当前 Agent 预设名。</summary>
    public string AgentPreset { get; set; } = "standard";

    /// <summary>权限级别。</summary>
    public PermissionLevel Permission { get; set; } = PermissionLevel.WorkspaceWrite;

    /// <summary>上次打开的会话 ID。</summary>
    public string? LastSessionId { get; set; }

    /// <summary>最近会话列表(工作区维度,最多 50)。</summary>
    public List<RecentSession> RecentSessions { get; set; } = new();

    public sealed class ProviderConfig
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string BaseUrl { get; set; } = "";
        public string ApiKeyEnv { get; set; } = "";
        public string? ApiKey { get; set; }
        public List<ModelConfig> Models { get; set; } = new();
    }

    public sealed class ModelConfig
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public bool Default { get; set; }
        public bool Thinking { get; set; } = true;
    }

    public sealed class RecentSession
    {
        public string Id { get; set; } = "";
        public string? Title { get; set; }
        public DateTimeOffset LastActivity { get; set; }
        public string? Workspace { get; set; }
    }

    // ---------- 持久化 ----------
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    private static string SettingsPath()
    {
        var home = Environment.GetEnvironmentVariable("DSH_HOME");
        if (string.IsNullOrEmpty(home))
            home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
        return Path.Combine(home, "settings.json");
    }

    public void Save()
    {
        var path = SettingsPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
    }

    public static AppSettings Load()
    {
        var path = SettingsPath();
        if (File.Exists(path))
        {
            try
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), JsonOpts) ?? new AppSettings();
            }
            catch { /* 损坏则回退默认 */ }
        }
        return new AppSettings();
    }

    public string? ResolveApiKey(string providerId)
    {
        var provider = Providers.FirstOrDefault(p => p.Id == providerId);
        if (provider == null) return null;
        if (!string.IsNullOrEmpty(provider.ApiKey)) return provider.ApiKey;
        if (!string.IsNullOrEmpty(provider.ApiKeyEnv))
            return Environment.GetEnvironmentVariable(provider.ApiKeyEnv);
        return null;
    }
}
