# DeepSeek Harness (WPF MVVM 重写版)

参考 [deepseek-ai/deepseek-harness](https://github.com/deepseek-ai/deepseek-harness)(v0.1.0-rc.8, "Everything is a Plugin") 开源项目,用 **WPF + MVVM + .NET 9** 完整重写的桌面端 Harness 系统。功能与原项目对齐,核心逻辑独立成类库以便复用。

---

## ✨ 核心能力(对照原项目)

| 原项目概念 | 本项目实现 | 状态 |
|---|---|---|
| **Cordis 插件系统**(Service/Context/Scope) | `DeepSeekHarness.Core.Plugins.PluginRegistry` + `IPlugin` 生命周期(Start/Stop) | ✅ |
| **追加式会话事件日志** (`SESSION_FORMAT_VERSION=0`) | `Session` + `SessionEvent`(type/seq/time/data),`SurfaceEvent` 投影 | ✅ |
| **ContentBlock 消息模型**(text/reasoning/image/tool-call/tool-result) | `Message` + `ContentBlock` 完整对齐 | ✅ |
| **反应式 Agent 循环** (turn/step/Inbox) | `AgentLoop`(send → turn → step → llm/stream → tool → next-step) | ✅ |
| **BlockAssembler 流式块汇总** | `BlockAssembler`(text/reasoning/tool-calls 累积) | ✅ |
| **工具执行管道** (approval → execute → finalize) | `ToolScheduler` + `ITool` + `IApprovalService` | ✅ |
| **DeepSeek 官方 LLM 适配器** (SSE 流式 + thinking) | `DeepSeekAdapter`(OpenAI 兼容) | ✅ |
| **JSONL 会话持久化** | `JsonlSessionStore`(可重放) | ✅ |
| **配置 + 凭据** (`$DSH_HOME/settings.json`) | `AppSettings`(含 API Key/模型/工作区/权限) | ✅ |
| **三档权限** (read-only / workspace-write / danger-full-access) | `PermissionLevel` 枚举 + 工具 `RequiresApproval` | ✅ |
| **Agent 预设组合** (4 个 preset) | `PresetLoader` + `AgentPreset`(standard/code/minimal/cordis) | ✅ |
| **Bundle 组合层** (base/headless/web-app) | `Bundle` 解析(`cordis.patch.yml`) | ✅ |
| **核心工具集** (bash/read/write/edit/str_replace_editor/glob/grep/ask_user_question/todo_write/web_search/web_fetch/session_info) | 11 个内置工具(对齐 tool-catalog) | ✅ |
| **WPF MVVM 桌面客户端** | `MainWindow` 三栏布局 + `SettingsWindow`(对齐 ui-conversation / ui-settings) | ✅ |
| **思考折叠行** (ui-reasoning) | `ReasoningTemplate` Expander | ✅ |
| **工具调用卡片** (ui-tool ToolRow) | `ToolCallTemplate` Expander(IN/OUT 卡片) | ✅ |
| **流式 Markdown 渲染** | `MarkdownView` + `MarkdownRenderer`(Markdig AST) | ✅ |
| **会话历史** (ui-workspace) | `SidebarViewModel` + `SessionListItem` | ✅ |
| **模型选择 + 推理强度** (ui-input-trigger) | 顶栏 `ComboBox` × 2 | ✅ |
| **设置对话框** (Models/Presets/Plugins/General) | `SettingsWindow` 四 Tab | ✅ |
| **API Gateway / HTTP server** | 简化(本版为纯客户端,后续可加 ASP.NET 集成) | ⚠️ 简化为子代理集成 |
| **ACP / Python SDK / 远程沙箱(E2B)** | 暂未实现 | ⏳ 后续 |

---

## 📂 目录结构

```
C:\Codes\Harness\
├── DeepSeekHarness.sln
├── reference/                          ← 完整克隆的参考项目(deepseek-ai/deepseek-harness, 7807 文件)
│
├── data/                                ← 从 reference 导入的数据资产
│   ├── presets/                         ← 4 个 Agent 预设(standard/code/minimal/cordis)
│   ├── bundles/                         ← 3 个组合层(base/headless/web-app)
│   ├── docs/                            ← 工具目录、配置目录、架构文档(MD)
│   ├── examples/                        ← 示例配置
│   └── manifest.json                    ← 导入清单
│
├── tools/
│   └── import_reference_data.py         ← 数据导入脚本(YAML → JSON)
│
├── src/
│   ├── DeepSeekHarness.Core/            ← 核心逻辑类库(可独立复用)
│   │   ├── Session/                     # 会话模型、事件、消息、ContentBlock
│   │   ├── Plugins/                     # 插件系统(注册/生命周期/Context)
│   │   ├── Agent/                       # Agent 循环(turn/step/Inbox)
│   │   ├── Tools/                       # 工具注册表 + 调度器 + 11 个内置工具
│   │   ├── LLM/                         # LLM 适配器(DeepSeek SSE 流式) + BlockAssembler
│   │   ├── Preset/                      # 预设加载器(YAML 转换为强类型)
│   │   ├── Config/                      # 应用设置(API Key/模型/工作区/权限)
│   │   ├── Persistence/                 # JSONL 会话存储
│   │   └── HarnessEngine.cs             # 引擎门面(组合所有组件)
│   │
│   ├── DeepSeekHarness.App/             ← WPF MVVM 主程序
│   │   ├── App.xaml + App.xaml.cs       # 应用入口 + 全局异常处理
│   │   ├── MainWindow.xaml              # 三栏布局(Sidebar/Conversation)
│   │   ├── MainWindow.xaml.cs           # DataContext + 滚动同步 + Enter 发送
│   │   ├── Views/SettingsWindow.xaml    # 设置对话框(Models/Presets/Plugins/General)
│   │   ├── ViewModels/                  # 5 个 VM(CommunityToolkit.Mvvm)
│   │   ├── Controls/MarkdownView.cs     # Markdown 流式渲染控件
│   │   ├── Converters/Converters.cs     # 6 个值转换器
│   │   └── (data/ 自动复制到输出)
│   │
│   └── DeepSeekHarness.Selftest/        ← 核心逻辑自测控制台(22/22 通过)
│       └── Program.cs
│
└── docs/
    ├── README.md                        ← 本文件
    └── ...
```

---

## 🚀 快速开始

### 1. 重新导入参考数据(可选,首次已导入)

```bash
python tools/import_reference_data.py
```

将 `reference/` 中的 YAML 配置(`cordis.patch.yml` / `agent.cordis.yml`)转换为 JSON,存入 `data/`。

### 2. 编译

```bash
dotnet build
```

输出: `src/DeepSeekHarness.App/bin/Debug/net9.0-windows/DeepSeekHarness.exe`

### 3. 配置 API Key(任选其一)

- **环境变量**:`$env:DEEPSEEK_API_KEY="sk-..."` (推荐)
- **设置对话框**:启动后 → 左侧 ⚙ 设置 → Models → 填入 Key → 保存

### 4. 启动

```bash
src/DeepSeekHarness.App/bin/Debug/net9.0-windows/DeepSeekHarness.exe
```

### 5. 自测核心逻辑

```bash
cd src/DeepSeekHarness.Selftest
dotnet run
```

预期输出: `========== 结果: 22 通过, 0 失败 ==========`

---

## 🏗️ 架构对位(Cordis → .NET)

| Cordis 概念 | C# 实现 | 位置 |
|---|---|---|
| `Context`(服务定位) | `PluginContext`(构造注入) | `Plugins/PluginSystem.cs` |
| `Service` 接口/实现/消费三位 seam | `IPlugin` + `StartAsync(ctx)` | 同上 |
| `Scope`(per-agent 作用域) | `PluginRegistry.StartAsync`/`StopAsync` | 同上 |
| `Session`(追加事件流) | `Session` + `SessionEvent` | `Session/Session.cs` |
| `SessionEventMap` | `SessionEventType` enum | `Session/SessionModels.cs` |
| `ToolRegistry` + `ToolRuntimeScheduler` | `ToolRegistry` + `ToolScheduler` | `Tools/ToolRegistry.cs` |
| `BlockAssembler` | `BlockAssembler` | `LLM/BlockAssembler.cs` |
| `deriveMessages()` | `Session.DeriveMessages()` | `Session/Session.cs` |
| `ReactLoopAgent` | `AgentLoop` | `Agent/AgentLoop.cs` |
| `LLM Adapter(prepareCall/stream)` | `ILlmAdapter.StreamAsync()` | `LLM/LlmModels.cs` |
| `JsonlSessionStore` | `JsonlSessionStore` | `Persistence/JsonlSessionStore.cs` |
| `Bundle` + `preset` | `Bundle` + `AgentPreset` | `Preset/PresetLoader.cs` |

---

## 🧩 工具清单(对齐 `docs/tool-catalog.md`)

| 工具 | 对齐原项目 | 审批 |
|---|---|---|
| `bash` | `@dsh-tool-bash`(PowerShell/cmd) | 否 |
| `read` | `@dsh-tool-fs/read` | 否 |
| `write` | `@dsh-tool-fs/write` | ✅ |
| `edit` | `@dsh-tool-fs/edit` | ✅ |
| `str_replace_editor` | `@dsh-tool-str_replace_editor` | ✅ |
| `glob` | `@dsh-tool-fs-search/glob` | 否 |
| `grep` | `@dsh-tool-fs-search/grep` | 否 |
| `ask_user_question` | `@dsh-tool-ask_user_question` | 否 |
| `todo_write` | `@dsh-tool-todo` | 否 |
| `web_search` | `@dsh-tool-web-search` | 否 |
| `web_fetch` | `@dsh-tool-web-fetch` | 否 |
| `session_info` | `@dsh-tool-session-event`(会话信息) | 否 |

未来可扩展:`subagent`/`workflow`/`mcp-client`/`lsp`/`schedule_*`/`terminal_*`/`cordis_*` 等。

---

## 🎨 视觉(对齐原 Web UI)

- **主色**:`#4176E6`(DeepSeek 蓝)
- **背景层级**:`#FFFFFF` / `#F6F7FA` / `#EDEFF4`
- **字体**:Microsoft YaHei UI;代码块 Cascadia Mono
- **布局**:三栏(280px Sidebar + * Conversation),Splitter 可拖拽
- **交互**:Enter 发送 / Shift+Enter 换行 / ⏹ 中断运行

---

## 🔌 扩展性(对齐"一切皆插件")

要添加新能力,实现 `IPlugin` 并在 `HarnessEngine.RegisterBuiltinPlugin(...)` 注册即可,UI 侧会自动出现。

要添加新工具,实现 `ITool` 并调用 `ToolRegistry.Register(...)`。

要添加新 LLM provider,实现 `ILlmAdapter` 并在 `LlmAdapterFactory` 注册。

---

## 📋 自测结果

`src/DeepSeekHarness.Selftest/Program.cs` 覆盖:

```
== [1] 数据导入与预设加载 ==          6/6  ✅
== [2] 会话事件日志与派生 ==          4/4  ✅
== [3] JSONL 持久化往返 ==            3/3  ✅
== [4] 工具注册与执行 ==              4/4  ✅
== [5] AgentLoop 冒烟(Mock LLM) ==    3/3  ✅
== [6] 系统提示组装 ==                2/2  ✅
-------------------------------------
========== 22 通过, 0 失败 ==========
```

包含一次完整的 Agent turn:用户消息 → LLM 推理 → bash 工具调用 → 结果回填 → LLM 二次推理 → 文本回答。

---

## 🛠️ 已知限制与后续

- **沙箱**:Windows 上仅提供权限 + 路径检查(原项目 landlock/E2B 不适用),`RequiresApproval` 触发 UI 弹窗待补
- **ACP / Python SDK / 远程沙箱(E2B)**:未实现
- **API Gateway / WebServer**:未实现
- **子代理 / 工作流 / 目标 / MCP 客户端**:架构已留位,工具待补
- **暗色主题**:浅色为主,暗色未做
- **macOS / Linux**:当前只验证 Windows(WinExe + WPF)

---

## 📜 许可

MIT(对齐参考项目)。`reference/` 完整保留上游 MIT 与第三方声明。
