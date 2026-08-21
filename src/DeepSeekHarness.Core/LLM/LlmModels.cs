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

    /// <summary>
    /// 将底层错误码映射为面向用户的友好文案与建议。
    /// 用于把 "HTTP_401"、"Insufficient Balance" 等原始信息转成可读、可行动的提示。
    /// </summary>
    public static string FriendlyMessage(string errorCode, string? rawMessage)
    {
        // 1) 依 errorCode 精确匹配(最高优先级)
        switch (errorCode)
        {
            case "AUTH_MISSING_API_KEY":
                return "未配置 API Key。请打开 ⚙ 设置,填写有效的 DeepSeek API Key 后再试。";
            case "AUTH_INVALID_API_KEY":
                return "API Key 无效或已过期。请在 ⚙ 设置中检查并更新 API Key。";
            case "INSUFFICIENT_BALANCE":
                return "账户余额不足,无法发起请求。请前往 DeepSeek 平台充值后再试。";
            case "RATE_LIMITED":
                return "请求过于频繁,已被限流。请稍等片刻后重试。";
            case "MODEL_NOT_FOUND":
                return "指定的模型不存在或无权访问。请检查 ⚙ 设置中的模型名称是否正确。";
            case "CONTEXT_LENGTH_EXCEEDED":
                return "对话上下文超出模型的长度限制。建议新建一个会话,或精简当前对话内容后再试。";
            case "STREAM_ERROR":
                return $"模型流式输出中断:{rawMessage ?? "未知错误"}";
        }

        // 2) 依 HTTP 状态码匹配
        if (errorCode.StartsWith("HTTP_", StringComparison.Ordinal))
        {
            var code = errorCode["HTTP_".Length..];
            switch (code)
            {
                case "400":
                    return "请求格式有误(400 Bad Request)。请检查输入内容或模型参数后重试。";
                case "401":
                    return "认证失败(401),API Key 无效或已过期。请在 ⚙ 设置中检查并更新 API Key。";
                case "402":
                    return "账户余额不足(402),无法继续使用。请先充值后再试。";
                case "403":
                    return "访问被拒绝(403),当前 Key 无权使用该模型或接口。请在设置中检查授权。";
                case "404":
                    return "接口或模型不存在(404)。请检查模型名称与接口地址配置。";
                case "408":
                    return "请求超时(408),模型响应太慢。请稍后重试。";
                case "429":
                    return "请求过于频繁(429),已被限流。请稍等片刻后重试。";
                case "413":
                    return "请求内容过大(413),超出模型输入上限。请精简内容或新建会话。";
                case "500":
                    return "模型服务内部错误(500)。请稍后重试;如持续失败可联系平台支持。";
                case "502":
                case "503":
                    return $"模型服务暂时不可用({code})。服务器繁忙或维护中,请稍后重试。";
                case "504":
                    return "网关超时(504),模型响应超时。请稍后重试。";
            }
        }

        // 3) 依错误消息内容兜底匹配
        var lower = (rawMessage ?? "").ToLowerInvariant();
        if (lower.Contains("insufficient") && lower.Contains("balance"))
            return "账户余额不足,无法发起请求。请前往 DeepSeek 平台充值后再试。";
        if (lower.Contains("rate limit") || lower.Contains("too many requests"))
            return "请求过于频繁,已被限流。请稍等片刻后重试。";
        if (lower.Contains("invalid api key") || lower.Contains("authentication"))
            return "API Key 无效或已过期。请在 ⚙ 设置中检查并更新 API Key。";
        if (lower.Contains("context length") || lower.Contains("maximum context"))
            return "对话上下文超出模型的长度限制。建议新建一个会话,或精简当前对话内容后再试。";
        if (lower.Contains("model not exist") || (lower.Contains("not found") && lower.Contains("model")))
            return "指定的模型不存在或无权访问。请检查 ⚙ 设置中的模型名称是否正确。";

        // 4) 默认:给出截断的原始信息
        var detail = Truncate(rawMessage ?? errorCode, 300);
        return string.IsNullOrWhiteSpace(detail) ? "请求失败,请稍后重试。" : $"请求失败:{detail}";
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "...";
}
