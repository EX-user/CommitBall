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
            var writeDef = "{\"type\":\"function\",\"function\":{\"name\":\"write\",\"description\":\"Write content to a file under data/agent-out/. Creates the file if it does not exist.\",\"parameters\":{\"type\":\"object\",\"properties\":{\"filename\":{\"type\":\"string\",\"description\":\"Filename to write under data/agent-out/\"},\"content\":{\"type\":\"string\",\"description\":\"Content to write\"}},\"required\":[\"filename\",\"content\"]}}}";
            var grepDef = "{\"type\":\"function\",\"function\":{\"name\":\"grep\",\"description\":\"Search text files under data/agent-out, data/exports, or a specified data/ subdirectory. Returns matching file, line number, and line text.\",\"parameters\":{\"type\":\"object\",\"properties\":{\"pattern\":{\"type\":\"string\",\"description\":\"Case-insensitive text or regex pattern to search for\"},\"path\":{\"type\":\"string\",\"description\":\"Optional subdirectory under data/. Defaults to agent-out and exports\"},\"maxMatches\":{\"type\":\"integer\",\"description\":\"Maximum matches to return, default 50\"}},\"required\":[\"pattern\"]}}}";
            var displayPanelDef = "{\"type\":\"function\",\"function\":{\"name\":\"display_panel\",\"description\":\"Write an HTML panel to data/agent-out/panel.html and ask CommitBall-Bar to refresh the panel.\",\"parameters\":{\"type\":\"object\",\"properties\":{\"html\":{\"type\":\"string\",\"description\":\"Complete HTML content to display in the Bar panel\"}},\"required\":[\"html\"]}}}";
            var updateMetaDef = "{\"type\":\"function\",\"function\":{\"name\":\"update_meta\",\"description\":\"Update archive metadata for one data/exports/**/*.meta.json file. Preserves fixed session/path/time fields and updates title, work_tags, summary, and optional per-cluster summaries.\",\"parameters\":{\"type\":\"object\",\"properties\":{\"file\":{\"type\":\"string\",\"description\":\"Metadata file path relative to data/, must be under exports/ and end with .meta.json\"},\"title\":{\"type\":\"string\",\"description\":\"Short human-readable session title\"},\"work_tags\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"description\":\"3-6 concise work-dimension tags\"},\"summary\":{\"type\":\"string\",\"description\":\"Brief session summary grounded in exported log content\"},\"cluster_summaries\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{\"cluster_id\":{\"type\":\"string\",\"description\":\"Cluster id from meta, e.g. cluster_01\"},\"summary\":{\"type\":\"string\",\"description\":\"What the user did in this focus cluster\"},\"inferred_intent\":{\"type\":\"string\",\"description\":\"What the user likely wanted to do, grounded in evidence\"},\"reminder\":{\"type\":\"string\",\"description\":\"A useful reminder or empty string if none\"}},\"required\":[\"cluster_id\",\"summary\"]},\"description\":\"Optional per-focus cluster summaries to merge into meta.clusters\"}},\"required\":[\"file\",\"title\",\"work_tags\",\"summary\"]}}}";
            var pwdDef = "{\"type\":\"function\",\"function\":{\"name\":\"pwd\",\"description\":\"Returns the directory where CommitBall-Agent.exe is located.\",\"parameters\":{\"type\":\"object\",\"properties\":{}}}}";
            var subtaskDef = "{\"type\":\"function\",\"function\":{\"name\":\"subtask\",\"description\":\"Launch a sub-task session to accomplish a complex goal. The sub-task has its own conversation and can use list/read/write tools. Returns the final result.\",\"parameters\":{\"type\":\"object\",\"properties\":{\"prompt\":{\"type\":\"string\",\"description\":\"The task description for the sub-task to accomplish\"}},\"required\":[\"prompt\"]}}}";

            var tools = new List<string> { listDef, readDef, writeDef, grepDef, displayPanelDef, updateMetaDef, pwdDef };
            if (includeSubtask) tools.Add(subtaskDef);
            return "[" + string.Join(",", tools) + "]";
        }

        public static string GetSystemPrompt(bool isSubtask = false)
        {
            if (isSubtask)
                return "You are a sub-task executor. Complete the given task using available tools, then provide a concise final result. " +
                       "Available tools: list, read, write. All file operations are scoped to data/.";
            return "You are CommitBall Agent, an AI assistant that can read and manage files in the data/ directory. " +
                   "Use the list tool to explore available files before reading them. " +
                   "All file operations are scoped to data/. " +
                   "If data/agent-out/summary_task_exp_decay_memory.md exists, read it for background context on the user's recent activities. " +
                   "If it doesn't exist, you should wait for further instructions.";
        }

        public static bool IsSubtask(string toolName) => toolName == "subtask";

        public static string Execute(string toolName, JsonObject args)
        {
            return toolName switch
            {
                "list" => ExecuteList(args),
                "read" => ExecuteRead(args),
                "write" => ExecuteWrite(args),
                "grep" => ExecuteGrep(args),
                "display_panel" => ExecuteDisplayPanel(args),
                "update_meta" => ExecuteUpdateMeta(args),
                "pwd" => AppDomain.CurrentDomain.BaseDirectory,
                _ => $"Unknown tool: {toolName}"
            };
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
            var content = args["content"]?.GetValue<string>() ?? "";
            if (string.IsNullOrWhiteSpace(filename))
                return "Error: 'filename' parameter is required";
            if (string.IsNullOrEmpty(content))
                return "Error: 'content' parameter is required";

            if (filename.Contains("..") || filename.Contains('/') || filename.Contains('\\'))
                return "Error: filename must be a simple name without path separators";

            var invalidChars = Path.GetInvalidFileNameChars();
            var badChar = filename.FirstOrDefault(c => invalidChars.Contains(c) || c == ':');
            if (badChar != '\0')
                return $"Error: filename contains invalid character '{badChar}'. Use only letters, digits, hyphens, underscores and dots.";

            var outDir = Path.Combine(BaseDir, "agent-out");
            Directory.CreateDirectory(outDir);
            var full = Path.Combine(outDir, filename);

            try
            {
                File.WriteAllText(full, content);
                return $"Written {content.Length} chars to agent-out/{filename}";
            }
            catch (Exception ex)
            {
                return $"Error writing file: {ex.Message}";
            }
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
            try
            {
                File.WriteAllText(full, html);
                NotifyBar("REFRESH_PANEL");
                return $"Panel updated ({html.Length} chars)";
            }
            catch (Exception ex)
            {
                return $"Error updating panel: {ex.Message}";
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
