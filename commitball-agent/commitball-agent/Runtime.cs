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
            bool isSubtask = false)
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
                    ct: ct);

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
                                catch (Exception ex) { System.IO.File.AppendAllText(System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "log", "agent.log"), $"[{System.DateTime.Now:HH:mm:ss.fff}] Subtask parse error: {ex.Message}, raw args: {Truncate(argsStr, 200)}\n"); }

                                if (string.IsNullOrWhiteSpace(prompt))
                                {
                                    var errMsg = "Error: subtask 'prompt' is required";
                                    onToolError(errMsg);
                                    session.Messages.Add(new Message { Role = "tool", Content = errMsg, ToolCallId = tc.Id });
                                    continue;
                                }

                                onToolStart($"subtask(\"{Truncate(prompt, 60)}...\")");
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
                                        isSubtask: true);

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
                                    session.Messages.Add(new Message { Role = "display", Content = doneText });
                                    onToolDone(doneText);
                                }
                                catch (OperationCanceledException) { throw; }
                                catch (Exception ex)
                                {
                                    var errMsg = $"Subtask error: {ex.Message}";
                                    onToolError(errMsg);
                                    session.Messages.Add(new Message { Role = "tool", Content = errMsg, ToolCallId = tc.Id });
                                }
                            }
                            else
                            {
                                var errMsg = "Error: subtask cannot be nested";
                                onToolError(errMsg);
                                session.Messages.Add(new Message { Role = "tool", Content = errMsg, ToolCallId = tc.Id });
                            }
                            continue;
                        }

                        string result;
                        bool isError = false;
                        try
                        {
                            var args = JsonNode.Parse(argsStr)?.AsObject() ?? new JsonObject();
                            result = Tools.Execute(tc.Name, args);
                            isError = result.StartsWith("Error") || result.StartsWith("File not found") ||
                                      result.StartsWith("Cannot read") || result.StartsWith("Unknown tool") ||
                                      result.StartsWith("Directory not found") ||
                                      result.StartsWith("Path escapes");
                        }
                        catch (Exception ex)
                        {
                            result = $"Tool error: {ex.Message}";
                            isError = true;
                        }

                        session.Messages.Add(new Message
                        {
                            Role = "tool",
                            Content = result,
                            ToolCallId = tc.Id
                        });
                        var displayText = isError ? $"{tc.Name}({argsStr}) ✗" : FormatToolDisplay(tc.Name, argsStr, result);
                        session.Messages.Add(new Message { Role = "display", Content = displayText });
                        onToolDone(displayText);
                        if (isError) onToolError(result);
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
                        onOutput("[模型返回空响应]\n");
                    }
                    break;
                }

                if (i > 0 && i % 10 == 9 && i >= 20)
                {
                    var msg = "提示：已连续调用较多次tool，请注意控制调用次数。";
                    session.Messages.Add(new Message { Role = "user", Content = msg });
                    onOutput(msg + "\n");
                }
            }

            Memory.Save(session);
        }

        private static string Truncate(string s, int maxLen)
        {
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
            return $"{name}({argsStr})";
        }
    }
}
