using System.Text.Json;

namespace DeepSeekHarness.Core.Preset;

/// <summary>组合层中的一行(对齐参考项目 cordis.patch.yml 的 insert 行)。</summary>
public sealed class CompositionRow
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public Dictionary<string, object?>? Config { get; set; }
}

/// <summary>Agent 预设(对齐参考项目 agent-presets)。</summary>
public sealed class AgentPreset
{
    public string Name { get; set; } = "";
    public string? DisplayName { get; set; }
    public string? Description { get; set; }
    public int? Order { get; set; }
    public List<CompositionRow> Rows { get; set; } = new();

    /// <summary>预设的 persona 文本(从 agent.cordis.yml 提取)。</summary>
    public string? Persona { get; set; }
}

/// <summary>Bundle 组合层(对齐参考项目 packages/bundle)。</summary>
public sealed class Bundle
{
    public string Name { get; set; } = "";
    public List<CompositionRow> Rows { get; set; } = new();
}

/// <summary>
/// 预设加载器:从导入的 data/ JSON 加载预设与 bundle(对齐参考项目 preset 目录发现)。
/// </summary>
public sealed class PresetLoader
{
    private readonly string _dataRoot;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public PresetLoader(string dataRoot)
    {
        _dataRoot = dataRoot;
    }

    public static PresetLoader FromAppDir()
    {
        // 优先使用环境变量 DSH_DATA,否则在可执行目录/项目目录下寻找 data/
        var env = Environment.GetEnvironmentVariable("DSH_DATA");
        if (!string.IsNullOrEmpty(env) && Directory.Exists(env)) return new PresetLoader(env);

        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "data"),
            Path.Combine(Directory.GetCurrentDirectory(), "data"),
            Path.Combine(FindRepoRoot(), "data"),
        };
        foreach (var c in candidates)
        {
            if (Directory.Exists(Path.Combine(c, "presets"))) return new PresetLoader(c);
        }
        return new PresetLoader(candidates[0]);
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "data"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    /// <summary>列出全部可用预设名。</summary>
    public IEnumerable<string> ListPresetNames()
    {
        var dir = Path.Combine(_dataRoot, "presets");
        return Directory.Exists(dir)
            ? Directory.GetDirectories(dir).Select(Path.GetFileName)!
            : Array.Empty<string>();
    }

    /// <summary>加载指定预设。</summary>
    public AgentPreset? LoadPreset(string name)
    {
        var path = Path.Combine(_dataRoot, "presets", name, "preset.json");
        if (!File.Exists(path)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        var preset = new AgentPreset { Name = name };

        if (root.TryGetProperty("preset_meta", out var meta) && meta.ValueKind == JsonValueKind.Object &&
            meta.TryGetProperty("content", out var mc) && mc.ValueKind == JsonValueKind.Object)
        {
            preset.DisplayName = GetStr(mc, "name");
            preset.Description = GetStr(mc, "description");
            preset.Order = GetInt(mc, "order");
        }

        if (root.TryGetProperty("agent", out var agent) && agent.ValueKind == JsonValueKind.Object &&
            agent.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            preset.Rows = ParseRows(content);
        }

        ExtractPersona(preset);
        return preset;
    }

    /// <summary>加载指定 bundle。</summary>
    public Bundle? LoadBundle(string name)
    {
        var path = Path.Combine(_dataRoot, "bundles", name, "bundle.json");
        if (!File.Exists(path)) return null;
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;
        var bundle = new Bundle { Name = name };
        if (root.TryGetProperty("patch", out var patch) && patch.ValueKind == JsonValueKind.Object &&
            patch.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
        {
            bundle.Rows = ParseRows(content);
        }
        return bundle;
    }

    private static List<CompositionRow> ParseRows(JsonElement content)
    {
        var rows = new List<CompositionRow>();
        foreach (var row in content.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if (row.TryGetProperty("insert", out var insert) && insert.ValueKind == JsonValueKind.Array)
            {
                // insert: [{id, name, config}, ...]
                foreach (var item in insert.EnumerateArray())
                {
                    rows.Add(new CompositionRow
                    {
                        Id = GetStr(item, "id"),
                        Name = GetStr(item, "name"),
                        Config = ParseConfig(item),
                    });
                }
            }
            else
            {
                rows.Add(new CompositionRow
                {
                    Id = GetStr(row, "id"),
                    Name = GetStr(row, "name"),
                    Config = ParseConfig(row),
                });
            }
        }
        return rows;
    }

    private static Dictionary<string, object?>? ParseConfig(JsonElement el)
    {
        if (!el.TryGetProperty("config", out var cfg) || cfg.ValueKind != JsonValueKind.Object) return null;
        var dict = new Dictionary<string, object?>();
        foreach (var prop in cfg.EnumerateObject())
            dict[prop.Name] = JsonElementToObject(prop.Value);
        return dict;
    }

    internal static object? JsonElementToObject(JsonElement el) => el.ValueKind switch
    {
        JsonValueKind.String => el.GetString(),
        JsonValueKind.Number => el.TryGetInt32(out var i) ? i : el.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null => null,
        JsonValueKind.Array => el.EnumerateArray().Select(JsonElementToObject).ToList(),
        JsonValueKind.Object => el.EnumerateObject().ToDictionary(p => p.Name, p => JsonElementToObject(p.Value)),
        _ => el.GetRawText(),
    };

    private static void ExtractPersona(AgentPreset preset)
    {
        // 从 agent.cordis.yml 的 persona 配置提取(在 preset.json 的 agent.content 中查找 persona 行)
        foreach (var row in preset.Rows)
        {
            if (row.Config != null && row.Config.TryGetValue("persona", out var p) && p is string s)
            {
                preset.Persona = s;
                return;
            }
        }
    }

    private static string? GetStr(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? GetInt(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
}
