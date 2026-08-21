using System.Text;

namespace DeepSeekHarness.Core.LLM;

using DeepSeekHarness.Core.Session;

/// <summary>
/// 流式块汇总器(对齐参考项目 BlockAssembler):
/// 把 assistant/chunk 流汇总为一个 assistant/message。
/// </summary>
public sealed class BlockAssembler
{
    private readonly StringBuilder _text = new();
    private readonly StringBuilder _reasoning = new();
    private readonly Dictionary<string, LlmToolCall> _toolCalls = new();
    private readonly Dictionary<string, ToolCallData> _finalizedCalls = new();
    private readonly List<Action<string>>? _chunkListeners;

    public string? FinishReason { get; private set; }
    public UsageInfo? Usage { get; private set; }
    public bool HasToolCalls => _finalizedCalls.Count > 0;

    /// <param name="chunkListeners">可选:逐增量块回调(用于 UI 实时流式显示)。</param>
    public BlockAssembler(List<Action<string>>? chunkListeners = null)
    {
        _chunkListeners = chunkListeners;
    }

    public string Text => _text.ToString();
    public string Reasoning => _reasoning.ToString();
    public IReadOnlyList<ToolCallData> ToolCalls => _finalizedCalls.Values.ToList();

    /// <summary>吸收一个流式块。</summary>
    public void Append(StreamChunk chunk)
    {
        if (!string.IsNullOrEmpty(chunk.ReasoningDelta))
        {
            _reasoning.Append(chunk.ReasoningDelta);
        }
        if (!string.IsNullOrEmpty(chunk.Delta))
        {
            _text.Append(chunk.Delta);
            _chunkListeners?.ForEach(l => l(chunk.Delta!));
        }
        if (chunk.ToolCalls is { Count: > 0 })
        {
            foreach (var tc in chunk.ToolCalls)
            {
                // 按 Id 分组累积(每个 chunk 的 tool_calls 是同一调用 id 的增量片段)
                if (!_toolCalls.TryGetValue(tc.Id, out var acc))
                {
                    acc = new LlmToolCall { Id = tc.Id };
                    _toolCalls[tc.Id] = acc;
                }
                if (!string.IsNullOrEmpty(tc.Name)) acc.Name = tc.Name;
                if (!string.IsNullOrEmpty(tc.Arguments)) acc.Arguments += tc.Arguments;
            }
            FinalizeCalls();
        }
        if (!string.IsNullOrEmpty(chunk.FinishReason)) FinishReason = chunk.FinishReason;
        if (chunk.Usage != null) Usage = chunk.Usage;
    }

    private void FinalizeCalls()
    {
        foreach (var (id, tc) in _toolCalls)
        {
            if (!_finalizedCalls.TryGetValue(id, out var f))
            {
                _finalizedCalls[id] = new ToolCallData
                {
                    CallId = tc.Id,
                    Name = tc.Name,
                    ArgumentsJson = tc.Arguments,
                };
            }
            else
            {
                if (!string.IsNullOrEmpty(tc.Name)) f.Name = tc.Name;
                if (!string.IsNullOrEmpty(tc.Arguments)) f.ArgumentsJson = tc.Arguments;
            }
        }
    }

    /// <summary>汇总为 assistant Message。</summary>
    public Message BuildMessage()
    {
        var msg = new Message
        {
            Role = MessageRole.Assistant,
            Source = MessageSource.Model,
        };
        if (_text.Length > 0)
            msg.Blocks.Add(new ContentBlock { Type = ContentBlockType.Text, Text = _text.ToString() });
        if (_reasoning.Length > 0)
            msg.Blocks.Add(new ContentBlock { Type = ContentBlockType.Reasoning, Text = _reasoning.ToString() });
        foreach (var tc in _finalizedCalls.Values)
            msg.Blocks.Add(new ContentBlock { Type = ContentBlockType.ToolCall, ToolCall = tc });
        return msg;
    }
}
