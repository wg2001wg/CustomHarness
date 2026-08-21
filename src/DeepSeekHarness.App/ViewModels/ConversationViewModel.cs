using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DeepSeekHarness.Core;
using DeepSeekHarness.Core.Agent;
using DeepSeekHarness.Core.Config;
using DeepSeekHarness.Core.Session;

namespace DeepSeekHarness.App.ViewModels;

/// <summary>会话视图模型:消息流 + 输入区 + 模型选择(对齐参考项目 ui-conversation)。</summary>
public partial class ConversationViewModel : ObservableObject
{
    private readonly HarnessEngine _engine;
    private readonly Dispatcher _dispatcher;
    private ChatItemViewModel? _streamingItem;
    private ToolCallItemViewModel? _pendingToolCall;

    public ObservableCollection<ChatItemViewModel> Items { get; } = new();

    [ObservableProperty]
    private string _input = "";

    [ObservableProperty]
    private bool _isRunning;

    [ObservableProperty]
    private string _turnStatus = "就绪";

    [ObservableProperty]
    private string _sessionTitle = "新会话";

    [ObservableProperty]
    private string _selectedProvider;

    [ObservableProperty]
    private string _selectedModel;

    [ObservableProperty]
    private string _selectedEffort = "medium";

    [ObservableProperty]
    private bool _canSend = true;

    public event Action? ScrollToBottomRequested;

    public ConversationViewModel(HarnessEngine engine)
    {
        _engine = engine;
        // 用构造线程本地的 Dispatcher(真实应用在主线程构造,与 Application.Current.Dispatcher 等价;
        // 测试/宿主场景下更健壮,不会排到无消息循环的线程)
        _dispatcher = Dispatcher.CurrentDispatcher;
        _selectedProvider = engine.Settings.ProviderId;
        _selectedModel = engine.Settings.ModelId;

        SubscribeAgent();
    }

    public void LoadSession(Session session)
    {
        // 从会话日志投影已有 surface 消息
        Items.Clear();
        foreach (var evt in session.SurfaceEvents())
        {
            switch (evt.Type)
            {
                case SessionEventType.UserMessage when evt.Data is Message m:
                    Items.Add(new ChatItemViewModel(MessageKind.User, m.Id) { Text = m.Text ?? "" });
                    break;
                case SessionEventType.AssistantMessage when evt.Data is Message am:
                {
                    var item = new ChatItemViewModel(MessageKind.Assistant, am.Id);
                    var text = am.Text ?? "";
                    var reasoning = string.Concat(am.Blocks
                        .Where(b => b.Type == ContentBlockType.Reasoning)
                        .Select(b => b.Text));
                    item.Text = text;
                    item.Reasoning = reasoning ?? "";
                    foreach (var tc in am.ToolCalls)
                        item.ToolCalls.Add(new ToolCallItemViewModel(tc.CallId, tc.Name, tc.ArgumentsJson));
                    Items.Add(item);
                    break;
                }
            }
        }
        SessionTitle = session.Header.Title ?? "新会话";
        NotifyScroll();
    }

    public void SubscribeAgent()
    {
        var agent = _engine.Agent;
        if (agent == null) return;

        agent.StateChanged += s => _dispatcher.BeginInvoke(() =>
        {
            IsRunning = s == AgentLoopState.Running;
            CanSend = s != AgentLoopState.Running;
            TurnStatus = s switch
            {
                AgentLoopState.Running => "Deep diving...",
                AgentLoopState.Interrupted => "已中断",
                AgentLoopState.Error => "运行出错",
                _ => "就绪",
            };
        });

        agent.StreamDelta += d => _dispatcher.BeginInvoke(() =>
        {
            EnsureStreamingItem();
            if (_streamingItem != null)
                _streamingItem.Text += d;
            NotifyScroll();
        });

        agent.ReasoningDelta += d => _dispatcher.BeginInvoke(() =>
        {
            EnsureStreamingItem();
            if (_streamingItem != null)
                _streamingItem.Reasoning += d;
        });

        agent.AssistantMessageCompleted += msg => _dispatcher.BeginInvoke(() =>
        {
            FinalizeStreamingItem(msg);
        });

        agent.ToolEvent += (callId, name, phase, payload) => _dispatcher.BeginInvoke(() =>
        {
            HandleToolEvent(callId, name, phase, payload);
        });

        agent.TurnEnded += reason => _dispatcher.BeginInvoke(() =>
        {
            _streamingItem = null;
            _pendingToolCall = null;
            IsRunning = false;
            CanSend = true;
            TurnStatus = reason switch
            {
                TurnEndReason.Completed => "完成",
                TurnEndReason.MaxTokens => "达到最大步数",
                TurnEndReason.UserInterrupt => "已中断",
                TurnEndReason.Error => "出错",
                _ => "就绪",
            };
            _engine.SaveCurrentSession();
            SessionTitle = _engine.CurrentSession?.Header.Title ?? SessionTitle;
        });
    }

    private void EnsureStreamingItem()
    {
        if (_streamingItem != null) return;
        var item = new ChatItemViewModel(MessageKind.Assistant, Guid.NewGuid().ToString("N"))
        {
            Streaming = true,
        };
        _streamingItem = item;
        Items.Add(item);
        NotifyScroll();
    }

    private void FinalizeStreamingItem(Message msg)
    {
        if (_streamingItem != null)
        {
            _streamingItem.Streaming = false;
            _streamingItem.Text = msg.Text ?? _streamingItem.Text;
            _streamingItem.Reasoning = string.Concat(msg.Blocks
                .Where(b => b.Type == ContentBlockType.Reasoning)
                .Select(b => b.Text));
            foreach (var tc in msg.ToolCalls)
                _streamingItem.ToolCalls.Add(new ToolCallItemViewModel(tc.CallId, tc.Name, tc.ArgumentsJson));
            _streamingItem = null;
        }
        else
        {
            var item = new ChatItemViewModel(MessageKind.Assistant, msg.Id)
            {
                Text = msg.Text ?? "",
            };
            foreach (var tc in msg.ToolCalls)
                item.ToolCalls.Add(new ToolCallItemViewModel(tc.CallId, tc.Name, tc.ArgumentsJson));
            Items.Add(item);
        }
        NotifyScroll();
    }

    private void HandleToolEvent(string callId, string name, string phase, object? payload)
    {
        // 找到包含该 callId 的消息(通常是最后一个 assistant 消息)
        ChatItemViewModel? owner = null;
        foreach (var item in Items)
        {
            if (item.ToolCalls.Any(tc => tc.CallId == callId))
            {
                owner = item;
                break;
            }
        }

        if (owner == null)
        {
            // 兜底:把工具调用挂到流式消息上
            EnsureStreamingItem();
            owner = _streamingItem;
            if (owner == null) return;
            var pending = new ToolCallItemViewModel(callId, name, payload as string ?? "{}");
            owner.ToolCalls.Add(pending);
            _pendingToolCall = pending;
        }

        var card = owner.ToolCalls.FirstOrDefault(tc => tc.CallId == callId);
        if (card == null)
        {
            card = new ToolCallItemViewModel(callId, name, payload as string ?? "{}");
            owner.ToolCalls.Add(card);
            _pendingToolCall = card;
        }

        switch (phase)
        {
            case "start":
                card.Status = "running";
                card.ArgumentsPreview = FormatArgs(payload as string);
                break;
            case "execute":
                card.Status = "running";
                break;
            case "done" when payload is ToolResultData data:
                card.Status = "done";
                card.Output = data.Output ?? "";
                card.DurationMs = data.DurationMs;
                break;
            case "error":
                card.Status = "error";
                card.Error = payload?.ToString() ?? "未知错误";
                break;
            case "blocked":
                card.Status = "blocked";
                card.Error = "用户拒绝了该操作";
                break;
            case "aborted":
                card.Status = "error";
                card.Error = "执行被取消";
                break;
        }
        NotifyScroll();
    }

    private static string FormatArgs(string? json)
    {
        if (string.IsNullOrEmpty(json)) return "";
        try
        {
            return System.Text.Json.JsonSerializer.Serialize(
                System.Text.Json.JsonDocument.Parse(json).RootElement,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        }
        catch { return json; }
    }

    private void NotifyScroll() => ScrollToBottomRequested?.Invoke();

    [RelayCommand]
    private async Task SendAsync()
    {
        var text = Input.Trim();
        if (string.IsNullOrEmpty(text) || IsRunning) return;
        Input = "";
        CanSend = false;

        // 防御:Agent 未初始化时先尝试懒初始化(兜底早期初始化失败/竞态)
        if (_engine.Agent == null)
        {
            try
            {
                _engine.EnsureAgent();
            }
            catch (Exception ex)
            {
                AddSystemMessage($"❌ Agent 初始化失败: {ex.Message}");
                CanSend = true;
                return;
            }
        }

        if (_engine.Agent == null)
        {
            AddSystemMessage("⚠️ Agent 无法初始化。请检查 ⚙ 设置 中的模型、API Key 与工作区配置,以及数据目录完整性。");
            CanSend = true;
            return;
        }

        try
        {
            // 更新模型选择
            _engine.Settings.ProviderId = SelectedProvider;
            _engine.Settings.ModelId = SelectedModel;
            _engine.Settings.ReasoningEffort = SelectedEffort;
            _engine.Settings.Save();

            // 在消息流中立即展示用户消息
            Items.Add(new ChatItemViewModel(MessageKind.User, Guid.NewGuid().ToString("N")) { Text = text });
            NotifyScroll();

            await _engine.Agent.SendAsync(text);
        }
        catch (Exception ex)
        {
            // 任何运行异常以系统消息呈现,保持 UI 可用
            AddSystemMessage($"❌ 发送失败: {ex.Message}");
        }
        finally
        {
            CanSend = true;
        }
    }

    [RelayCommand]
    private void Interrupt() => _engine.Agent?.Interrupt();

    /// <summary>追加一条系统提示消息(用于错误/状态反馈)。</summary>
    public void AddSystemMessage(string text)
    {
        Items.Add(new ChatItemViewModel(MessageKind.System, Guid.NewGuid().ToString("N")) { Text = text });
        NotifyScroll();
    }
}
