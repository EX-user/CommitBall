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
        private static readonly HttpClient HttpDirect = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromMinutes(5) };

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
            try
            {
                var url = $"{baseUrl.TrimEnd('/')}/v1/models";
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Add("Authorization", $"Bearer {apiKey}");
                using var resp = await SendNoStreamAsync(req);
                if (!resp.IsSuccessStatusCode)
                    return (false, $"API 返回 {(int)resp.StatusCode}: {await resp.Content.ReadAsStringAsync()}");
                var body = await resp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("data", out var data))
                    return (false, "响应中无 data 字段");
                var found = false;
                foreach (var item in data.EnumerateArray())
                {
                    if (item.TryGetProperty("id", out var id) && id.GetString() == model)
                    { found = true; break; }
                }
                if (!found)
                    return (false, $"模型 '{model}' 不在可用列表中");
                return (true, "OK");
            }
            catch (Exception ex)
            {
                return (false, $"连接失败: {ex.Message}");
            }
        }

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

            using var resp = await SendAsync(req, ct).ConfigureAwait(false);
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
