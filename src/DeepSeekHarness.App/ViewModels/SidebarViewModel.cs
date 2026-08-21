using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeepSeekHarness.Core;

namespace DeepSeekHarness.App.ViewModels;

/// <summary>侧边栏会话历史(对齐参考项目 ui-workspace)。</summary>
public partial class SidebarViewModel : ObservableObject
{
    private readonly HarnessEngine _engine;
    private readonly Dispatcher _dispatcher;

    public ObservableCollection<SessionListItemViewModel> Sessions { get; } = new();

    [ObservableProperty]
    private SessionListItemViewModel? _selectedSession;

    public event Action<SessionListItemViewModel?>? SessionSelected;
    public event Action? NewSessionRequested;

    public SidebarViewModel(HarnessEngine engine)
    {
        _engine = engine;
        _dispatcher = Application.Current.Dispatcher;
    }

    public void Refresh()
    {
        Sessions.Clear();
        foreach (var (id, title, created) in _engine.ListSessions().OrderByDescending(s => s.Created))
        {
            Sessions.Add(new SessionListItemViewModel
            {
                Id = id,
                Title = title ?? "未命名会话",
                Created = created ?? DateTimeOffset.Now,
            });
        }
        // 恢复上次会话选中
        var lastId = _engine.Settings.LastSessionId;
        if (lastId != null)
            SelectedSession = Sessions.FirstOrDefault(s => s.Id == lastId);
    }

    partial void OnSelectedSessionChanged(SessionListItemViewModel? value)
    {
        if (value != null)
            SessionSelected?.Invoke(value);
    }

    [RelayCommand]
    private void NewSession() => NewSessionRequested?.Invoke();

    [RelayCommand]
    private void DeleteSession(SessionListItemViewModel? item)
    {
        if (item == null) return;
        var path = System.IO.Path.Combine(HarnessEngine.GetDshHome(), "sessions", item.Id + ".jsonl");
        try
        {
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            Sessions.Remove(item);
        }
        catch { /* 忽略 */ }
    }
}

public partial class SessionListItemViewModel : ObservableObject
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public DateTimeOffset Created { get; init; }

    public string TimeLabel => Created.ToString("MM-dd HH:mm");
}
