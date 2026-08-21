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

    public string ProviderName => "deepseek-official";

    public DeepSeekAdapter(HttpClient http, Func<string?> apiKeyResolver)
    {
        _http = http;
        _apiKeyResolver = apiKeyResolver;
    }

    public bool CanHandle(string provider)
        => provider is "deepseek-official" or "deepseek" or "dsh-deepseek";

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
        var request = new HttpRequestMessage(HttpMethod.Post, Endpoint(options.Provider) + "/chat/completions")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        using var response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var errBody = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            throw new LlmException(options.Provider, options.Model, $"HTTP_{((int)response.StatusCode)}",
                $"LLM 请求失败 ({response.StatusCode}): {Truncate(errBody, 300)}");
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
            if (m.ToolCalls != null) dict["tool_calls"] = m.ToolCalls;
            msgs.Add(dict);
        }

        var dict2 = new Dictionary<string, object?>
        {
            ["model"] = options.Model,
            ["messages"] = msgs,
            ["stream"] = true,
        };
        if (options.Tools is { Count: > 0 })
            dict2["tools"] = options.Tools;
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

    private static string Endpoint(string provider) => provider switch
    {
        "deepseek" or "deepseek-official" => Environment.GetEnvironmentVariable("DEEPSEEK_API_BASE") ?? DefaultEndpoint,
        _ => DefaultEndpoint,
    };

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "...";
}
