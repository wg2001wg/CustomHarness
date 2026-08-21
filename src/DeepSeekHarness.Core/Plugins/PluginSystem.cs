namespace DeepSeekHarness.Core.Plugins;

using DeepSeekHarness.Core.LLM;
using DeepSeekHarness.Core.Session;
using DeepSeekHarness.Core.Tools;

/// <summary>
/// 插件上下文(对齐参考项目 ctx:服务定位器)。
/// 一切皆插件 —— 每个插件通过上下文访问服务。
/// </summary>
public sealed class PluginContext
{
    public required string PluginId { get; init; }
    public required ToolRegistry Tools { get; init; }
    public required Session Session { get; init; }
    public ILlmAdapter? Llm { get; init; }
    public PluginRegistry Registry { get; init; } = null!;
    public string WorkingDirectory { get; init; } = Environment.CurrentDirectory;

    /// <summary>插件自身配置(来自 preset 组合层)。</summary>
    public Dictionary<string, object?>? Config { get; init; }

    public void Log(string message) => Session.Append(SessionEventType.Error, $"[{PluginId}] {message}", ignorable: true);
}

/// <summary>插件接口(对齐参考项目 Cordis plugin 生命周期)。</summary>
public interface IPlugin
{
    string Id { get; }
    string Name { get; }
    string Description { get; }
    Task StartAsync(PluginContext ctx);
    Task StopAsync();
}

/// <summary>
/// 插件注册表(对齐参考项目 Cordis 注册表 + 组合层)。
/// 支持按 preset 组合加载/卸载插件。
/// </summary>
public sealed class PluginRegistry
{
    private readonly Dictionary<string, IPlugin> _plugins = new();
    private readonly Dictionary<string, PluginContext> _contexts = new();

    public event Action<IPlugin>? PluginStarted;
    public event Action<IPlugin>? PluginStopped;

    public void Register(IPlugin plugin)
    {
        if (!_plugins.ContainsKey(plugin.Id))
            _plugins[plugin.Id] = plugin;
    }

    public async Task StartAsync(IPlugin plugin, PluginContext ctx)
    {
        if (_contexts.ContainsKey(plugin.Id)) return; // 已启动
        _contexts[plugin.Id] = ctx;
        await plugin.StartAsync(ctx);
        PluginStarted?.Invoke(plugin);
    }

    public async Task StopAsync(string id)
    {
        if (_contexts.Remove(id, out var _) && _plugins.TryGetValue(id, out var plugin))
        {
            await plugin.StopAsync();
            PluginStopped?.Invoke(plugin);
        }
    }

    public async Task StopAllAsync()
    {
        foreach (var id in _contexts.Keys.ToList())
            await StopAsync(id);
    }

    public IReadOnlyList<IPlugin> All => _plugins.Values.ToList();

    public IReadOnlyList<IPlugin> Running => _contexts.Keys.Select(k => _plugins[k]).ToList();

    public IPlugin? Get(string id) => _plugins.GetValueOrDefault(id);

    public bool IsRunning(string id) => _contexts.ContainsKey(id);
}

/// <summary>
/// 工具插件适配器:把 preset 组合行(如 tool-bash / tool-fs)包装为 IPlugin,
/// 使上游插件行在本项目 PluginRegistry 中真正可见、可启动("一切皆插件")。
/// StartAsync 时把映射的工具注册进上下文,供 Agent 调用。
/// </summary>
public sealed class ToolPluginAdapter : IPlugin
{
    private readonly Func<IEnumerable<ITool>> _toolResolver;

    public string Id { get; }
    public string Name { get; }
    public string Description { get; }

    public ToolPluginAdapter(string id, string name, string? description, Func<IEnumerable<ITool>> toolResolver)
    {
        Id = id;
        Name = name;
        Description = description ?? "";
        _toolResolver = toolResolver;
    }

    public Task StartAsync(PluginContext ctx)
    {
        foreach (var tool in _toolResolver())
        {
            if (tool != null)
                ctx.Tools.Register(tool); // 幂等:同名工具覆盖注册
        }
        return Task.CompletedTask;
    }

    public Task StopAsync() => Task.CompletedTask;
}
