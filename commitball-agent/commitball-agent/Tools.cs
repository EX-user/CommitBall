using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CommitBallAgent
{
    static class Tools
    {
        private static string BaseDir => Path.GetFullPath(Config.DataDir);
        private static readonly object OutputToolLock = new();

        private static string ResolvePath(string relativePath)
        {
            var full = Path.GetFullPath(Path.Combine(BaseDir, relativePath.TrimStart('/', '\\')));
            if (!full.StartsWith(BaseDir))
                throw new UnauthorizedAccessException("Path escapes data directory");
            return full;
        }

        private static string FormatSize(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes / 1024.0 / 1024.0:F1} MB";
        }

        public static string GetToolDefinitionsJson(bool includeSubtask = true)
        {
            var listDef = "{\"type\":\"function\",\"function\":{\"name\":\"list\",\"description\":\"List files and directories under data/. Shows name, size, and modification time. Use 'match' to filter by wildcard pattern (e.g. '*2026-06-03*').\",\"parameters\":{\"type\":\"object\",\"properties\":{\"path\":{\"type\":\"string\",\"description\":\"Subdirectory relative to data/, empty or omitted for root\"},\"match\":{\"type\":\"string\",\"description\":\"Wildcard pattern to filter filenames (e.g. '*2026-06-03*', '*.txt')\"}}}}}";
            var readDef = "{\"type\":\"function\",\"function\":{\"name\":\"read\",\"description\":\"Read a text file under data/. Returns file content with line numbers.\",\"parameters\":{\"type\":\"object\",\"properties\":{\"file\":{\"type\":\"string\",\"description\":\"File path relative to data/\"},\"start\":{\"type\":\"integer\",\"description\":\"Starting line number (1-based), default 1\"},\"lines\":{\"type\":\"integer\",\"description\":\"Max number of lines to read, default 50\"},\"maxLen\":{\"type\":\"integer\",\"description\":\"Max total characters to return, default 4000\"}},\"required\":[\"file\"]}}}";
            var writeDef = "{\"type\":\"function\",\"function\":{\"name\":\"write\",\"description\":\"Write content to a text file under data/agent-out/. Prefer category plus filename. Categories create organized subdirectories: reports/YYYY-MM/, extracts/YYYY-MM/, reminders/YYYY-MM/, responses/YYYY-MM/, analysis/YYYY-MM/, scratch/YYYY-MM/, or memory/.\",\"parameters\":{\"type\":\"object\",\"properties\":{\"filename\":{\"type\":\"string\",\"description\":\"Filename or relative path under the selected category. Must not be absolute or contain ..\"},\"category\":{\"type\":\"string\",\"enum\":[\"reports\",\"extracts\",\"reminders\",\"responses\",\"analysis\",\"scratch\",\"memory\"],\"description\":\"Optional output category. If set and filename has no directory, non-memory categories are written under category/YYYY-MM/.\"},\"content\":{\"type\":\"string\",\"description\":\"Content to write\"}},\"required\":[\"filename\",\"content\"]}}}";
            var grepDef = "{\"type\":\"function\",\"function\":{\"name\":\"grep\",\"description\":\"Search text files under data/agent-out, data/exports, or a specified data/ subdirectory. Returns matching file, line number, and line text.\",\"parameters\":{\"type\":\"object\",\"properties\":{\"pattern\":{\"type\":\"string\",\"description\":\"Case-insensitive text or regex pattern to search for\"},\"path\":{\"type\":\"string\",\"description\":\"Optional subdirectory under data/. Defaults to agent-out and exports\"},\"maxMatches\":{\"type\":\"integer\",\"description\":\"Maximum matches to return, default 50\"}},\"required\":[\"pattern\"]}}}";
            var displayPanelDef = "{\"type\":\"function\",\"function\":{\"name\":\"display_panel\",\"description\":\"Write an HTML panel to data/agent-out/panel.html and ask CommitBall-Bar to show the panel immediately.\",\"parameters\":{\"type\":\"object\",\"properties\":{\"html\":{\"type\":\"string\",\"description\":\"Complete HTML content to display in the Bar panel\"}},\"required\":[\"html\"]}}}";
            var updateMetaDef = "{\"type\":\"function\",\"function\":{\"name\":\"update_meta\",\"description\":\"Update archive metadata for one data/exports/**/*.meta.json file. Preserves fixed session/path/time fields and updates title, work_tags, summary, and optional per-cluster summaries.\",\"parameters\":{\"type\":\"object\",\"properties\":{\"file\":{\"type\":\"string\",\"description\":\"Metadata file path relative to data/, must be under exports/ and end with .meta.json\"},\"title\":{\"type\":\"string\",\"description\":\"Short human-readable session title\"},\"work_tags\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"description\":\"3-6 concise work-dimension tags\"},\"summary\":{\"type\":\"string\",\"description\":\"Brief session summary grounded in exported log content\"},\"cluster_summaries\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{\"cluster_id\":{\"type\":\"string\",\"description\":\"Cluster id from meta, e.g. cluster_01\"},\"summary\":{\"type\":\"string\",\"description\":\"What the user did in this focus cluster\"},\"inferred_intent\":{\"type\":\"string\",\"description\":\"What the user likely wanted to do, grounded in evidence\"},\"reminder\":{\"type\":\"string\",\"description\":\"A useful reminder or empty string if none\"}},\"required\":[\"cluster_id\",\"summary\"]},\"description\":\"Optional per-focus cluster summaries to merge into meta.clusters\"}},\"required\":[\"file\",\"title\",\"work_tags\",\"summary\"]}}}";
            var renameSessionDef = "{\"type\":\"function\",\"function\":{\"name\":\"rename_session\",\"description\":\"Rename the current Agent conversation tab/session when the topic is clear or has changed. Use a concise title around 20 Chinese characters or 80 English characters.\",\"parameters\":{\"type\":\"object\",\"properties\":{\"title\":{\"type\":\"string\",\"description\":\"Concise session title\"}},\"required\":[\"title\"]}}}";
            var setBarTriggerDef = "{\"type\":\"function\",\"function\":{\"name\":\"set_bar_trigger\",\"description\":\"Set the CommitBall Bar wake trigger sequence. Equivalent to the old Bar /trigger command. Use only when the user asks to change the Bar wake sequence.\",\"parameters\":{\"type\":\"object\",\"properties\":{\"trigger\":{\"type\":\"string\",\"description\":\"Wake sequence, length 1-10, ASCII letters/digits or one of \\\\ ; / ` [ ] - = , .\"}},\"required\":[\"trigger\"]}}}";
            var setEyeModeDef = "{\"type\":\"function\",\"function\":{\"name\":\"set_eye_mode\",\"description\":\"Turn CommitBall Ball eye mode on, off, or toggle it. Equivalent to the old Bar /eye command.\",\"parameters\":{\"type\":\"object\",\"properties\":{\"mode\":{\"type\":\"string\",\"enum\":[\"on\",\"off\",\"toggle\"],\"description\":\"Desired eye mode\"}},\"required\":[\"mode\"]}}}";
            var repairArchivesDef = "{\"type\":\"function\",\"function\":{\"name\":\"repair_archives\",\"description\":\"Run machine-only archive repair: scan data/sessions, export missing txt files, and generate missing meta/cluster files. Existing meta.json files are never regenerated or modified. This tool does not perform model analysis and does not queue tasks.\",\"parameters\":{\"type\":\"object\",\"properties\":{}}}}";
            var showBallBubbleDef = "{\"type\":\"function\",\"function\":{\"name\":\"show_ball_bubble\",\"description\":\"Show a short plain-text message bubble from the CommitBall floating ball. Use it to notify the user about results of commands, especially commands received from CommitBall Bar's 指令 mode. Avoid emoji and decorative symbols because the bubble renderer is optimized for plain text.\",\"parameters\":{\"type\":\"object\",\"properties\":{\"message\":{\"type\":\"string\",\"description\":\"Short plain-text user-facing message, preferably under 40 Chinese characters or 100 English characters, without emoji\"}},\"required\":[\"message\"]}}}";
            var pwdDef = "{\"type\":\"function\",\"function\":{\"name\":\"pwd\",\"description\":\"Returns the directory where CommitBall-Agent.exe is located.\",\"parameters\":{\"type\":\"object\",\"properties\":{}}}}";
            var subtaskDef = "{\"type\":\"function\",\"function\":{\"name\":\"subtask\",\"description\":\"Launch a sub-task session to accomplish a complex goal. The sub-task has its own conversation and can use list/read/write tools. Returns the final result.\",\"parameters\":{\"type\":\"object\",\"properties\":{\"prompt\":{\"type\":\"string\",\"description\":\"The task description for the sub-task to accomplish\"}},\"required\":[\"prompt\"]}}}";

            var tools = new List<string> { listDef, readDef, writeDef, grepDef, displayPanelDef, updateMetaDef, setBarTriggerDef, setEyeModeDef, repairArchivesDef, showBallBubbleDef, pwdDef };
            if (includeSubtask)
            {
                tools.Add(renameSessionDef);
                tools.Add(subtaskDef);
            }
            return "[" + string.Join(",", tools) + "]";
        }

        public static string GetSystemPrompt(bool isSubtask = false)
        {
            if (isSubtask)
                return "You are a sub-task executor. Complete the given task using available tools, then provide a concise final result. " +
                       "Available tools: list, read, write. All file operations are scoped to data/. " +
                       "When writing analysis output, keep data/agent-out organized: reports/YYYY-MM/ for reports, extracts/YYYY-MM/ for extracted notes, reminders/YYYY-MM/ for reminders, responses/YYYY-MM/ for replies, analysis/YYYY-MM/ for analysis artifacts, scratch/YYYY-MM/ for temporary working files, and memory/ for long-lived memory files.";
            return "You are CommitBall Agent, an AI assistant that can read and manage files in the data/ directory. " +
                   "Use the list tool to explore available files before reading them. " +
                   "All file operations are scoped to data/. " +
                   "When the conversation topic becomes clear, or when the user's topic changes materially, call rename_session to keep the current Agent tab title accurate. Use a concise human-readable title around 20 Chinese characters or 80 English characters. " +
                   "Users may send natural-language control commands from CommitBall Bar's 指令 mode. For Bar/Ball controls, use the dedicated tools instead of asking the user to type slash commands: set_bar_trigger for wake sequence changes, set_eye_mode for eye mode, and repair_archives for machine-only archive file repair. repair_archives scans data/sessions, exports missing txt files, and creates missing meta/cluster files without modifying existing meta.json files. If a request came from CommitBall Bar's 指令 mode, call show_ball_bubble with a short plain-text result message without emoji so the user gets feedback from the floating ball. " +
                   "Keep data/agent-out organized: keep panel.html and panel-template.html at the root; use display_panel to update panel.html; maintain long-term memory only in memory/summary_task_exp_decay_memory.md; treat root summary_task_exp_decay_memory.md as legacy read-only compatibility data; write new reports to reports/YYYY-MM/, extracted persistent notes to extracts/YYYY-MM/, reminders to reminders/YYYY-MM/, user-facing replies to responses/YYYY-MM/, analysis artifacts to analysis/YYYY-MM/, temporary files to scratch/YYYY-MM/, and auxiliary memory files to memory/. " +
                   "If data/agent-out/memory/summary_task_exp_decay_memory.md exists, read it for background context on the user's recent activities. " +
                   "If it doesn't exist but data/agent-out/summary_task_exp_decay_memory.md exists, you may read the root file once as legacy context and then write any updated memory to memory/summary_task_exp_decay_memory.md. " +
                   "If neither exists, you should wait for further instructions.";
        }

        public static bool IsSubtask(string toolName) => toolName == "subtask";

        public static string Execute(string toolName, JsonObject args, Session? session = null)
        {
            return toolName switch
            {
                "list" => ExecuteList(args),
                "read" => ExecuteRead(args),
                "write" => ExecuteWrite(args),
                "grep" => ExecuteGrep(args),
                "display_panel" => ExecuteDisplayPanel(args),
                "update_meta" => ExecuteUpdateMeta(args),
                "rename_session" => ExecuteRenameSession(args, session),
                "set_bar_trigger" => ExecuteSetBarTrigger(args),
                "set_eye_mode" => ExecuteSetEyeMode(args),
                "repair_archives" => ExecuteRepairArchives(),
                "show_ball_bubble" => ExecuteShowBallBubble(args),
                "pwd" => AppDomain.CurrentDomain.BaseDirectory,
                _ => $"Unknown tool: {toolName}"
            };
        }

        private static string ExecuteRenameSession(JsonObject args, Session? session)
        {
            if (session == null)
                return "Error: current session is unavailable";
            var title = args["title"]?.GetValue<string>() ?? "";
            return Memory.RenameSession(session, title, "agent");
        }

        private static string ExecuteSetBarTrigger(JsonObject args)
        {
            var trigger = args["trigger"]?.GetValue<string>()?.Trim() ?? "";
            if (!IsValidTrigger(trigger))
                return "Error: trigger must be 1-10 chars and contain only ASCII letters, digits, or \\ ; / ` [ ] - = , .";
            return SendCommitBallCommand("SET_TRIGGER " + trigger)
                ? $"Bar trigger updated: {trigger}"
                : "Error: failed to send trigger command to CommitBall";
        }

        private static string ExecuteSetEyeMode(JsonObject args)
        {
            var mode = (args["mode"]?.GetValue<string>() ?? "").Trim().ToLowerInvariant();
            var commandMode = mode switch
            {
                "on" or "open" or "true" => "ON",
                "off" or "close" or "false" => "OFF",
                "toggle" or "" => "TOGGLE",
                _ => ""
            };
            if (commandMode.Length == 0)
                return "Error: mode must be on, off, or toggle";
            return SendCommitBallCommand("SET_EYE_MODE " + commandMode)
                ? $"Eye mode command sent: {mode}"
                : "Error: failed to send eye mode command to CommitBall";
        }

        private static string ExecuteRepairArchives()
        {
            try
            {
                return ArchiveRepair.RepairFiles().ToString();
            }
            catch (Exception ex)
            {
                AgentWindow.Log("repair_archives failed: " + ex);
                return "Error: repair_archives failed: " + ex.Message;
            }
        }

        private static string ExecuteShowBallBubble(JsonObject args)
        {
            var message = (args["message"]?.GetValue<string>() ?? "").Trim();
            if (string.IsNullOrWhiteSpace(message))
                return "Error: message is required";
            if (message.Length > 160)
                message = message[..160];
            return SendCommitBallCommand("BUBBLE " + message)
                ? "Ball bubble shown"
                : "Error: failed to send bubble command to CommitBall";
        }

        private static bool IsValidTrigger(string trigger)
        {
            if (string.IsNullOrEmpty(trigger) || trigger.Length > 10) return false;
            foreach (var c in trigger)
            {
                if (char.IsLetterOrDigit(c)) continue;
                if ("\\;/`[]-=,.".IndexOf(c) >= 0) continue;
                return false;
            }
            return true;
        }

        private static bool SendCommitBallCommand(string command)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", "CommitBall-direct", PipeDirection.Out);
                pipe.Connect(1000);
                var bytes = System.Text.Encoding.UTF8.GetBytes("CMD " + command);
                pipe.Write(bytes, 0, bytes.Length);
                return true;
            }
            catch (Exception ex)
            {
                AgentWindow.Log("SendCommitBallCommand failed: " + ex.Message);
                return false;
            }
        }

        private static string ExecuteList(JsonObject args)
        {
            var relPath = args["path"]?.GetValue<string>() ?? "";
            var pattern = args["match"]?.GetValue<string>() ?? "";
            var full = ResolvePath(relPath);
            if (!Directory.Exists(full))
                return $"Directory not found: {relPath}";

            var lines = new List<string>();
            var display = string.IsNullOrEmpty(relPath) ? "data/" : $"data/{relPath.Trim('/', '\\')}/";
            lines.Add(display);

            foreach (var dir in Directory.GetDirectories(full))
            {
                var name = Path.GetFileName(dir);
                if (!string.IsNullOrEmpty(pattern) && !WildcardMatch(name, pattern))
                    continue;
                var info = new DirectoryInfo(dir);
                lines.Add($"  [{name}/]  {info.LastWriteTime:yyyy-MM-dd HH:mm}");
            }

            foreach (var file in Directory.GetFiles(full))
            {
                var name = Path.GetFileName(file);
                if (!string.IsNullOrEmpty(pattern) && !WildcardMatch(name, pattern))
                    continue;
                var info = new FileInfo(file);
                lines.Add($"  {name}  {FormatSize(info.Length)}  {info.LastWriteTime:yyyy-MM-dd HH:mm}");
            }

            if (lines.Count == 1)
                lines.Add($"  (no matches for '{pattern}')");

            return string.Join("\n", lines);
        }

        private static bool WildcardMatch(string input, string pattern)
        {
            var regex = "^" + System.Text.RegularExpressions.Regex.Escape(pattern)
                .Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return System.Text.RegularExpressions.Regex.IsMatch(input, regex,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        private static string ExecuteRead(JsonObject args)
        {
            var file = args["file"]?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(file))
                return "Error: 'file' parameter is required";

            var full = ResolvePath(file);
            if (!File.Exists(full))
                return $"File not found: {file}";

            var ext = Path.GetExtension(full).ToLower();
            if (ext == ".db" || ext == ".sqlite" || ext == ".exe" || ext == ".dll")
                return $"Cannot read binary file: {file}";

            var startLine = args["start"]?.GetValue<int>() ?? 1;
            var maxLines = args["lines"]?.GetValue<int>() ?? 50;
            var maxLen = args["maxLen"]?.GetValue<int>() ?? 4000;
            if (startLine < 1) startLine = 1;
            if (maxLines < 1) maxLines = 50;
            if (maxLen < 100) maxLen = 4000;

            try
            {
                var allLines = File.ReadAllLines(full);
                var totalLines = allLines.Length;
                var skip = startLine - 1;
                var take = Math.Min(maxLines, totalLines - skip);
                if (skip >= totalLines)
                    return $"File has {totalLines} lines, start={startLine} is beyond end";

                var sb = new System.Text.StringBuilder();
                var endLine = Math.Min(skip + take, totalLines);
                for (int i = skip; i < endLine; i++)
                {
                    var line = $"{i + 1}: {allLines[i]}";
                    if (sb.Length + line.Length + 1 > maxLen)
                    {
                        sb.AppendLine($"[... truncated at {maxLen} chars]");
                        break;
                    }
                    sb.AppendLine(line);
                }

                sb.AppendLine($"[lines {startLine}-{endLine} of {totalLines}]");
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return $"Error reading file: {ex.Message}";
            }
        }

        private static string ExecuteWrite(JsonObject args)
        {
            var filename = args["filename"]?.GetValue<string>() ?? "";
            var category = args["category"]?.GetValue<string>() ?? "";
            var content = args["content"]?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(filename))
                return "Error: 'filename' parameter is required";
            if (string.IsNullOrEmpty(content))
                return "Error: 'content' parameter is required";

            var outDir = Path.Combine(BaseDir, "agent-out");
            Directory.CreateDirectory(outDir);
            if (!TryBuildAgentOutWriteName(filename, category, out var writeName, out var buildError))
                return buildError;
            if (!TryResolveAgentOutWritePath(writeName, outDir, out var normalized, out var full, out var error))
                return error;

            lock (OutputToolLock)
            {
                try
                {
                    var dir = Path.GetDirectoryName(full);
                    if (!string.IsNullOrEmpty(dir))
                        Directory.CreateDirectory(dir);
                    File.WriteAllText(full, content);
                    return $"Written {content.Length} chars to agent-out/{normalized}";
                }
                catch (Exception ex)
                {
                    return $"Error writing file: {ex.Message}";
                }
            }
        }

        private static bool TryBuildAgentOutWriteName(string filename, string category, out string writeName, out string error)
        {
            writeName = filename.Replace('\\', '/').Trim().TrimStart('/');
            error = "";
            var normalizedCategory = NormalizeAgentOutCategory(category);
            if (!string.IsNullOrWhiteSpace(category) && normalizedCategory.Length == 0)
            {
                error = "Error: category must be one of reports, extracts, reminders, responses, analysis, scratch, or memory";
                return false;
            }

            if (normalizedCategory.Length == 0)
                return true;

            if (writeName.StartsWith(normalizedCategory + "/", StringComparison.OrdinalIgnoreCase))
                return true;

            if (writeName.Contains('/'))
            {
                writeName = normalizedCategory + "/" + writeName;
                return true;
            }

            writeName = normalizedCategory == "memory"
                ? $"memory/{writeName}"
                : $"{normalizedCategory}/{DateTime.Now:yyyy-MM}/{writeName}";
            return true;
        }

        private static string NormalizeAgentOutCategory(string category)
        {
            category = (category ?? "").Trim().ToLowerInvariant();
            return category switch
            {
                "report" or "reports" => "reports",
                "extract" or "extracts" => "extracts",
                "reminder" or "reminders" => "reminders",
                "response" or "responses" => "responses",
                "analysis" => "analysis",
                "scratch" or "tmp" or "temp" => "scratch",
                "memory" => "memory",
                "" => "",
                _ => ""
            };
        }

        private static bool TryResolveAgentOutWritePath(string filename, string outDir, out string normalized, out string full, out string error)
        {
            normalized = filename.Replace('\\', '/').Trim().TrimStart('/');
            full = "";
            error = "";

            if (string.IsNullOrWhiteSpace(normalized))
            {
                error = "Error: filename must not be empty";
                return false;
            }
            if (Path.IsPathRooted(filename) || normalized.Contains("..") || normalized.Contains(':'))
            {
                error = "Error: filename must be a relative path under agent-out and must not contain '..' or ':'";
                return false;
            }

            var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
            {
                error = "Error: filename must not be empty";
                return false;
            }

            var invalidChars = Path.GetInvalidFileNameChars();
            foreach (var segment in segments)
            {
                if (segment == "." || segment == ".." || segment.Any(c => invalidChars.Contains(c) || c == ':'))
                {
                    error = $"Error: filename contains invalid path segment '{segment}'.";
                    return false;
                }
            }

            if (segments.Length > 1)
            {
                var allowedTopDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "reports", "extracts", "reminders", "responses", "analysis", "scratch", "memory"
                };
                if (!allowedTopDirs.Contains(segments[0]))
                {
                    error = "Error: subdirectory must be one of reports, extracts, reminders, responses, analysis, scratch, or memory";
                    return false;
                }
            }

            normalized = string.Join("/", segments);
            var outRoot = Path.GetFullPath(outDir);
            full = Path.GetFullPath(Path.Combine(outRoot, Path.Combine(segments)));
            if (!full.StartsWith(outRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(full, outRoot, StringComparison.OrdinalIgnoreCase))
            {
                error = "Error: filename escapes agent-out directory";
                return false;
            }
            return true;
        }

        private static string ExecuteGrep(JsonObject args)
        {
            var pattern = args["pattern"]?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(pattern))
                return "Error: 'pattern' parameter is required";

            var maxMatches = args["maxMatches"]?.GetValue<int>() ?? 50;
            if (maxMatches < 1) maxMatches = 50;
            if (maxMatches > 200) maxMatches = 200;

            var relPath = args["path"]?.GetValue<string>() ?? "";
            var roots = new List<string>();
            if (string.IsNullOrWhiteSpace(relPath))
            {
                roots.Add(Path.Combine(BaseDir, "agent-out"));
                roots.Add(Path.Combine(BaseDir, "exports"));
            }
            else
            {
                roots.Add(ResolvePath(relPath));
            }

            System.Text.RegularExpressions.Regex regex;
            try
            {
                regex = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }
            catch
            {
                regex = new System.Text.RegularExpressions.Regex(System.Text.RegularExpressions.Regex.Escape(pattern), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            }

            var binaryExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".db", ".sqlite", ".exe", ".dll", ".pdb", ".png", ".jpg", ".jpeg", ".gif", ".webp" };
            var lines = new List<string>();
            foreach (var root in roots)
            {
                if (!Directory.Exists(root)) continue;
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    if (binaryExts.Contains(Path.GetExtension(file))) continue;
                    var rel = Path.GetRelativePath(BaseDir, file).Replace('\\', '/');
                    int lineNo = 0;
                    try
                    {
                        foreach (var line in File.ReadLines(file))
                        {
                            lineNo++;
                            if (!regex.IsMatch(line)) continue;
                            var trimmed = line.Length > 240 ? line[..240] + "..." : line;
                            lines.Add($"{rel}:{lineNo}: {trimmed}");
                            if (lines.Count >= maxMatches)
                                return string.Join("\n", lines) + $"\n[truncated at {maxMatches} matches]";
                        }
                    }
                    catch { }
                }
            }

            if (lines.Count == 0) return $"No matches for '{pattern}'";
            return string.Join("\n", lines);
        }

        private static string ExecuteDisplayPanel(JsonObject args)
        {
            var html = args["html"]?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(html))
                return "Error: 'html' parameter is required";

            var outDir = Path.Combine(BaseDir, "agent-out");
            Directory.CreateDirectory(outDir);
            var full = Path.Combine(outDir, "panel.html");
            lock (OutputToolLock)
            {
                try
                {
                    File.WriteAllText(full, html);
                    NotifyBar("SHOW_PANEL");
                    return $"Panel updated and shown ({html.Length} chars)";
                }
                catch (Exception ex)
                {
                    return $"Error updating panel: {ex.Message}";
                }
            }
        }

        private static string ExecuteUpdateMeta(JsonObject args)
        {
            var file = args["file"]?.GetValue<string>() ?? "";
            var title = (args["title"]?.GetValue<string>() ?? "").Trim();
            var summary = (args["summary"]?.GetValue<string>() ?? "").Trim();
            var tagsNode = args["work_tags"] as JsonArray;
            var clusterSummariesNode = args["cluster_summaries"] as JsonArray;

            if (string.IsNullOrWhiteSpace(file))
                return "Error: 'file' parameter is required";
            var normalized = file.Replace('\\', '/').TrimStart('/');
            if (!normalized.StartsWith("exports/", StringComparison.OrdinalIgnoreCase) ||
                !normalized.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains(".."))
                return "Error: file must be under exports/ and end with .meta.json";
            if (string.IsNullOrWhiteSpace(title))
                return "Error: 'title' parameter is required";
            if (string.IsNullOrWhiteSpace(summary))
                return "Error: 'summary' parameter is required";

            var tags = new List<string>();
            if (tagsNode != null)
            {
                foreach (var node in tagsNode)
                {
                    var tag = node?.GetValue<string>()?.Trim() ?? "";
                    if (tag.Length == 0) continue;
                    if (tag.Length > 32) tag = tag[..32];
                    if (!tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                        tags.Add(tag);
                    if (tags.Count >= 8) break;
                }
            }
            if (tags.Count == 0)
                return "Error: 'work_tags' must contain at least one tag";
            if (title.Length > 80) title = title[..80];
            if (summary.Length > 600) summary = summary[..600];

            var full = ResolvePath(normalized);
            if (!File.Exists(full))
                return $"Metadata file not found: {normalized}";

            lock (OutputToolLock)
            {
                try
                {
                    var root = JsonNode.Parse(File.ReadAllText(full))?.AsObject() ?? new JsonObject();
                    root["title"] = title;
                    root["work_tags"] = new JsonArray(tags.Select(t => JsonValue.Create(t)).ToArray());
                    root["summary"] = summary;
                    root["summary_source"] = "agent";
                    root["source"] = "agent";
                    root["agent_named_at"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    var updatedClusters = 0;
                    if (clusterSummariesNode != null && root["clusters"] is JsonArray clusters)
                    {
                        var byId = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
                        foreach (var node in clusters)
                        {
                            if (node is not JsonObject cluster) continue;
                            var id = cluster["id"]?.GetValue<string>() ?? "";
                            if (!string.IsNullOrWhiteSpace(id)) byId[id] = cluster;
                        }

                        foreach (var node in clusterSummariesNode)
                        {
                            if (node is not JsonObject incoming) continue;
                            var id = incoming["cluster_id"]?.GetValue<string>()?.Trim() ?? "";
                            if (string.IsNullOrWhiteSpace(id) || !byId.TryGetValue(id, out var cluster)) continue;
                            var clusterSummary = (incoming["summary"]?.GetValue<string>() ?? "").Trim();
                            var intent = (incoming["inferred_intent"]?.GetValue<string>() ?? "").Trim();
                            var reminder = (incoming["reminder"]?.GetValue<string>() ?? "").Trim();
                            if (clusterSummary.Length > 500) clusterSummary = clusterSummary[..500];
                            if (intent.Length > 300) intent = intent[..300];
                            if (reminder.Length > 300) reminder = reminder[..300];
                            if (!string.IsNullOrWhiteSpace(clusterSummary)) cluster["agent_summary"] = clusterSummary;
                            if (!string.IsNullOrWhiteSpace(intent)) cluster["inferred_intent"] = intent;
                            if (!string.IsNullOrWhiteSpace(reminder)) cluster["reminder"] = reminder;
                            cluster["summary_source"] = "agent";
                            updatedClusters++;
                        }
                        root["cluster_summary_updated_at"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                    }

                    var opts = new JsonSerializerOptions { WriteIndented = true };
                    File.WriteAllText(full, root.ToJsonString(opts));
                    return updatedClusters > 0
                        ? $"Metadata updated: {normalized}, clusters={updatedClusters}"
                        : $"Metadata updated: {normalized}";
                }
                catch (Exception ex)
                {
                    return $"Error updating metadata: {ex.Message}";
                }
            }
        }

        private static void NotifyBar(string command)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", "CommitBall-bar", PipeDirection.Out);
                pipe.Connect(300);
                var bytes = System.Text.Encoding.UTF8.GetBytes(command + "\r\n");
                pipe.Write(bytes, 0, bytes.Length);
            }
            catch { }
        }
    }
}
