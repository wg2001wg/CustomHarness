using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeepSeekHarness.Core.Persistence;

using DeepSeekHarness.Core.Session;

/// <summary>
/// JSONL 会话持久化(对齐参考项目 dsh-session-persistence-jsonl)。
/// 追加式写入事件日志,支持会话重放。
/// </summary>
public sealed class JsonlSessionStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _root;

    public JsonlSessionStore(string root)
    {
        _root = root;
        Directory.CreateDirectory(root);
    }

    public string Root => _root;

    private string PathFor(string sessionId) => System.IO.Path.Combine(_root, sessionId + SessionFormat.JsonlExtension);

    private sealed record PersistedEvent(int Seq, string Type, string Json, DateTimeOffset Time, bool Ignorable);

    /// <summary>持久化一个会话(全量写入,含 header 首行)。</summary>
    public void Save(Session session)
    {
        var path = PathFor(session.Header.Id);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var lines = new List<string>();
        lines.Add(JsonSerializer.Serialize(new { header = session.Header }, JsonOpts));
        foreach (var evt in session.Events())
        {
            var evtObj = new
            {
                type = evt.Type.ToString(),
                seq = evt.Seq,
                time = evt.Time,
                data = evt.Data,
                ignorable = evt.Ignorable,
            };
            lines.Add(JsonSerializer.Serialize(evtObj, JsonOpts));
        }
        File.WriteAllLines(path, lines);
    }

    /// <summary>加载会话(不存在返回 null;单行损坏自动跳过,不抛异常)。</summary>
    public Session? Load(string sessionId)
    {
        var path = PathFor(sessionId);
        if (!File.Exists(path)) return null;
        Session? session = null;
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                // 容错:跳过损坏行,不拖垮初始化
                continue;
            }
            using (doc)
            {
                var root = doc.RootElement;
                if (session == null && root.TryGetProperty("header", out var headerEl))
                {
                    try
                    {
                        var header = JsonSerializer.Deserialize<SessionHeader>(headerEl.GetRawText(), JsonOpts);
                        if (header != null)
                        {
                            session = new Session(header);
                            continue;
                        }
                    }
                    catch (JsonException)
                    {
                        continue;
                    }
                }
                if (session == null) continue;
                if (root.TryGetProperty("type", out var typeEl) &&
                    Enum.TryParse<SessionEventType>(typeEl.GetString(), out var type))
                {
                    object? data = null;
                    try
                    {
                        if (root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind != JsonValueKind.Null)
                        {
                            data = DeserializeData(type, dataEl.GetRawText());
                        }
                    }
                    catch (JsonException)
                    {
                        data = null;
                    }
                    var evt = session.Append(type, data);
                    // 从文件恢复 seq,保持单调
                    if (root.TryGetProperty("seq", out var seqEl) && seqEl.ValueKind == JsonValueKind.Number)
                    {
                        session.RestoreSeq(seqEl.GetInt64() + 1);
                    }
                }
            }
        }
        session ??= new Session();
        return session;
    }

    private static object? DeserializeData(SessionEventType type, string json) => type switch
    {
        SessionEventType.UserMessage or SessionEventType.AssistantMessage
            => JsonSerializer.Deserialize<Message>(json, JsonOpts),
        SessionEventType.ToolResult => JsonSerializer.Deserialize<ToolResultData>(json, JsonOpts),
        _ => JsonSerializer.Deserialize<JsonElement>(json, JsonOpts),
    };

    /// <summary>列出全部会话文件。</summary>
    public IEnumerable<string> ListSessionIds()
        => Directory.Exists(_root)
            ? Directory.GetFiles(_root, "*.jsonl").Select(System.IO.Path.GetFileNameWithoutExtension)
            : Array.Empty<string>();
}
