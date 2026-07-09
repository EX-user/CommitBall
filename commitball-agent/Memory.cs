using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace CommitBallAgent
{
    public class ToolCall
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Arguments { get; set; } = "";
    }

    public class Message
    {
        public string Role { get; set; } = "";
        public string Content { get; set; } = "";
        public string? ReasoningContent { get; set; }
        public List<ToolCall>? ToolCalls { get; set; }
        public string? ToolCallId { get; set; }
        public string? DisplayType { get; set; }

        public object ToApiFormat()
        {
            if (Role == "display") return new { role = "display", content = Content };
            if (Role == "assistant" && ToolCalls != null && ToolCalls.Count > 0)
            {
                var tcArray = new JsonArray();
                foreach (var tc in ToolCalls)
                {
                    tcArray.Add(new JsonObject
                    {
                        ["id"] = tc.Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = tc.Name,
                            ["arguments"] = NormalizeToolArgumentsForApi(tc.Arguments)
                        }
                    });
                }
                return new { role = Role, content = (string?)null, tool_calls = tcArray };
            }
            if (Role == "tool")
            {
                return new { role = Role, content = Content, tool_call_id = ToolCallId };
            }
            if (Role == "assistant" && !string.IsNullOrEmpty(ReasoningContent))
            {
                return new { role = Role, reasoning_content = ReasoningContent, content = Content };
            }
            return new { role = Role, content = Content };
        }

        private static string NormalizeToolArgumentsForApi(string? arguments)
        {
            if (string.IsNullOrWhiteSpace(arguments))
                return "{}";

            try
            {
                var node = JsonNode.Parse(arguments);
                return node is JsonObject ? arguments : "{}";
            }
            catch
            {
                return "{}";
            }
        }
    }

    public class Session
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
        public List<Message> Messages { get; set; } = new();
        public string? ParentSessionId { get; set; }
        public string? Purpose { get; set; }
        public string? Title { get; set; }
        public string? TitleSource { get; set; }
        public DateTime? NamedAt { get; set; }
    }

    static class Memory
    {
        public const string PurposeBarCommand = "bar_command";
        private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

        public static string GetPath(string sessionId, bool isSubtask = false)
        {
            var dir = isSubtask
                ? Path.Combine(Config.MemoryDir, "subtasks")
                : Config.MemoryDir;
            return Path.Combine(dir, $"{sessionId}.json");
        }

        public static Session LoadOrCreate(string? sessionId = null)
        {
            Directory.CreateDirectory(Config.MemoryDir);

            if (sessionId != null)
            {
                var path = GetPath(sessionId);
                var subtaskPath = GetPath(sessionId, true);
                var loadPath = File.Exists(path) ? path : (File.Exists(subtaskPath) ? subtaskPath : null);
                if (loadPath != null)
                {
                    var json = File.ReadAllText(loadPath);
                    return JsonSerializer.Deserialize<Session>(json, JsonOpts) ?? new Session();
                }
            }

            var session = new Session();
            Save(session);
            return session;
        }

        public static Session CreateNew(string? purpose = null)
        {
            Directory.CreateDirectory(Config.MemoryDir);
            return new Session { Purpose = purpose };
        }

        public static void Save(Session session)
        {
            if (session.Messages.Count == 0) return;
            var isSubtask = !string.IsNullOrEmpty(session.ParentSessionId);
            var dir = isSubtask
                ? Path.Combine(Config.MemoryDir, "subtasks")
                : Config.MemoryDir;
            Directory.CreateDirectory(dir);
            session.UpdatedAt = DateTime.Now;
            var json = JsonSerializer.Serialize(session, JsonOpts);
            File.WriteAllText(GetPath(session.Id, isSubtask), json);
        }

        public static Task EnsureNamedAsync(Session session)
        {
            if (session == null) return Task.CompletedTask;
            if (!string.IsNullOrWhiteSpace(session.ParentSessionId)) return Task.CompletedTask;
            if (session.Messages.Count == 0) return Task.CompletedTask;
            if (!string.IsNullOrWhiteSpace(session.Title)) return Task.CompletedTask;

            RenameSession(session, BuildFallbackTitle(session), "fallback");
            return Task.CompletedTask;
        }

        public static string RenameSession(Session session, string title, string source = "agent")
        {
            if (session == null) return "Error: session is required";
            if (!string.IsNullOrWhiteSpace(session.ParentSessionId))
                return "Error: subtask sessions cannot be renamed";

            var clean = CleanTitle(title);
            if (string.IsNullOrWhiteSpace(clean))
                return "Error: title is required";

            session.Title = MakeUniqueTitle(clean, session.Id);
            session.TitleSource = source;
            session.NamedAt = DateTime.Now;
            Save(session);
            AgentWindow.Log($"Session renamed: {session.Id} title={session.Title} source={source}");
            return $"Session renamed: {session.Title}";
        }

        public static bool IsBarCommandSession(Session session)
        {
            return string.Equals(session.Purpose, PurposeBarCommand, StringComparison.OrdinalIgnoreCase);
        }

        public static Session? LoadLatestBarCommandSession()
        {
            Directory.CreateDirectory(Config.MemoryDir);
            Session? latest = null;
            foreach (var file in Directory.GetFiles(Config.MemoryDir, "*.json"))
            {
                try
                {
                    var session = JsonSerializer.Deserialize<Session>(File.ReadAllText(file), JsonOpts);
                    if (session == null || !string.IsNullOrEmpty(session.ParentSessionId)) continue;
                    if (!IsBarCommandSession(session)) continue;
                    if (latest == null || session.UpdatedAt > latest.UpdatedAt)
                        latest = session;
                }
                catch { }
            }
            return latest;
        }

        public static List<(string Id, DateTime UpdatedAt, int MsgCount, string Title)> ListSessions(bool includeBarCommand = true)
        {
            Directory.CreateDirectory(Config.MemoryDir);
            var result = new List<(string, DateTime, int, string)>();
            foreach (var file in Directory.GetFiles(Config.MemoryDir, "*.json"))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var s = JsonSerializer.Deserialize<Session>(json, JsonOpts);
                    if (s != null && string.IsNullOrEmpty(s.ParentSessionId))
                    {
                        if (!includeBarCommand && IsBarCommandSession(s)) continue;
                        result.Add((s.Id, s.UpdatedAt, s.Messages.Count, s.Title ?? ""));
                    }
                }
                catch { }
            }
            result.Sort((a, b) => b.Item2.CompareTo(a.Item2));
            return result;
        }

        private static string BuildFallbackTitle(Session session)
        {
            foreach (var msg in session.Messages)
            {
                if (msg.Role != "user" && msg.Role != "assistant") continue;
                var text = CleanTitle(msg.Content);
                if (!string.IsNullOrWhiteSpace(text))
                    return text.Length <= 20 ? text : text[..20];
            }
            return $"会话 {session.Id}";
        }

        private static string CleanTitle(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            var title = NormalizeWhitespace(text);
            title = title.Trim(' ', '\t', '\r', '\n', '"', '\'', '“', '”', '‘', '’', '`', '#', '-', '*');
            title = Regex.Replace(title, @"^(标题|会话标题)\s*[:：]\s*", "", RegexOptions.IgnoreCase);
            if (title.Contains('\n')) title = title.Split('\n')[0];
            if (title.Length > 40) title = title[..40];
            return title.Trim();
        }

        private static string NormalizeWhitespace(string text)
        {
            return Regex.Replace(text, @"\s+", " ").Trim();
        }

        private static string MakeUniqueTitle(string title, string sessionId)
        {
            var clean = string.IsNullOrWhiteSpace(title) ? $"会话 {sessionId}" : title.Trim();
            var exists = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Directory.CreateDirectory(Config.MemoryDir);
            foreach (var file in Directory.GetFiles(Config.MemoryDir, "*.json"))
            {
                try
                {
                    var s = JsonSerializer.Deserialize<Session>(File.ReadAllText(file), JsonOpts);
                    if (s == null || s.Id == sessionId || string.IsNullOrWhiteSpace(s.Title)) continue;
                    exists.Add(s.Title);
                }
                catch { }
            }
            if (!exists.Contains(clean)) return clean;
            var rnd = new Random();
            string candidate;
            do
            {
                candidate = $"{clean}-{rnd.Next(1000, 10000)}";
            } while (exists.Contains(candidate));
            return candidate;
        }
    }
}
