using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;

namespace DeepSeekHarness.App.ViewModels;

/// <summary>消息显示角色。</summary>
public enum MessageKind
{
    User,
    Assistant,
    System,
}

/// <summary>消息流中的一条 UI 项目(用户/助手/系统消息 + 工具调用 + 思考行)。</summary>
public partial class ChatItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string _id;

    [ObservableProperty]
    private MessageKind _kind;

    /// <summary>显示文本(助手消息流式更新)。</summary>
    [ObservableProperty]
    private string _text = "";

    /// <summary>思考过程全文(展开显示)。</summary>
    [ObservableProperty]
    private string _reasoning = "";

    /// <summary>思考是否展开。</summary>
    [ObservableProperty]
    private bool _reasoningExpanded;

    /// <summary>是否正在流式输出。</summary>
    [ObservableProperty]
    private bool _streaming;

    /// <summary>时间戳。</summary>
    public DateTimeOffset Time { get; init; } = DateTimeOffset.Now;

    /// <summary>子工具调用(递归)。</summary>
    public ObservableCollection<ToolCallItemViewModel> ToolCalls { get; } = new();

    /// <summary>是否包含思考过程。</summary>
    public bool HasReasoning => !string.IsNullOrEmpty(Reasoning);

    public bool HasToolCalls => ToolCalls.Count > 0;

    public string TimeLabel => Time.ToString("HH:mm:ss");

    public ChatItemViewModel(MessageKind kind, string id)
    {
        _kind = kind;
        _id = id;
    }

    partial void OnReasoningChanged(string value) => OnPropertyChanged(nameof(HasReasoning));
    partial void OnReasoningExpandedChanged(bool value) => OnPropertyChanged(nameof(HasReasoning));
    partial void OnTextChanged(string value) => OnPropertyChanged(nameof(IsEmpty));

    public bool IsEmpty => string.IsNullOrEmpty(Text) && !HasToolCalls && !HasReasoning;
}

/// <summary>工具调用卡片(对齐参考项目 ToolRow)。</summary>
public partial class ToolCallItemViewModel : ObservableObject
{
    [ObservableProperty]
    private string _callId;

    [ObservableProperty]
    private string _name;

    /// <summary>运行中 | 完成 | 失败 | 已阻止。</summary>
    [ObservableProperty]
    private string _status = "pending";

    [ObservableProperty]
    private string _argumentsPreview = "";

    /// <summary>工具输出(IN/OUT 的 OUT)。</summary>
    [ObservableProperty]
    private string _output = "";

    [ObservableProperty]
    private string _error = "";

    [ObservableProperty]
    private bool _expanded;

    [ObservableProperty]
    private double _durationMs;

    public bool IsRunning => Status == "running";
    public bool IsDone => Status == "done";
    public bool IsError => Status == "error" || !string.IsNullOrEmpty(Error);
    public bool IsBlocked => Status == "blocked";

    public string DurationLabel => DurationMs > 0 ? $"{DurationMs:0}ms" : "";

    public ToolCallItemViewModel(string callId, string name, string argumentsJson)
    {
        _callId = callId;
        _name = name;
        _argumentsPreview = PreviewArgs(argumentsJson);
    }

    private static string PreviewArgs(string json)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json) || json == "{}") return "(无参数)";
            var doc = System.Text.Json.JsonDocument.Parse(json);
            var parts = new List<string>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                var v = prop.Value.ValueKind == System.Text.Json.JsonValueKind.String
                    ? prop.Value.GetString()
                    : prop.Value.GetRawText();
                var s = v ?? "";
                parts.Add($"{prop.Name}: {s}");
            }
            return string.Join("\n", parts.Take(6));
        }
        catch
        {
            return json;
        }
    }

    partial void OnStatusChanged(string value)
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsDone));
        OnPropertyChanged(nameof(IsError));
        OnPropertyChanged(nameof(IsBlocked));
    }

    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(IsError));
    partial void OnDurationMsChanged(double value) => OnPropertyChanged(nameof(DurationLabel));
}
