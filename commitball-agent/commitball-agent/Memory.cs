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
                            ["arguments"] = tc.Arguments
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
    }

    public class Session
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
        public List<Message> Messages { get; set; } = new();
        public string? ParentSessionId { get; set; }
        public string? Title { get; set; }
        public string? TitleSource { get; set; }
        public DateTime? NamedAt { get; set; }
    }

    static class Memory
    {
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

        public static async Task EnsureNamedAsync(Session session)
        {
            if (session == null) return;
            if (!string.IsNullOrWhiteSpace(session.ParentSessionId)) return;
            if (session.Messages.Count == 0) return;
            if (!string.IsNullOrWhiteSpace(session.Title)) return;

            var fallback = BuildFallbackTitle(session);
            var title = fallback;
            var source = "fallback";

            if (Config.IsConfigured)
            {
                try
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    var transcript = BuildTranscriptForNaming(session, 3600);
                    if (!string.IsNullOrWhiteSpace(transcript))
                    {
                        var messages = new List<Message>
                        {
                            new Message { Role = "system", Content = "你只负责给对话命名。输出一个20字左右的中文标题，不要解释，不要加引号，不要使用Markdown。" },
                            new Message { Role = "user", Content = "请总结下面 CommitBall Agent 对话的主要内容，并输出20字左右标题：\n\n" + transcript }
                        };
                        var resp = await LLMClient.ChatAsync(messages, toolsJson: null, onToken: null, ct: cts.Token);
                        var candidate = CleanTitle(resp.Content);
                        if (!string.IsNullOrWhiteSpace(candidate) && !candidate.StartsWith("[API Error", StringComparison.OrdinalIgnoreCase))
                        {
                            title = candidate;
                            source = "agent";
                        }
                    }
                }
                catch (Exception ex)
                {
                    AgentWindow.Log($"EnsureNamedAsync fallback for {session.Id}: {ex.Message}");
                }
            }

            session.Title = MakeUniqueTitle(title, session.Id);
            session.TitleSource = source;
            session.NamedAt = DateTime.Now;
            Save(session);
            AgentWindow.Log($"Session named: {session.Id} title={session.Title} source={source}");
        }

        public static List<(string Id, DateTime UpdatedAt, int MsgCount, string Title)> ListSessions()
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
                        result.Add((s.Id, s.UpdatedAt, s.Messages.Count, s.Title ?? ""));
                }
                catch { }
            }
            result.Sort((a, b) => b.Item2.CompareTo(a.Item2));
            return result;
        }

        private static string BuildTranscriptForNaming(Session session, int maxLen)
        {
            var parts = new List<string>();
            foreach (var msg in session.Messages)
            {
                if (msg.Role != "user" && msg.Role != "assistant") continue;
                if (string.IsNullOrWhiteSpace(msg.Content)) continue;
                var content = NormalizeWhitespace(msg.Content);
                if (content.Length == 0) continue;
                parts.Add($"{msg.Role}: {content}");
                var joined = string.Join("\n", parts);
                if (joined.Length >= maxLen) return joined[..maxLen];
            }
            return string.Join("\n", parts);
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
