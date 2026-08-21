using System.Text.Json.Serialization;

namespace DeepSeekHarness.Core.Session;

/// <summary>会话格式版本(对齐参考项目 SESSION_FORMAT_VERSION = 0)。</summary>
public static class SessionFormat
{
    public const int Version = 0;
    public const string JsonlExtension = ".jsonl";
}

/// <summary>会话头(对齐参考项目 SessionHeader)。</summary>
public sealed class SessionHeader
{
    public int Version { get; set; } = SessionFormat.Version;
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
    public string Cwd { get; set; } = Environment.CurrentDirectory;
    public string? ParentSession { get; set; }
    public int? SeedLength { get; set; }
    public string? Origin { get; set; }
    public int DelegationDepth { get; set; }
    public string? AgentPreset { get; set; }
    /// <summary>会话标题(由 session-title 插件生成)。</summary>
    public string? Title { get; set; }
    /// <summary>会话所属工作区名。</summary>
    public string? Workspace { get; set; }
}

/// <summary>会话事件类型(对齐参考项目 SessionEventMap)。</summary>
public enum SessionEventType
{
    TurnStart,
    TurnEnd,
    StepStart,
    StepEnd,
    UserMessage,
    AssistantChunk,
    AssistantMessage,
    ToolCall,
    ToolResult,
    RequestHeader,
    RequestContext,
    TodoWrite,
    SessionEndSeed,
    Error,
}

/// <summary>
/// 追加式会话事件(对齐参考项目 SessionEvent)。
/// 不可变追加日志,持久化与投影均由日志派生。
/// </summary>
public sealed class SessionEvent
{
    public SessionEventType Type { get; init; }
    /// <summary>单调递增序号。</summary>
    public long Seq { get; init; }
    public DateTimeOffset Time { get; init; } = DateTimeOffset.Now;
    public object? Data { get; init; }
    public bool Ignorable { get; init; }

    [JsonIgnore]
    public bool IsSurface =>
        Type is SessionEventType.UserMessage or SessionEventType.AssistantMessage or SessionEventType.ToolResult;

    public override string ToString() => $"[{Seq}] {Type} @ {Time:HH:mm:ss.fff}";
}

/// <summary>内容块类型(对齐参考项目 ContentBlock,merge 可扩展)。</summary>
public enum ContentBlockType
{
    Text,
    Reasoning,
    Image,
    ToolCall,
    ToolResult,
}

/// <summary>消息内容块。</summary>
public sealed class ContentBlock
{
    public ContentBlockType Type { get; init; }
    public string? Text { get; init; }
    public string? ImageData { get; init; }
    public string? MimeType { get; init; }
    public ToolCallData? ToolCall { get; init; }
    public ToolResultData? ToolResult { get; init; }
}

/// <summary>工具调用数据。</summary>
public sealed class ToolCallData
{
    public string CallId { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string Name { get; set; } = "";
    public string ArgumentsJson { get; set; } = "{}";
    public string? Raw { get; set; }
}

/// <summary>工具结果数据。</summary>
public sealed class ToolResultData
{
    public string CallId { get; set; } = "";
    public string? Name { get; set; }
    public bool IsError { get; set; }
    public string? Output { get; set; }
    public string? ErrorMessage { get; set; }
    public string? MetaJson { get; set; }
    public double DurationMs { get; set; }
}

/// <summary>消息角色(对齐参考项目 Message.role)。</summary>
public enum MessageRole
{
    System,
    User,
    Assistant,
    Tool,
}

/// <summary>消息来源。</summary>
public enum MessageSource
{
    User,
    Plugin,
    Model,
    Tool,
}

/// <summary>
/// 消息(对齐参考项目 Message:id + role + ContentBlock[] + source)。
/// </summary>
public sealed class Message
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public MessageRole Role { get; init; }
    public MessageSource Source { get; init; } = MessageSource.Model;
    public List<ContentBlock> Blocks { get; init; } = new();

    public string? Text
    {
        get
        {
            var t = string.Concat(Blocks.Where(b => b.Type == ContentBlockType.Text).Select(b => b.Text));
            return string.IsNullOrEmpty(t) ? null : t;
        }
    }

    public IEnumerable<ToolCallData> ToolCalls => Blocks
        .Where(b => b.Type == ContentBlockType.ToolCall && b.ToolCall != null)
        .Select(b => b.ToolCall!);

    public static Message OfText(MessageRole role, string text, MessageSource source = MessageSource.User)
        => new()
        {
            Role = role,
            Source = source,
            Blocks = { new ContentBlock { Type = ContentBlockType.Text, Text = text } },
        };
}

/// <summary>用量统计。</summary>
public sealed class UsageInfo
{
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }
    public int? TotalTokens { get; set; }
    public int? ReasoningTokens { get; set; }
}

/// <summary>turn 结束原因(对齐参考项目 turn/end reason)。</summary>
public enum TurnEndReason
{
    Completed,
    MaxTokens,
    Error,
    UserInterrupt,
    Stopped,
}
