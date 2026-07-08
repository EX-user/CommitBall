using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        public int PromptTokens { get; set; }
        public int CompletionTokens { get; set; }
        public long ElapsedMs { get; set; }
    }

    public class LLMException : Exception
    {
        public LLMException(string message) : base(message) { }
    }

    static class LLMClient
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
        private static readonly HttpClient HttpDirect = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromMinutes(5) };

        private static string NormalizeBaseUrl(string url)
        {
            return url.TrimEnd('/');
        }

        private static bool IsProxyError(Exception ex)
        {
            var msg = ex.InnerException?.Message ?? ex.Message;
            return msg.Contains("拒绝") || msg.Contains("refused") || msg.Contains("proxy") || msg.Contains("隧道");
        }

        private static async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            try
            {
                return await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (HttpRequestException ex) when (IsProxyError(ex))
            {
                var clone = await CloneRequest(req);
                return await HttpDirect.SendAsync(clone, HttpCompletionOption.ResponseHeadersRead, ct);
            }
        }

        private static async Task<HttpResponseMessage> SendNoStreamAsync(HttpRequestMessage req)
        {
            try
            {
                return await Http.SendAsync(req);
            }
            catch (HttpRequestException ex) when (IsProxyError(ex))
            {
                var clone = await CloneRequest(req);
                return await HttpDirect.SendAsync(clone);
            }
        }

        private static async Task<HttpRequestMessage> CloneRequest(HttpRequestMessage req)
        {
            var clone = new HttpRequestMessage(req.Method, req.RequestUri);
            foreach (var h in req.Headers) clone.Headers.TryAddWithoutValidation(h.Key, h.Value);
            if (req.Content != null) clone.Content = new ByteArrayContent(await req.Content.ReadAsByteArrayAsync());
            return clone;
        }

        public static async Task<(bool ok, string msg)> ValidateAsync(string baseUrl, string model, string apiKey)
        {
            var modelsFailure = "";
            try
            {
                var url = $"{NormalizeBaseUrl(baseUrl)}/models";
                AgentWindow.Log($"ValidateAsync: requesting {url}");
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Add("Authorization", $"Bearer {apiKey}");
                using var resp = await SendNoStreamAsync(req);
                AgentWindow.Log($"ValidateAsync: status={resp.StatusCode}");
                if (!resp.IsSuccessStatusCode)
                {
                    modelsFailure = $"GET /models 返回 {(int)resp.StatusCode}: {CleanErrorBody(await resp.Content.ReadAsStringAsync())}";
                }
                else
                {
                    var body = await resp.Content.ReadAsStringAsync();
                    AgentWindow.Log($"ValidateAsync: body len={body.Length}");
                    using var doc = JsonDocument.Parse(body);
                    if (!doc.RootElement.TryGetProperty("data", out var data))
                    {
                        modelsFailure = "GET /models 响应中无 data 字段";
                    }
                    else
                    {
                        var found = false;
                        foreach (var item in data.EnumerateArray())
                        {
                            if (item.TryGetProperty("id", out var id) && id.GetString() == model)
                            { found = true; break; }
                        }
                        if (found)
                            return (true, "OK");
                        modelsFailure = $"GET /models 成功，但模型 '{model}' 不在可用列表中";
                    }
                }
            }
            catch (Exception ex)
            {
                modelsFailure = $"GET /models 失败: {ex.Message}";
            }

            AgentWindow.Log($"ValidateAsync: /models incompatible, fallback to chat. reason={modelsFailure}");
            try
            {
                var url = $"{NormalizeBaseUrl(baseUrl)}/chat/completions";
                using var req = new HttpRequestMessage(HttpMethod.Post, url);
                req.Headers.Add("Authorization", $"Bearer {apiKey}");
                var body = JsonSerializer.Serialize(new
                {
                    model,
                    messages = new object[] { new { role = "user", content = "ping" } },
                    stream = false,
                    max_tokens = 1
                });
                req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                using var resp = await SendNoStreamAsync(req);
                var respBody = await resp.Content.ReadAsStringAsync();
                AgentWindow.Log($"ValidateAsync fallback chat: status={resp.StatusCode}, body len={respBody.Length}");
                if (!resp.IsSuccessStatusCode)
                    return (false, $"校验失败。\n  {modelsFailure}\n  POST /chat/completions 返回 {(int)resp.StatusCode}: {CleanErrorBody(respBody)}");

                using var doc = JsonDocument.Parse(respBody);
                var apiError = ExtractApiError(doc.RootElement);
                if (!string.IsNullOrWhiteSpace(apiError))
                    return (false, $"校验失败。\n  {modelsFailure}\n  POST /chat/completions error: {CleanErrorBody(apiError)}");
                if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                    return (false, $"校验失败。\n  {modelsFailure}\n  POST /chat/completions 响应中无 choices");

                return (true, "OK (chat fallback)");
            }
            catch (Exception ex)
            {
                return (false, $"校验失败。\n  {modelsFailure}\n  POST /chat/completions 失败: {ex.Message}");
            }
        }

        private static string CleanErrorBody(string? body, int maxLen = 1200)
        {
            if (string.IsNullOrWhiteSpace(body)) return "(empty response)";
            var s = body.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ").Trim();
            while (s.Contains("  ")) s = s.Replace("  ", " ");
            return Truncate(s, maxLen);
        }

        private static string ExtractApiError(JsonElement root)
        {
            if (!root.TryGetProperty("error", out var err))
                return "";

            if (err.ValueKind == JsonValueKind.String)
                return err.GetString() ?? "";

            if (err.ValueKind == JsonValueKind.Object)
            {
                var message = err.TryGetProperty("message", out var msgEl) ? msgEl.GetString() : null;
                var code = err.TryGetProperty("code", out var codeEl) ? codeEl.ToString() : null;
                var type = err.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
                var parts = new List<string>();
                if (!string.IsNullOrWhiteSpace(code)) parts.Add($"code={code}");
                if (!string.IsNullOrWhiteSpace(type)) parts.Add($"type={type}");
                if (!string.IsNullOrWhiteSpace(message)) parts.Add(message!);
                return parts.Count > 0 ? string.Join(", ", parts) : err.ToString();
            }

            return err.ToString();
        }

        private static void ThrowApiError(int statusCode, string body)
        {
            var detail = CleanErrorBody(body);
            try
            {
                using var doc = JsonDocument.Parse(body);
                var apiError = ExtractApiError(doc.RootElement);
                if (!string.IsNullOrWhiteSpace(apiError))
                    detail = CleanErrorBody(apiError);
            }
            catch { }
            throw new LLMException($"API Error {statusCode}: {detail}");
        }

        private static void ThrowStreamErrorIfAny(JsonElement root)
        {
            var apiError = ExtractApiError(root);
            if (!string.IsNullOrWhiteSpace(apiError))
                throw new LLMException($"API Stream Error: {CleanErrorBody(apiError)}");
        }

        public static async Task<LLMResponse> ChatAsync(
            List<Message> messages,
            string? toolsJson = null,
            Action<string>? onToken = null,
            CancellationToken ct = default)
        {
            var url = $"{NormalizeBaseUrl(Config.BaseUrl)}/chat/completions";

            var sanitizedMessages = SanitizeMessagesForApi(messages, out var sanitizeFixes);
            var msgList = new List<object>();
            foreach (var m in sanitizedMessages)
                msgList.Add(m.ToApiFormat());
            if (sanitizeFixes > 0)
                AgentWindow.Log($"ChatAsync: sanitized messages {messages.Count}->{sanitizedMessages.Count}, fixes={sanitizeFixes}");

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
            AgentWindow.Log($"ChatAsync: sending {msgList.Count} msgs, {reqJson.Length} chars");
            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Headers.Add("Authorization", $"Bearer {Config.ApiKey}");
            req.Content = new StringContent(reqJson, Encoding.UTF8, "application/json");

            using var resp = await SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var errBody = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                AgentWindow.Log($"ChatAsync: HTTP {(int)resp.StatusCode} - {errBody}");
                ThrowApiError((int)resp.StatusCode, errBody);
            }

            var sw = Stopwatch.StartNew();
            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            var content = new StringBuilder();
            var toolCallsMap = new Dictionary<int, ToolCall>();
            int promptTokens = 0, completionTokens = 0;

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
                    ThrowStreamErrorIfAny(root);
                    if (!root.TryGetProperty("choices", out var choices)) continue;
                    if (choices.GetArrayLength() == 0) continue;

                    var delta = choices[0];
                    if (delta.TryGetProperty("delta", out var deltaEl))
                        delta = deltaEl;

                    if (delta.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String)
                    {
                        var chunk = c.GetString() ?? "";
                        content.Append(chunk);
                        if (onToken != null) onToken(chunk);
                    }

                    if (delta.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == System.Text.Json.JsonValueKind.Array)
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
                catch (LLMException) { throw; }
                catch (Exception ex) { AgentWindow.Log($"ChatAsync: stream parse error: {ex.Message}"); continue; }

                try
                {
                    var uDoc = JsonDocument.Parse(data);
                    if (uDoc.RootElement.TryGetProperty("usage", out var usage))
                    {
                        if (usage.TryGetProperty("prompt_tokens", out var pt)) promptTokens = pt.GetInt32();
                        if (usage.TryGetProperty("completion_tokens", out var ct2)) completionTokens = ct2.GetInt32();
                    }
                }
                catch { }
            }

            sw.Stop();
            var result = new LLMResponse
            {
                Content = content.ToString(),
                ToolCalls = new List<ToolCall>(toolCallsMap.Values),
                PromptTokens = promptTokens,
                CompletionTokens = completionTokens,
                ElapsedMs = sw.ElapsedMilliseconds
            };
            return result;
        }

        private static List<Message> SanitizeMessagesForApi(List<Message> messages, out int fixes)
        {
            fixes = 0;
            var result = new List<Message>();
            var pendingToolCallIds = new List<string>();
            string? firstSystemContent = null;

            foreach (var message in messages)
            {
                var role = NormalizeRole(message.Role);
                if (role == "display")
                {
                    continue;
                }

                if (role == "system")
                {
                    if (firstSystemContent == null)
                        firstSystemContent = message.Content ?? "";
                    else
                        fixes++;
                    continue;
                }

                if (pendingToolCallIds.Count > 0 && role != "tool")
                {
                    AppendCancelledToolResults(result, pendingToolCallIds);
                    fixes += pendingToolCallIds.Count;
                    pendingToolCallIds.Clear();
                }

                if (role == "tool")
                {
                    var toolCallId = message.ToolCallId ?? "";
                    if (RemovePendingToolCallId(pendingToolCallIds, toolCallId))
                    {
                        result.Add(new Message
                        {
                            Role = "tool",
                            ToolCallId = toolCallId,
                            Content = message.Content ?? ""
                        });
                    }
                    else
                    {
                        fixes++;
                    }
                    continue;
                }

                if (role == "assistant")
                {
                    var toolCalls = SanitizeToolCallsForApi(message.ToolCalls, ref fixes);
                    if (toolCalls.Count > 0)
                    {
                        result.Add(new Message
                        {
                            Role = "assistant",
                            ToolCalls = toolCalls
                        });
                        foreach (var toolCall in toolCalls)
                            pendingToolCallIds.Add(toolCall.Id);
                    }
                    else if (!string.IsNullOrEmpty(message.Content))
                    {
                        result.Add(new Message
                        {
                            Role = "assistant",
                            Content = message.Content ?? ""
                        });
                    }
                    else
                    {
                        fixes++;
                    }
                    continue;
                }

                if (!string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase))
                    fixes++;
                result.Add(new Message
                {
                    Role = "user",
                    Content = message.Content ?? ""
                });
            }

            if (pendingToolCallIds.Count > 0)
            {
                AppendCancelledToolResults(result, pendingToolCallIds);
                fixes += pendingToolCallIds.Count;
            }

            if (firstSystemContent != null)
                result.Insert(0, new Message { Role = "system", Content = firstSystemContent });

            return result;
        }

        private static string NormalizeRole(string? role)
        {
            if (string.IsNullOrWhiteSpace(role))
                return "user";
            var normalized = role.Trim().ToLowerInvariant();
            return normalized switch
            {
                "system" => "system",
                "user" => "user",
                "assistant" => "assistant",
                "tool" => "tool",
                "display" => "display",
                _ => "user"
            };
        }

        private static List<ToolCall> SanitizeToolCallsForApi(List<ToolCall>? toolCalls, ref int fixes)
        {
            var result = new List<ToolCall>();
            if (toolCalls == null || toolCalls.Count == 0)
                return result;

            var seenIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var toolCall in toolCalls)
            {
                var id = toolCall.Id?.Trim() ?? "";
                var name = toolCall.Name?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) || !seenIds.Add(id))
                {
                    fixes++;
                    continue;
                }

                var arguments = NormalizeToolArgumentsForApi(toolCall.Arguments, ref fixes);
                result.Add(new ToolCall
                {
                    Id = id,
                    Name = name,
                    Arguments = arguments
                });
            }
            return result;
        }

        private static string NormalizeToolArgumentsForApi(string? arguments, ref int fixes)
        {
            if (string.IsNullOrWhiteSpace(arguments))
            {
                fixes++;
                return "{}";
            }

            try
            {
                using var doc = JsonDocument.Parse(arguments);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    return arguments;
            }
            catch { }

            fixes++;
            return "{}";
        }

        private static bool RemovePendingToolCallId(List<string> pendingToolCallIds, string toolCallId)
        {
            if (string.IsNullOrWhiteSpace(toolCallId))
                return false;
            for (var i = 0; i < pendingToolCallIds.Count; i++)
            {
                if (pendingToolCallIds[i] == toolCallId)
                {
                    pendingToolCallIds.RemoveAt(i);
                    return true;
                }
            }
            return false;
        }

        private static void AppendCancelledToolResults(List<Message> messages, List<string> pendingToolCallIds)
        {
            foreach (var toolCallId in pendingToolCallIds)
            {
                messages.Add(new Message
                {
                    Role = "tool",
                    ToolCallId = toolCallId,
                    Content = "[cancelled]"
                });
            }
        }

        private static string Truncate(string? s, int maxLen)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length <= maxLen ? s : s[..maxLen] + "...";
        }
    }
}
