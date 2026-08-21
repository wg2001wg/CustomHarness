using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace DeepSeekHarness.Core.LLM;

using DeepSeekHarness.Core.Session;

/// <summary>
/// DeepSeek 官方适配器(OpenAI 兼容 API,SSE 流式)。
/// 支持 thinking(reasoning_content)与 function calling。
/// </summary>
public sealed class DeepSeekAdapter : ILlmAdapter
{
    public const string DefaultEndpoint = "https://api.deepseek.com/v1";
    public const string DefaultModel = "deepseek-v4-flash";

    private readonly HttpClient _http;
    private readonly Func<string?> _apiKeyResolver;
    private readonly Func<string?> _baseUrlResolver;

    public string ProviderName => "deepseek-official";

    public DeepSeekAdapter(HttpClient http, Func<string?> apiKeyResolver, Func<string?>? baseUrlResolver = null)
    {
        _http = http;
        _apiKeyResolver = apiKeyResolver;
        _baseUrlResolver = baseUrlResolver ?? (() => DefaultEndpoint);
    }

    public bool CanHandle(string provider)
        => provider is "deepseek-official" or "deepseek" or "dsh-deepseek" or "openai" or "anthropic"
            or "google" or "qwen" or "moonshot" or "zhipu" or "doubao" or "baidu" or "mistral"
            or "xai" or "groq" or "cohere" or "ollama" or "lmstudio" or "vllm" or "openrouter";

    public async IAsyncEnumerable<StreamChunk> StreamAsync(
        GenerateOptions options,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ct = options.CancellationToken == default ? ct : options.CancellationToken;
        var apiKey = _apiKeyResolver();
        if (string.IsNullOrEmpty(apiKey))
            throw new LlmException(options.Provider, options.Model, "AUTH_MISSING_API_KEY",
                "未配置 DeepSeek API Key。请在设置中填写(环境变量 DEEPSEEK_API_KEY 或应用设置)。");

        var payload = BuildPayload(options);
        var baseUrl = ResolveBaseUrl(options);
        var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var errBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            var (errCode, errMessage) = ParseError((int)response.StatusCode, errBody);
            var friendly = LlmException.FriendlyMessage(errCode, errMessage);
            throw new LlmException(options.Provider, options.Model, errCode, friendly);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        var callIndex = new Dictionary<int, LlmToolCall>();
        string? line;
        while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
        {
            if (ct.IsCancellationRequested) break;
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.StartsWith("data:", StringComparison.Ordinal))
            {
                var data = line[5..].Trim();
                if (data == "[DONE]")
                {
                    yield return new StreamChunk { IsDone = true };
                    yield break;
                }
                yield return ParseSseData(data, callIndex);
            }
            else if (line.StartsWith("event:", StringComparison.Ordinal))
            {
                // 忽略 event 行,直接消费 data
            }
        }
    }

    private static object BuildPayload(GenerateOptions options)
    {
        var msgs = new List<object>();
        foreach (var m in options.Messages)
        {
            var dict = new Dictionary<string, object?>
            {
                ["role"] = m.Role,
                ["content"] = m.Content ?? "",
            };
            if (m.ToolCallId != null) dict["tool_call_id"] = m.ToolCallId;
            if (m.ToolCalls != null)
            {
                // OpenAI 兼容格式: tool_calls 元素为 {id, type, function:{name, arguments}}
                dict["tool_calls"] = m.ToolCalls.Select(tc => (object)new Dictionary<string, object?>
                {
                    ["id"] = tc.Id,
                    ["type"] = "function",
                    ["function"] = new Dictionary<string, object?>
                    {
                        ["name"] = tc.Name,
                        ["arguments"] = tc.Arguments,
                    },
                }).ToList();
            }
            msgs.Add(dict);
        }

        var dict2 = new Dictionary<string, object?>
        {
            ["model"] = options.Model,
            ["messages"] = msgs,
            ["stream"] = true,
        };
        if (options.Tools is { Count: > 0 })
        {
            // OpenAI 兼容格式: tools 元素为 {type:"function", function:{name,description,parameters}}
            dict2["tools"] = options.Tools.Select(t => (object)new Dictionary<string, object?>
            {
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?>
                {
                    ["name"] = t.Function.Name,
                    ["description"] = t.Function.Description,
                    ["parameters"] = t.Function.Parameters,
                },
            }).ToList();
        }
        if (options.Temperature.HasValue)
            dict2["temperature"] = options.Temperature.Value;
        if (options.MaxTokens.HasValue)
            dict2["max_tokens"] = options.MaxTokens.Value;
        if (!string.IsNullOrEmpty(options.ReasoningEffort))
            dict2["reasoning_effort"] = options.ReasoningEffort;
        return dict2;
    }

    private static StreamChunk ParseSseData(string data, Dictionary<int, LlmToolCall> callIndex)
    {
        using var doc = JsonDocument.Parse(data);
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var errEl))
        {
            var msg = errEl.TryGetProperty("message", out var m) ? m.GetString() : data;
            return new StreamChunk { Error = Truncate(msg ?? data, 300) };
        }

        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
        {
            foreach (var choice in choices.EnumerateArray())
            {
                var delta = choice.TryGetProperty("delta", out var d) ? d : default;
                if (delta.ValueKind != JsonValueKind.Object) continue;

                // thinking: reasoning_content
                if (delta.TryGetProperty("reasoning_content", out var rc) && rc.ValueKind == JsonValueKind.String)
                {
                    var text = rc.GetString();
                    if (!string.IsNullOrEmpty(text))
                        return new StreamChunk { ReasoningDelta = text };
                }

                // content
                if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                {
                    var text = c.GetString();
                    if (!string.IsNullOrEmpty(text))
                        return new StreamChunk { Delta = text };
                }

                // tool_calls
                if (delta.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
                {
                    var calls = new List<LlmToolCall>();
                    foreach (var tc in tcs.EnumerateArray())
                    {
                        var index = tc.TryGetProperty("index", out var idxEl) ? idxEl.GetInt32() : 0;
                        if (!callIndex.TryGetValue(index, out var acc))
                        {
                            acc = new LlmToolCall
                            {
                                Id = tc.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                                    ? idEl.GetString() ?? Guid.NewGuid().ToString("N")[..12]
                                    : Guid.NewGuid().ToString("N")[..12],
                            };
                            callIndex[index] = acc;
                        }
                        if (tc.TryGetProperty("function", out var fn) && fn.ValueKind == JsonValueKind.Object)
                        {
                            if (fn.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                                acc.Name += n.GetString() ?? "";
                            if (fn.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.String)
                                acc.Arguments += a.GetString() ?? "";
                        }
                        // 每块都返回累积快照,由 BlockAssembler 合并去重
                        calls.Add(new LlmToolCall
                        {
                            Id = acc.Id,
                            Name = acc.Name,
                            Arguments = acc.Arguments,
                        });
                    }
                    return new StreamChunk { ToolCalls = calls };
                }

                if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String)
                    return new StreamChunk { FinishReason = fr.GetString() };
            }
        }

        if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
        {
            return new StreamChunk
            {
                Usage = new UsageInfo
                {
                    PromptTokens = GetInt(usage, "prompt_tokens"),
                    CompletionTokens = GetInt(usage, "completion_tokens"),
                    TotalTokens = GetInt(usage, "total_tokens"),
                    ReasoningTokens = GetInt(usage, "completion_tokens_details", "reasoning_tokens"),
                },
            };
        }

        return new StreamChunk();
    }

    private static int? GetInt(JsonElement el, params string[] path)
    {
        JsonElement cur = el;
        foreach (var p in path)
        {
            if (cur.ValueKind != JsonValueKind.Object || !cur.TryGetProperty(p, out var next))
                return null;
            cur = next;
        }
        return cur.ValueKind == JsonValueKind.Number ? cur.GetInt32() : null;
    }

    /// <summary>解析当前 provider 的基地址:优先实例级 resolver(由工厂按 provider 配置注入)。</summary>
    private string ResolveBaseUrl(GenerateOptions options)
    {
        // 自定义 provider 通过实例 resolver 提供其 BaseUrl
        var custom = _baseUrlResolver();
        if (!string.IsNullOrWhiteSpace(custom)) return custom.TrimEnd('/');

        // DeepSeek 官方支持环境变量覆盖(向后兼容)
        var env = Environment.GetEnvironmentVariable("DEEPSEEK_API_BASE");
        if (!string.IsNullOrWhiteSpace(env)) return env.TrimEnd('/');

        return DefaultEndpoint;
    }

    /// <summary>解析 HTTP 错误响应,提取稳定的错误码与可读的底层信息(供 FriendlyMessage 使用)。</summary>
    private static (string Code, string Message) ParseError(int status, string body)
    {
        var code = $"HTTP_{status}";
        var message = Truncate(body, 500);
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            // OpenAI 兼容: { "error": { "message", "type", "code", "param" } }
            if (root.TryGetProperty("error", out var err))
            {
                if (err.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String)
                    message = Truncate(m.GetString() ?? message, 500);
                if (err.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String)
                {
                    var apiCode = c.GetString();
                    if (!string.IsNullOrWhiteSpace(apiCode))
                        code = apiCode.ToUpperInvariant().Replace(' ', '_').Replace("-", "_");
                }
                else if (err.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String)
                {
                    var type = t.GetString();
                    if (!string.IsNullOrWhiteSpace(type))
                        code = type.ToUpperInvariant().Replace(' ', '_').Replace("-", "_");
                }
            }
        }
        catch
        {
            // 非 JSON 响应体,保留原始内容
        }
        return (code, message);
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "...";
}
