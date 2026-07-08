using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CommitBallAgent
{
    sealed class ArchiveRepairResult
    {
        public int DbCount { get; set; }
        public int TxtFixed { get; set; }
        public int MetaFixed { get; set; }
        public int ClusterFixed { get; set; }
        public List<string> Errors { get; } = new();

        public override string ToString()
        {
            var summary = $"Archive file repair done: db={DbCount}, txt+{TxtFixed}, meta+{MetaFixed}, clusters+{ClusterFixed}";
            if (Errors.Count == 0) return summary;
            return summary + $", errors={Errors.Count}";
        }
    }

    sealed class ArchiveClusterInfo
    {
        public int Id { get; set; }
        public string Key { get; set; } = "";
        public string Window { get; set; } = "";
        public string Process { get; set; } = "";
        public string Path { get; set; } = "";
        public string RelPath { get; set; } = "";
        public List<string> WindowSamples { get; } = new();
        public List<string> CommitSamples { get; } = new();
        public StringBuilder Content { get; } = new();
        public string PendingInput { get; set; } = "";
        public string PendingInputStart { get; set; } = "";
        public string PendingInputEnd { get; set; } = "";
        public int EventCount { get; set; }
        public int DirectInputCount { get; set; }
    }

    static class ArchiveRepair
    {
        private enum DbTextProfile
        {
            Raw,
            Agent
        }

        private static string DataDir => Path.GetFullPath(Config.DataDir);
        private static string SessionsDir => Path.Combine(DataDir, "sessions");
        private static string ExportsDir => Path.Combine(DataDir, "exports");

        public static ArchiveRepairResult RepairFiles()
        {
            var result = new ArchiveRepairResult();
            Directory.CreateDirectory(ExportsDir);
            if (!Directory.Exists(SessionsDir))
                return result;

            foreach (var monthDir in Directory.EnumerateDirectories(SessionsDir))
            {
                var month = Path.GetFileName(monthDir);
                if (string.IsNullOrWhiteSpace(month)) continue;
                foreach (var dbPath in Directory.EnumerateFiles(monthDir, "*.db"))
                {
                    result.DbCount++;
                    try
                    {
                        RepairOneArchiveDb(dbPath, month, result);
                    }
                    catch (Exception ex)
                    {
                        result.Errors.Add($"{Path.GetFileName(dbPath)}: {ex.Message}");
                        AgentWindow.Log("ArchiveRepair failed for " + dbPath + ": " + ex);
                    }
                }
            }
            AgentWindow.Log(result.ToString());
            return result;
        }

        private static void RepairOneArchiveDb(string dbPath, string month, ArchiveRepairResult result)
        {
            var sessionId = Path.GetFileNameWithoutExtension(dbPath);
            if (string.IsNullOrWhiteSpace(sessionId)) return;

            var exportDir = Path.Combine(ExportsDir, month);
            Directory.CreateDirectory(exportDir);
            var exportBase = Path.Combine(exportDir, "commitball_" + sessionId);
            var txtPath = exportBase + ".txt";
            var metaPath = exportBase + ".meta.json";
            var clusterDir = exportBase + "_clusters";
            result.TxtFixed += EnsureSessionTextExports(dbPath, txtPath);

            List<ArchiveClusterInfo>? clusters = null;
            if (!File.Exists(metaPath) || NeedsClusterRepair(metaPath, clusterDir))
            {
                clusters = BuildArchiveClusters(dbPath, clusterDir);
                var writtenClusters = WriteMissingArchiveClusters(clusters);
                result.ClusterFixed += writtenClusters;
                if (writtenClusters > 0)
                    AgentWindow.Log($"Archive repair generated missing cluster files: {clusterDir}, files={writtenClusters}");
            }

            if (!File.Exists(metaPath))
            {
                clusters ??= BuildArchiveClusters(dbPath, clusterDir);
                GenerateSessionMetadata(sessionId, dbPath, txtPath, metaPath, clusterDir, clusters);
                result.MetaFixed++;
                AgentWindow.Log("Archive repair generated missing meta: " + metaPath);
            }
        }

        private static int EnsureSessionTextExports(string dbPath, string txtPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(txtPath)!);
            var written = 0;
            var rawPath = WithExportProfileSuffix(txtPath, ".raw");
            if (!File.Exists(rawPath))
            {
                WriteTextFile(rawPath, DbToText(dbPath, DbTextProfile.Raw));
                written++;
                AgentWindow.Log("Archive repair generated missing raw txt: " + rawPath);
            }
            if (!File.Exists(txtPath))
            {
                WriteTextFile(txtPath, DbToText(dbPath, DbTextProfile.Agent));
                written++;
                AgentWindow.Log("Archive repair generated missing agent txt: " + txtPath);
            }
            return written;
        }

        private static void WriteTextFile(string path, string text)
        {
            File.WriteAllText(path, text, Encoding.UTF8);
        }

        private static string WithExportProfileSuffix(string txtPath, string suffix)
        {
            return txtPath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)
                ? txtPath[..^".txt".Length] + suffix + ".txt"
                : txtPath + suffix + ".txt";
        }

        private static string DbToText(string dbPath, DbTextProfile profile = DbTextProfile.Agent)
        {
            var output = new StringBuilder();
            var body = new StringBuilder();
            var curRecordId = -1;
            var firstTs = "";
            var lastTs = "";
            var lastFocus = "";
            var pendingInput = "";
            var pendingInputStart = "";
            var pendingInputEnd = "";
            var skippedFocusRepeats = 0;
            var awayActive = false;

            void FlushRecord()
            {
                if (curRecordId < 0) return;
                if (profile != DbTextProfile.Raw)
                    FlushInput(body, ref pendingInput, ref pendingInputStart, pendingInputEnd);
                if (profile != DbTextProfile.Raw && body.Length == 0) return;
                output.Append("--- #").Append(curRecordId).Append(" [").Append(firstTs).Append(" ~ ").Append(lastTs).AppendLine("] ---");
                output.Append(body);
                if (body.Length > 0 && body[^1] != '\n') output.AppendLine();
                output.AppendLine();
            }

            using var conn = OpenReadOnly(dbPath);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT record_id, ts, type, content FROM log ORDER BY record_id, id";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var recordId = reader.GetInt32(0);
                var ts = reader.IsDBNull(1) ? "" : reader.GetString(1);
                var type = reader.IsDBNull(2) ? "" : reader.GetString(2);
                var content = reader.IsDBNull(3) ? "" : reader.GetString(3);

                if (recordId != curRecordId)
                {
                    FlushRecord();
                    curRecordId = recordId;
                    firstTs = ts;
                    lastTs = ts;
                    body.Clear();
                    pendingInput = "";
                    pendingInputStart = "";
                    pendingInputEnd = "";
                    skippedFocusRepeats = 0;
                }
                else
                {
                    lastTs = ts;
                }

                if (type.StartsWith("focus", StringComparison.Ordinal))
                {
                    if (profile == DbTextProfile.Raw)
                    {
                        AppendEventLine(body, ts, type, FlattenText(content));
                        continue;
                    }
                    if (profile != DbTextProfile.Raw && ExcludedFocus(content)) continue;
                    FlushInput(body, ref pendingInput, ref pendingInputStart, pendingInputEnd);
                    if (profile != DbTextProfile.Raw && content == lastFocus)
                    {
                        skippedFocusRepeats++;
                        continue;
                    }
                    skippedFocusRepeats = 0;
                    lastFocus = content;
                    AppendEventLine(body, ts, "focus", content);
                }
                else if (type == "direct-input")
                {
                    if (profile == DbTextProfile.Raw)
                    {
                        AppendEventLine(body, ts, type, FlattenText(content));
                    }
                    else
                    {
                        FlushInput(body, ref pendingInput, ref pendingInputStart, pendingInputEnd);
                        AppendEventLine(body, ts, "direct", content);
                    }
                }
                else if (type == "click")
                {
                    if (profile == DbTextProfile.Raw)
                    {
                        AppendEventLine(body, ts, type, FlattenText(content));
                    }
                    else if (pendingInput.Length > 0)
                    {
                        FlushInput(body, ref pendingInput, ref pendingInputStart, pendingInputEnd);
                        AppendEventLine(body, ts, "click", content);
                    }
                }
                else if (type == "timer")
                {
                    if (profile == DbTextProfile.Raw)
                        AppendEventLine(body, ts, type, FlattenText(content));
                }
                else if (type == "away")
                {
                    if (profile == DbTextProfile.Raw)
                    {
                        AppendEventLine(body, ts, type, FlattenText(content));
                    }
                    else
                    {
                        if (awayActive) continue;
                        FlushInput(body, ref pendingInput, ref pendingInputStart, pendingInputEnd);
                        awayActive = true;
                        AppendEventLine(body, ts, "away", content);
                    }
                }
                else if (type == "back")
                {
                    if (profile == DbTextProfile.Raw)
                    {
                        AppendEventLine(body, ts, type, FlattenText(content));
                    }
                    else
                    {
                        FlushInput(body, ref pendingInput, ref pendingInputStart, pendingInputEnd);
                        awayActive = false;
                        AppendEventLine(body, ts, "back", content);
                    }
                }
                else if (type == "commit")
                {
                    if (profile == DbTextProfile.Raw)
                    {
                        AppendEventLine(body, ts, type, FlattenText(content));
                    }
                    else
                    {
                        if (pendingInput.Length == 0) pendingInputStart = ts;
                        pendingInputEnd = ts;
                        pendingInput += content;
                    }
                }
                else if (type is "paste" or "paste-big" or "paste-mega")
                {
                    if (profile != DbTextProfile.Raw)
                        FlushInput(body, ref pendingInput, ref pendingInputStart, pendingInputEnd);
                    AppendEventLine(body, ts, type, FlattenText(content));
                }
                else
                {
                    if (profile == DbTextProfile.Raw)
                        AppendEventLine(body, ts, type, type == "keystroke" ? KeyText(content) : FlattenText(content));
                    else if (type == "keystroke")
                    {
                        FlushInput(body, ref pendingInput, ref pendingInputStart, pendingInputEnd);
                        AppendEventLine(body, ts, "key", KeyText(content));
                    }
                }
            }
            FlushRecord();
            return output.ToString();
        }

        private static void GenerateSessionMetadata(
            string sessionId,
            string dbPath,
            string txtPath,
            string metaPath,
            string clusterDir,
            List<ArchiveClusterInfo> clusters)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(metaPath)!);

            var startedAt = "";
            var endedAt = "";
            var firstDirect = "";
            var totalRows = 0;
            var directInputCount = 0;
            var focusCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var processCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var commitSamples = new List<string>();

            using var conn = OpenReadOnly(dbPath);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT ts, type, content FROM log ORDER BY id";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var ts = reader.IsDBNull(0) ? "" : reader.GetString(0);
                var type = reader.IsDBNull(1) ? "" : reader.GetString(1);
                var content = reader.IsDBNull(2) ? "" : reader.GetString(2);
                totalRows++;
                if (startedAt.Length == 0) startedAt = ts;
                endedAt = ts;

                if (type.StartsWith("focus", StringComparison.Ordinal) && content.Length > 0)
                {
                    if (ExcludedFocus(content)) continue;
                    focusCounts[content] = focusCounts.GetValueOrDefault(content) + 1;
                    var proc = FocusProcessName(content);
                    if (proc.Length > 0) processCounts[proc] = processCounts.GetValueOrDefault(proc) + 1;
                }
                else if (type == "direct-input" && content.Length > 0)
                {
                    directInputCount++;
                    if (firstDirect.Length == 0) firstDirect = content;
                }
                else if (type == "commit" && content.Length > 0)
                {
                    AddUniqueLimited(commitSamples, content, 5, 180);
                }
            }

            var ranked = focusCounts.OrderByDescending(kv => kv.Value).ToList();
            var rankedProc = processCounts.OrderByDescending(kv => kv.Value).ToList();
            var title = "CommitBall session";
            if (firstDirect.Length > 0)
                title = Shorten(firstDirect, 80);
            else if (ranked.Count > 0)
            {
                var windowTitle = FocusWindowTitle(ranked[0].Key);
                title = Shorten(windowTitle.Length == 0 ? ranked[0].Key : windowTitle, 80);
            }

            var tags = new List<string>();
            foreach (var proc in rankedProc.Take(4))
                AddUniqueLimited(tags, proc.Key, 5, 32);
            if (commitSamples.Count > 0)
                AddUniqueLimited(tags, "commit", 5, 32);

            var ruleSummary = BuildRuleSummary(ranked, commitSamples, totalRows, startedAt, endedAt);
            var meta = new JsonObject
            {
                ["session_id"] = sessionId,
                ["started_at"] = startedAt,
                ["ended_at"] = endedAt,
                ["txt_path"] = ToDataPrefixedPath(txtPath),
                ["txt_profile"] = "agent",
                ["raw_txt_path"] = ToDataPrefixedPath(WithExportProfileSuffix(txtPath, ".raw")),
                ["db_path"] = ToDataPrefixedPath(dbPath),
                ["cluster_strategy"] = "process",
                ["cluster_dir"] = ToDataRelativePath(clusterDir),
                ["cluster_count"] = clusters.Count,
                ["title"] = title,
                ["work_tags"] = new JsonArray(tags.Select(t => JsonValue.Create(t)).ToArray()),
                ["rule_summary"] = ruleSummary,
                ["source"] = "rule",
                ["event_count"] = totalRows,
                ["direct_input_count"] = directInputCount,
                ["focus_top"] = new JsonArray(ranked.Take(5).Select(kv => new JsonObject
                {
                    ["window"] = FocusWindowTitle(kv.Key),
                    ["process"] = FocusProcessName(kv.Key),
                    ["count"] = kv.Value
                }).ToArray()),
                ["clusters"] = new JsonArray(clusters.Select(cluster => new JsonObject
                {
                    ["id"] = $"cluster_{cluster.Id:00}",
                    ["window"] = cluster.Window,
                    ["process"] = cluster.Process,
                    ["txt_path"] = cluster.RelPath,
                    ["event_count"] = cluster.EventCount,
                    ["direct_input_count"] = cluster.DirectInputCount,
                    ["window_samples"] = new JsonArray(cluster.WindowSamples.Select(s => JsonValue.Create(s)).ToArray()),
                    ["rule_summary"] = ClusterRuleSummary(cluster)
                }).ToArray())
            };

            File.WriteAllText(metaPath, meta.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            }), Encoding.UTF8);
        }

        private static List<ArchiveClusterInfo> BuildArchiveClusters(string dbPath, string clusterDir)
        {
            var clusters = new List<ArchiveClusterInfo>();
            var clusterIndex = new Dictionary<string, ArchiveClusterInfo>(StringComparer.OrdinalIgnoreCase);
            var currentFocus = "misc|misc";

            void FlushClusterInput(ArchiveClusterInfo cluster)
            {
                if (cluster.PendingInput.Length == 0) return;
                var range = cluster.PendingInputStart;
                if (!string.IsNullOrWhiteSpace(cluster.PendingInputEnd) && cluster.PendingInputEnd != cluster.PendingInputStart)
                    range += "~" + cluster.PendingInputEnd;
                cluster.Content.Append('[').Append(range).Append("] [input] ").Append(cluster.PendingInput).AppendLine();
                cluster.EventCount++;
                AddUniqueLimited(cluster.CommitSamples, cluster.PendingInput, 5, 180);
                cluster.PendingInput = "";
                cluster.PendingInputStart = "";
                cluster.PendingInputEnd = "";
            }

            void AppendClusterEvent(ArchiveClusterInfo cluster, string ts, string tag, string content)
            {
                cluster.EventCount++;
                cluster.Content.Append('[').Append(ts).Append("] [").Append(tag).Append(']');
                if (content.Length > 0) cluster.Content.Append(' ').Append(content);
                cluster.Content.AppendLine();
            }

            ArchiveClusterInfo EnsureCluster(string focus)
            {
                var process = FocusProcessName(focus);
                if (process.Length == 0) process = "unknown";
                if (clusterIndex.TryGetValue(process, out var existing))
                    return existing;

                var info = new ArchiveClusterInfo
                {
                    Id = clusters.Count + 1,
                    Key = process,
                    Window = FocusWindowTitle(focus),
                    Process = process
                };
                AddUniqueLimited(info.WindowSamples, info.Window, 5, 100);
                var stem = SafeFileStem(info.Process.Length == 0 ? info.Window : info.Process);
                info.Path = Path.Combine(clusterDir, $"cluster_{info.Id:00}_{stem}.txt");
                info.RelPath = ToDataRelativePath(info.Path);
                clusters.Add(info);
                clusterIndex[process] = info;
                return info;
            }

            using var conn = OpenReadOnly(dbPath);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT ts, type, content FROM log ORDER BY id";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var ts = reader.IsDBNull(0) ? "" : reader.GetString(0);
                var type = reader.IsDBNull(1) ? "" : reader.GetString(1);
                var content = reader.IsDBNull(2) ? "" : reader.GetString(2);
                if (type.StartsWith("focus", StringComparison.Ordinal) && content.Length > 0)
                {
                    if (ExcludedFocus(content)) continue;
                    currentFocus = content;
                }

                var cluster = EnsureCluster(currentFocus);
                if (type.StartsWith("focus", StringComparison.Ordinal) && content.Length > 0)
                {
                    FlushClusterInput(cluster);
                    var window = FocusWindowTitle(content);
                    AddUniqueLimited(cluster.WindowSamples, window, 5, 100);
                    if (cluster.Window.Length == 0) cluster.Window = window;
                    AppendClusterEvent(cluster, ts, "focus", content);
                }
                else if (type == "direct-input")
                {
                    FlushClusterInput(cluster);
                    cluster.DirectInputCount++;
                    AppendClusterEvent(cluster, ts, "direct", content);
                }
                else if (type == "commit" && content.Length > 0)
                {
                    if (cluster.PendingInput.Length == 0) cluster.PendingInputStart = ts;
                    cluster.PendingInputEnd = ts;
                    cluster.PendingInput += content;
                }
                else if (type is "paste" or "paste-big" or "paste-mega")
                {
                    FlushClusterInput(cluster);
                    AppendClusterEvent(cluster, ts, type, FlattenText(content));
                }
                else if (type is "away" or "back")
                {
                    FlushClusterInput(cluster);
                    AppendClusterEvent(cluster, ts, type, content);
                }
                else if (type == "keystroke")
                {
                    FlushClusterInput(cluster);
                    AppendClusterEvent(cluster, ts, "key", KeyText(content));
                }
                else if (type == "click")
                {
                    if (cluster.PendingInput.Length == 0) continue;
                    FlushClusterInput(cluster);
                    AppendClusterEvent(cluster, ts, "click", content);
                }
            }

            foreach (var cluster in clusters)
                FlushClusterInput(cluster);

            return clusters.OrderByDescending(c => c.EventCount).ToList();
        }

        private static int WriteMissingArchiveClusters(List<ArchiveClusterInfo> clusters)
        {
            var written = 0;
            foreach (var cluster in clusters)
            {
                if (cluster.Content.Length == 0) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(cluster.Path)!);
                if (File.Exists(cluster.Path)) continue;
                File.WriteAllText(cluster.Path, cluster.Content.ToString(), Encoding.UTF8);
                written++;
            }
            return written;
        }

        private static bool NeedsClusterRepair(string metaPath, string clusterDir)
        {
            if (TryReadClusterPathsFromMeta(metaPath, out var expectedPaths))
            {
                if (expectedPaths.Count == 0) return false;
                return expectedPaths.Any(path => !File.Exists(ToAbsoluteDataPath(path)));
            }

            return !Directory.Exists(clusterDir) ||
                   !Directory.EnumerateFiles(clusterDir, "*.txt").Any();
        }

        private static bool TryReadClusterPathsFromMeta(string metaPath, out List<string> paths)
        {
            paths = new List<string>();
            try
            {
                if (!File.Exists(metaPath)) return false;
                var root = JsonNode.Parse(File.ReadAllText(metaPath)) as JsonObject;
                if (root?["clusters"] is not JsonArray clusters) return false;
                foreach (var node in clusters)
                {
                    if (node is not JsonObject cluster) continue;
                    var rel = cluster["txt_path"]?.GetValue<string>() ?? "";
                    if (!string.IsNullOrWhiteSpace(rel))
                        paths.Add(rel);
                }
                return true;
            }
            catch (Exception ex)
            {
                AgentWindow.Log("ArchiveRepair could not inspect cluster paths in meta: " + metaPath + ": " + ex.Message);
                return false;
            }
        }

        private static SqliteConnection OpenReadOnly(string dbPath)
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly
            };
            var conn = new SqliteConnection(builder.ToString());
            conn.Open();
            return conn;
        }

        private static string BuildRuleSummary(List<KeyValuePair<string, int>> ranked, List<string> commitSamples, int totalRows, string startedAt, string endedAt)
        {
            var sb = new StringBuilder();
            if (ranked.Count > 0)
            {
                sb.Append("Top windows: ");
                foreach (var item in ranked.Take(3).Select((kv, i) => (kv, i)))
                {
                    if (item.i > 0) sb.Append("; ");
                    var title = FocusWindowTitle(item.kv.Key);
                    var proc = FocusProcessName(item.kv.Key);
                    sb.Append(Shorten(title.Length == 0 ? item.kv.Key : title, 80));
                    if (proc.Length > 0) sb.Append(" (").Append(proc).Append(')');
                    sb.Append(" x").Append(item.kv.Value);
                }
                sb.Append(". ");
            }
            if (commitSamples.Count > 0)
            {
                sb.Append("Commit notes: ");
                sb.Append(string.Join(" / ", commitSamples));
                sb.Append(". ");
            }
            if (sb.Length == 0)
            {
                sb.Append("Recorded ").Append(totalRows).Append(" events");
                if (startedAt.Length > 0 || endedAt.Length > 0)
                    sb.Append(", from ").Append(startedAt).Append(" to ").Append(endedAt);
                sb.Append('.');
            }
            return Shorten(sb.ToString(), 600);
        }

        private static string ClusterRuleSummary(ArchiveClusterInfo cluster)
        {
            var sb = new StringBuilder();
            sb.Append("Process: ").Append(cluster.Process.Length == 0 ? "(unknown)" : cluster.Process);
            sb.Append(", events=").Append(cluster.EventCount);
            if (cluster.WindowSamples.Count > 0)
                sb.Append(", windows: ").Append(string.Join(" / ", cluster.WindowSamples));
            if (cluster.CommitSamples.Count > 0)
                sb.Append(", commit notes: ").Append(string.Join(" / ", cluster.CommitSamples));
            return Shorten(sb.ToString(), 500);
        }

        private static string FocusProcessName(string focus)
        {
            var bar = focus.LastIndexOf('|');
            if (bar < 0) return TrimCopy(focus);
            return bar + 1 < focus.Length ? TrimCopy(focus[(bar + 1)..]) : "";
        }

        private static string FocusWindowTitle(string focus)
        {
            var bar = focus.LastIndexOf('|');
            return TrimCopy(bar >= 0 ? focus[..bar] : focus);
        }

        private static bool ExcludedFocus(string focus)
        {
            var proc = FocusProcessName(focus).ToUpperInvariant();
            if (proc.Length == 0 || FocusWindowTitle(focus).Length == 0) return true;
            return (proc is "TEXTINPUTHOST.EXE"
                or "SHELLEXPERIENCEHOST.EXE"
                or "STARTMENUEXPERIENCEHOST.EXE"
                or "SEARCHAPP.EXE"
                or "LOCKAPP.EXE"
                or "COMMITBALL-BAR.EXE"
                or "COMMITBALL-AGENT.EXE"
                or "COMMITBALL-BALLSHELL.EXE"
                or "GIT-CREDENTIAL-MANAGER.EXE")
                || proc.Contains("INSTALLER", StringComparison.Ordinal);
        }

        private static void FlushInput(StringBuilder body, ref string input, ref string inputStart, string inputEnd)
        {
            if (input.Length == 0) return;
            EnsureLineBreak(body);
            body.Append('[').Append(inputStart);
            if (!string.IsNullOrWhiteSpace(inputEnd) && inputEnd != inputStart)
                body.Append('~').Append(inputEnd);
            body.Append("] [input] ").Append(input).AppendLine();
            input = "";
            inputStart = "";
        }

        private static void AppendEventLine(StringBuilder body, string ts, string tag, string content)
        {
            EnsureLineBreak(body);
            body.Append('[').Append(ts).Append("] [").Append(tag).Append(']');
            if (!string.IsNullOrEmpty(content)) body.Append(' ').Append(content);
            body.AppendLine();
        }

        private static string FlattenText(string text)
        {
            return text.Replace("\r\n", "\u21B5").Replace("\n", "\u21B5");
        }

        private static string KeyText(string text)
        {
            var shortMap = new Dictionary<string, string>
            {
                ["[Backspace]"] = "[<bs]",
                ["[Tab]"] = "[<tab]",
                ["[Enter]"] = "[<cr]",
                ["[Delete]"] = "[<del]",
                ["[Left]"] = "[<-]",
                ["[Right]"] = "[->]",
                ["[Up]"] = "[<up]",
                ["[Down]"] = "[<dn]",
                ["[Home]"] = "[<hm]",
                ["[End]"] = "[<end]",
                ["[PageUp]"] = "[<pu]",
                ["[PageDown]"] = "[<pd]",
                ["[Esc]"] = "[<esc]",
                ["[Copy]"] = "[<copy]",
                ["[Cut]"] = "[<cut]",
                ["[Undo]"] = "[<undo]",
                ["[Paste]"] = "[<paste]",
            };
            foreach (var pair in shortMap)
                text = text.Replace(pair.Key, pair.Value, StringComparison.Ordinal);
            return text;
        }

        private static void AddUniqueLimited(List<string> items, string value, int maxItems, int maxLen)
        {
            var clean = Shorten(TrimCopy(value), maxLen);
            if (clean.Length == 0) return;
            if (items.Contains(clean, StringComparer.OrdinalIgnoreCase)) return;
            if (items.Count < maxItems) items.Add(clean);
        }

        private static string SafeFileStem(string value)
        {
            var sb = new StringBuilder();
            foreach (var c in value)
            {
                if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_')
                    sb.Append(c);
                else if (sb.Length == 0 || sb[^1] != '_')
                    sb.Append('_');
                if (sb.Length >= 40) break;
            }
            while (sb.Length > 0 && sb[^1] == '_')
                sb.Length--;
            return sb.Length == 0 ? "focus" : sb.ToString();
        }

        private static string ToDataPrefixedPath(string path)
        {
            return "data/" + ToDataRelativePath(path);
        }

        private static string ToDataRelativePath(string path)
        {
            var full = Path.GetFullPath(path);
            var data = DataDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (full.StartsWith(data, StringComparison.OrdinalIgnoreCase))
                return full[data.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');
            return path.Replace('\\', '/');
        }

        private static string ToAbsoluteDataPath(string path)
        {
            var normalized = path.Replace('\\', '/').TrimStart('/');
            if (normalized.StartsWith("data/", StringComparison.OrdinalIgnoreCase))
                normalized = normalized["data/".Length..];
            if (Path.IsPathRooted(path)) return Path.GetFullPath(path);
            return Path.GetFullPath(Path.Combine(DataDir, normalized.Replace('/', Path.DirectorySeparatorChar)));
        }

        private static bool TextFileContains(string path, string needle)
        {
            try
            {
                return File.Exists(path) && File.ReadAllText(path).Contains(needle, StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        private static void EnsureLineBreak(StringBuilder sb)
        {
            if (sb.Length > 0 && sb[^1] != '\n') sb.AppendLine();
        }

        private static string TrimCopy(string value) => value.Trim();

        private static string Shorten(string value, int maxLen)
        {
            if (value.Length <= maxLen) return value;
            return value[..maxLen];
        }
    }
}
