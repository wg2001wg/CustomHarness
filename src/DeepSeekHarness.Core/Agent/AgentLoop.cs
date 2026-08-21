using System.Text.Json;

namespace DeepSeekHarness.Core.Agent;

using DeepSeekHarness.Core.Config;
using DeepSeekHarness.Core.LLM;
using DeepSeekHarness.Core.Preset;
using DeepSeekHarness.Core.Session;
using DeepSeekHarness.Core.Tools;

/// <summary>Agent 循环状态。</summary>
public enum AgentLoopState
{
    Idle,
    Running,
    Interrupted,
    Error,
}

/// <summary>
/// 反应式 Agent 循环(对齐参考项目 ReactLoopAgent)。
/// turn/step 双层结构:一个 turn 处理一次用户输入,内部由多步(step)组成,
/// 每步:LLM 推理 → (可选)工具调用 → 结果回填 → 下一步,直到完成。
/// </summary>
public sealed class AgentLoop
{
    private readonly Session _session;
    private readonly ILlmAdapter _llm;
    private readonly ToolScheduler _scheduler;
    private readonly AppSettingsConfig _settings;
    private readonly AgentPreset _preset;
    private readonly string _workingDirectory;

    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _stepCount;
    private int _turnCount;

    // ---------- 事件(供 UI 订阅) ----------
    public event Action<SessionEventType, object?>? SessionEvent;
    public event Action<string>? StreamDelta;          // 流式文本增量
    public event Action<string>? ReasoningDelta;       // 思考增量
    public event Action<string, string, string, object?>? ToolEvent; // (callId, name, phase, payload)
    public event Action<AgentLoopState>? StateChanged;
    public event Action<Message>? AssistantMessageCompleted;
    public event Action<TurnEndReason>? TurnEnded;

    public AgentLoopState State { get; private set; } = AgentLoopState.Idle;

    public sealed class AppSettingsConfig
    {
        // 优先使用实时 AppSettings 引用(切换 provider/model 立即生效);
        // 若未提供,则回退到构造时的快照值(用于自测/单测)。
        private readonly AppSettings? _live;

        public string ProviderId => _live?.ProviderId ?? _providerId!;
        public string ModelId => _live?.ModelId ?? _modelId!;
        public string ReasoningEffort => _live?.ReasoningEffort ?? _reasoningEffort;
        public PermissionLevel PermissionLevel => _live?.Permission ?? _permissionLevel;
        public Func<string?, string?> ApiKeyResolver { get; init; } = _ => null;

        // 快照字段(无 AppSettings 引用时使用)
        private readonly string? _providerId;
        private readonly string? _modelId;
        private readonly string _reasoningEffort;
        private readonly PermissionLevel _permissionLevel;

        // 1) 实时模式:直接绑定 AppSettings,所有读取为最新值
        public AppSettingsConfig(AppSettings live, Func<string?, string?>? apiKeyResolver = null)
        {
            _live = live;
            _reasoningEffort = "medium";
            _permissionLevel = PermissionLevel.WorkspaceWrite;
            if (apiKeyResolver != null) ApiKeyResolver = apiKeyResolver;
        }

        // 2) 快照模式(用于自测/单测/无 settings 场景)
        public AppSettingsConfig(
            string providerId, string modelId,
            string reasoningEffort = "medium",
            PermissionLevel permissionLevel = PermissionLevel.WorkspaceWrite,
            Func<string?, string?>? apiKeyResolver = null)
        {
            _providerId = providerId;
            _modelId = modelId;
            _reasoningEffort = reasoningEffort;
            _permissionLevel = permissionLevel;
            if (apiKeyResolver != null) ApiKeyResolver = apiKeyResolver;
        }
    }

    public AgentLoop(
        Session session,
        ILlmAdapter llm,
        ToolScheduler scheduler,
        AppSettingsConfig settings,
        AgentPreset preset,
        string workingDirectory)
    {
        _session = session;
        _llm = llm;
        _scheduler = scheduler;
        _settings = settings;
        _preset = preset;
        _workingDirectory = workingDirectory;

        _scheduler.ToolEvent += (callId, name, phase, payload) => ToolEvent?.Invoke(callId, name, phase, payload);
    }

    private void SetState(AgentLoopState s)
    {
        State = s;
        StateChanged?.Invoke(s);
    }

    /// <summary>提交用户消息并开始一个 turn(对齐 send → wake → turn)。</summary>
    public async Task SendAsync(string userText)
    {
        if (string.IsNullOrWhiteSpace(userText)) return;
        await _gate.WaitAsync();
        try
        {
            if (State == AgentLoopState.Running)
                throw new InvalidOperationException("Agent 正在运行中,请等待完成或中断");

            _cts = new CancellationTokenSource();
            var userMsg = Message.OfText(MessageRole.User, userText);
            _session.Append(SessionEventType.UserMessage, userMsg);
            SessionEvent?.Invoke(SessionEventType.UserMessage, userMsg);

            await RunTurnAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>中断当前运行。</summary>
    public void Interrupt()
    {
        _cts?.Cancel();
        SetState(AgentLoopState.Interrupted);
    }

    /// <summary>执行一个完整 turn(多步循环)。</summary>
    public async Task RunTurnAsync(CancellationToken? external = null)
    {
        if (State == AgentLoopState.Running) return;
        SetState(AgentLoopState.Running);
        var ct = _cts?.Token ?? external ?? CancellationToken.None;

        try
        {
            _turnCount++;
            var turnNo = _turnCount;
            _session.Append(SessionEventType.TurnStart, new { turn = turnNo });
            SessionEvent?.Invoke(SessionEventType.TurnStart, new { turn = turnNo });

            _stepCount = 0;
            TurnEndReason finalReason = TurnEndReason.Completed;

            while (!ct.IsCancellationRequested)
            {
                if (_stepCount >= MaxSteps)
                {
                    finalReason = TurnEndReason.MaxTokens;
                    break;
                }
                _stepCount++;
                var stepNo = _stepCount;

                _session.Append(SessionEventType.StepStart, new { turn = turnNo, step = stepNo });
                SessionEvent?.Invoke(SessionEventType.StepStart, new { turn = turnNo, step = stepNo });

                var stopReason = await RunStepAsync(ct, turnNo, stepNo);

                _session.Append(SessionEventType.StepEnd, new { turn = turnNo, step = stepNo, reason = stopReason });
                SessionEvent?.Invoke(SessionEventType.StepEnd, new { turn = turnNo, step = stepNo, reason = stopReason });

                if (stopReason == StepStopReason.Completed)
                {
                    finalReason = TurnEndReason.Completed;
                    break;
                }
                if (stopReason == StepStopReason.MaxTokens)
                {
                    finalReason = TurnEndReason.MaxTokens;
                    break;
                }
                if (stopReason == StepStopReason.NoTools)
                {
                    finalReason = TurnEndReason.Completed;
                    break;
                }
                if (ct.IsCancellationRequested)
                {
                    finalReason = TurnEndReason.UserInterrupt;
                    break;
                }
                // 否则继续下一步(工具已执行,结果已回填)
            }

            _session.Append(SessionEventType.TurnEnd, new { turn = turnNo, reason = finalReason.ToString() });
            SessionEvent?.Invoke(SessionEventType.TurnEnd, new { turn = turnNo, reason = finalReason.ToString() });
            TurnEnded?.Invoke(finalReason);
        }
        catch (LlmException ex)
        {
            var friendly = LlmException.FriendlyMessage(ex.ErrorCode, ex.Message);
            var err = new Message
            {
                Role = MessageRole.Assistant,
                Source = MessageSource.Model,
                Blocks = { new ContentBlock { Type = ContentBlockType.Text, Text = $"⚠️ 模型调用出错:{friendly}" } },
            };
            _session.Append(SessionEventType.AssistantMessage, err);
            SessionEvent?.Invoke(SessionEventType.AssistantMessage, err);
            _session.Append(SessionEventType.Error, new { code = ex.ErrorCode, message = friendly });
            AssistantMessageCompleted?.Invoke(err); // 通知 UI 展示错误消息
            SetState(AgentLoopState.Error);
            TurnEnded?.Invoke(TurnEndReason.Error);
        }
        catch (OperationCanceledException)
        {
            SetState(AgentLoopState.Interrupted);
            TurnEnded?.Invoke(TurnEndReason.UserInterrupt);
        }
        catch (Exception ex)
        {
            var err = new Message
            {
                Role = MessageRole.Assistant,
                Source = MessageSource.Model,
                Blocks = { new ContentBlock { Type = ContentBlockType.Text, Text = $"❌ 运行错误: {ex.Message}" } },
            };
            _session.Append(SessionEventType.AssistantMessage, err);
            SessionEvent?.Invoke(SessionEventType.AssistantMessage, err);
            _session.Append(SessionEventType.Error, new { message = ex.Message });
            AssistantMessageCompleted?.Invoke(err); // 通知 UI 展示错误消息
            SetState(AgentLoopState.Error);
            TurnEnded?.Invoke(TurnEndReason.Error);
        }
        finally
        {
            if (State != AgentLoopState.Error && State != AgentLoopState.Interrupted)
                SetState(AgentLoopState.Idle);
        }
    }

    public enum StepStopReason { Continue, Completed, MaxTokens, NoTools }

    /// <summary>执行一步:LLM 推理 + 工具执行。</summary>
    private async Task<StepStopReason> RunStepAsync(CancellationToken ct, int turnNo, int stepNo)
    {
        // 1. 组装请求
        var requestHeader = new { turn = turnNo, step = stepNo, provider = _settings.ProviderId, model = _settings.ModelId };
        _session.Append(SessionEventType.RequestHeader, requestHeader, ignorable: true);

        var messages = BuildLlmMessages();
        var tools = _scheduler.Registry.Definitions
            .Select(d => d.ToLlmDefinition())
            .ToList();

        var options = new GenerateOptions
        {
            Provider = _settings.ProviderId,
            Model = _settings.ModelId,
            Messages = messages,
            Tools = tools.Count > 0 ? tools : null,
            ReasoningEffort = _settings.ReasoningEffort,
            CancellationToken = ct,
        };

        // 2. 流式推理
        var assembler = new BlockAssembler(new List<Action<string>> { d => StreamDelta?.Invoke(d) });
        var requestContext = new { systemPrompt = BuildSystemPrompt() };
        _session.Append(SessionEventType.RequestContext, requestContext, ignorable: true);

        await foreach (var chunk in _llm.StreamAsync(options))
        {
            ct.ThrowIfCancellationRequested();
            if (!string.IsNullOrEmpty(chunk.Error))
                throw new LlmException(_settings.ProviderId, _settings.ModelId, "STREAM_ERROR", chunk.Error);

            if (!string.IsNullOrEmpty(chunk.ReasoningDelta))
                ReasoningDelta?.Invoke(chunk.ReasoningDelta);

            assembler.Append(chunk);

            // 增量事件(供 UI 流式渲染,ignorable)
            _session.Append(SessionEventType.AssistantChunk,
                new { delta = chunk.Delta, reasoning = chunk.ReasoningDelta }, ignorable: true);
        }

        // 3. 汇总 assistant 消息
        var assistantMsg = assembler.BuildMessage();
        var messageId = Guid.NewGuid().ToString("N");
        assistantMsg.Id = messageId;
        _session.Append(SessionEventType.AssistantMessage, assistantMsg);
        SessionEvent?.Invoke(SessionEventType.AssistantMessage, assistantMsg);
        AssistantMessageCompleted?.Invoke(assistantMsg);

        if (assembler.FinishReason == "length" || assembler.FinishReason == "max_tokens")
            return StepStopReason.MaxTokens;

        // 4. 执行工具调用
        if (assembler.HasToolCalls)
        {
            var results = new List<ToolResultData>();
            foreach (var call in assembler.ToolCalls)
            {
                ct.ThrowIfCancellationRequested();
                _session.Append(SessionEventType.ToolCall, call);
                SessionEvent?.Invoke(SessionEventType.ToolCall, call);

                var toolCtx = new ToolContext
                {
                    WorkingDirectory = _workingDirectory,
                    Permission = _settings.PermissionLevel,
                    SessionId = _session.Header.Id,
                    Log = msg => _session.Append(SessionEventType.Error, msg, ignorable: true),
                    CancellationToken = ct,
                };

                var result = await _scheduler.DispatchAsync(call, toolCtx);
                results.Add(result);

                _session.Append(SessionEventType.ToolResult, result);
                SessionEvent?.Invoke(SessionEventType.ToolResult, result);
            }

            // 工具已执行 → 继续下一步
            return results.Any(r => r.IsError) && results.All(r => r.IsError)
                ? StepStopReason.Continue   // 出错也回填给模型,让其修正
                : StepStopReason.Continue;
        }

        // 5. 无工具调用 → turn 完成
        return assembler.HasToolCalls ? StepStopReason.Continue : StepStopReason.Completed;
    }

    /// <summary>构建系统提示(对齐参考项目 system-prompt 组装:harness 身份 + persona + 工具指引)。</summary>
    public string BuildSystemPrompt()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# DeepSeek Harness — 编码 Agent");
        sb.AppendLine();
        sb.AppendLine($"- 模型: {_settings.ModelId}({_settings.ProviderId})");
        sb.AppendLine($"- 工作目录: {_workingDirectory}");
        sb.AppendLine($"- 会话 ID: {_session.Header.Id}");
        sb.AppendLine($"- 权限级别: {_settings.PermissionLevel}");
        sb.AppendLine();

        if (!string.IsNullOrEmpty(_preset.Persona))
        {
            sb.AppendLine("## 角色设定 (Persona)");
            sb.AppendLine(_preset.Persona.Replace("{{model}}", _settings.ModelId)
                .Replace("{{cwd}}", _workingDirectory));
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("## 角色设定");
            sb.AppendLine($"You are a coding agent powered by the {_settings.ModelId} model. " +
                          $"Your working directory is {_workingDirectory}.");
            sb.AppendLine();
        }

        if (_preset.Description != null)
        {
            sb.AppendLine($"## 预设: {_preset.DisplayName ?? _preset.Name}");
            sb.AppendLine(_preset.Description);
            sb.AppendLine();
        }

        sb.AppendLine("## 工具使用指引");
        sb.AppendLine("- 需要读取/修改文件时,优先使用 read / write / edit / glob / grep 工具。");
        sb.AppendLine("- 需要运行命令时使用 bash 工具。");
        sb.AppendLine("- 任务复杂时先用 todo_write 拆解待办清单,再逐步完成。");
        sb.AppendLine("- 需要最新信息时使用 web_search / web_fetch。");
        sb.AppendLine("- 需要用户决策时使用 ask_user_question。");
        sb.AppendLine("- 完成全部工作后,用文本总结结果,不要调用工具。");
        return sb.ToString();
    }

    /// <summary>构建 LLM 消息历史(从会话日志派生)。</summary>
    private List<LlmMessage> BuildLlmMessages()
    {
        var msgs = new List<LlmMessage> { LlmMessage.Of("system", BuildSystemPrompt()) };

        var derived = _session.DeriveMessages();
        var pendingToolCalls = new List<ToolCallData>();

        foreach (var msg in derived)
        {
            switch (msg.Role)
            {
                case MessageRole.User:
                    msgs.Add(LlmMessage.Of("user", msg.Text ?? ""));
                    break;
                case MessageRole.Assistant:
                {
                    var content = msg.Text ?? "";
                    var lm = new LlmMessage
                    {
                        Role = "assistant",
                        Content = string.IsNullOrEmpty(content) ? null : content,
                    };
                    var calls = msg.ToolCalls.ToList();
                    if (calls.Count > 0)
                    {
                        lm.ToolCalls = calls.Select(tc => new LlmToolCall
                        {
                            Id = tc.CallId,
                            Name = tc.Name,
                            Arguments = tc.ArgumentsJson,
                        }).ToList();
                        pendingToolCalls.AddRange(calls);
                    }
                    msgs.Add(lm);
                    break;
                }
                case MessageRole.Tool:
                {
                    var tr = msg.Blocks.FirstOrDefault(b => b.Type == ContentBlockType.ToolResult)?.ToolResult;
                    if (tr != null)
                    {
                        msgs.Add(LlmMessage.OfTool(tr.CallId, tr.IsError
                            ? $"Error: {tr.ErrorMessage}"
                            : tr.Output ?? ""));
                        pendingToolCalls.RemoveAll(pc => pc.CallId == tr.CallId);
                    }
                    break;
                }
            }
        }
        return msgs;
    }

    public int MaxSteps { get; set; } = 50;
    public int StepCount => _stepCount;
    public int TurnCount => _turnCount;
}
