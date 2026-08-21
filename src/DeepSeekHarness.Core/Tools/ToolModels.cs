using System.Text.Json;

namespace DeepSeekHarness.Core.Tools;

using DeepSeekHarness.Core.LLM;

/// <summary>权限级别(对齐参考项目 permission-presets: read-only / workspace-write / danger-full-access)。</summary>
public enum PermissionLevel
{
    /// <summary>只读:文件读取、搜索。</summary>
    ReadOnly,
    /// <summary>工作区写入:文件编辑、shell(限工作区)。</summary>
    WorkspaceWrite,
    /// <summary>完全访问:任意 shell、任意路径。</summary>
    DangerFullAccess,
}

/// <summary>工具执行上下文。</summary>
public sealed class ToolContext
{
    public required string WorkingDirectory { get; init; }
    public PermissionLevel Permission { get; init; } = PermissionLevel.WorkspaceWrite;
    public string? SessionId { get; init; }
    /// <summary>工具主动向用户提问时回调(ask_user_question)。</summary>
    public Func<string, string, Task<string>>? AskUser { get; init; }
    /// <summary>工具输出写入会话日志的回调。</summary>
    public Action<string>? Log { get; init; }
    /// <summary>工具产生结构化事件(如 fs/read)的回调。</summary>
    public Action<string, object?>? Emit { get; init; }
    public CancellationToken CancellationToken { get; init; }
}

/// <summary>工具结果。</summary>
public sealed class ToolResult
{
    public bool IsError { get; set; }
    public string? Output { get; set; }
    public string? Error { get; set; }
    public Dictionary<string, object?>? Meta { get; set; }

    public static ToolResult Ok(string output, Dictionary<string, object?>? meta = null)
        => new() { Output = output, Meta = meta };

    public static ToolResult Fail(string error)
        => new() { IsError = true, Error = error };

    public string RenderText()
        => IsError ? $"Error: {Error}" : Output ?? "";
}

/// <summary>工具定义(JSON Schema 参数)。</summary>
public sealed class ToolDefinition
{
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    /// <summary>参数 JSON Schema(object)。</summary>
    public Dictionary<string, object?> Parameters { get; init; } = new();
    /// <summary>需要审批(危险操作)。</summary>
    public bool RequiresApproval { get; init; }

    public LlmToolDefinition ToLlmDefinition() => new()
    {
        Function = new LlmFunctionDefinition
        {
            Name = Name,
            Description = Description,
            Parameters = Parameters.Count > 0 ? Parameters : null,
        },
    };
}

/// <summary>工具接口。</summary>
public interface ITool
{
    ToolDefinition Definition { get; }
    Task<ToolResult> ExecuteAsync(JsonElement arguments, ToolContext ctx);
}
