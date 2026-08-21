using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeepSeekHarness.Core;
using DeepSeekHarness.Core.Config;
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

        // Plugins: 内置插件 = 工具集
        Plugins.Clear();
        foreach (var def in _engine.Tools.Definitions)
        {
            Plugins.Add(new PluginItemViewModel
            {
                Name = def.Name,
                Description = def.Description.Split('\n')[0],
                Enabled = true,
                RequiresApproval = def.RequiresApproval,
            });
        }

        Workspace = _engine.Settings.Workspace;
    }

    partial void OnSelectedProviderChanged(ProviderItemViewModel? value)
    {
        if (value != null)
            ApiKey = value.Config.ApiKey ?? "";
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

        s.Save();
        SettingsApplied?.Invoke();
    }
}

public partial class ProviderItemViewModel : ObservableObject
{
    public AppSettings.ProviderConfig Config { get; }

    public ObservableCollection<AppSettings.ModelConfig> Models { get; }

    [ObservableProperty]
    private AppSettings.ModelConfig? _selectedModel;

    public string Name => Config.Name;
    public string Id => Config.Id;

    public ProviderItemViewModel(AppSettings.ProviderConfig config)
    {
        Config = config;
        Models = new ObservableCollection<AppSettings.ModelConfig>(config.Models);
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
}
