using DeepSeekHarness.Core.Agent;
using DeepSeekHarness.Core.Config;
using DeepSeekHarness.Core.LLM;
using DeepSeekHarness.Core.Persistence;
using DeepSeekHarness.Core.Plugins;
using DeepSeekHarness.Core.Preset;
using DeepSeekHarness.Core.Tools;
using DeepSeekHarness.Core.Tools.Builtin;
using DeepSeekHarness.Core.Session;
using SessionType = DeepSeekHarness.Core.Session.Session;

namespace DeepSeekHarness.Core;

/// <summary>
/// Harness 引擎门面:组合全部核心组件(对齐参考项目 dsh 的组合层)。
/// UI 层只依赖本类。核心逻辑独立于 UI,可被任何宿主(CLI/服务)复用。
/// </summary>
public sealed class HarnessEngine : IDisposable
{
    private readonly AppSettings _settings;
    private readonly PresetLoader _presetLoader;
    private readonly PresetPluginMapper _pluginMapper;
    private readonly JsonlSessionStore _sessionStore;
    private readonly ToolRegistry _toolRegistry = new();
    private readonly PluginRegistry _pluginRegistry = new();
    private readonly List<IPlugin> _builtinPlugins = new();
    private ILlmAdapter _llm = null!;
    private ToolScheduler? _scheduler;
    private AgentLoop? _agentLoop;

    public HarnessEngine(AppSettings? settings = null)
    {
        _settings = settings ?? AppSettings.Load();
        _presetLoader = PresetLoader.FromAppDir();
        _pluginMapper = new PresetPluginMapper(_presetLoader.DataRoot);
        var sessionRoot = Path.Combine(GetDshHome(), "sessions");
        _sessionStore = new JsonlSessionStore(sessionRoot);
    }

    public AppSettings Settings => _settings;
    public PresetLoader PresetLoader => _presetLoader;
    /// <summary>上游插件同步目录(226 个 @dsh-* 包 + 实现状态映射)。</summary>
    public PresetPluginMapper PluginMapper => _pluginMapper;
    public ToolRegistry Tools => _toolRegistry;
    public PluginRegistry Plugins => _pluginRegistry;
    public SessionType? CurrentSession { get; private set; }
    public AgentLoop? Agent { get; private set; }

    public static string GetDshHome()
    {
        var env = Environment.GetEnvironmentVariable("DSH_HOME");
        if (!string.IsNullOrEmpty(env)) return env;
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh");
    }

    // ---------- 会话 ----------

    /// <summary>创建新会话。</summary>
    public SessionType NewSession(string? workspace = null)
    {
        var session = new SessionType(new SessionHeader
        {
            Cwd = _settings.Workspace,
            AgentPreset = _settings.AgentPreset,
            Workspace = workspace ?? Path.GetFileName(_settings.Workspace),
        });
        CurrentSession = session;
        return session;
    }

    /// <summary>加载既有会话。</summary>
    public SessionType? LoadSession(string id)
    {
        var session = _sessionStore.Load(id);
        if (session != null) CurrentSession = session;
        return session;
    }

    public void SaveCurrentSession() => SaveSession(CurrentSession);

    public void SaveSession(SessionType? session)
    {
        if (session == null) return;
        _sessionStore.Save(session);
        _settings.RecentSessions.RemoveAll(r => r.Id == session.Header.Id);
        _settings.RecentSessions.Insert(0, new AppSettings.RecentSession
        {
            Id = session.Header.Id,
            Title = session.Header.Title ?? "未命名会话",
            LastActivity = DateTimeOffset.Now,
            Workspace = session.Header.Workspace,
        });
        _settings.RecentSessions = _settings.RecentSessions.Take(50).ToList();
        _settings.LastSessionId = session.Header.Id;
        _settings.Save();
    }

    public IEnumerable<(string Id, string? Title, DateTimeOffset? Created)> ListSessions()
    {
        foreach (var id in _sessionStore.ListSessionIds())
        {
            var s = _sessionStore.Load(id);
            if (s != null)
                yield return (id, s.Header.Title, s.Header.CreatedAt);
        }
    }

    // ---------- Agent ----------

    /// <summary>初始化 Agent(工具注册 + LLM + 循环)。</summary>
    public AgentLoop InitAgent(SessionType session)
    {
        CurrentSession = session;
        RegisterBuiltinTools();

        var preset = _presetLoader.LoadPreset(_settings.AgentPreset)
                     ?? _presetLoader.LoadPreset("standard")
                     ?? new AgentPreset { Name = "standard" };
        _llm = LlmAdapterFactory.Create(_settings);

        _scheduler = new ToolScheduler(_toolRegistry);
        _agentLoop = new AgentLoop(
            session,
            _llm,
            _scheduler,
            // 实时绑定 AppSettings:切换 provider/model 后,AgentLoop 内部读取始终是最新值
            // (不再用 InitAgent 时的固化快照)。
            new AgentLoop.AppSettingsConfig(_settings, id => _settings.ResolveApiKey(id)),
            preset,
            _settings.Workspace);
        Agent = _agentLoop; // 同步公开属性(此前缺失导致 Agent 恒为 null)

        // 一切皆插件:启动内置插件(在 preset 组合层中的)
        StartPresetPlugins(session, preset);

        return _agentLoop;
    }

    /// <summary>
    /// 懒初始化:Agent 为 null 时自动初始化(当前会话或新会话)。
    /// 用于发送消息前的兜底,避免因早期初始化失败导致 Agent 永久不可用。
    /// </summary>
    public AgentLoop EnsureAgent()
    {
        if (_agentLoop != null) return _agentLoop;
        var session = CurrentSession ?? NewSession();
        return InitAgent(session);
    }

    private void RegisterBuiltinTools()
    {
        if (_toolRegistry.Definitions.Count > 0) return; // 已注册

        _toolRegistry.Register(new ShellTool("auto"));
        _toolRegistry.Register(new FileReadTool());
        _toolRegistry.Register(new FileWriteTool());
        _toolRegistry.Register(new FileEditTool());
        _toolRegistry.Register(new StrReplaceEditorTool());
        _toolRegistry.Register(new GlobTool());
        _toolRegistry.Register(new GrepTool());
        _toolRegistry.Register(new AskUserTool());
        _toolRegistry.Register(new TodoWriteTool((sid, items) =>
        {
            CurrentSession?.Append(SessionEventType.TodoWrite,
                new { sessionId = sid, items = items.Select(i => new { i.Content, i.Completed }) });
        }));
        _toolRegistry.Register(new WebSearchTool(SearchImpl, (sid, m) =>
        {
            CurrentSession?.Append(SessionEventType.Error, m, ignorable: true);
        }));
        _toolRegistry.Register(new WebFetchTool(FetchImpl));
        _toolRegistry.Register(new SessionInfoTool());
    }

    // ---------- 内置插件(对齐"一切皆插件",UI/能力均为插件) ----------

    private void StartPresetPlugins(SessionType session, AgentPreset preset)
    {
        var pluginIds = preset.Rows.Select(r => r.Id).ToHashSet();

        // 1. 显式注册的内置插件(按 preset 行匹配)
        foreach (var plugin in _builtinPlugins)
        {
            if (pluginIds.Contains(plugin.Id))
            {
                _pluginRegistry.Register(plugin);
            }
        }

        // 2. preset 行 → 工具插件适配:上游 @dsh-tool-* 行映射到本项目内置工具,
        //    使插件行真正进入 PluginRegistry(之前从未启动,仅数据层同步)。
        foreach (var row in preset.Rows)
        {
            if (row.Id == null) continue;
            if (_pluginRegistry.IsRunning(row.Id)) continue;
            if (!_pluginMapper.TryGetToolNames(row.Id, out var toolNames)) continue;
            if (toolNames.Count == 0) continue;

            var adapter = new ToolPluginAdapter(
                row.Id,
                row.Name ?? row.Id,
                _pluginMapper.GetDescription(row.Id),
                () => toolNames
                    .Select(n => _toolRegistry.TryGet(n, out var t) ? t : null)
                    .Where(t => t != null)
                    .Cast<ITool>()
                    .ToList());

            _pluginRegistry.Register(adapter);
            var ctx = new PluginContext
            {
                PluginId = row.Id,
                Tools = _toolRegistry,
                Session = session,
                Registry = _pluginRegistry,
                WorkingDirectory = _settings.Workspace,
                Config = row.Config,
            };
            _pluginRegistry.StartAsync(adapter, ctx).GetAwaiter().GetResult();
        }
    }

    /// <summary>注册一个 UI/能力插件。</summary>
    public void RegisterBuiltinPlugin(IPlugin plugin) => _builtinPlugins.Add(plugin);

    // ---------- 搜索实现(可替换) ----------

    public Func<string, int, Task<string>>? SearchImpl { get; set; }
    public Func<string, Task<string>>? FetchImpl { get; set; }

    // ---------- 生命周期 ----------

    public void Dispose()
    {
        _agentLoop?.Interrupt();
        _pluginRegistry.StopAllAsync().GetAwaiter().GetResult();
    }
}
