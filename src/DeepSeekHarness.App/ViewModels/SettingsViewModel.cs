using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeepSeekHarness.Core;
using DeepSeekHarness.Core.Config;
using DeepSeekHarness.Core.Plugins;
using DeepSeekHarness.Core.Tools;

namespace DeepSeekHarness.App.ViewModels;

/// <summary>设置对话框 ViewModel(对齐参考项目 ui-settings: Models/Presets/Plugins/General)。</summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly HarnessEngine _engine;
    private readonly Dispatcher _dispatcher;

    [ObservableProperty]
    private string _selectedTab = "Models";

    private static readonly string[] ValidTabs = { "Models", "Presets", "Plugins", "General" };

    /// <summary>防御:SelectedTab 无效时回退 Models,避免内容区永久空白。</summary>
    partial void OnSelectedTabChanged(string value)
    {
        if (value == null || !ValidTabs.Contains(value))
            _selectedTab = "Models";
    }

    // ---- Models ----
    public ObservableCollection<ProviderItemViewModel> Providers { get; } = new();

    [ObservableProperty]
    private ProviderItemViewModel? _selectedProvider;

    [ObservableProperty]
    private string _apiKey = "";

    public string ApiKeyPlaceholder => "已配置(环境变量)";

    // ---- 自定义提供商输入 ----
    [ObservableProperty]
    private string _newProviderName = "";

    [ObservableProperty]
    private string _newProviderId = "";

    [ObservableProperty]
    private string _newProviderBaseUrl = "";

    [ObservableProperty]
    private string _newProviderApiKey = "";

    // ---- Presets ----
    public ObservableCollection<PresetItemViewModel> Presets { get; } = new();

    [ObservableProperty]
    private PresetItemViewModel? _selectedPreset;

    // ---- Plugins ----
    public ObservableCollection<PluginItemViewModel> Plugins { get; } = new();

    // ---- General ----
    [ObservableProperty]
    private string _workspace = "";

    [ObservableProperty]
    private string _selectedPermission;

    public string DataDirLabel => HarnessEngine.GetDshHome();

    public string[] Permissions { get; } = { "只读 (read-only)", "工作区写入 (workspace-write)", "完全访问 (danger-full-access)" };

    public event Action? SettingsApplied;

    public SettingsViewModel(HarnessEngine engine)
    {
        _engine = engine;
        _dispatcher = Application.Current.Dispatcher;
        _selectedPermission = PermissionLabel(engine.Settings.Permission);
        Load();
    }

    private static string PermissionLabel(PermissionLevel level) => level switch
    {
        PermissionLevel.ReadOnly => "只读 (read-only)",
        PermissionLevel.DangerFullAccess => "完全访问 (danger-full-access)",
        _ => "工作区写入 (workspace-write)",
    };

    public void Load()
    {
        // Models
        Providers.Clear();
        foreach (var p in _engine.Settings.Providers)
        {
            var vm = new ProviderItemViewModel(p);
            Providers.Add(vm);
            if (p.Id == _engine.Settings.ProviderId) SelectedProvider = vm;
        }
        ApiKey = _engine.Settings.Providers
            .FirstOrDefault(p => p.Id == _engine.Settings.ProviderId)?.ApiKey ?? "";

        // Presets
        Presets.Clear();
        foreach (var name in _engine.PresetLoader.ListPresetNames())
        {
            var preset = _engine.PresetLoader.LoadPreset(name);
            var vm = new PresetItemViewModel
            {
                Name = name,
                DisplayName = preset?.DisplayName ?? name,
                Description = preset?.Description ?? "",
                Order = preset?.Order ?? 99,
            };
            Presets.Add(vm);
            if (name == _engine.Settings.AgentPreset) SelectedPreset = vm;
        }

        // Plugins: 当前预设的插件组合行(同步自上游 reference)+ 实现状态
        Plugins.Clear();
        var pluginPreset = _engine.PresetLoader.LoadPreset(_engine.Settings.AgentPreset)
                           ?? _engine.PresetLoader.LoadPreset("standard");
        if (pluginPreset != null && pluginPreset.Rows.Count > 0)
        {
            foreach (var row in pluginPreset.Rows)
            {
                if (row.Id == null) continue;
                var cap = _engine.PluginMapper.ResolveCapability(row.Id);
                Plugins.Add(new PluginItemViewModel
                {
                    Name = row.Id,
                    Description = _engine.PluginMapper.GetDescription(row.Id)
                                  ?? row.Name ?? "上游组合行(同步自 reference)",
                    Enabled = cap == PluginCapability.Implemented,
                    RequiresApproval = false,
                    Capability = cap,
                });
            }
        }
        else
        {
            // 兜底:直接列工具
            foreach (var def in _engine.Tools.Definitions)
            {
                Plugins.Add(new PluginItemViewModel
                {
                    Name = def.Name,
                    Description = def.Description.Split('\n')[0],
                    Enabled = true,
                    RequiresApproval = def.RequiresApproval,
                    Capability = PluginCapability.Implemented,
                });
            }
        }

        Workspace = _engine.Settings.Workspace;
    }

    partial void OnSelectedProviderChanged(ProviderItemViewModel? value)
    {
        if (value != null)
            ApiKey = value.Config.ApiKey ?? "";
    }

    // ---- 自定义提供商:添加 / 删除 ----

    /// <summary>添加自定义提供商(OpenAI 兼容端点)。</summary>
    [RelayCommand]
    private void AddProvider()
    {
        var id = NewProviderId.Trim();
        var name = NewProviderName.Trim();
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name)) return;
        if (_engine.Settings.Providers.Any(p => p.Id == id)) return; // 防重复

        var cfg = new AppSettings.ProviderConfig
        {
            Id = id,
            Name = name,
            BaseUrl = string.IsNullOrWhiteSpace(NewProviderBaseUrl)
                ? DeepSeekHarness.Core.LLM.DeepSeekAdapter.DefaultEndpoint
                : NewProviderBaseUrl.Trim(),
            ApiKey = string.IsNullOrWhiteSpace(NewProviderApiKey) ? null : NewProviderApiKey.Trim(),
            ApiKeyEnv = "",
        };
        // 自定义提供商默认给一个可用的模型占位,避免空列表无法发送
        cfg.Models.Add(new AppSettings.ModelConfig { Id = "default", Name = "默认模型", Default = true, Thinking = true });

        _engine.Settings.Providers.Add(cfg);
        Providers.Add(new ProviderItemViewModel(cfg));
        SelectedProvider = Providers[^1];
        NewProviderId = "";
        NewProviderName = "";
        NewProviderBaseUrl = "";
        NewProviderApiKey = "";
    }

    /// <summary>删除自定义提供商(至少保留一个)。</summary>
    [RelayCommand]
    private void RemoveProvider(ProviderItemViewModel? provider)
    {
        if (provider == null || _engine.Settings.Providers.Count <= 1) return;
        _engine.Settings.Providers.Remove(provider.Config);
        Providers.Remove(provider);
        if (SelectedProvider == provider)
            SelectedProvider = Providers.FirstOrDefault();
    }

    [RelayCommand]
    private void Apply()
    {
        var s = _engine.Settings;
        if (SelectedProvider != null)
        {
            s.ProviderId = SelectedProvider.Config.Id;
            var cfg = s.Providers.First(p => p.Id == SelectedProvider.Config.Id);
            cfg.ApiKey = string.IsNullOrWhiteSpace(ApiKey) ? null : ApiKey.Trim();
        }
        if (SelectedPreset != null)
            s.AgentPreset = SelectedPreset.Name;

        if (!string.IsNullOrWhiteSpace(Workspace) && Directory.Exists(Workspace))
            s.Workspace = Workspace;

        s.Permission = SelectedPermission switch
        {
            "只读 (read-only)" => PermissionLevel.ReadOnly,
            "完全访问 (danger-full-access)" => PermissionLevel.DangerFullAccess,
            _ => PermissionLevel.WorkspaceWrite,
        };

        // 模型选择校验:当前 ModelId 若已被删除,回退到该 provider 第一个模型
        var cur = s.Providers.FirstOrDefault(p => p.Id == s.ProviderId);
        if (cur != null && cur.Models.Count > 0 && !cur.Models.Any(m => m.Id == s.ModelId))
            s.ModelId = cur.Models[0].Id;

        s.Save();
        SettingsApplied?.Invoke();
    }
}

public partial class ProviderItemViewModel : ObservableObject
{
    public AppSettings.ProviderConfig Config { get; }

    /// <summary>模型列表(与 Config.Models 同步:增删立即写回设置)。</summary>
    public ObservableCollection<AppSettings.ModelConfig> Models { get; }

    [ObservableProperty]
    private AppSettings.ModelConfig? _selectedModel;

    // ---- 新增模型输入 ----
    [ObservableProperty]
    private string _newModelId = "";

    [ObservableProperty]
    private string _newModelName = "";

    [ObservableProperty]
    private bool _newModelThinking = true;

    public string Name => Config.Name;
    public string Id => Config.Id;
    public string BaseUrl => Config.BaseUrl;
    public bool HasApiKey => !string.IsNullOrEmpty(Config.ApiKey);

    public ProviderItemViewModel(AppSettings.ProviderConfig config)
    {
        Config = config;
        Models = new ObservableCollection<AppSettings.ModelConfig>(config.Models);
        SelectedModel = config.Models.FirstOrDefault(m => m.Default) ?? config.Models.FirstOrDefault();
    }

    /// <summary>添加自定义模型到当前提供商。</summary>
    [RelayCommand]
    private void AddModel()
    {
        var id = NewModelId.Trim();
        if (string.IsNullOrEmpty(id)) return;
        if (Config.Models.Any(m => m.Id == id)) return; // 防重复

        var model = new AppSettings.ModelConfig
        {
            Id = id,
            Name = string.IsNullOrWhiteSpace(NewModelName) ? id : NewModelName.Trim(),
            Thinking = NewModelThinking,
        };
        Config.Models.Add(model);
        Models.Add(model);
        SelectedModel = model;
        NewModelId = "";
        NewModelName = "";
    }

    /// <summary>从当前提供商删除模型。</summary>
    [RelayCommand]
    private void RemoveModel(AppSettings.ModelConfig? model)
    {
        if (model == null) return;
        Config.Models.Remove(model);
        Models.Remove(model);
        if (SelectedModel == model)
            SelectedModel = Models.FirstOrDefault();
    }
}

public partial class PresetItemViewModel : ObservableObject
{
    public required string Name { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }
    public required int Order { get; init; }
}

public partial class PluginItemViewModel : ObservableObject
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public bool Enabled { get; init; }
    public bool RequiresApproval { get; init; }

    /// <summary>实现状态(已实现 / 仅数据 / 待实现)。</summary>
    public PluginCapability Capability { get; init; } = PluginCapability.Pending;

    public string CapabilityLabel => Capability switch
    {
        PluginCapability.Implemented => "已实现",
        PluginCapability.DataOnly => "框架级",
        _ => "待实现",
    };

    public string CapabilityBadge => Capability switch
    {
        PluginCapability.Implemented => "#EAF3DE", // 绿
        PluginCapability.DataOnly => "#EDEFF4",    // 灰
        _ => "#FAEEDA",                            // 琥珀
    };

    public string CapabilityForeground => Capability switch
    {
        PluginCapability.Implemented => "#3B6D11",
        PluginCapability.DataOnly => "#6B7280",
        _ => "#854F0B",
    };
}
