using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
        public int EventCount { get; set; }
        public int DirectInputCount { get; set; }
    }

    static class ArchiveRepair
    {
        private enum DbTextProfile
        {
            Raw,
            Summary,
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
            var rawTxtPath = WithExportProfileSuffix(txtPath, ".raw");
            var summaryTxtPath = WithExportProfileSuffix(txtPath, ".summary");

            if (!File.Exists(txtPath) || !File.Exists(rawTxtPath) || !File.Exists(summaryTxtPath))
            {
                ExportSessionDb(dbPath, txtPath);
                result.TxtFixed++;
                AgentWindow.Log("Archive repair exported txt: " + txtPath);
            }

            if (!File.Exists(metaPath))
            {
                GenerateSessionMetadata(sessionId, dbPath, txtPath, metaPath);
                result.MetaFixed++;
                result.ClusterFixed++;
                AgentWindow.Log("Archive repair generated missing meta/clusters: " + metaPath);
            }
        }

        private static void ExportSessionDb(string dbPath, string txtPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(txtPath)!);
            WriteTextFile(WithExportProfileSuffix(txtPath, ".raw"), DbToText(dbPath, DbTextProfile.Raw));
            WriteTextFile(WithExportProfileSuffix(txtPath, ".summary"), DbToText(dbPath, DbTextProfile.Summary));
            WriteTextFile(txtPath, DbToText(dbPath, DbTextProfile.Agent));
        }

        private static void WriteTextFile(string path, string text)
        {
            if (text.Length == 0) return;
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
            };

            var output = new StringBuilder();
            var body = new StringBuilder();
            var curRecordId = -1;
            var firstTs = "";
            var lastTs = "";
            var lastFocus = "";
            var skippedFocusRepeats = 0;
            var wroteTimerInRecord = false;

            void FlushRecord()
            {
                if (curRecordId < 0) return;
                if (skippedFocusRepeats > 0 && profile == DbTextProfile.Raw)
                {
                    EnsureLineBreak(body);
                    body.Append("[focus-repeat] +").Append(skippedFocusRepeats).AppendLine();
                }
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
                    skippedFocusRepeats = 0;
                    wroteTimerInRecord = false;
                }
                else
                {
                    lastTs = ts;
                }

                if (type.StartsWith("focus", StringComparison.Ordinal))
                {
                    if (profile != DbTextProfile.Raw && ExcludedFocus(content)) continue;
                    if (profile != DbTextProfile.Raw && content == lastFocus)
                    {
                        skippedFocusRepeats++;
                        continue;
                    }
                    if (skippedFocusRepeats > 0 && profile == DbTextProfile.Raw)
                    {
                        EnsureLineBreak(body);
                        body.Append("[focus-repeat] +").Append(skippedFocusRepeats).AppendLine();
                    }
                    skippedFocusRepeats = 0;
                    lastFocus = content;
                    EnsureLineBreak(body);
                    body.Append('[').Append(type).Append("] ").Append(content).AppendLine();
                }
                else if (type == "direct-input")
                {
                    EnsureLineBreak(body);
                    if (profile == DbTextProfile.Summary)
                        content = Shorten(content, 200) + (content.Length > 200 ? "..." : "");
                    body.Append("[direct] ").Append(content).AppendLine();
                }
                else if (type == "click")
                {
                    if (profile != DbTextProfile.Raw) continue;
                    EnsureLineBreak(body);
                    body.Append("[click]").Append(content).AppendLine();
                }
                else if (type == "timer")
                {
                    if (profile != DbTextProfile.Raw && wroteTimerInRecord) continue;
                    EnsureLineBreak(body);
                    var timerTs = ts.Length >= 16 ? ts.Substring(11, 5) : ts;
                    body.Append("[timer] ").Append(timerTs).AppendLine();
                    wroteTimerInRecord = true;
                }
                else if (type == "away")
                {
                    EnsureLineBreak(body);
                    var awayTs = ts.Length >= 16 ? ts.Substring(11, 5) : ts;
                    body.Append("[away] ").Append(awayTs).Append(' ').Append(content).AppendLine();
                }
                else if (type == "back")
                {
                    EnsureLineBreak(body);
                    var backTs = ts.Length >= 16 ? ts.Substring(11, 5) : ts;
                    body.Append("[back] ").Append(backTs).Append(' ').Append(content).AppendLine();
                }
                else if (type == "commit")
                {
                    EnsureLineBreak(body);
                    body.Append("[commit] ").Append(content).AppendLine();
                }
                else if (type is "paste" or "paste-big" or "paste-mega")
                {
                    EnsureLineBreak(body);
                    var paste = content.Replace("\r\n", "\u21B5").Replace("\n", "\u21B5");
                    if (profile == DbTextProfile.Summary)
                    {
                        var preview = Shorten(paste, 160);
                        if (paste.Length > 160) preview += "...";
                        body.Append('[').Append(type).Append(" chars=").Append(paste.Length).Append(']').Append(preview).AppendLine();
                    }
                    else
                    {
                        body.Append('[').Append(type).Append(']').Append(paste).AppendLine();
                    }
                }
                else
                {
                    if (profile != DbTextProfile.Raw) continue;
                    foreach (var pair in shortMap)
                        content = content.Replace(pair.Key, pair.Value, StringComparison.Ordinal);
                    body.Append(content);
                }
            }
            FlushRecord();
            return output.ToString();
        }

        private static void GenerateSessionMetadata(string sessionId, string dbPath, string txtPath, string metaPath)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(metaPath)!);
            var clusterDir = metaPath.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase)
                ? metaPath[..^".meta.json".Length] + "_clusters"
                : metaPath + "_clusters";
            var clusters = WriteArchiveClusters(dbPath, clusterDir);

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
                ["summary_txt_path"] = ToDataPrefixedPath(WithExportProfileSuffix(txtPath, ".summary")),
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

            File.WriteAllText(metaPath, meta.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
        }

        private static List<ArchiveClusterInfo> WriteArchiveClusters(string dbPath, string clusterDir)
        {
            Directory.CreateDirectory(clusterDir);
            foreach (var file in Directory.EnumerateFiles(clusterDir))
                File.Delete(file);

            var clusters = new List<ArchiveClusterInfo>();
            var clusterIndex = new Dictionary<string, ArchiveClusterInfo>(StringComparer.OrdinalIgnoreCase);
            var currentFocus = "unknown|unknown";

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
                    currentFocus = content;

                var cluster = EnsureCluster(currentFocus);
                if (type.StartsWith("focus", StringComparison.Ordinal) && content.Length > 0)
                {
                    var window = FocusWindowTitle(content);
                    AddUniqueLimited(cluster.WindowSamples, window, 5, 100);
                    if (cluster.Window.Length == 0) cluster.Window = window;
                }
                cluster.EventCount++;
                if (type == "direct-input") cluster.DirectInputCount++;
                if (type == "commit" && content.Length > 0)
                    AddUniqueLimited(cluster.CommitSamples, content, 5, 180);

                File.AppendAllText(cluster.Path, $"[{ts}] [{type}] {content}\n", Encoding.UTF8);
            }

            return clusters.OrderByDescending(c => c.EventCount).ToList();
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
            return TrimCopy(bar >= 0 && bar + 1 < focus.Length ? focus[(bar + 1)..] : focus);
        }

        private static string FocusWindowTitle(string focus)
        {
            var bar = focus.LastIndexOf('|');
            return TrimCopy(bar >= 0 ? focus[..bar] : focus);
        }

        private static bool ExcludedFocus(string focus)
        {
            var proc = FocusProcessName(focus).ToUpperInvariant();
            return proc is "TEXTINPUTHOST.EXE" or "SHELLEXPERIENCEHOST.EXE" or "STARTMENUEXPERIENCEHOST.EXE";
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
