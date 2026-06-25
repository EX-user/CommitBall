using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Navigation;

namespace CommitBallAgent
{
    public partial class AgentWindow : Window
    {
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool OpenClipboard(IntPtr hWndNewOwner);

        [DllImport("user32.dll")]
        private static extern bool CloseClipboard();

        [DllImport("user32.dll")]
        private static extern bool EmptyClipboard();

        [DllImport("user32.dll")]
        private static extern IntPtr SetClipboardData(uint uFormat, IntPtr hMem);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalAlloc(uint uFlags, UIntPtr dwBytes);

        [DllImport("kernel32.dll")]
        private static extern IntPtr GlobalLock(IntPtr hMem);

        [DllImport("kernel32.dll")]
        private static extern bool GlobalUnlock(IntPtr hMem);

        private static void SetClipboardText(string text)
        {
            var bytes = System.Text.Encoding.Unicode.GetBytes(text + "\0");
            var hMem = GlobalAlloc(0x0042, (UIntPtr)bytes.Length);
            var ptr = GlobalLock(hMem);
            Marshal.Copy(bytes, 0, ptr, bytes.Length);
            GlobalUnlock(hMem);
            if (OpenClipboard(IntPtr.Zero))
            {
                EmptyClipboard();
                SetClipboardData(13, hMem);
                CloseClipboard();
            }
        }

        private static readonly string LogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "log", "agent.log");
        private static readonly string StatusPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "agent-status");

        private static void WriteStatus(string status)
        {
            try { File.WriteAllText(StatusPath, status); } catch { }
        }

        public static void Log(string msg)
        {
            try
            {
                var dir = Path.GetDirectoryName(LogPath);
                if (dir != null && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
            }
            catch { }
        }

        private Session _session;
        private CancellationTokenSource? _cts;
        private bool _isBusy;
        private bool _inSessionMenu;
        private long _lastOutputTick;
        private int _escCount;
        private long _firstEscTick;
        private readonly Queue<string> _invokeQueue = new();

        public AgentWindow()
        {
            InitializeComponent();
            WriteStatus("idle");
            PositionWindow();
            OutputBox.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, OnOutputCopy));

            if (!Config.IsConfigured)
            {
                AppendOutput("CommitBall Agent Terminal v0.1.3\n\n", "#FFFFFF");
                AppendOutput("未检测到 API 配置。请使用 /vendor 命令配置：\n\n", "#E8915A");
                AppendOutput("  /vendor {\"base_url\":\"...\",\"model\":\"...\",\"api_key\":\"...\"}\n\n");
                AppendOutput("常用提供商：\n");
                AppendOutput("  DeepSeek:    base_url=https://api.deepseek.com   model=deepseek-chat\n");
                AppendOutput("  OpenAI:      base_url=https://api.openai.com      model=gpt-4o-mini\n");
                AppendOutput("  SiliconFlow: base_url=https://api.siliconflow.cn   model=Qwen/Qwen3-8B\n\n");
                return;
            }

            var sessions = Memory.ListSessions();
            if (sessions.Count > 0)
                _session = Memory.LoadOrCreate(sessions[0].Id);
            else
                _session = Memory.LoadOrCreate();
            AppendOutput($"CommitBall Agent Terminal v0.1.3\n");
            AppendOutput(FormatSessionHeader(_session));
            foreach (var msg in _session.Messages)
            {
                if (msg.Role == "user")
                    AppendOutput($"> {msg.Content}\n", "#FFFFFF");
                else if (msg.Role == "display")
                {
                    Log($"Loading display: {msg.Content}");
                    AppendToolDone(msg.Content);
                }
                else if (msg.Role == "assistant" && !string.IsNullOrEmpty(msg.Content))
                    AppendOutput($"{msg.Content}\n\n");
            }
        }

        private void OnOutputCopy(object sender, ExecutedRoutedEventArgs? e)
        {
            try
            {
                var sel = OutputBox.Selection;
                if (!sel.IsEmpty)
                    SetClipboardText(sel.Text);
            }
            catch { }
            if (e != null) e.Handled = true;
        }

        private void PositionWindow()
        {
            var workArea = SystemParameters.WorkArea;
            Width = Math.Max(480, Math.Min(680, workArea.Width * 0.3));
            Height = Math.Max(360, Math.Min(480, workArea.Height * 0.4));
            Left = (workArea.Width - Width) / 2 + workArea.Left;
            Top = workArea.Top + workArea.Height * 0.1;
        }

        private void Window_KeyDown(object sender, KeyEventArgs e)
        {
            Log($"KeyDown: {e.Key} busy={_isBusy}");
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                if (_isBusy)
                {
                    var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    if (_escCount == 0)
                    {
                        _escCount = 1;
                        _firstEscTick = now;
                        return;
                    }
                    if (now - _firstEscTick < 1000)
                    {
                        Log("Esc×2: cancelling + clearing queue");
                        _cts?.Cancel();
                        lock (_invokeQueue) _invokeQueue.Clear();
                    }
                    _escCount = 0;
                    return;
                }
                var elapsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - Interlocked.Read(ref _lastOutputTick);
                if (elapsed < 500)
                {
                    Log($"Esc: suppressed ({elapsed}ms)");
                    return;
                }
                Log("Esc: hiding window");
                Hide();
            }
        }

        private void BgBtn_Click(object sender, RoutedEventArgs e)
        {
            Hide();
        }

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                DragMove();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            InputBox.Focus();
        }

        private void OutputBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                e.Handled = true;
                try
                {
                    var sel = OutputBox.Selection;
                    if (!sel.IsEmpty)
                        SetClipboardText(sel.Text);
                }
                catch { }
            }
        }

        private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.C && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                if (InputBox.SelectionLength > 0)
                {
                    e.Handled = true;
                    try { SetClipboardText(InputBox.SelectedText); } catch { }
                }
            }
        }

        private async void InputBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                if (_isBusy) return;
                var text = InputBox.Text.Trim();
                InputBox.Clear();
                if (string.IsNullOrEmpty(text)) return;
                ProcessInput(text);
            }
        }

        private async void ProcessInput(string text)
        {
            if (text == "/help" || text == "/vendor" || text.StartsWith("/vendor "))
            {
                if (text == "/help")
                {
                    AppendOutput("\nCommands:\n", "#FFFFFF");
                    AppendOutput("  /help      Show this help\n");
                    AppendOutput("  /new       Create a new session\n");
                    AppendOutput("  /session   List and switch sessions\n");
                    AppendOutput("  /analyse          Analyse live.txt work log (subtask mode)\n");
                    AppendOutput("  /summary_to_panel Analyse + panel in one pass (single task)\n");
                    AppendOutput("  /name_archive     Improve one archive .meta.json title/tags/summary\n");
                    AppendOutput("  /repair_archives  Complete missing archive txt/meta/agent analysis\n");
                    AppendOutput("  /vendor           Show or update API config\n");
                    AppendOutput("\n");
                    return;
                }

                if (text == "/vendor")
                {
                    Log("ProcessInput: /vendor show current");
                    AppendOutput("\nCurrent config:\n", "#FFFFFF");
                    AppendOutput($"  base_url: {Config.BaseUrl}\n");
                    AppendOutput($"  model:    {Config.Model}\n");
                    var keyPreview = Config.ApiKey.Length > 0 ? Config.ApiKey.Substring(0, Math.Min(8, Config.ApiKey.Length)) + "..." : "(empty)";
                    AppendOutput($"  api_key:  {keyPreview}\n\n");
                    AppendOutput("  /vendor {\"base_url\":\"...\",\"model\":\"...\",\"api_key\":\"...\"}\n\n");
                    return;
                }

                Log("ProcessInput: /vendor set - starting async validation");
                _ = Task.Run(async () =>
                {
                    Log("/vendor: parsing JSON");
                    var json = text.Substring("/vendor ".Length).Trim();
                    string baseUrl, model, apiKey;
                    try
                    {
                        var doc = System.Text.Json.JsonDocument.Parse(json);
                        var root = doc.RootElement;
                        if (!root.TryGetProperty("base_url", out var buEl) || string.IsNullOrEmpty(buEl.GetString())
                            || !root.TryGetProperty("model", out var mEl) || string.IsNullOrEmpty(mEl.GetString())
                            || !root.TryGetProperty("api_key", out var akEl) || string.IsNullOrEmpty(akEl.GetString()))
                        {
                            Dispatcher.BeginInvoke(() => AppendOutput("\n缺少必要字段，需要 base_url、model、api_key 三个非空字段\n\n", "#E8915A"));
                            return;
                        }
                        baseUrl = buEl.GetString()!;
                        model = mEl.GetString()!;
                        apiKey = akEl.GetString()!;
                    }
                    catch (System.Text.Json.JsonException)
                    {
                        Dispatcher.BeginInvoke(() => AppendOutput("\nJSON parse failed. Check format.\n\n", "#E8915A"));
                        return;
                    }

                    Log($"/vendor: validating {baseUrl} model={model}");
                    Dispatcher.BeginInvoke(() => AppendOutput($"\nValidating {baseUrl} ... ", "#AAAAAE"));
                    var (ok, msg) = await LLMClient.ValidateAsync(baseUrl, model, apiKey);
                    Log($"/vendor: validation result ok={ok} msg={msg}");

                    if (!ok)
                    {
                        Dispatcher.BeginInvoke(() => AppendOutput($"failed\n  {msg}\n\n", "#E8915A"));
                        return;
                    }
                    Dispatcher.BeginInvoke(() =>
                    {
                        AppendOutput("OK\n", "#6ECF6E");
                        Config.Save(baseUrl, model, apiKey);
                        if (_session == null)
                        {
                            var sessions = Memory.ListSessions();
                            _session = sessions.Count > 0 ? Memory.LoadOrCreate(sessions[0].Id) : Memory.LoadOrCreate();
                            AppendOutput($"\nConfig saved → {baseUrl} / {model}\n", "#6ECF6E");
                            AppendOutput(FormatSessionHeader(_session));
                        }
                        else
                        {
                            AppendOutput($"\nConfig updated → {baseUrl} / {model}\n\n", "#6ECF6E");
                        }
                    });
                });
                return;
            }

            if (_inSessionMenu)
            {
                HandleSessionMenuInput(text);
                return;
            }

            if (!Config.IsConfigured)
            {
                AppendOutput("\n请先使用 /vendor 配置 API\n\n", "#E8915A");
                return;
            }

            if (text == "/session")
            {
                EnterSessionMenu();
                return;
            }

            if (text == "/new")
            {
                await Memory.EnsureNamedAsync(_session);
                _session = Memory.LoadOrCreate();
                OutputBox.Document.Blocks.Clear();
                AppendOutput(FormatSessionHeader(_session));
                return;
            }

            if (text == "/analyse" || text.StartsWith("/analyse "))
            {
                var promptFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "analyse-prompt.md");
                string prompt;
                if (File.Exists(promptFile))
                    prompt = File.ReadAllText(promptFile);
                else
                    prompt = "Error: analyse-prompt.md not found";

                if (text.Length > "/analyse".Length)
                    prompt += "\n\n" + text.Substring("/analyse".Length).Trim();

                AppendOutput($"> /analyse\n", "#FFFFFF");
                _ = RunChatAsync(prompt);
                return;
            }

            if (text == "/analyse_st" || text.StartsWith("/analyse_st "))
            {
                var promptFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "analyse-prompt-st.md");
                string prompt;
                if (File.Exists(promptFile))
                    prompt = File.ReadAllText(promptFile);
                else
                    prompt = "Error: analyse-prompt-st.md not found";

                if (text.Length > "/analyse_st".Length)
                    prompt += "\n\n" + text.Substring("/analyse_st".Length).Trim();

                AppendOutput($"> /analyse_st\n", "#FFFFFF");
                _ = RunChatAsync(prompt);
                return;
            }

            if (text == "/summary_to_panel")
            {
                var promptFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "summary_to_panel-prompt.md");
                string prompt;
                if (File.Exists(promptFile))
                    prompt = File.ReadAllText(promptFile);
                else
                    prompt = "Error: summary_to_panel-prompt.md not found";

                AppendOutput($"> /summary_to_panel\n", "#FFFFFF");
                _ = RunChatAsync(prompt);
                return;
            }

            if (text == "/repair_archives")
            {
                RepairArchiveAnalysis();
                return;
            }

            if (text.StartsWith("/name_archive ", StringComparison.OrdinalIgnoreCase))
            {
                var args = text.Substring("/name_archive ".Length)
                    .Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                if (args.Length < 2)
                {
                    AppendOutput("\nUsage: /name_archive exports/YYYY-MM/file.txt exports/YYYY-MM/file.meta.json\n\n", "#E8915A");
                    return;
                }

                var txtFile = args[0].Replace('\\', '/');
                var metaFile = args[1].Replace('\\', '/');
                var prompt =
                    "你正在为 CommitBall 归档 session 生成更准确的标题、工作维度标签、整体摘要和按应用聚类的独立总结。\n" +
                    "请严格按以下步骤执行：\n" +
                    $"1. 使用 read 工具读取 `{metaFile}`，了解现有字段，尤其是 cluster_dir 和 clusters 数组；rule_summary 只是规则摘录，不要直接当成最终摘要。\n" +
                    $"2. 使用 read 工具读取 `{txtFile}`，必要时分段读取，重点关注 direct 输入、focus 窗口、timer 分段和实际工作内容。\n" +
                    "3. 对 meta.clusters 中每个重要 cluster，使用 read 工具读取它的 txt_path。每个 cluster 表示同一应用/process 下按时间顺序归并的操作。\n" +
                    "4. 为每个 cluster 生成独立总结：用户在这个应用里做了什么、可能想做什么、需要提醒用户什么。必须基于 cluster 文件内容，不要猜测不存在的事实。\n" +
                    "5. 生成一个不超过 30 个中文字符或 80 个英文字符的 title。\n" +
                    "6. 生成 3-6 个 work_tags，标签应体现工作维度，而不是泛泛的软件名；可包含项目名、任务类型、文档/代码/测试/调试等维度。\n" +
                    "7. 生成 1-3 句话 summary，必须由你阅读导出文本和 cluster 后总结，忠实于内容，不要猜测不存在的工作。\n" +
                    $"8. 调用 update_meta 工具更新 `{metaFile}`，参数必须包含 title、work_tags、summary；如果读取了 cluster，还要传入 cluster_summaries 数组，cluster_id 使用 meta 中的 id。\n" +
                    "9. 不要使用 write 工具覆盖 meta。最后简短说明已更新的 title、tags 和 cluster 数量。\n";

                AppendOutput($"> /name_archive {txtFile} {metaFile}\n", "#FFFFFF");
                _ = RunChatAsync(prompt);
                return;
            }

            AppendOutput($"> {text}\n", "#FFFFFF");
            _ = RunChatAsync(text);
        }

        private void RepairArchiveAnalysis()
        {
            var exportDir = Path.Combine(Config.DataDir, "exports");
            if (!Directory.Exists(exportDir))
            {
                AppendOutput("\nNo exports directory found.\n\n", "#E8915A");
                return;
            }

            var pending = new List<string>();
            foreach (var metaPath in Directory.GetFiles(exportDir, "*.meta.json", SearchOption.AllDirectories))
            {
                try
                {
                    if (IsArchiveAgentComplete(metaPath)) continue;
                    var metaRel = ToDataRelativePath(metaPath);
                    var txtRel = GetArchiveTxtPath(metaPath);
                    if (string.IsNullOrWhiteSpace(txtRel))
                    {
                        AppendOutput($"Archive missing txt_path: {metaRel}\n", "#E8915A");
                        continue;
                    }
                    pending.Add($"/name_archive {txtRel} {metaRel}");
                }
                catch (Exception ex)
                {
                    AppendOutput($"Archive scan error: {Path.GetFileName(metaPath)} {ex.Message}\n", "#E8915A");
                }
            }

            if (pending.Count == 0)
            {
                AppendOutput("\nArchives already have agent analysis.\n\n", "#6ECF6E");
                return;
            }

            var queue = new List<string>();
            foreach (var command in pending)
            {
                queue.Add("/new");
                queue.Add(command);
            }
            EnqueueInvoke(queue.ToArray());
            AppendOutput($"\nQueued archive agent analysis: {pending.Count}\n\n", "#6ECF6E");
        }

        private static bool IsArchiveAgentComplete(string metaPath)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
            var root = doc.RootElement;
            var sourceOk = root.TryGetProperty("source", out var source) && source.GetString() == "agent";
            var summaryOk = root.TryGetProperty("summary_source", out var summarySource) && summarySource.GetString() == "agent" &&
                root.TryGetProperty("summary", out var summary) && !string.IsNullOrWhiteSpace(summary.GetString());
            if (!sourceOk || !summaryOk) return false;

            if (root.TryGetProperty("clusters", out var clusters) && clusters.ValueKind == JsonValueKind.Array)
            {
                foreach (var cluster in clusters.EnumerateArray())
                {
                    var eventCount = cluster.TryGetProperty("event_count", out var count) && count.ValueKind == JsonValueKind.Number
                        ? count.GetInt32()
                        : 0;
                    if (eventCount <= 0) continue;
                    var hasSummary = cluster.TryGetProperty("summary_source", out var clusterSource) && clusterSource.GetString() == "agent" &&
                        cluster.TryGetProperty("agent_summary", out var agentSummary) && !string.IsNullOrWhiteSpace(agentSummary.GetString());
                    if (!hasSummary) return false;
                }
            }
            return true;
        }

        private static string GetArchiveTxtPath(string metaPath)
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(metaPath));
                var root = doc.RootElement;
                if (root.TryGetProperty("txt_path", out var txtPath))
                {
                    var rel = NormalizeDataRelativePath(txtPath.GetString() ?? "");
                    if (!string.IsNullOrWhiteSpace(rel)) return rel;
                }
            }
            catch { }

            var filename = Path.GetFileName(metaPath);
            if (filename.EndsWith(".meta.json", StringComparison.OrdinalIgnoreCase))
            {
                var txt = metaPath[..^".meta.json".Length] + ".txt";
                return ToDataRelativePath(txt);
            }
            return "";
        }

        private static string ToDataRelativePath(string path)
        {
            var full = Path.GetFullPath(path);
            var baseDir = Path.GetFullPath(Config.DataDir);
            if (full.StartsWith(baseDir, StringComparison.OrdinalIgnoreCase))
                return full[baseDir.Length..].TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');
            return NormalizeDataRelativePath(path);
        }

        private static string NormalizeDataRelativePath(string path)
        {
            var rel = path.Replace('\\', '/').TrimStart('/');
            if (rel.StartsWith("data/", StringComparison.OrdinalIgnoreCase))
                rel = rel[5..];
            return rel;
        }

        private void EnterSessionMenu()
        {
            _inSessionMenu = true;
            OutputBox.Document.Blocks.Clear();
            AppendOutput("--- Sessions ---\n");
            var sessions = Memory.ListSessions();
            if (sessions.Count == 0)
            {
                AppendOutput("(no sessions)\n");
            }
            foreach (var (id, updatedAt, msgCount, title) in sessions)
            {
                var marker = id == _session.Id ? " *" : "";
                var fi = new FileInfo(Path.Combine(Config.MemoryDir, $"{id}.json"));
                var created = fi.Exists ? fi.CreationTime : updatedAt;
                var titleText = string.IsNullOrWhiteSpace(title) ? "(未命名)" : title;
                AppendOutput($"  {id}  {titleText}  {created:MM-dd HH:mm} ~ {updatedAt:MM-dd HH:mm}  {msgCount}msgs{marker}\n");
            }
            AppendOutput("\nEnter session id to switch, /new for new. Esc to cancel.\n");
        }

        private async void HandleSessionMenuInput(string input)
        {
            if (input == "/new")
            {
                _inSessionMenu = false;
                await Memory.EnsureNamedAsync(_session);
                _session = Memory.LoadOrCreate();
                OutputBox.Document.Blocks.Clear();
                AppendOutput(FormatSessionHeader(_session));
                return;
            }

            if (input == "/session")
            {
                EnterSessionMenu();
                return;
            }

            var target = Memory.LoadOrCreate(input);
            if (target.Id != input && target.Messages.Count == 0)
            {
                AppendOutput($"Session '{input}' not found. Try again, /new, or Esc.\n");
                return;
            }

            _inSessionMenu = false;
            await Memory.EnsureNamedAsync(_session);
            _session = target;
            OutputBox.Document.Blocks.Clear();
            AppendOutput(FormatSessionHeader(_session));
            foreach (var msg in _session.Messages)
            {
                if (msg.Role == "user")
                    AppendOutput($"> {msg.Content}\n", "#FFFFFF");
                else if (msg.Role == "display")
                    AppendToolDone(msg.Content);
                else if (msg.Role == "assistant" && !string.IsNullOrEmpty(msg.Content))
                    AppendOutput($"{msg.Content}\n\n");
            }
        }

        private static string FormatSessionHeader(Session session)
        {
            var title = string.IsNullOrWhiteSpace(session.Title) ? "" : $"  {session.Title}";
            return $"Session: {session.Id}{title} ({session.Messages.Count} msgs)\n\n";
        }

        private string _subtaskTail = "";
        private System.Windows.Documents.Run? _subtaskRun;
        private System.Windows.Documents.Paragraph? _subtaskPara;

        private void ResetSubtaskTail()
        {
            _subtaskTail = "";
            _subtaskRun = null;
            _subtaskPara = null;
        }

        private void AppendSubtaskProgress(string? chunk)
        {
            if (chunk == null)
            {
                _subtaskTail = "";
                _subtaskPara = new System.Windows.Documents.Paragraph { Margin = new Thickness(0, 2, 0, 6) };
                _subtaskRun = new System.Windows.Documents.Run("  │ ...")
                {
                    Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#5A5A7A")
                };
                _subtaskPara.Inlines.Add(_subtaskRun);
                OutputBox.Document.Blocks.Add(_subtaskPara);
                OutputBox.ScrollToEnd();
                return;
            }
            var clean = chunk.Replace("\n", "").Replace("\r", "").Replace("\t", "");
            _subtaskTail += clean;
            const int maxShow = 20;
            if (_subtaskTail.Length > maxShow * 2)
                _subtaskTail = _subtaskTail[^maxShow..];

            var show = _subtaskTail.Length > maxShow ? _subtaskTail[^maxShow..] : _subtaskTail;

            if (_subtaskRun == null)
            {
                _subtaskPara = new System.Windows.Documents.Paragraph { Margin = new Thickness(0, 2, 0, 6), Tag = "tool" };
                _subtaskRun = new System.Windows.Documents.Run($"  │ {show}")
                {
                    Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#5A5A7A")
                };
                _subtaskPara.Inlines.Add(_subtaskRun);
                OutputBox.Document.Blocks.Add(_subtaskPara);
            }
            else
            {
                _subtaskRun.Text = $"  │ {show}";
            }
            OutputBox.ScrollToEnd();
        }

        private async Task RunChatAsync(string input)
        {
            _isBusy = true;
            WriteStatus("busy");
            _cts = new CancellationTokenSource();
            InputBox.Visibility = Visibility.Hidden;
            InputHint.Visibility = Visibility.Visible;
            ResetSubtaskTail();

            try
            {
                await Runtime.RunAsync(
                    _session,
                    input,
                    onOutput: chunk => Dispatcher.BeginInvoke(() => AppendOutput(chunk)),
                    onToolStart: info => Dispatcher.BeginInvoke(() => AppendToolStart(info)),
                    onToolDone: info => Dispatcher.BeginInvoke(() => AppendToolDone(info)),
                    onToolError: err => Dispatcher.BeginInvoke(() => AppendOutput($"  ✗ {err}\n", "#E8915A")),
                    onSubtaskProgress: chunk => Dispatcher.BeginInvoke(() => AppendSubtaskProgress(chunk)),
                    ct: _cts.Token);
            }
            catch (OperationCanceledException)
            {
                FixIncompleteToolCalls(_session);
                Memory.Save(_session);
                Dispatcher.BeginInvoke(() => AppendOutput("\n[cancelled]\n"));
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(() => AppendOutput($"\n[error] {ex.Message}\n"));
            }
            finally
            {
                _cts?.Dispose();
                _cts = null;
            }

            Interlocked.Exchange(ref _lastOutputTick, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            Dispatcher.BeginInvoke(() =>
            {
                AppendOutput("\n\n");
                _isBusy = false;
                WriteStatus("idle");
                _escCount = 0;
                InputHint.Visibility = Visibility.Collapsed;
                InputBox.Visibility = Visibility.Visible;
                InputBox.IsEnabled = true;
                InputBox.Focus();
                TryDequeueInvoke();
            });
        }

        public void AppendOutput(string text, string? color = null)
        {
            var doc = OutputBox.Document;
            var para = doc.Blocks.LastBlock as System.Windows.Documents.Paragraph;
            if (para != null && para.Tag as string == "tool")
            {
                para = new System.Windows.Documents.Paragraph();
                doc.Blocks.Add(para);
            }
            if (para == null)
            {
                para = new System.Windows.Documents.Paragraph();
                doc.Blocks.Add(para);
            }
            AppendTextWithLinks(para, text, color);
            OutputBox.ScrollToEnd();
        }

        private static readonly Regex LinkRegex = new(
            @"https?://[^\s<>()]+|[A-Za-z]:\\[^\r\n\t<>|""]+",
            RegexOptions.Compiled);

        private void AppendTextWithLinks(System.Windows.Documents.Paragraph para, string text, string? color)
        {
            var brush = color == null
                ? null
                : new System.Windows.Media.SolidColorBrush(
                    (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(color));
            int pos = 0;
            foreach (Match match in LinkRegex.Matches(text))
            {
                if (match.Index > pos)
                    para.Inlines.Add(MakeRun(text.Substring(pos, match.Index - pos), brush));

                var target = match.Value.TrimEnd('.', ',', ';', ')', ']');
                var trailing = match.Value.Substring(target.Length);
                var link = new System.Windows.Documents.Hyperlink(new System.Windows.Documents.Run(target))
                {
                    NavigateUri = MakeNavigateUri(target),
                    Foreground = brush ?? (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#7AB7FF")
                };
                link.RequestNavigate += Link_RequestNavigate;
                para.Inlines.Add(link);
                if (trailing.Length > 0)
                    para.Inlines.Add(MakeRun(trailing, brush));
                pos = match.Index + match.Length;
            }
            if (pos < text.Length)
                para.Inlines.Add(MakeRun(text.Substring(pos), brush));
        }

        private static System.Windows.Documents.Run MakeRun(string text, System.Windows.Media.Brush? brush)
        {
            var run = new System.Windows.Documents.Run(text);
            if (brush != null) run.Foreground = brush;
            return run;
        }

        private static Uri MakeNavigateUri(string target)
        {
            if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                target.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return new Uri(target);
            return new Uri(target);
        }

        private void Link_RequestNavigate(object sender, RequestNavigateEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo(e.Uri.ToString()) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppendOutput($"\n[open link failed] {ex.Message}\n", "#E8915A");
            }
            e.Handled = true;
        }

        private void AppendToolStart(string info)
        {
            var doc = OutputBox.Document;
            var para = new System.Windows.Documents.Paragraph { Margin = new Thickness(0, 6, 0, 0), Tag = "tool" };
            var run = new System.Windows.Documents.Run($"→ {info}")
            {
                Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#6A6A8A")
            };
            para.Inlines.Add(run);
            doc.Blocks.Add(para);
            OutputBox.ScrollToEnd();
        }

        private void AppendToolDone(string info)
        {
            var doc = OutputBox.Document;
            var para = new System.Windows.Documents.Paragraph { Margin = new Thickness(0, 4, 0, 0), Tag = "tool" };
            var run = new System.Windows.Documents.Run($"[tool: {info}]")
            {
                Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#6A6A8A")
            };
            para.Inlines.Add(run);
            doc.Blocks.Add(para);
            OutputBox.ScrollToEnd();
        }

        public void AppendOutputLine(string text, string? color = null)
        {
            AppendOutput(text + "\n", color);
        }

        private static void FixIncompleteToolCalls(Session session)
        {
            var msgs = session.Messages;
            if (msgs.Count == 0) return;
            var last = msgs[msgs.Count - 1];
            if (last.Role != "assistant" || last.ToolCalls == null || last.ToolCalls.Count == 0) return;
            var answered = new HashSet<string>();
            for (int i = msgs.Count - 1; i >= 0; i--)
            {
                if (msgs[i].Role == "tool" && msgs[i].ToolCallId != null)
                    answered.Add(msgs[i].ToolCallId!);
            }
            foreach (var tc in last.ToolCalls)
            {
                if (!answered.Contains(tc.Id))
                {
                    msgs.Add(new Message { Role = "tool", ToolCallId = tc.Id, Content = "[cancelled]" });
                }
            }
        }

        public void EnqueueInvoke(string[] inputs)
        {
            lock (_invokeQueue)
            {
                foreach (var input in inputs)
                    _invokeQueue.Enqueue(input);
            }
            Dispatcher.BeginInvoke((Action)TryDequeueInvoke);
        }

        private void TryDequeueInvoke()
        {
            if (_isBusy) return;
            string? input;
            lock (_invokeQueue)
            {
                if (_invokeQueue.Count == 0) return;
                input = _invokeQueue.Dequeue();
            }
            Log($"Invoke dequeue: {input?.Substring(0, Math.Min(input.Length, 40))}");
            ProcessInput(input!);
            if (!_isBusy)
                Dispatcher.BeginInvoke((Action)TryDequeueInvoke);
        }

        public new void Show()
        {
            base.Show();
            Activate();
            InputBox.Focus();
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            SetForegroundWindow(hwnd);
        }

        public new void Hide()
        {
            Log("Hide called");
            InputBox.Clear();
            base.Hide();
        }
    }
}
