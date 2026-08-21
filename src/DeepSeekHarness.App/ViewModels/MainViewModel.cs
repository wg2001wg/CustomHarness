using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeepSeekHarness.Core;
using DeepSeekHarness.Core.Session;

namespace DeepSeekHarness.App.ViewModels;

/// <summary>主窗口 ViewModel:三栏布局的编排中心。</summary>
public partial class MainViewModel : ObservableObject
{
    public HarnessEngine Engine { get; }
    public SidebarViewModel Sidebar { get; }
    public ConversationViewModel Conversation { get; private set; }
    public SettingsViewModel Settings { get; private set; }

    [ObservableProperty]
    private bool _isSidebarCollapsed;

    [ObservableProperty]
    private bool _isSettingsOpen;

    [ObservableProperty]
    private string _workspaceLabel = "";

    public event Action? SettingsOpenRequested;

    // ---- 模型选择 ----
    public System.Collections.ObjectModel.ObservableCollection<ModelItemViewModel> Models { get; } = new();

    [ObservableProperty]
    private ModelItemViewModel? _selectedModelItem;

    public string[] Efforts { get; } = { "low", "medium", "high" };

    public MainViewModel()
    {
        Engine = new HarnessEngine();
        Sidebar = new SidebarViewModel(Engine);
        Conversation = new ConversationViewModel(Engine);
        Settings = new SettingsViewModel(Engine);

        WireEvents();
        InitializeAsync();
    }

    private async void InitializeAsync()
    {
        await System.Threading.Tasks.Task.Yield();
        try
        {
            LoadModels();
            // 优先恢复上次会话
            var lastId = Engine.Settings.LastSessionId;
            var session = lastId != null ? Engine.LoadSession(lastId) : null;
            if (session == null || string.IsNullOrEmpty(Engine.Settings.Workspace) ||
                !Directory.Exists(Engine.Settings.Workspace))
            {
                session = Engine.NewSession();
            }

            Engine.InitAgent(session);
            Sidebar.Refresh();
            Conversation.LoadSession(session);
            Conversation.SubscribeAgent();
            WorkspaceLabel = session.Header.Workspace ?? Engine.Settings.Workspace;
        }
        catch (Exception ex)
        {
            // 兜底:即使某步失败也保证 Agent 可用,避免"未初始化"
            try
            {
                Engine.EnsureAgent();
                Conversation.SubscribeAgent();
            }
            catch { /* 忽略:发送时另有懒初始化兜底 */ }
            Conversation.AddSystemMessage($"⚠️ 初始化部分失败: {ex.Message}");
        }
    }

    private void LoadModels()
    {
        Models.Clear();
        foreach (var provider in Engine.Settings.Providers)
        {
            foreach (var model in provider.Models)
            {
                Models.Add(new ModelItemViewModel
                {
                    ProviderId = provider.Id,
                    ModelId = model.Id,
                    Name = $"{model.Name} · {provider.Name}",
                });
            }
        }
        SelectedModelItem = Models.FirstOrDefault(m =>
            m.ProviderId == Engine.Settings.ProviderId && m.ModelId == Engine.Settings.ModelId);
    }

    partial void OnSelectedModelItemChanged(ModelItemViewModel? value)
    {
        if (value == null) return;
        Engine.Settings.ProviderId = value.ProviderId;
        Engine.Settings.ModelId = value.ModelId;
        Engine.Settings.Save();
    }

    private void WireEvents()
    {
        Sidebar.SessionSelected += item =>
        {
            if (item == null) return;
            var session = Engine.LoadSession(item.Id);
            if (session == null) return;
            Engine.InitAgent(session);
            Conversation.LoadSession(session);
            Conversation.SubscribeAgent();
            WorkspaceLabel = session.Header.Workspace ?? "—";
        };

        Sidebar.NewSessionRequested += () =>
        {
            var session = Engine.NewSession();
            Engine.InitAgent(session);
            Conversation.LoadSession(session);
            Conversation.SubscribeAgent();
            Sidebar.Refresh();
            WorkspaceLabel = session.Header.Workspace ?? "—";
        };

        Settings.SettingsApplied += () =>
        {
            // 重新加载模型列表,同步顶栏选择(新增/删除的 provider/model 生效)
            LoadModels();
            // 重新初始化 Agent 以应用新配置;失败给出明确提示而非静默
            try
            {
                var session = Engine.CurrentSession ?? Engine.NewSession();
                Engine.InitAgent(session);
                Conversation.SubscribeAgent();
            }
            catch (Exception ex)
            {
                Conversation.AddSystemMessage($"❌ 应用设置后初始化 Agent 失败: {ex.Message}");
            }
        };
    }

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarCollapsed = !IsSidebarCollapsed;

    [RelayCommand]
    private void OpenSettings() => SettingsOpenRequested?.Invoke();

    [RelayCommand]
    private void CloseSettings() => IsSettingsOpen = false;
}

public partial class ModelItemViewModel : ObservableObject
{
    public required string ProviderId { get; init; }
    public required string ModelId { get; init; }
    public required string Name { get; init; }
}
