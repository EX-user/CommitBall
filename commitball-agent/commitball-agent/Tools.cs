using System;
using System.Collections.Generic;
using System.IO;
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
            var pwdDef = "{\"type\":\"function\",\"function\":{\"name\":\"pwd\",\"description\":\"Returns the directory where CommitBall-Agent.exe is located.\",\"parameters\":{\"type\":\"object\",\"properties\":{}}}}";
            var subtaskDef = "{\"type\":\"function\",\"function\":{\"name\":\"subtask\",\"description\":\"Launch a sub-task session to accomplish a complex goal. The sub-task has its own conversation and can use list/read/write tools. Returns the final result.\",\"parameters\":{\"type\":\"object\",\"properties\":{\"prompt\":{\"type\":\"string\",\"description\":\"The task description for the sub-task to accomplish\"}},\"required\":[\"prompt\"]}}}";

            var tools = new List<string> { listDef, readDef, writeDef, pwdDef };
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
                   "If data/agent-out/summary_task_exp_decay_memory.md exists, read it for background context on the user's recent activities." + 
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
    }
}
