using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeepSeekHarness.Core.Tools.Builtin;

/// <summary>glob 工具(基于 .NET 目录枚举,对齐参考项目 tool-fs-search)。</summary>
public sealed class GlobTool : ITool
{
    public ToolDefinition Definition => new()
    {
        Name = "glob",
        Description = "按模式查找文件路径,返回匹配列表。参数 pattern 为 glob 模式(如 **/*.cs),path 为可选搜索根目录。",
        Parameters = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>
            {
                ["pattern"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "glob 模式" },
                ["path"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "搜索根目录(默认工作区)" },
            },
            ["required"] = new[] { "pattern" },
        },
    };

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx)
    {
        var pattern = args.TryGetProperty("pattern", out var p) ? p.GetString() : null;
        if (string.IsNullOrEmpty(pattern)) return Task.FromResult(ToolResult.Fail("缺少参数: pattern"));

        var root = args.TryGetProperty("path", out var pa) && pa.ValueKind == JsonValueKind.String
            ? pa.GetString()!
            : ctx.WorkingDirectory;
        if (!Directory.Exists(root)) return Task.FromResult(ToolResult.Fail($"目录不存在: {root}"));

        try
        {
            // 简化 glob:支持 **/ 前缀的递归匹配
            var files = new List<string>();
            if (pattern.StartsWith("**/", StringComparison.Ordinal))
            {
                var rest = pattern[3..];
                foreach (var f in Directory.EnumerateFiles(root, rest, SearchOption.AllDirectories))
                    files.Add(Path.GetRelativePath(root, f).Replace('\\', '/'));
            }
            else
            {
                foreach (var f in Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly))
                    files.Add(Path.GetRelativePath(root, f).Replace('\\', '/'));
            }

            var sb = new StringBuilder();
            foreach (var f in files.Take(500))
                sb.AppendLine(f);
            if (files.Count > 500) sb.AppendLine($"... (共 {files.Count} 个,仅显示前 500)");
            return Task.FromResult(ToolResult.Ok(sb.ToString().TrimEnd()));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail($"glob 失败: {ex.Message}"));
        }
    }
}

/// <summary>grep 工具(正则搜索文件内容,对齐参考项目 tool-fs-search)。</summary>
public sealed class GrepTool : ITool
{
    public ToolDefinition Definition => new()
    {
        Name = "grep",
        Description = "在文件中按正则搜索文本,返回匹配行及行号。参数 pattern 为正则,path 为可选搜索目录或文件,glob 为可选文件过滤。",
        Parameters = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>
            {
                ["pattern"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "正则表达式" },
                ["path"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "搜索目录或文件(默认工作区)" },
                ["glob"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "文件过滤(如 *.cs)" },
                ["-i"] = new Dictionary<string, object?> { ["type"] = "boolean", ["description"] = "忽略大小写" },
            },
            ["required"] = new[] { "pattern" },
        },
    };

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx)
    {
        var pattern = args.TryGetProperty("pattern", out var p) ? p.GetString() : null;
        if (string.IsNullOrEmpty(pattern)) return Task.FromResult(ToolResult.Fail("缺少参数: pattern"));
        var ignoreCase = args.TryGetProperty("-i", out var ic) && ic.ValueKind == JsonValueKind.True;

        Regex regex;
        try
        {
            regex = new Regex(pattern,
                RegexOptions.Compiled | (ignoreCase ? RegexOptions.IgnoreCase : RegexOptions.None));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail($"正则无效: {ex.Message}"));
        }

        var target = args.TryGetProperty("path", out var pa) && pa.ValueKind == JsonValueKind.String
            ? pa.GetString()!
            : ctx.WorkingDirectory;
        var glob = args.TryGetProperty("glob", out var g) && g.ValueKind == JsonValueKind.String
            ? g.GetString() : null;

        try
        {
            var matches = new List<string>();
            var files = File.Exists(target)
                ? new[] { target }
                : Directory.Exists(target)
                    ? Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories)
                        .Where(f => glob == null || MatchGlob(f, glob))
                        .Take(2000)
                        .ToArray()
                    : Array.Empty<string>();

            foreach (var file in files)
            {
                try
                {
                    var lines = File.ReadAllLines(file);
                    for (var i = 0; i < lines.Length; i++)
                    {
                        if (regex.IsMatch(lines[i]))
                        {
                            matches.Add($"{Path.GetRelativePath(ctx.WorkingDirectory, file)}:{i + 1}: {lines[i].Trim()}");
                            if (matches.Count >= 300) break;
                        }
                    }
                }
                catch { /* 跳过无法读取的文件 */ }
                if (matches.Count >= 300) break;
            }

            var sb = new StringBuilder();
            foreach (var m in matches) sb.AppendLine(m);
            if (matches.Count == 300) sb.AppendLine("...(达到 300 条上限)");
            return Task.FromResult(ToolResult.Ok(sb.ToString().TrimEnd()));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail($"grep 失败: {ex.Message}"));
        }
    }

    private static bool MatchGlob(string path, string glob)
    {
        var name = Path.GetFileName(path);
        if (glob.StartsWith("*.")) return name.EndsWith(glob[1..], StringComparison.OrdinalIgnoreCase);
        if (!glob.Contains('*')) return name.Equals(glob, StringComparison.OrdinalIgnoreCase);
        var re = "^" + Regex.Escape(glob).Replace("\\*", ".*") + "$";
        return Regex.IsMatch(name, re, RegexOptions.IgnoreCase);
    }
}
