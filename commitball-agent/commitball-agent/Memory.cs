using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

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

        public static List<(string Id, DateTime UpdatedAt, int MsgCount)> ListSessions()
        {
            Directory.CreateDirectory(Config.MemoryDir);
            var result = new List<(string, DateTime, int)>();
            foreach (var file in Directory.GetFiles(Config.MemoryDir, "*.json"))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var s = JsonSerializer.Deserialize<Session>(json, JsonOpts);
                    if (s != null && string.IsNullOrEmpty(s.ParentSessionId))
                        result.Add((s.Id, s.UpdatedAt, s.Messages.Count));
                }
                catch { }
            }
            result.Sort((a, b) => b.Item2.CompareTo(a.Item2));
            return result;
        }
    }
}
