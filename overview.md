# DeepSeek Harness WPF MVVM 重写 — 交付总览

## ✅ 完成情况

依据 [DeepSeek Harness v0.1.0-rc.8](https://github.com/deepseek-ai/deepseek-harness) 的核心架构与数据资产,用 **WPF + MVVM + .NET 9** 完整重写,核心逻辑独立成可复用类库。

## 🎯 核心交付

- **`src/DeepSeekHarness.Core/`** — 核心逻辑类库(对位 Cordis 插件框架 + ReactLoopAgent)
  - `Session`/`SessionEvent` — 追加式事件日志(SESSION_FORMAT_VERSION=0)
  - `AgentLoop` — 反应式 Agent 循环(turn/step/Inbox/流式)
  - `BlockAssembler` — 流式块汇总
  - `ToolScheduler` + 11 个内置工具(bash/read/write/edit/str_replace/glob/grep/ask_user/todo/web_search/web_fetch/session_info)
  - `DeepSeekAdapter` — OpenAI 兼容 SSE 流式 LLM 适配器(含 thinking)
  - `PluginRegistry` — 一切皆插件的服务注册
  - `PresetLoader` — 从导入 JSON 加载 4 个预设(standard/code/minimal/cordis)
  - `JsonlSessionStore` — 会话持久化
  - `HarnessEngine` — 引擎门面(UI 与核心的解耦点)

- **`src/DeepSeekHarness.App/`** — WPF MVVM 主程序
  - `MainWindow` — 三栏布局(Sidebar/Conversation,模型+Effort 顶栏,流式输入区)
  - `SettingsWindow` — 四 Tab(Models/Presets/Plugins/General)
  - `MarkdownView` — 流式 Markdown 渲染控件(Markdig)
  - 5 个 ViewModel + 6 个转换器
  - DeepSeek 蓝(`#4176E6`)主色,完整样式资源

- **`src/DeepSeekHarness.Selftest/`** — 核心逻辑自测控制台 **22/22 通过**

## 📥 数据导入

`tools/import_reference_data.py` 已将 `reference/` 全部数据资产转换到 `data/`:

| 类型 | 数量 | 路径 |
|---|---|---|
| Agent 预设 | 4 | `data/presets/{standard,code,minimal,cordis}/` |
| Bundle 组合层 | 3 | `data/bundles/{base,headless,web-app}/` |
| 文档目录 | 6 | `data/docs/{tool,config,persistence,architecture,...}.md` |
| 示例配置 | 2 | `data/examples/{headless,acp}/cordis.yml` |
| 原始 YAML | 1.8KB × 多 | `reference/packages/bundle/...`、`reference/apps/cli/config/agent-presets/...` |

- 4 个 Agent 预设的完整 persona、工具组合、描述全部导入
- Base/Headless/WebApp 三个 bundle 的 plugin 行表完整导入
- tool-catalog.md(90KB)、config-catalog.md(140KB) 完整保留供查阅
- 转换脚本处理了 `!!js` 自定义 YAML 标签(JS 表达式 → 占位字符串)

## 🏗️ 架构对位

| Cordis 概念 | C# 实现 |
|---|---|
| Context(服务定位) | `PluginContext` |
| Service 三角 seam | `IPlugin` + `StartAsync(ctx)` |
| Scope 生命周期 | `PluginRegistry.StartAsync`/`StopAsync` |
| Session 追加事件 | `Session` + `SessionEvent`(monotonic seq) |
| Surface events(UI 投影) | `SessionEvent.IsSurface` |
| `deriveMessages()` | `Session.DeriveMessages()` |
| ReactLoopAgent | `AgentLoop` |
| BlockAssembler | `BlockAssembler` |
| ToolRuntimeScheduler | `ToolScheduler` |
| LLM adapter(`prepareCall/stream`) | `ILlmAdapter.StreamAsync` |
| JsonlSessionStore | `JsonlSessionStore` |
| Bundle patch + preset | `Bundle` + `AgentPreset` |

## ✅ 自测结果

```
[1] 数据导入与预设加载          6/6  ✅
[2] 会话事件日志与派生          4/4  ✅
[3] JSONL 持久化往返            3/3  ✅
[4] 工具注册与执行              4/4  ✅
[5] AgentLoop 冒烟(Mock LLM)    3/3  ✅
[6] 系统提示组装                2/2  ✅
----------------------------------
========== 22 通过, 0 失败 ==========
```

自测包含一次完整的 Agent turn 循环:用户消息 → LLM 推理 → bash 工具调用 → 结果回填 → LLM 二次推理 → 文本回答。

## 🖥️ GUI 冒烟

`DeepSeekHarness.exe` 启动成功(进程占 ~150MB,无错误日志),XAML/ViewModel 初始化正常,资源键与转换器全部解析成功。

## 📦 运行

```bash
# 1. 编译
dotnet build

# 2. 配置 API Key
set DEEPSEEK_API_KEY=sk-...

# 3. 启动(任意一种)
dotnet run --project src/DeepSeekHarness.App
# 或直接运行
src/DeepSeekHarness.App/bin/Debug/net9.0-windows/DeepSeekHarness.exe
```

## ⏳ 已知限制(后续可补)

- Windows 上无 Linux Landlock 沙箱(只做权限检查 + 工作区路径校验)
- API Gateway / Webserver / ACP / Python SDK / 远程沙箱(E2B)未实现
- 暗色主题、macOS/Linux 适配未做
- subagent / workflow / mcp-client / goal / lsp 等插件架构已留位,工具待补

## 📂 关键文件清单

| 文件 | 作用 |
|---|---|
| `src/DeepSeekHarness.Core/HarnessEngine.cs` | 引擎门面,所有组件的组合点 |
| `src/DeepSeekHarness.Core/Agent/AgentLoop.cs` | 反应式 Agent 循环 |
| `src/DeepSeekHarness.Core/Session/Session.cs` | 追加式事件日志 |
| `src/DeepSeekHarness.Core/LLM/DeepSeekAdapter.cs` | DeepSeek SSE 流式 |
| `src/DeepSeekHarness.Core/Tools/Builtin/*` | 11 个内置工具 |
| `src/DeepSeekHarness.App/MainWindow.xaml` | 三栏布局主窗口 |
| `src/DeepSeekHarness.App/Views/SettingsWindow.xaml` | 设置对话框 |
| `src/DeepSeekHarness.App/Controls/MarkdownView.cs` | 流式 Markdown 渲染 |
| `src/DeepSeekHarness.Selftest/Program.cs` | 22 项自测 |
| `tools/import_reference_data.py` | 数据导入脚本 |
| `data/manifest.json` | 导入清单 |
