namespace DeepSeekHarness.Core.LLM;

using DeepSeekHarness.Core.Session;

/// <summary>LLM 消息(OpenAI 兼容格式)。</summary>
public sealed class LlmMessage
{
    public string Role { get; set; } = "user"; // system | user | assistant | tool
    public string? Content { get; set; }
    public string? ToolCallId { get; set; }
    public List<LlmToolCall>? ToolCalls { get; set; }
    public string? Name { get; set; }

    public static LlmMessage Of(string role, string content) => new() { Role = role, Content = content };
    public static LlmMessage OfTool(string callId, string content) => new() { Role = "tool", ToolCallId = callId, Content = content };
}

/// <summary>工具调用(OpenAI 格式)。</summary>
public sealed class LlmToolCall
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "function";
    public string Name { get; set; } = "";
    public string Arguments { get; set; } = "{}";
}

/// <summary>工具定义(供 LLM function calling)。</summary>
public sealed class LlmToolDefinition
{
    public string Type { get; set; } = "function";
    public LlmFunctionDefinition Function { get; set; } = new();
}

public sealed class LlmFunctionDefinition
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public object? Parameters { get; set; } // JSON Schema
    public bool? Strict { get; set; }
}

/// <summary>推理请求参数。</summary>
public sealed class GenerateOptions
{
    public string Provider { get; set; } = "deepseek-official";
    public string Model { get; set; } = "deepseek-v4-flash";
    public List<LlmMessage> Messages { get; set; } = new();
    public List<LlmToolDefinition>? Tools { get; set; }
    public double? Temperature { get; set; }
    public int? MaxTokens { get; set; }
    /// <summary>deepseek reasoning effort: low | medium | high</summary>
    public string? ReasoningEffort { get; set; }
    public bool Thinking { get; set; } = true;
    public CancellationToken CancellationToken { get; set; }
}

/// <summary>流式块。</summary>
public sealed class StreamChunk
{
    public string? Delta { get; set; }
    public string? ReasoningDelta { get; set; }
    public List<LlmToolCall>? ToolCalls { get; set; }
    public string? FinishReason { get; set; }
    public UsageInfo? Usage { get; set; }
    public string? Error { get; set; }
    public bool IsDone { get; set; }
}

/// <summary>LLM 适配器接口(对齐参考项目 ctx.llm adapter: prepareCall/stream)。</summary>
public interface ILlmAdapter
{
    string ProviderName { get; }
    bool CanHandle(string provider);
    /// <summary>流式生成,逐块回调。</summary>
    IAsyncEnumerable<StreamChunk> StreamAsync(GenerateOptions options, CancellationToken ct = default);
}

/// <summary>LLM 调用失败(对齐参考项目 LlmFailure 结构化错误)。</summary>
public sealed class LlmException : Exception
{
    public string Provider { get; }
    public string Model { get; }
    public string ErrorCode { get; }

    public LlmException(string provider, string model, string errorCode, string message)
        : base(message)
    {
        Provider = provider;
        Model = model;
        ErrorCode = errorCode;
    }
}
