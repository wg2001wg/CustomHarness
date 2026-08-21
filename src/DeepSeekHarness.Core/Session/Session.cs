namespace DeepSeekHarness.Core.Session;

/// <summary>
/// 会话:不可变追加式事件日志(对齐参考项目 dsh-session)。
/// 所有持久化与 UI 投影均由事件日志派生。
/// </summary>
public sealed class Session
{
    private readonly List<SessionEvent> _events = new();
    private readonly object _lock = new();

    public SessionHeader Header { get; }
    public long NextSeq { get; private set; }

    /// <summary>从持久化恢复下一个序号。</summary>
    public void RestoreSeq(long nextSeq)
    {
        lock (_lock)
        {
            if (nextSeq > NextSeq) NextSeq = nextSeq;
        }
    }

    /// <summary>事件追加时触发(用于 UI 实时订阅)。</summary>
    public event Action<Session, SessionEvent>? EventAppended;

    public Session(SessionHeader? header = null)
    {
        Header = header ?? new SessionHeader();
    }

    /// <summary>追加一个事件,分配单调序号。</summary>
    public SessionEvent Append(SessionEventType type, object? data = null, bool ignorable = false)
    {
        SessionEvent evt;
        lock (_lock)
        {
            evt = new SessionEvent
            {
                Type = type,
                Seq = NextSeq++,
                Time = DateTimeOffset.Now,
                Data = data,
                Ignorable = ignorable,
            };
            _events.Add(evt);
        }
        EventAppended?.Invoke(this, evt);
        return evt;
    }

    /// <summary>全部事件快照(按 seq 升序)。</summary>
    public IReadOnlyList<SessionEvent> Events()
    {
        lock (_lock) return _events.ToList();
    }

    /// <summary>
    /// 派生模型可见消息历史(对齐参考项目 deriveMessages)。
    /// 将事件日志投影为 Message 列表。
    /// </summary>
    public List<Message> DeriveMessages()
    {
        var messages = new List<Message>();
        lock (_lock)
        {
            foreach (var evt in _events)
            {
                switch (evt.Type)
                {
                    case SessionEventType.UserMessage:
                        if (evt.Data is Message um)
                            messages.Add(um);
                        break;
                    case SessionEventType.AssistantMessage:
                        if (evt.Data is Message am)
                        {
                            var merged = messages.LastOrDefault(m => m.Role == MessageRole.Assistant
                                                                     && m.Id == am.Id);
                            if (merged == null) messages.Add(am);
                            else merged.Blocks.AddRange(am.Blocks);
                        }
                        break;
                    case SessionEventType.ToolResult:
                        if (evt.Data is ToolResultData tr)
                        {
                            // 找到对应的 assistant tool-call 消息,将结果作为 tool 消息追加
                            var callId = tr.CallId;
                            var owner = messages.LastOrDefault(m =>
                                m.Role == MessageRole.Assistant &&
                                m.ToolCalls.Any(tc => tc.CallId == callId));
                            if (owner != null)
                            {
                                var block = owner.Blocks.FirstOrDefault(b =>
                                    b.Type == ContentBlockType.ToolCall && b.ToolCall!.CallId == callId);
                                if (block != null)
                                {
                                    // 参考项目将 tool result 作为独立 tool 角色消息
                                    messages.Add(new Message
                                    {
                                        Role = MessageRole.Tool,
                                        Source = MessageSource.Tool,
                                        Id = "tool-" + callId,
                                        Blocks =
                                        {
                                            new ContentBlock
                                            {
                                                Type = ContentBlockType.ToolResult,
                                                ToolResult = tr,
                                            },
                                        },
                                    });
                                }
                            }
                        }
                        break;
                }
            }
        }
        return messages;
    }

    /// <summary>Surface 消息(仅 user/message、assistant/message、tool/result)——用于 UI 渲染。</summary>
    public List<SessionEvent> SurfaceEvents()
    {
        lock (_lock) return _events.Where(e => e.IsSurface).ToList();
    }

    /// <summary>最近 N 条事件。</summary>
    public List<SessionEvent> RecentEvents(int count)
    {
        lock (_lock) return _events.TakeLast(count).ToList();
    }
}
