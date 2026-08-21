using System.Collections.Concurrent;
using System.Text.Json;

namespace DeepSeekHarness.Core.Tools;

using DeepSeekHarness.Core.Session;

/// <summary>
/// 工具注册表(对齐参考项目 ctx.tools 注册表)。
/// 一切能力即插件注册的工具。
/// </summary>
public sealed class ToolRegistry
{
    private readonly ConcurrentDictionary<string, ITool> _tools = new();

    public event Action<string, ITool>? ToolRegistered;
    public event Action<string>? ToolUnregistered;

    public void Register(ITool tool)
    {
        if (string.IsNullOrEmpty(tool.Definition.Name))
            throw new ArgumentException("工具名不能为空");
        _tools[tool.Definition.Name] = tool;
        ToolRegistered?.Invoke(tool.Definition.Name, tool);
    }

    public bool TryGet(string name, out ITool? tool) => _tools.TryGetValue(name, out tool);

    public IReadOnlyList<ITool> All => _tools.Values.ToList();

    public IReadOnlyList<ToolDefinition> Definitions => _tools.Values.Select(t => t.Definition).ToList();

    public void Unregister(string name) => _tools.TryRemove(name, out _);
}

/// <summary>
/// 工具执行调度器(对齐参考项目 ToolRuntimeScheduler:prepare/dispatch/finalize/finish)。
/// 执行管道:approval 审批 → execute → finalize。
/// </summary>
public sealed class ToolScheduler
{
    private readonly ToolRegistry _registry;
    private readonly IApprovalService? _approval;

    /// <summary>暴露注册表供 AgentLoop 读取工具定义。</summary>
    public ToolRegistry Registry => _registry;

    /// <summary>工具调用事件(供 UI 订阅):(callId, name, phase, payload)。</summary>
    public event Action<string, string, string, object?>? ToolEvent;

    public ToolScheduler(ToolRegistry registry, IApprovalService? approval = null)
    {
        _registry = registry;
        _approval = approval;
    }

    /// <summary>审批服务接口。</summary>
    public interface IApprovalService
    {
        Task<bool> RequestAsync(ToolDefinition def, string argumentsJson, ToolContext ctx);
    }

    /// <summary>执行一个工具调用。</summary>
    public async Task<ToolResultData> DispatchAsync(ToolCallData call, ToolContext ctx)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        ToolEvent?.Invoke(call.CallId, call.Name, "start", call.ArgumentsJson);

        if (!_registry.TryGet(call.Name, out var tool) || tool == null)
        {
            sw.Stop();
            ToolEvent?.Invoke(call.CallId, call.Name, "error", $"未知工具: {call.Name}");
            return new ToolResultData
            {
                CallId = call.CallId,
                Name = call.Name,
                IsError = true,
                ErrorMessage = $"工具不存在: {call.Name}",
                DurationMs = sw.Elapsed.TotalMilliseconds,
            };
        }

        try
        {
            // 审批(参考项目 tools/pre-execute 瀑布中的 ctx.approval)
            if (tool.Definition.RequiresApproval && _approval != null)
            {
                var allowed = await _approval.RequestAsync(tool.Definition, call.ArgumentsJson, ctx);
                if (!allowed)
                {
                    sw.Stop();
                    ToolEvent?.Invoke(call.CallId, call.Name, "blocked", null);
                    return new ToolResultData
                    {
                        CallId = call.CallId,
                        Name = call.Name,
                        IsError = true,
                        ErrorMessage = "用户拒绝了该工具调用",
                        DurationMs = sw.Elapsed.TotalMilliseconds,
                    };
                }
            }

            JsonElement args;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrEmpty(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
                args = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                args = JsonDocument.Parse("{}").RootElement.Clone();
            }

            ToolEvent?.Invoke(call.CallId, call.Name, "execute", args);
            var result = await tool.ExecuteAsync(args, ctx);

            sw.Stop();
            var data = new ToolResultData
            {
                CallId = call.CallId,
                Name = call.Name,
                IsError = result.IsError,
                Output = result.Output,
                ErrorMessage = result.Error,
                MetaJson = result.Meta is { Count: > 0 } ? JsonSerializer.Serialize(result.Meta) : null,
                DurationMs = Math.Round(sw.Elapsed.TotalMilliseconds, 1),
            };
            ToolEvent?.Invoke(call.CallId, call.Name, result.IsError ? "error" : "done", data);
            return data;
        }
        catch (OperationCanceledException)
        {
            sw.Stop();
            ToolEvent?.Invoke(call.CallId, call.Name, "aborted", null);
            return new ToolResultData
            {
                CallId = call.CallId,
                Name = call.Name,
                IsError = true,
                ErrorMessage = "执行被取消",
                DurationMs = sw.Elapsed.TotalMilliseconds,
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            ToolEvent?.Invoke(call.CallId, call.Name, "error", ex.Message);
            return new ToolResultData
            {
                CallId = call.CallId,
                Name = call.Name,
                IsError = true,
                ErrorMessage = ex.Message,
                DurationMs = sw.Elapsed.TotalMilliseconds,
            };
        }
    }
}
