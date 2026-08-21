using System.Text.Json;

namespace DeepSeekHarness.Core.Tools.Builtin;

/// <summary>
/// 文件工具集(read / write / edit / str_replace_editor,对齐参考项目 tool-fs + str_replace_editor)。
/// </summary>
public sealed class FileReadTool : ITool
{
    public ToolDefinition Definition => new()
    {
        Name = "read",
        Description = "读取文件内容(纯文本)。参数 path 为文件路径,offset 为可选行偏移(1 起),limit 为可选最大行数。",
        Parameters = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>
            {
                ["path"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "文件路径(绝对或相对工作区)" },
                ["offset"] = new Dictionary<string, object?> { ["type"] = "integer", ["description"] = "起始行号(1 起,默认 1)" },
                ["limit"] = new Dictionary<string, object?> { ["type"] = "integer", ["description"] = "最多读取行数" },
            },
            ["required"] = new[] { "path" },
        },
    };

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx)
    {
        var path = Resolve(ctx, args, "path");
        if (path == null) return Task.FromResult(ToolResult.Fail("缺少参数: path"));

        try
        {
            if (!File.Exists(path)) return Task.FromResult(ToolResult.Fail($"文件不存在: {path}"));
            var offset = args.TryGetProperty("offset", out var o) && o.ValueKind == JsonValueKind.Number ? o.GetInt32() : 1;
            var limit = args.TryGetProperty("limit", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetInt32() : 0;

            var lines = File.ReadAllLines(path);
            var start = Math.Max(0, offset - 1);
            var count = limit > 0 ? Math.Min(limit, lines.Length - start) : lines.Length - start;
            var sb = new System.Text.StringBuilder();
            for (var i = start; i < start + count && i < lines.Length; i++)
                sb.AppendLine($"{i + 1,6}\t{lines[i]}");
            var total = File.ReadLines(path).Count();
            var head = $"--- {path} ({total} 行) ---\n";
            return Task.FromResult(ToolResult.Ok(head + sb.ToString().TrimEnd()));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail($"读取失败: {ex.Message}"));
        }
    }

    internal static string? Resolve(ToolContext ctx, JsonElement args, string key)
    {
        if (!args.TryGetProperty(key, out var p) || p.ValueKind != JsonValueKind.String)
            return null;
        var path = p.GetString()!;
        return System.IO.Path.IsPathRooted(path) ? path : System.IO.Path.GetFullPath(System.IO.Path.Combine(ctx.WorkingDirectory, path));
    }
}

public sealed class FileWriteTool : ITool
{
    public ToolDefinition Definition => new()
    {
        Name = "write",
        Description = "创建或覆盖写入文件。参数 path 为文件路径,content 为完整新内容。若文件存在将整体覆盖。",
        RequiresApproval = true,
        Parameters = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>
            {
                ["path"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "文件路径" },
                ["content"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "完整文件内容" },
            },
            ["required"] = new[] { "path", "content" },
        },
    };

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx)
    {
        var path = FileReadTool.Resolve(ctx, args, "path");
        if (path == null) return Task.FromResult(ToolResult.Fail("缺少参数: path"));
        var content = args.TryGetProperty("content", out var c) ? c.GetString() : null;
        if (content == null) return Task.FromResult(ToolResult.Fail("缺少参数: content"));

        try
        {
            if (!IsUnderWorkspace(path, ctx) && ctx.Permission != PermissionLevel.DangerFullAccess)
                return Task.FromResult(ToolResult.Fail($"拒绝写入工作区之外的路径: {path}"));

            var dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, content);
            return Task.FromResult(ToolResult.Ok($"已写入 {path} ({content.Length} 字符)"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail($"写入失败: {ex.Message}"));
        }
    }

    internal static bool IsUnderWorkspace(string path, ToolContext ctx)
    {
        var ws = System.IO.Path.GetFullPath(ctx.WorkingDirectory).TrimEnd('\\', '/') + System.IO.Path.DirectorySeparatorChar;
        return System.IO.Path.GetFullPath(path).StartsWith(ws, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class FileEditTool : ITool
{
    public ToolDefinition Definition => new()
    {
        Name = "edit",
        Description = "精确替换文件中唯一出现的一段文本(old_string → new_string)。用于小范围修改。",
        RequiresApproval = true,
        Parameters = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>
            {
                ["path"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "文件路径" },
                ["old_string"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "要被替换的原文(必须唯一)" },
                ["new_string"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "替换后的新文本" },
            },
            ["required"] = new[] { "path", "old_string", "new_string" },
        },
    };

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx)
    {
        var path = FileReadTool.Resolve(ctx, args, "path");
        var oldStr = args.TryGetProperty("old_string", out var o) ? o.GetString() : null;
        var newStr = args.TryGetProperty("new_string", out var n) ? n.GetString() : "";
        if (path == null || oldStr == null) return Task.FromResult(ToolResult.Fail("缺少参数"));

        try
        {
            if (!File.Exists(path)) return Task.FromResult(ToolResult.Fail($"文件不存在: {path}"));
            var text = File.ReadAllText(path);
            var count = CountOccurrences(text, oldStr);
            if (count == 0) return Task.FromResult(ToolResult.Fail($"old_string 未找到:\n{oldStr}"));
            if (count > 1) return Task.FromResult(ToolResult.Fail($"old_string 出现 {count} 次,不唯一,请扩大上下文:\n{oldStr}"));

            File.WriteAllText(path, text.Replace(oldStr, newStr));
            return Task.FromResult(ToolResult.Ok($"已编辑 {path}"));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail($"编辑失败: {ex.Message}"));
        }
    }

    private static int CountOccurrences(string text, string pattern)
    {
        var count = 0;
        var idx = 0;
        while ((idx = text.IndexOf(pattern, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += pattern.Length;
        }
        return count;
    }
}

/// <summary>str_replace_editor(Claude Code 风格组合编辑器:view/str_replace/create/insert)。</summary>
public sealed class StrReplaceEditorTool : ITool
{
    public ToolDefinition Definition => new()
    {
        Name = "str_replace_editor",
        Description = "文本编辑器工具。command 为 view(查看)/str_replace(替换)/create(创建)/insert(插入);view 用 path+view_range,str_replace 用 old_string+new_string,create 用 path+file_text,insert 用 insert_line+new_str。",
        RequiresApproval = true,
        Parameters = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>
            {
                ["command"] = new Dictionary<string, object?> { ["type"] = "string", ["enum"] = new[] { "view", "str_replace", "create", "insert" } },
                ["path"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["view_range"] = new Dictionary<string, object?> { ["type"] = "array", ["items"] = new Dictionary<string, object?> { ["type"] = "integer" } },
                ["old_string"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["new_string"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["file_text"] = new Dictionary<string, object?> { ["type"] = "string" },
                ["insert_line"] = new Dictionary<string, object?> { ["type"] = "integer" },
                ["new_str"] = new Dictionary<string, object?> { ["type"] = "string" },
            },
            ["required"] = new[] { "command", "path" },
        },
    };

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx)
    {
        var command = args.TryGetProperty("command", out var cmd) ? cmd.GetString() : "";
        var path = FileReadTool.Resolve(ctx, args, "path");
        if (path == null) return Task.FromResult(ToolResult.Fail("缺少参数: path"));

        try
        {
            switch (command)
            {
                case "view":
                {
                    if (!File.Exists(path)) return Task.FromResult(ToolResult.Fail($"文件不存在: {path}"));
                    var lines = File.ReadAllLines(path);
                    int start = 0, end = lines.Length;
                    if (args.TryGetProperty("view_range", out var vr) && vr.ValueKind == JsonValueKind.Array &&
                        vr.GetArrayLength() == 2)
                    {
                        start = Math.Max(0, vr[0].GetInt32() - 1);
                        end = Math.Min(lines.Length, vr[1].GetInt32());
                    }
                    var sb = new System.Text.StringBuilder();
                    for (var i = start; i < end; i++)
                        sb.AppendLine($"{i + 1,6}\t{lines[i]}");
                    return Task.FromResult(ToolResult.Ok(sb.ToString().TrimEnd()));
                }
                case "str_replace":
                {
                    var oldStr = args.TryGetProperty("old_string", out var os) ? os.GetString() : null;
                    var newStr = args.TryGetProperty("new_string", out var ns) ? ns.GetString() : "";
                    if (oldStr == null) return Task.FromResult(ToolResult.Fail("缺少 old_string"));
                    var text = File.ReadAllText(path);
                    var idx = text.IndexOf(oldStr, StringComparison.Ordinal);
                    if (idx < 0) return Task.FromResult(ToolResult.Fail("old_string 未找到"));
                    if (text.IndexOf(oldStr, idx + oldStr.Length, StringComparison.Ordinal) >= 0)
                        return Task.FromResult(ToolResult.Fail("old_string 不唯一"));
                    File.WriteAllText(path, text[..idx] + newStr + text[(idx + oldStr.Length)..]);
                    return Task.FromResult(ToolResult.Ok($"已替换 {path}"));
                }
                case "create":
                {
                    var text = args.TryGetProperty("file_text", out var ft) ? ft.GetString() : "";
                    if (File.Exists(path)) return Task.FromResult(ToolResult.Fail($"文件已存在: {path}"));
                    var dir = System.IO.Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                    File.WriteAllText(path, text);
                    return Task.FromResult(ToolResult.Ok($"已创建 {path}"));
                }
                case "insert":
                {
                    var line = args.TryGetProperty("insert_line", out var il) ? il.GetInt32() : 1;
                    var newStr = args.TryGetProperty("new_str", out var ns2) ? ns2.GetString() : "";
                    var lines = File.ReadAllLines(path).ToList();
                    lines.Insert(Math.Clamp(line - 1, 0, lines.Count), newStr);
                    File.WriteAllLines(path, lines);
                    return Task.FromResult(ToolResult.Ok($"已在第 {line} 行插入 {path}"));
                }
                default:
                    return Task.FromResult(ToolResult.Fail($"未知命令: {command}"));
            }
        }
        catch (Exception ex)
        {
            return Task.FromResult(ToolResult.Fail($"编辑失败: {ex.Message}"));
        }
    }
}
