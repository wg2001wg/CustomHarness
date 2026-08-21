using System.Text.Json;

namespace DeepSeekHarness.Core.Plugins;

using DeepSeekHarness.Core.Preset;

/// <summary>插件实现状态(相对本项目的 C# 实现)。</summary>
public enum PluginCapability
{
    /// <summary>已有 C# 实现(映射到内置工具/插件)。</summary>
    Implemented,
    /// <summary>仅数据层同步(上游组合行已导入,行为由框架级逻辑处理)。</summary>
    DataOnly,
    /// <summary>待实现(上游存在,本项目暂无对应 C# 能力)。</summary>
    Pending,
}

/// <summary>插件目录条目(对齐 data/plugins/catalog.json)。</summary>
public sealed class PluginCatalogItem
{
    public string Name { get; init; } = "";
    public string Id { get; init; } = "";
    public string Scope { get; init; } = "core"; // tool | plugin | core
    public string? Version { get; init; }
    public string? Description { get; init; }
    public PluginCapability Capability { get; init; } = PluginCapability.Pending;
}

/// <summary>
/// 上游插件同步映射器:把 data/plugins/catalog.json(上游 226 个插件包)与
/// preset 组合行(id/name)映射到本项目的 C# 实现状态。
/// 对齐参考项目 "@dsh-tool-*" 工具行 → 本项目 ToolRegistry 内置工具。
/// </summary>
public sealed class PresetPluginMapper
{
    /// <summary>preset 行 id → 本项目已实现工具名列表。</summary>
    private static readonly Dictionary<string, string[]> ToolMap = new()
    {
        ["tool-bash"] = new[] { "bash" },
        ["tool-pwsh"] = new[] { "bash" }, // Windows 默认 pwsh
        ["tool-fs"] = new[] { "read", "write", "edit", "str_replace_editor" },
        ["tool-fs-search"] = new[] { "glob", "grep" },
        ["tool-ask-user"] = new[] { "ask_user_question" },
        ["tool-todo"] = new[] { "todo_write" },
        ["tool-web"] = new[] { "web_search", "web_fetch" },
        ["tool-session-query"] = new[] { "session_info" },
    };

    /// <summary>仅数据层覆盖的行(框架级/系统提示已处理,无需 C# 工具)。</summary>
    private static readonly HashSet<string> DataOnlyRows = new()
    {
        "persona",
        "agent-instructions",
        "planning",
        "compaction",
        "delegation",
    };

    private readonly Dictionary<string, PluginCatalogItem> _packages = new();
    private readonly Dictionary<string, string> _descriptions = new(); // id → 描述

    public IReadOnlyList<PluginCatalogItem> Packages => _packages.Values.ToList();
    public int PackageCount => _packages.Count;

    /// <summary>是否加载到目录数据。</summary>
    public bool HasCatalog => _packages.Count > 0;

    public static PresetPluginMapper FromAppDir()
    {
        var loader = PresetLoader.FromAppDir();
        return new PresetPluginMapper(loader.DataRoot);
    }

    public PresetPluginMapper(string dataRoot)
    {
        var path = Path.Combine(dataRoot, "plugins", "catalog.json");
        if (!File.Exists(path)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("packages", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in arr.EnumerateArray())
                {
                    var name = GetStr(el, "name") ?? "";
                    var id = GetStr(el, "id") ?? "";
                    if (string.IsNullOrEmpty(name)) continue;
                    var item = new PluginCatalogItem
                    {
                        Name = name,
                        Id = id,
                        Scope = GetStr(el, "scope") ?? "core",
                        Version = GetStr(el, "version"),
                        Description = GetStr(el, "description"),
                        Capability = ResolveCapability(id),
                    };
                    _packages[name] = item;
                    if (!string.IsNullOrEmpty(id) && !_descriptions.ContainsKey(id))
                        _descriptions[id] = item.Description ?? "";
                }
            }
        }
        catch (JsonException)
        {
            // 目录损坏则视为未同步
        }
    }

    /// <summary>解析 preset 行的实现状态。</summary>
    public PluginCapability ResolveCapability(string rowId)
        => ToolMap.ContainsKey(rowId) ? PluginCapability.Implemented
         : DataOnlyRows.Contains(rowId) ? PluginCapability.DataOnly
         : PluginCapability.Pending;

    /// <summary>preset 行 id → 已实现工具名(仅 Implemented 行)。</summary>
    public bool TryGetToolNames(string rowId, out IReadOnlyList<string> toolNames)
    {
        if (ToolMap.TryGetValue(rowId, out var names))
        {
            toolNames = names;
            return true;
        }
        toolNames = Array.Empty<string>();
        return false;
    }

    /// <summary>按包名查目录条目。</summary>
    public PluginCatalogItem? Get(string packageName)
        => _packages.GetValueOrDefault(packageName);

    /// <summary>按行 id 查描述(优先目录中的同名包)。</summary>
    public string? GetDescription(string rowId)
        => _descriptions.GetValueOrDefault(rowId);

    private static string? GetStr(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
