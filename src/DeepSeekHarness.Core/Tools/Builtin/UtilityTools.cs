using System.Text.Json;

namespace DeepSeekHarness.Core.Tools.Builtin;

/// <summary>ask_user_question 工具(向用户提问并等待回答)。</summary>
public sealed class AskUserTool : ITool
{
    public ToolDefinition Definition => new()
    {
        Name = "ask_user_question",
        Description = "向用户提出一个问题,等待用户的回答。当需要澄清需求、确认决策或获取关键信息时使用。返回用户的回答文本。",
        RequiresApproval = false,
        Parameters = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>
            {
                ["question"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "要问用户的问题" },
                ["options"] = new Dictionary<string, object?>
                {
                    ["type"] = "array",
                    ["items"] = new Dictionary<string, object?> { ["type"] = "string" },
                    ["description"] = "可选,预置选项列表",
                },
            },
            ["required"] = new[] { "question" },
        },
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx)
    {
        var question = args.TryGetProperty("question", out var q) ? q.GetString() : null;
        if (string.IsNullOrEmpty(question)) return ToolResult.Fail("缺少参数: question");
        if (ctx.AskUser == null) return ToolResult.Fail("当前环境不支持向用户提问");

        var answer = await ctx.AskUser(question, args.GetRawText());
        return ToolResult.Ok(answer ?? "(用户未回答)");
    }
}

/// <summary>todo_write 工具(记录待办清单,对齐参考项目 todo 插件)。</summary>
public sealed class TodoWriteTool : ITool
{
    private readonly Action<string, List<TodoItem>>? _onWrite;

    public TodoWriteTool(Action<string, List<TodoItem>>? onWrite = null) => _onWrite = onWrite;

    public sealed class TodoItem
    {
        public string Content { get; set; } = "";
        public bool Completed { get; set; }
        public string? Status { get; set; }
        public string? Id { get; set; }
    }

    public ToolDefinition Definition => new()
    {
        Name = "todo_write",
        Description = "将任务拆解为待办清单写入会话,便于跟踪多步工作。参数 todos 为待办数组,每个含 content(内容)与可选 completed/status。",
        Parameters = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>
            {
                ["todos"] = new Dictionary<string, object?>
                {
                    ["type"] = "array",
                    ["items"] = new Dictionary<string, object?>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object?>
                        {
                            ["content"] = new Dictionary<string, object?> { ["type"] = "string" },
                            ["completed"] = new Dictionary<string, object?> { ["type"] = "boolean" },
                            ["status"] = new Dictionary<string, object?> { ["type"] = "string" },
                        },
                        ["required"] = new[] { "content" },
                    },
                },
            },
            ["required"] = new[] { "todos" },
        },
    };

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx)
    {
        var items = new List<TodoItem>();
        if (args.TryGetProperty("todos", out var todos) && todos.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in todos.EnumerateArray())
            {
                items.Add(new TodoItem
                {
                    Content = t.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "",
                    Completed = t.TryGetProperty("completed", out var d) && d.ValueKind == JsonValueKind.True,
                    Status = t.TryGetProperty("status", out var s) ? s.GetString() : null,
                    Id = t.TryGetProperty("id", out var i) ? i.GetString() : null,
                });
            }
        }
        _onWrite?.Invoke(ctx.SessionId ?? "", items);
        return Task.FromResult(ToolResult.Ok($"已记录 {items.Count} 项待办"));
    }
}

/// <summary>web_search 工具(调用外部搜索,可注入实现)。</summary>
public sealed class WebSearchTool : ITool
{
    private readonly Func<string, int, Task<string>>? _searchImpl;
    private readonly Action<string, string>? _log;

    public WebSearchTool(Func<string, int, Task<string>>? searchImpl = null, Action<string, string>? log = null)
    {
        _searchImpl = searchImpl;
        _log = log;
    }

    public ToolDefinition Definition => new()
    {
        Name = "web_search",
        Description = "搜索互联网获取最新信息,返回搜索结果摘要。参数 query 为搜索关键词,count 为可选结果数量(默认 5)。",
        Parameters = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>
            {
                ["query"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "搜索关键词" },
                ["count"] = new Dictionary<string, object?> { ["type"] = "integer", ["description"] = "结果数量,默认 5" },
            },
            ["required"] = new[] { "query" },
        },
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx)
    {
        var query = args.TryGetProperty("query", out var q) ? q.GetString() : null;
        if (string.IsNullOrEmpty(query)) return ToolResult.Fail("缺少参数: query");
        var count = args.TryGetProperty("count", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 5;

        if (_searchImpl == null)
            return ToolResult.Fail("web_search 未配置搜索实现(需要网络或插件)");

        try
        {
            _log?.Invoke(ctx.SessionId ?? "", $"搜索: {query}");
            var result = await _searchImpl(query, count);
            return ToolResult.Ok(result);
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"搜索失败: {ex.Message}");
        }
    }
}

/// <summary>web_fetch 工具(抓取网页内容)。</summary>
public sealed class WebFetchTool : ITool
{
    private readonly Func<string, Task<string>>? _fetchImpl;
    private readonly Action<string, string>? _log;

    public WebFetchTool(Func<string, Task<string>>? fetchImpl = null, Action<string, string>? log = null)
    {
        _fetchImpl = fetchImpl;
        _log = log;
    }

    public ToolDefinition Definition => new()
    {
        Name = "web_fetch",
        Description = "抓取指定 URL 的网页内容并转换为可读文本。参数 url 为目标网址。",
        Parameters = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?>
            {
                ["url"] = new Dictionary<string, object?> { ["type"] = "string", ["description"] = "要抓取的 URL" },
            },
            ["required"] = new[] { "url" },
        },
    };

    public async Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx)
    {
        var url = args.TryGetProperty("url", out var u) ? u.GetString() : null;
        if (string.IsNullOrEmpty(url)) return ToolResult.Fail("缺少参数: url");
        if (_fetchImpl == null) return ToolResult.Fail("web_fetch 未配置实现");

        try
        {
            _log?.Invoke(ctx.SessionId ?? "", $"抓取: {url}");
            var result = await _fetchImpl(url);
            return ToolResult.Ok(result);
        }
        catch (Exception ex)
        {
            return ToolResult.Fail($"抓取失败: {ex.Message}");
        }
    }
}

/// <summary>session 工具(读取当前会话信息)。</summary>
public sealed class SessionInfoTool : ITool
{
    private readonly Func<string>? _sessionTitle;

    public SessionInfoTool(Func<string>? sessionTitle = null) => _sessionTitle = sessionTitle;

    public ToolDefinition Definition => new()
    {
        Name = "session_info",
        Description = "获取当前会话信息(会话 ID、工作目录、标题等)。",
        Parameters = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = new Dictionary<string, object?> { },
            ["required"] = Array.Empty<string>(),
        },
    };

    public Task<ToolResult> ExecuteAsync(JsonElement args, ToolContext ctx)
    {
        var info = $"session_id: {ctx.SessionId}\nworkdir: {ctx.WorkingDirectory}\npermission: {ctx.Permission}";
        return Task.FromResult(ToolResult.Ok(info));
    }
}
