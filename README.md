# DeepSeek Harness (WPF MVVM 重写版)

WPF + MVVM + .NET 9 完整重写的 [DeepSeek Harness](https://github.com/deepseek-ai/deepseek-harness) 桌面客户端,核心逻辑独立成类库,数据资产全部导入。

**详细文档**: [docs/README.md](docs/README.md) · **架构与交付总览**: [overview.md](overview.md)

## 一行启动

```bash
dotnet build && src/DeepSeekHarness.App/bin/Debug/net9.0-windows/DeepSeekHarness.exe
```

## 自测

```bash
cd src/DeepSeekHarness.Selftest && dotnet run    # 22/22 通过
```

## 解决方案结构

- `src/DeepSeekHarness.Core/` — 核心逻辑类库(可独立复用,无 UI 依赖)
- `src/DeepSeekHarness.App/` — WPF MVVM 主程序
- `src/DeepSeekHarness.Selftest/` — 核心逻辑自测控制台
- `data/` — 从 `reference/` 导入的预设、bundle、文档
- `reference/` — 完整克隆的上游仓库(7807 文件)
- `tools/import_reference_data.py` — 数据导入脚本
