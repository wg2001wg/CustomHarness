using System.IO;
using System.Windows;
using DeepSeekHarness.App.ViewModels;
using DeepSeekHarness.Core;
using DeepSeekHarness.Core.Agent;
using DeepSeekHarness.Core.Config;
using DeepSeekHarness.Core.LLM;
using DeepSeekHarness.Core.Persistence;
using DeepSeekHarness.Core.Preset;
using DeepSeekHarness.Core.Session;
using DeepSeekHarness.Core.Tools;
using DeepSeekHarness.Core.Tools.Builtin;
using DeepSeekHarness.Core.Plugins;

// ============ DeepSeek Harness 核心逻辑自测 ============
var pass = 0;
var fail = 0;

void Check(string name, bool ok, string? detail = null)
{
    if (ok) { pass++; Console.WriteLine($"  ✅ {name}"); }
    else { fail++; Console.WriteLine($"  ❌ {name} {(detail != null ? "— " + detail : "")}"); }
}

Console.WriteLine("== [1] 数据导入与预设加载 ==");
var loader = PresetLoader.FromAppDir();
var presetNames = loader.ListPresetNames().ToList();
Check("发现预设目录", presetNames.Count == 4, $"实际: {string.Join(",", presetNames)}");

foreach (var name in new[] { "standard", "code", "minimal", "cordis" })
{
    var p = loader.LoadPreset(name);
    Check($"预设 [{name}] 可解析", p != null && p.Rows.Count > 0,
        p == null ? "null" : $"{p.Rows.Count} 行, 显示名: {p.DisplayName}, 描述: {(p.Description ?? "")[..Math.Min(24, (p.Description ?? "").Length)]}");
}

var baseBundle = loader.LoadBundle("base");
Check("bundle [base] 可解析", baseBundle != null && baseBundle.Rows.Count > 10, $"行数: {baseBundle?.Rows.Count}");

Console.WriteLine("\n== [2] 会话事件日志与派生 ==");
var session = new Session(new SessionHeader { Id = "test-session", Cwd = Directory.GetCurrentDirectory() });
session.Append(SessionEventType.UserMessage, Message.OfText(MessageRole.User, "你好"));
var asst = new Message
{
    Role = MessageRole.Assistant,
    Blocks =
    {
        new ContentBlock { Type = ContentBlockType.Text, Text = "好的" },
        new ContentBlock
        {
            Type = ContentBlockType.ToolCall,
            ToolCall = new ToolCallData { CallId = "call-1", Name = "read", ArgumentsJson = "{\"path\":\"x\"}" },
        },
    },
};
session.Append(SessionEventType.AssistantMessage, asst);
session.Append(SessionEventType.ToolResult, new ToolResultData { CallId = "call-1", Output = "file content", DurationMs = 5 });
Check("事件序号单调", session.Events().Last().Seq == session.Events().Count - 1);
Check("surface 事件数=3(user/assistant/tool-result)", session.SurfaceEvents().Count == 3);

var derived = session.DeriveMessages();
Check("派生消息含 user+assistant+tool(3 条)", derived.Count == 3,
    $"实际 {derived.Count}: {string.Join(",", derived.Select(m => m.Role))}");
Check("tool 消息关联 callId", derived.Any(m => m.Role == MessageRole.Tool &&
    m.Blocks[0].ToolResult?.CallId == "call-1"));

Console.WriteLine("\n== [3] JSONL 持久化往返 ==");
var store = new JsonlSessionStore(Path.Combine(Path.GetTempPath(), "dsh-selftest-sessions"));
store.Save(session);
var loaded = store.Load("test-session");
Check("会话可重载", loaded != null);
Check("重载后消息数一致", loaded != null && loaded.DeriveMessages().Count == derived.Count);
Check("重载后标题/工作区保留", loaded != null && loaded.Header.Cwd == Directory.GetCurrentDirectory());

Console.WriteLine("\n== [4] 工具注册与执行 ==");
var registry = new ToolRegistry();
registry.Register(new FileReadTool());
registry.Register(new FileWriteTool());
registry.Register(new GlobTool());
registry.Register(new GrepTool());
registry.Register(new ShellTool("auto"));
registry.Register(new AskUserTool());
Check("工具注册(6)", registry.Definitions.Count == 6);

var scheduler = new ToolScheduler(registry);
var tmpFile = Path.Combine(Path.GetTempPath(), "dsh-selftest-file.txt");
File.WriteAllText(tmpFile, "line1\nline2\nhello dsh");
var ctx = new ToolContext { WorkingDirectory = Path.GetTempPath() };

var readResult = await scheduler.DispatchAsync(new ToolCallData { CallId = "r1", Name = "read", ArgumentsJson = JsonArg("path", tmpFile) }, ctx);
Check("read 工具执行", !readResult.IsError && readResult.Output!.Contains("hello dsh"), readResult.ErrorMessage);

var globResult = await scheduler.DispatchAsync(new ToolCallData { CallId = "g1", Name = "glob", ArgumentsJson = "{\"pattern\":\"dsh-selftest-*.txt\"}" }, ctx);
Check("glob 工具执行", !globResult.IsError && globResult.Output!.Contains("dsh-selftest-file.txt"), globResult.ErrorMessage);

var grepResult = await scheduler.DispatchAsync(new ToolCallData { CallId = "gr1", Name = "grep", ArgumentsJson = "{\"pattern\":\"hello\",\"path\":\"" + tmpFile.Replace("\\", "\\\\") + "\"}" }, ctx);
Check("grep 工具执行", !grepResult.IsError && grepResult.Output!.Contains("hello dsh"), grepResult.ErrorMessage);

Console.WriteLine("\n== [5] AgentLoop 冒烟(Mock LLM) ==");
var mockLlm = new MockLlm();
var agentSession = new Session(new SessionHeader { Id = "agent-test", Cwd = Directory.GetCurrentDirectory() });
var agent = new AgentLoop(agentSession, mockLlm, scheduler,
    new AgentLoop.AppSettingsConfig("mock", "mock-model",
        permissionLevel: PermissionLevel.WorkspaceWrite),
    loader.LoadPreset("standard") ?? new AgentPreset { Name = "standard" },
    Directory.GetCurrentDirectory());
agent.MaxSteps = 3;

var turnReasons = new List<TurnEndReason>();
agent.TurnEnded += r => turnReasons.Add(r);
var streamText = new System.Text.StringBuilder();
agent.StreamDelta += d => streamText.Append(d);

await agent.SendAsync("列出当前目录的文件");
Check("turn 正常结束", turnReasons.Count == 1 && turnReasons[0] == TurnEndReason.Completed,
    turnReasons.Count == 0 ? "未触发 TurnEnded" : turnReasons[0].ToString());
Check("流式文本已输出", streamText.Length > 0, $"长度 {streamText.Length}");
Check("会话含 assistant 消息", agentSession.Events().Any(e => e.Type == SessionEventType.AssistantMessage));

Console.WriteLine("\n== [6] 系统提示组装 ==");
var sysPrompt = agent.BuildSystemPrompt();
Check("系统提示含 persona", sysPrompt.Contains("编码 Agent") || sysPrompt.Contains("coding agent"));
Check("系统提示含工具指引", sysPrompt.Contains("tool") || sysPrompt.Contains("工具"));

// ============ [7] 设置窗口 Tab 切换 UI 测试(回归:修复内容区空白 bug) ============
Console.WriteLine("\n== [7] 设置窗口 Tab 切换(UI 回归) ==");
var uiResults = new List<(string, bool, string?)>();
var uiThread = new Thread(() => { uiResults.AddRange(RunSettingsUiTests()); })
{
    IsBackground = true,
    Name = "ui-test",
};
uiThread.SetApartmentState(ApartmentState.STA);
uiThread.Start();
if (!uiThread.Join(TimeSpan.FromSeconds(30)))
{
    uiThread.Abort();
    Console.WriteLine("  ❌ UI 测试超时");
    fail++;
}
foreach (var (name, ok, detail) in uiResults)
    Check(name, ok, detail);

// ============ [8] 发送消息空引用防御测试(回归:发送报空引用异常) ============
Console.WriteLine("\n== [8] 发送消息空引用防御(UI 回归) ==");
var sendResults = new List<(string, bool, string?)>();
var sendThread = new Thread(() => { sendResults.AddRange(RunSendDefenseTests()); })
{
    IsBackground = true,
    Name = "send-test",
};
sendThread.SetApartmentState(ApartmentState.STA);
sendThread.Start();
if (!sendThread.Join(TimeSpan.FromSeconds(30)))
{
    sendThread.Abort();
    Console.WriteLine("  ❌ 发送防御测试超时");
    fail++;
}
foreach (var (name, ok, detail) in sendResults)
    Check(name, ok, detail);

// ============ [9] 主窗口尺寸自适应测试(回归:适配当前显示器) ============
Console.WriteLine("\n== [9] 主窗口尺寸自适应(UI) ==");
var fitResults = new List<(string, bool, string?)>();
var fitThread = new Thread(() => { fitResults.AddRange(RunWindowFitTests()); })
{
    IsBackground = true,
    Name = "fit-test",
};
fitThread.SetApartmentState(ApartmentState.STA);
fitThread.Start();
if (!fitThread.Join(TimeSpan.FromSeconds(30)))
{
    fitThread.Abort();
    Console.WriteLine("  ❌ 窗口自适应测试超时");
    fail++;
}
foreach (var (name, ok, detail) in fitResults)
    Check(name, ok, detail);

// ============ [10] 配置后 Agent 可用 + 损坏会话容错 ============
Console.WriteLine("\n== [10] 配置后 Agent 可用 + 损坏会话容错 ==");
var cfgResults = new List<(string, bool, string?)>();
var cfgThread = new Thread(() => { cfgResults.AddRange(RunPostConfigTests()); })
{
    IsBackground = true,
    Name = "cfg-test",
};
cfgThread.SetApartmentState(ApartmentState.STA);
cfgThread.Start();
if (!cfgThread.Join(TimeSpan.FromSeconds(30)))
{
    cfgThread.Abort();
    Console.WriteLine("  ❌ 配置可用性测试超时");
    fail++;
}
foreach (var (name, ok, detail) in cfgResults)
    Check(name, ok, detail);

// ============ [11] 插件同步(上游 catalog + preset 映射 + 插件启动) ============
Console.WriteLine("\n== [11] 插件同步(上游 catalog + preset 映射) ==");
var syncEngine = new HarnessEngine();
Check("插件目录已同步(>100 包)", syncEngine.PluginMapper.PackageCount > 100,
    $"实际 {syncEngine.PluginMapper.PackageCount}");
Check("catalog 含 dsh-tool-bash 包", syncEngine.PluginMapper.Get("@deepseek-ai/dsh-tool-bash") != null);
var stdPreset = syncEngine.PresetLoader.LoadPreset("standard");
Check("standard 预设含 16 行插件", stdPreset != null && stdPreset.Rows.Count >= 16,
    $"实际 {stdPreset?.Rows.Count}");
if (stdPreset != null)
{
    var mapped = stdPreset.Rows.Count(r => syncEngine.PluginMapper.TryGetToolNames(r.Id ?? "", out _));
    Check("已实现映射行 >= 6", mapped >= 6, $"实际 {mapped}");
}
Check("tool-goal 标记待实现",
    syncEngine.PluginMapper.ResolveCapability("tool-goal") == PluginCapability.Pending);
var syncSession = syncEngine.NewSession();
syncEngine.InitAgent(syncSession);
Check("InitAgent 后插件已启动(>=6)", syncEngine.Plugins.Running.Count >= 6,
    $"实际 {syncEngine.Plugins.Running.Count}: {string.Join(",", syncEngine.Plugins.Running.Select(x => x.Id))}");
Check("tool-bash 插件已启动", syncEngine.Plugins.IsRunning("tool-bash"));

Console.WriteLine($"\n========== 结果: {pass} 通过, {fail} 失败 ==========");
return fail == 0 ? 0 : 1;

static string JsonArg(string key, string value) => "{\"" + key + "\":\"" + value.Replace("\\", "\\\\") + "\"}";

/// <summary>
/// [7] 设置窗口 Tab 切换回归测试(在 STA 线程运行)。
/// 回归场景:反复点击设置左侧导航后内容区变空白无法恢复。
/// </summary>
static List<(string, bool, string?)> RunSettingsUiTests()
{
    var results = new List<(string, bool, string?)>();
    void UiCheck(string name, bool ok, string? detail = null) => results.Add((name, ok, detail));

    try
    {
        // 加载 App 资源(Application 单例 + App.xaml 资源字典)
        var app = Application.Current as DeepSeekHarness.App.App;
        if (app == null)
        {
            app = new DeepSeekHarness.App.App();
            app.InitializeComponent(); // 加载 App.xaml 的 StaticResource 资源
        }

        var engine = new HarnessEngine();
        var vm = new SettingsViewModel(engine);
        var win = new DeepSeekHarness.App.Views.SettingsWindow(vm);
        win.Show(); // 触发布局与绑定

        // 等布局完成
        win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
        win.Measure(new System.Windows.Size(double.PositiveInfinity, double.PositiveInfinity));
        win.Arrange(new System.Windows.Rect(0, 0, 960, 640));

        var nav = win.FindName("NavList") as System.Windows.Controls.ListBox;
        UiCheck("找到导航 ListBox", nav != null);
        if (nav == null) { win.Close(); return results; }
        UiCheck("导航有 4 个 Tab", nav.Items.Count == 4, $"实际 {nav.Items.Count}");

        var panels = new Dictionary<string, System.Windows.Controls.StackPanel>
        {
            ["Models"] = win.FindName("ModelsPanel") as System.Windows.Controls.StackPanel,
            ["Presets"] = win.FindName("PresetsPanel") as System.Windows.Controls.StackPanel,
            ["Plugins"] = win.FindName("PluginsPanel") as System.Windows.Controls.StackPanel,
            ["General"] = win.FindName("GeneralPanel") as System.Windows.Controls.StackPanel,
        };

        // 初始状态:默认 Models 可见
        win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);
        UiCheck("初始 SelectedTab=Models", vm.SelectedTab == "Models", $"实际 {vm.SelectedTab}");
        UiCheck("初始 Models 可见", panels["Models"]!.Visibility == Visibility.Visible);
        UiCheck("初始其它页隐藏", panels["Plugins"]!.Visibility == Visibility.Collapsed);

        // 来回切换 3 轮 × 4 个 Tab,每轮断言
        var order = new[] { "Models", "Presets", "Plugins", "General" };
        var allOk = true;
        var firstFail = "";
        for (var round = 0; round < 3; round++)
        {
            foreach (var tab in order)
            {
                var idx = Array.IndexOf(order, tab);
                nav.SelectedIndex = idx;
                win.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.DataBind);

                if (vm.SelectedTab != tab)
                {
                    allOk = false;
                    firstFail = $"第{round + 1}轮选中[{tab}]后 SelectedTab={vm.SelectedTab}";
                    break;
                }
                foreach (var (name, panel) in panels)
                {
                    var expect = name == tab ? Visibility.Visible : Visibility.Collapsed;
                    if (panel!.Visibility != expect)
                    {
                        allOk = false;
                        firstFail = $"第{round + 1}轮选中[{tab}]后面板[{name}].Visibility={panel.Visibility}";
                        break;
                    }
                }
                if (!allOk) break;
            }
            if (!allOk) break;
        }
        UiCheck("来回切换 3 轮:SelectedTab 与面板可见性始终正确", allOk, firstFail);

        win.Close();
    }
    catch (Exception ex)
    {
        results.Add(("UI 测试执行", false, ex.ToString().Split('\n')[0]));
    }
    return results;
}

/// <summary>
/// [8] 发送消息空引用防御回归测试(在 STA 线程运行)。
/// 回归场景:Agent 未初始化(null)时发送消息报空引用异常。
/// </summary>
static List<(string, bool, string?)> RunSendDefenseTests()
{
    var results = new List<(string, bool, string?)>();
    void UiCheck(string name, bool ok, string? detail = null) => results.Add((name, ok, detail));

    try
    {
        // 确保 Application 与资源就绪
        var app = Application.Current as DeepSeekHarness.App.App;
        if (app == null)
        {
            app = new DeepSeekHarness.App.App();
            app.InitializeComponent();
        }

        // 隔离 DSH_HOME,避免覆盖/依赖用户 ~/.dsh/settings.json
        var tmpHome = Path.Combine(Path.GetTempPath(), "dsh-home-" + Guid.NewGuid().ToString("N")[..6]);
        var prevHome = Environment.GetEnvironmentVariable("DSH_HOME");
        Environment.SetEnvironmentVariable("DSH_HOME", tmpHome);

        // 构造未初始化 Agent 的引擎 + 会话 VM(Agent 应为 null)
        var engine = new HarnessEngine();
        UiCheck("初始 Agent 为 null(模拟未初始化)", engine.Agent == null,
            engine.Agent != null ? "Agent 意外非空" : null);

        var conv = new ConversationViewModel(engine);
        conv.Input = "测试消息";
        var beforeCount = conv.Items.Count;

        // 触发发送(应走懒初始化兜底,不抛异常)
        bool threw = false;
        try
        {
            conv.SendCommand.Execute(null);
        }
        catch (Exception ex)
        {
            threw = true;
            UiCheck("发送不抛异常", false, ex.Message);
        }

        // 泵 Dispatcher,让异步命令完成
        PumpDispatcher(1200);

        UiCheck("发送不抛异常(Agent=null 时)", !threw);
        UiCheck("有反馈消息(懒初始化后 user 消息或提示)", conv.Items.Count > beforeCount,
            $"Items {beforeCount} → {conv.Items.Count}");
        UiCheck("无空引用崩溃", conv.Items.Count > beforeCount || threw == false);

        Environment.SetEnvironmentVariable("DSH_HOME", prevHome);
        try { Directory.Delete(tmpHome, true); } catch { /* 忽略 */ }
    }
    catch (Exception ex)
    {
        results.Add(("发送防御测试执行", false, ex.ToString().Split('\n')[0]));
    }
    return results;
}

/// <summary>泵 Dispatcher 消息循环一段时间。</summary>
static void PumpDispatcher(int ms)
{
    var end = Environment.TickCount + ms;
    while (Environment.TickCount < end)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
        Thread.Sleep(10);
    }
}

/// <summary>
/// [10] 配置后 Agent 可用性 + 损坏会话容错回归测试(STA 线程)。
/// 场景:历史会话文件损坏不应拖垮初始化;配置后发送不应再提示"未初始化"。
/// </summary>
static List<(string, bool, string?)> RunPostConfigTests()
{
    var results = new List<(string, bool, string?)>();
    void UiCheck(string name, bool ok, string? detail = null) => results.Add((name, ok, detail));

    try
    {
        // 1. 损坏 jsonl 容错
        var tmp = Path.Combine(Path.GetTempPath(), "dsh-corrupt-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(tmp);
        var store = new JsonlSessionStore(tmp);
        const string badId = "corrupt-session";
        File.WriteAllLines(Path.Combine(tmp, badId + ".jsonl"), new[]
        {
            "{\"header\":{\"version\":0,\"id\":\"corrupt-session\",\"createdAt\":\"2026-08-21T00:00:00+08:00\"}}",
            "{this line is broken json!!",
            "{\"type\":\"UserMessage\",\"seq\":0,\"data\":{\"role\":1,\"blocks\":[]}}",
        });

        Session? loaded = null;
        var loadThrew = false;
        try { loaded = store.Load(badId); } catch { loadThrew = true; }
        UiCheck("损坏 jsonl 加载不抛异常", !loadThrew && loaded != null,
            loadThrew ? "抛异常" : loaded == null ? "返回 null" : null);
        UiCheck("损坏行被跳过、header 恢复", loaded?.Header.Id == badId,
            $"id={loaded?.Header.Id}");

        // 2. 配置后 InitAgent → Agent 可用
        var app = Application.Current as DeepSeekHarness.App.App;
        if (app == null)
        {
            app = new DeepSeekHarness.App.App();
            app.InitializeComponent();
        }

        // 隔离 DSH_HOME,避免依赖/覆盖用户 ~/.dsh/settings.json(可能含真实 API Key)
        var tmpHome = Path.Combine(Path.GetTempPath(), "dsh-home-" + Guid.NewGuid().ToString("N")[..6]);
        var prevHome = Environment.GetEnvironmentVariable("DSH_HOME");
        Environment.SetEnvironmentVariable("DSH_HOME", tmpHome);

        var engine = new HarnessEngine();
        var session = engine.NewSession();
        engine.InitAgent(session);
        UiCheck("InitAgent 后 Agent 非 null", engine.Agent != null);
        UiCheck("EnsureAgent 幂等(不重建)", ReferenceEquals(engine.EnsureAgent(), engine.Agent));

        // 3. 配置后发送:隔离环境无 API Key → 应得到 LLM 错误反馈而非"未初始化"
        var conv = new ConversationViewModel(engine);
        conv.Input = "你好";
        var before = conv.Items.Count;
        var sendThrew = false;
        var completedMsgs = new List<Message>();
        engine.Agent!.AssistantMessageCompleted += m => completedMsgs.Add(m);
        try { conv.SendCommand.Execute(null); }
        catch (Exception ex) { sendThrew = true; UiCheck("发送不抛异常", false, ex.Message); }
        PumpDispatcher(2000);

        UiCheck("AssistantMessageCompleted 事件已触发", completedMsgs.Count > 0,
            $"count={completedMsgs.Count}");

        UiCheck("配置后发送不抛异常", !sendThrew);
        UiCheck("发送后有反馈消息", conv.Items.Count > before, $"Items {before}→{conv.Items.Count}");
        UiCheck("无'未初始化'提示", !conv.Items.Any(i => i.Text?.Contains("未初始化") == true),
            conv.Items.LastOrDefault()?.Text ?? "(无)");

        // 诊断:会话日志是否出现错误事件(区分 LLM 未触发 vs UI 显示问题)
        var sessEvents = engine.CurrentSession?.Events().Select(e => e.Type.ToString()).ToList() ?? new();
        UiCheck("会话日志含错误事件", sessEvents.Contains(nameof(SessionEventType.Error)) ||
                sessEvents.Contains(nameof(SessionEventType.AssistantMessage)),
            string.Join(",", sessEvents));

        UiCheck("错误反馈为 LLM 配置类提示", conv.Items.Any(i =>
                i.Text?.Contains("API Key") == true || i.Text?.Contains("LLM 错误") == true ||
                i.Text?.Contains("发送失败") == true),
            string.Join(" | ", conv.Items.Select(i => $"[{i.Kind}] {i.Text ?? ""}")));

        Environment.SetEnvironmentVariable("DSH_HOME", prevHome);

        // 清理临时目录
        try { Directory.Delete(tmp, true); } catch { /* 忽略 */ }
    }
    catch (Exception ex)
    {
        results.Add(("配置可用性测试执行", false, ex.ToString().Split('\n')[0]));
    }
    return results;
}

/// <summary>
/// [9] 主窗口尺寸自适应回归测试(STA 线程)。
/// 断言:窗口宽高不超出工作区、不小于 MinWidth/MinHeight、且居中于工作区。
/// </summary>
static List<(string, bool, string?)> RunWindowFitTests()
{
    var results = new List<(string, bool, string?)>();
    void UiCheck(string name, bool ok, string? detail = null) => results.Add((name, ok, detail));

    try
    {
        var app = Application.Current as DeepSeekHarness.App.App;
        if (app == null)
        {
            app = new DeepSeekHarness.App.App();
            app.InitializeComponent();
        }

        var win = new DeepSeekHarness.App.MainWindow(); // 构造中调用 FitToScreen
        var wa = SystemParameters.WorkArea;

        UiCheck("宽度不超出工作区", win.Width <= wa.Width + 0.5,
            $"Width={win.Width:F0} 工作区宽={wa.Width}");
        UiCheck("高度不超出工作区", win.Height <= wa.Height + 0.5,
            $"Height={win.Height:F0} 工作区高={wa.Height}");
        UiCheck("不小于最小尺寸", win.Width >= win.MinWidth && win.Height >= win.MinHeight,
            $"W={win.Width:F0}>=MinW={win.MinWidth}, H={win.Height:F0}>=MinH={win.MinHeight}");
        UiCheck("水平居中于工作区", Math.Abs((win.Left + win.Width / 2) - (wa.Left + wa.Width / 2)) < 2,
            $"Left={win.Left:F0} 中心={(win.Left + win.Width / 2):F0} vs 工作区中心={wa.Left + wa.Width / 2}");
        UiCheck("垂直居中于工作区", Math.Abs((win.Top + win.Height / 2) - (wa.Top + wa.Height / 2)) < 2,
            $"Top={win.Top:F0} 中心={(win.Top + win.Height / 2):F0} vs 工作区中心={wa.Top + wa.Height / 2}");
        UiCheck("窗口占比合理(>60%)", win.Width > wa.Width * 0.6 && win.Height > wa.Height * 0.6,
            $"W={win.Width:F0} H={win.Height:F0}");

        win.Close();
    }
    catch (Exception ex)
    {
        results.Add(("窗口自适应测试执行", false, ex.ToString().Split('\n')[0]));
    }
    return results;
}

/// <summary>Mock LLM:固定回复,第一轮调工具,第二轮结束。</summary>
sealed class MockLlm : ILlmAdapter
{
    public string ProviderName => "mock";
    public bool CanHandle(string provider) => provider == "mock";

    public async IAsyncEnumerable<StreamChunk> StreamAsync(GenerateOptions options, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var hasTool = options.Messages.Any(m => m.Role == "tool");
        if (!hasTool)
        {
            // 第一轮:调 bash 工具
            yield return new StreamChunk { ReasoningDelta = "我来看看目录里有什么。" };
            yield return new StreamChunk { Delta = "让我先运行一个命令查看目录。" };
            yield return new StreamChunk
            {
                ToolCalls = new List<LlmToolCall>
                {
                    new() { Id = "call-mock-1", Name = "bash", Arguments = "{\"command\":\"echo hello-mock\"}" },
                },
            };
            yield return new StreamChunk { FinishReason = "tool_calls" };
        }
        else
        {
            // 第二轮:直接回答
            yield return new StreamChunk { Delta = "完成!这是 mock 环境的测试回复。" };
            yield return new StreamChunk { FinishReason = "stop" };
            yield return new StreamChunk
            {
                Usage = new UsageInfo { PromptTokens = 10, CompletionTokens = 8, TotalTokens = 18 },
            };
        }
        await Task.CompletedTask;
    }
}
