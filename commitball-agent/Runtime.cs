using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace CommitBallAgent
{
    static class Runtime
    {
        public static async Task RunAsync(
            Session session,
            string userInput,
            Action<string> onOutput,
            Action<string> onToolStart,
            Action<string> onToolDone,
            Action<string> onToolError,
            Action<string?> onSubtaskProgress,
            CancellationToken ct,
            bool isSubtask = false,
            Action<int, int>? onUsage = null)
        {
            session.Messages.Add(new Message { Role = "user", Content = userInput });

            var toolsJson = Tools.GetToolDefinitionsJson(includeSubtask: !isSubtask);
            var systemPrompt = Tools.GetSystemPrompt(isSubtask);
            var systemAdded = false;

            for (int i = 0; ; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (!systemAdded)
                {
                    if (session.Messages.Count > 0 && session.Messages[0].Role == "system")
                        session.Messages[0].Content = systemPrompt;
                    else
                        session.Messages.Insert(0, new Message { Role = "system", Content = systemPrompt });
                    systemAdded = true;
                }

                var resp = await LLMClient.ChatAsync(
                    session.Messages,
                    toolsJson,
                    onToken: chunk =>
                    {
                        if (isSubtask) onSubtaskProgress(chunk);
                        else onOutput(chunk);
                    },
                    ct: ct).ConfigureAwait(false);

                var toolNames = resp.ToolCalls.Count > 0 ? $" [{string.Join(", ", resp.ToolCalls.ConvertAll(tc => tc.Name))}]" : "";
                AgentWindow.Log($"[{session.Id}] LLM #{i}: {resp.ElapsedMs}ms, tokens={resp.PromptTokens}+{resp.CompletionTokens}, toolCalls={resp.ToolCalls.Count}{toolNames}, msgs={session.Messages.Count}");
                onUsage?.Invoke(resp.PromptTokens, resp.CompletionTokens);

                if (resp.ToolCalls.Count > 0)
                {
                    var assistantMsg = new Message
                    {
                        Role = "assistant",
                        ToolCalls = resp.ToolCalls
                    };
                    session.Messages.Add(assistantMsg);

                    foreach (var tc in resp.ToolCalls)
                    {
                        var argsStr = string.IsNullOrWhiteSpace(tc.Arguments) ? "{}" : tc.Arguments;

                        if (Tools.IsSubtask(tc.Name))
                        {
                            if (!isSubtask)
                            {
                                var prompt = "";
                                try
                                {
                                    var args = JsonNode.Parse(argsStr)?.AsObject();
                                    prompt = args?["prompt"]?.GetValue<string>() ?? "";
                                }
                                catch (Exception ex) { AgentWindow.Log($"Subtask parse error: {ex.Message}, raw args: {Truncate(argsStr, 200)}"); }

                                if (string.IsNullOrWhiteSpace(prompt))
                                {
                                    var errMsg = "Error: subtask 'prompt' is required";
                                    onToolError(errMsg);
                                    session.Messages.Add(new Message { Role = "tool", Content = errMsg, ToolCallId = tc.Id });
                                    AddDisplay(session, "tool_error_detail", errMsg);
                                    continue;
                                }

                                var startText = $"subtask(\"{Truncate(prompt, 60)}...\")";
                                AddDisplay(session, "tool_start", startText);
                                onToolStart(startText);
                                onSubtaskProgress(null);
                                onSubtaskProgress("...");

                                var subSession = new Session { ParentSessionId = session.Id };
                                subSession.Messages.Add(new Message { Role = "user", Content = prompt });

                                try
                                {
                                    await RunAsync(
                                        subSession,
                                        prompt,
                                        onOutput: _ => { },
                                        onToolStart: _ => { },
                                        onToolDone: _ => { },
                                        onToolError: _ => { },
                                        onSubtaskProgress,
                                        ct,
                                        isSubtask: true).ConfigureAwait(false);

                                    var lastAssistant = "";
                                    for (int j = subSession.Messages.Count - 1; j >= 0; j--)
                                    {
                                        if (subSession.Messages[j].Role == "assistant" && !string.IsNullOrEmpty(subSession.Messages[j].Content))
                                        {
                                            lastAssistant = subSession.Messages[j].Content;
                                            break;
                                        }
                                    }

                                    session.Messages.Add(new Message { Role = "tool", Content = lastAssistant, ToolCallId = tc.Id });
                                    var tail = Truncate(lastAssistant.Replace("\n", " ").Replace("\r", "").Replace("\t", " ").Trim(), 40);
                                    var doneText = $"subtask(\"{Truncate(prompt, 40)}...\") → {tail}";
                                    AddDisplay(session, "tool_done", doneText);
                                    onToolDone(doneText);
                                }
                                catch (OperationCanceledException) { throw; }
                                catch (Exception ex)
                                {
                                    var errMsg = $"Subtask error: {ex.Message}";
                                    onToolError(errMsg);
                                    session.Messages.Add(new Message { Role = "tool", Content = errMsg, ToolCallId = tc.Id });
                                    AddDisplay(session, "tool_error_detail", errMsg);
                                }
                            }
                            else
                            {
                                var errMsg = "Error: subtask cannot be nested";
                                onToolError(errMsg);
                                session.Messages.Add(new Message { Role = "tool", Content = errMsg, ToolCallId = tc.Id });
                                AddDisplay(session, "tool_error_detail", errMsg);
                            }
                            continue;
                        }

                        AgentWindow.Log($"[{session.Id}] Tool exec: {tc.Name}({Truncate(argsStr, 120)})");
                        var toolStartText = $"{tc.Name}({Truncate(argsStr, 120)})";
                        AddDisplay(session, "tool_start", toolStartText);
                        onToolStart(toolStartText);
                        string result;
                        bool isError = false;
                        try
                        {
                            var args = JsonNode.Parse(argsStr)?.AsObject() ?? new JsonObject();
                            result = Tools.Execute(tc.Name, args, session);
                            isError = result.StartsWith("Error") || result.StartsWith("File not found") ||
                                      result.StartsWith("Cannot read") || result.StartsWith("Unknown tool") ||
                                      result.StartsWith("Directory not found") ||
                                      result.StartsWith("Path escapes");
                        }
                        catch (Exception ex)
                        {
                            result = $"Tool error: {ex.Message}";
                            isError = true;
                            AgentWindow.Log($"[{session.Id}] Tool error: {tc.Name} → {ex.Message}");
                        }

                        session.Messages.Add(new Message
                        {
                            Role = "tool",
                            Content = result,
                            ToolCallId = tc.Id
                        });
                        var displayText = isError ? $"{tc.Name}({argsStr}) ✗" : FormatToolDisplay(tc.Name, argsStr, result);
                        AddDisplay(session, "tool_done", displayText);
                        onToolDone(displayText);
                        if (isError)
                        {
                            AddDisplay(session, "tool_error_detail", result);
                            onToolError(result);
                        }
                    }
                }
                else
                {
                    if (!string.IsNullOrEmpty(resp.Content))
                    {
                        session.Messages.Add(new Message { Role = "assistant", Content = resp.Content });
                    }
                    else if (resp.ToolCalls.Count == 0)
                    {
                        AgentWindow.Log($"Runtime: empty response from LLM (content=null, toolCalls=0)");
                        var emptyMsg = "[模型返回空响应]\n";
                        AddDisplay(session, "notice", emptyMsg);
                        onOutput(emptyMsg);
                    }
                    break;
                }

                if (i > 0 && i % 10 == 9 && i >= 20)
                {
                    var msg = "提示：已连续调用较多次tool，请注意控制调用次数。";
                    session.Messages.Add(new Message { Role = "user", Content = msg });
                    AddDisplay(session, "notice", msg + "\n");
                    onOutput(msg + "\n");
                }
            }

            Memory.Save(session);
        }

        private static void AddDisplay(Session session, string type, string content)
        {
            session.Messages.Add(new Message
            {
                Role = "display",
                DisplayType = type,
                Content = content
            });
        }

        private static string Truncate(string? s, int maxLen)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.Length <= maxLen) return s;
            return s[..maxLen];
        }

        private static string FormatToolDisplay(string name, string argsStr, string result)
        {
            if (name == "write")
            {
                try
                {
                    var args = JsonNode.Parse(argsStr)?.AsObject();
                    var filename = args?["filename"]?.GetValue<string>() ?? "?";
                    var content = args?["content"]?.GetValue<string>() ?? "";
                    var lines = content.Split('\n').Length;
                    return $"write({filename}, {lines} lines, {content.Length} chars)";
                }
                catch { }
            }
            if (name == "edit")
            {
                try
                {
                    var args = JsonNode.Parse(argsStr)?.AsObject();
                    var filename = args?["filename"]?.GetValue<string>() ?? "?";
                    var category = args?["category"]?.GetValue<string>() ?? "";
                    var displayName = string.IsNullOrWhiteSpace(category) ? filename : $"{category}/{filename}";
                    var oldText = args?["oldText"]?.GetValue<string>() ?? "";
                    var newText = args?["newText"]?.GetValue<string>() ?? "";
                    return $"edit({displayName}, {oldText.Length}->{newText.Length} chars)";
                }
                catch { return "edit()"; }
            }
            if (name == "rename_session")
            {
                try
                {
                    var args = JsonNode.Parse(argsStr)?.AsObject();
                    var title = args?["title"]?.GetValue<string>() ?? "?";
                    return $"rename_session({Truncate(title, 40)})";
                }
                catch { }
            }
            if (name == "display_panel")
            {
                try
                {
                    var args = JsonNode.Parse(argsStr)?.AsObject();
                    var html = args?["html"]?.GetValue<string>() ?? "";
                    var lines = string.IsNullOrEmpty(html) ? 0 : html.Split('\n').Length;
                    return $"display_panel({lines} lines, {html.Length} chars)";
                }
                catch { return "display_panel()"; }
            }
            if (name == "now")
            {
                return $"now() → {result}";
            }
            if (name == "set_bar_trigger" || name == "set_eye_mode" || name == "repair_archives" || name == "show_ball_bubble")
            {
                return $"{name}()";
            }
            return $"{name}({argsStr})";
        }
    }
}
