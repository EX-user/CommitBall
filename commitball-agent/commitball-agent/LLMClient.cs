using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CommitBallAgent
{
    public class LLMResponse
    {
        public string Content { get; set; } = "";
        public List<ToolCall> ToolCalls { get; set; } = new();
    }

    static class LLMClient
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

        public static async Task<LLMResponse> ChatAsync(
            List<Message> messages,
            string? toolsJson = null,
            Action<string>? onToken = null,
            CancellationToken ct = default)
        {
            var url = $"{Config.BaseUrl}/v1/chat/completions";

            var msgList = new List<object>();
            foreach (var m in messages)
            {
                if (m.Role == "display") continue;
                msgList.Add(m.ToApiFormat());
            }

            var bodyDict = new Dictionary<string, object>
            {
                ["model"] = Config.Model,
                ["messages"] = msgList,
                ["stream"] = true
            };

            if (toolsJson != null)
            {
                var toolsDoc = JsonDocument.Parse(toolsJson);
                bodyDict["tools"] = toolsDoc.RootElement;
            }

            var reqJson = JsonSerializer.Serialize(bodyDict);
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Add("Authorization", $"Bearer {Config.ApiKey}");
            req.Content = new StringContent(reqJson, Encoding.UTF8, "application/json");

            using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();

            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            var content = new StringBuilder();
            var toolCallsMap = new Dictionary<int, ToolCall>();

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line == null) break;
                if (string.IsNullOrEmpty(line)) continue;
                if (!line.StartsWith("data: ")) continue;

                var data = line[6..];
                if (data == "[DONE]") break;

                try
                {
                    using var doc = JsonDocument.Parse(data);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("choices", out var choices)) continue;
                    if (choices.GetArrayLength() == 0) continue;

                    var delta = choices[0].GetProperty("delta");

                    if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                    {
                        var chunk = c.GetString() ?? "";
                        content.Append(chunk);
                        if (onToken != null) onToken(chunk);
                    }

                    if (delta.TryGetProperty("tool_calls", out var tcs))
                    {
                        foreach (var tc in tcs.EnumerateArray())
                        {
                            var idx = tc.TryGetProperty("index", out var idxEl) ? idxEl.GetInt32() : 0;
                            if (!toolCallsMap.TryGetValue(idx, out var call))
                            {
                                call = new ToolCall();
                                toolCallsMap[idx] = call;
                            }
                            if (tc.TryGetProperty("id", out var idEl))
                                call.Id = idEl.GetString() ?? "";
                            if (tc.TryGetProperty("function", out var fnEl))
                            {
                                if (fnEl.TryGetProperty("name", out var nameEl))
                                    call.Name += nameEl.GetString() ?? "";
                                if (fnEl.TryGetProperty("arguments", out var argEl))
                                    call.Arguments += argEl.GetString() ?? "";
                            }
                        }
                    }
                }
                catch (JsonException) { }
            }

            var result = new LLMResponse
            {
                Content = content.ToString(),
                ToolCalls = new List<ToolCall>(toolCallsMap.Values)
            };
            return result;
        }
    }
}
