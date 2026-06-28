using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
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

        private sealed class AgentTabState
        {
            public Session Session { get; }
            public FlowDocument Document { get; } = CreateOutputDocument();
            public CancellationTokenSource? Cts { get; set; }
            public bool IsBusy { get; set; }
            public bool IsContextFull { get; set; }
            public bool IsInSessionMenu { get; set; }
            public bool HasUnread { get; set; }
            public bool HasError { get; set; }
            public int LastContextTokens { get; set; }
            public string InputDraft { get; set; } = "";
            public string SubtaskTail { get; set; } = "";
            public Run? SubtaskRun { get; set; }
            public Paragraph? SubtaskPara { get; set; }
            public Button? TabButton { get; set; }
            public AgentTabState? QueueContinuationTab { get; set; }

            public AgentTabState(Session session)
            {
                Session = session;
            }
        }

        private readonly List<AgentTabState> _tabs = new();
        private AgentTabState? _activeTab;
        private long _lastOutputTick;
        private int _escCount;
        private long _firstEscTick;
        private sealed class QueuedInvoke
        {
            public AgentTabState? Target { get; init; }
            public string Input { get; init; } = "";
        }
        private readonly Queue<QueuedInvoke> _invokeQueue = new();

        private AgentTabState CurrentTab => _activeTab ?? throw new InvalidOperationException("No active Agent tab");

        public AgentWindow()
        {
            InitializeComponent();
            WriteStatus("idle");
            PositionWindow();
            OutputBox.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, OnOutputCopy));

            Session initialSession;
            var sessions = Memory.ListSessions();
            if (sessions.Count > 0)
                initialSession = Memory.LoadOrCreate(sessions[0].Id);
            else
                initialSession = Memory.LoadOrCreate();
            var initialTab = CreateTab(initialSession, renderHistory: Config.IsConfigured);
            SwitchToTab(initialTab);

            if (!Config.IsConfigured)
            {
                AppendOutput("CommitBall Agent Terminal v0.2.1\n\n", "#FFFFFF");
                AppendOutput("未检测到 API 配置。请使用 /vendor 命令配置：\n\n", "#E8915A");
                AppendOutput("  /vendor {\"base_url\":\"...\",\"model\":\"...\",\"api_key\":\"...\"}\n\n");
                AppendOutput("常用提供商：\n");
                AppendOutput("  DeepSeek:    base_url=https://api.deepseek.com   model=deepseek-chat\n");
                AppendOutput("  OpenAI:      base_url=https://api.openai.com      model=gpt-4o-mini\n");
                AppendOutput("  SiliconFlow: base_url=https://api.siliconflow.cn   model=Qwen/Qwen3-8B\n\n");
                return;
            }
        }

        private static FlowDocument CreateOutputDocument()
        {
            return new FlowDocument
            {
                PagePadding = new Thickness(0),
                FontFamily = new FontFamily("Cascadia Code, Consolas, Courier New"),
                FontSize = 13,
                Foreground = (Brush)new BrushConverter().ConvertFromString("#C8D8F8")
            };
        }

        private AgentTabState CreateTab(Session session, bool renderHistory = true, bool switchTo = false)
        {
            var existing = _tabs.FirstOrDefault(t => t.Session.Id == session.Id);
            if (existing != null)
            {
                if (switchTo) SwitchToTab(existing);
                return existing;
            }

            var tab = new AgentTabState(session);
            _tabs.Add(tab);
            if (renderHistory)
                RenderSession(tab);
            CreateTabButton(tab);
            if (switchTo || _activeTab == null)
                SwitchToTab(tab);
            return tab;
        }

        private void CreateTabButton(AgentTabState tab)
        {
            var btn = new Button
            {
                MinWidth = 92,
                MaxWidth = 180,
                Height = 22,
                Margin = new Thickness(0, 0, 4, 0),
                Padding = new Thickness(8, 0, 8, 0),
                FontSize = 12,
                FontFamily = new FontFamily("Cascadia Code, Consolas"),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Tag = tab
            };
            btn.Click += TabButton_Click;
            btn.PreviewMouseRightButtonUp += TabButton_RightClick;
            tab.TabButton = btn;
            TabsPanel.Children.Add(btn);
            RefreshTabButton(tab);
        }

        private void RefreshTabButton(AgentTabState tab)
        {
            if (tab.TabButton == null) return;
            var title = !string.IsNullOrWhiteSpace(tab.Session.Title) ? tab.Session.Title! : tab.Session.Id;
            if (title.Length > 16) title = title[..16];
            var prefix = (tab.IsBusy || tab.IsContextFull) ? "● " : (tab.HasUnread ? "• " : "");
            tab.TabButton.Content = prefix + title;
            var state = tab.IsContextFull ? "context full" : (tab.IsBusy ? "busy" : "idle");
            tab.TabButton.ToolTip = $"{tab.Session.Id}\n{tab.Session.Title}\n{state}\n右键关闭标签";
            var active = tab == _activeTab;
            tab.TabButton.Background = (Brush)new BrushConverter().ConvertFromString(active ? "#3D4058" : "#2C2F3A");
            tab.TabButton.Foreground = (Brush)new BrushConverter().ConvertFromString(tab.HasError ? "#E8915A" : active ? "#FFFFFF" : "#AAB1C8");
        }

        private static bool CanAcceptInput(AgentTabState tab) => !tab.IsBusy && !tab.IsContextFull;

        private void RefreshAllTabs()
        {
            foreach (var tab in _tabs)
                RefreshTabButton(tab);
            WriteStatus(_tabs.Any(CanAcceptInput) ? "idle" : "busy");
        }

        private void SwitchToTab(AgentTabState tab)
        {
            if (_activeTab != null)
                _activeTab.InputDraft = InputBox.Text;
            _activeTab = tab;
            tab.HasUnread = false;
            OutputBox.Document = tab.Document;
            InputBox.Text = tab.InputDraft;
            _escCount = 0;
            UpdateInputState(tab);
            RefreshAllTabs();
            InputBox.Focus();
            OutputBox.ScrollToEnd();
        }

        private void UpdateInputState(AgentTabState tab)
        {
            if (tab != _activeTab) return;
            if (tab.IsBusy)
            {
                InputBox.Visibility = Visibility.Hidden;
                InputBox.IsEnabled = false;
                InputHint.Text = "连按 Esc×2 中断模型输出";
                InputHint.Visibility = Visibility.Visible;
                return;
            }
            if (tab.IsContextFull)
            {
                InputBox.Visibility = Visibility.Hidden;
                InputBox.IsEnabled = false;
                InputHint.Text = "模型上下文已满，请新建会话";
                InputHint.Visibility = Visibility.Visible;
                return;
            }
            InputHint.Visibility = Visibility.Collapsed;
            InputBox.Visibility = Visibility.Visible;
            InputBox.IsEnabled = true;
        }

        private void RenderSession(AgentTabState tab)
        {
            AppendOutput(tab, $"CommitBall Agent Terminal v0.2.1\n");
            AppendOutput(tab, FormatSessionHeader(tab.Session));
            foreach (var msg in tab.Session.Messages)
            {
                if (msg.Role == "user")
                    AppendOutput(tab, $"> {msg.Content}\n", "#FFFFFF");
                else if (msg.Role == "display")
                {
                    Log($"Loading display: {msg.Content}");
                    AppendToolDone(tab, msg.Content);
                }
                else if (msg.Role == "assistant" && !string.IsNullOrEmpty(msg.Content))
                    AppendOutput(tab, $"{msg.Content}\n\n");
            }
        }

        private void TabButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button { Tag: AgentTabState tab })
                SwitchToTab(tab);
        }

        private void TabButton_RightClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is Button { Tag: AgentTabState tab })
            {
                e.Handled = true;
                CloseTab(tab);
            }
        }

        private void NewTabBtn_Click(object sender, RoutedEventArgs e)
        {
            _ = CreateNewTabAsync();
        }

        private Task<AgentTabState> CreateNewTabAsync()
        {
            var previous = _activeTab;
            if (previous != null)
            {
                _ = Task.Run(async () =>
                {
                    await Memory.EnsureNamedAsync(previous.Session);
                    Dispatcher.BeginInvoke(() => RefreshTabButton(previous));
                });
            }
            var tab = CreateTab(Memory.LoadOrCreate(), renderHistory: false, switchTo: true);
            AppendOutput(tab, $"CommitBall Agent Terminal v0.2.1\n");
            AppendOutput(tab, FormatSessionHeader(tab.Session));
            return Task.FromResult(tab);
        }

        private void CloseTab(AgentTabState tab)
        {
            if (tab.IsBusy)
            {
                tab.Cts?.Cancel();
                FixIncompleteToolCalls(tab.Session);
            }
            Memory.Save(tab.Session);
            if (tab.TabButton != null)
                TabsPanel.Children.Remove(tab.TabButton);
            _tabs.Remove(tab);
            tab.Cts?.Dispose();
            tab.Cts = null;

            if (_tabs.Count == 0)
            {
                var newTab = CreateTab(Memory.LoadOrCreate(), renderHistory: false, switchTo: true);
                AppendOutput(newTab, $"CommitBall Agent Terminal v0.2.1\n");
                AppendOutput(newTab, FormatSessionHeader(newTab.Session));
            }
            else if (_activeTab == tab)
            {
                SwitchToTab(_tabs[^1]);
            }
            RefreshAllTabs();
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
            var tab = _activeTab;
            Log($"KeyDown: {e.Key} busy={tab?.IsBusy}");
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                if (tab != null && tab.IsInSessionMenu)
                {
                    tab.IsInSessionMenu = false;
                    tab.Document.Blocks.Clear();
                    RenderSession(tab);
                    return;
                }
                if (tab != null && tab.IsBusy)
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
                        tab.Cts?.Cancel();
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

        private void Window_Closing(object? sender, CancelEventArgs e)
        {
            foreach (var tab in _tabs.ToArray())
            {
                if (tab.IsBusy)
                {
                    tab.Cts?.Cancel();
                    FixIncompleteToolCalls(tab.Session);
                }
                Memory.Save(tab.Session);
            }
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
                var tab = CurrentTab;
                if (!CanAcceptInput(tab)) return;
                var text = InputBox.Text.Trim();
                InputBox.Clear();
                tab.InputDraft = "";
                if (string.IsNullOrEmpty(text)) return;
                ProcessInput(tab, text);
            }
        }

        private async void ProcessInput(AgentTabState tab, string text)
        {
            if (text == "/help" || text == "/vendor" || text.StartsWith("/vendor "))
            {
                if (text == "/help")
                {
                    AppendOutput(tab, "\nCommands:\n", "#FFFFFF");
                    AppendOutput(tab, "  /help      Show this help\n");
                    AppendOutput(tab, "  /new       Create a new session\n");
                    AppendOutput(tab, "  /session   List and switch sessions\n");
                    AppendOutput(tab, "  /analyse          Analyse live.txt work log (subtask mode)\n");
                    AppendOutput(tab, "  /summary_to_panel Analyse + panel in one pass (single task)\n");
                    AppendOutput(tab, "  /name_archive     Improve one archive .meta.json title/tags/summary\n");
                    AppendOutput(tab, "  /repair_archives  Complete missing archive txt/meta/agent analysis\n");
                    AppendOutput(tab, "  /organize_agent_out Organize agent-out files and rebuild index.json\n");
                    AppendOutput(tab, "  /vendor           Show or update API config\n");
                    AppendOutput(tab, "\n");
                    return;
                }

                if (text == "/vendor")
                {
                    Log("ProcessInput: /vendor show current");
                    AppendOutput(tab, "\nCurrent config:\n", "#FFFFFF");
                    AppendOutput(tab, $"  base_url: {Config.BaseUrl}\n");
                    AppendOutput(tab, $"  model:    {Config.Model}\n");
                    var keyPreview = Config.ApiKey.Length > 0 ? Config.ApiKey.Substring(0, Math.Min(8, Config.ApiKey.Length)) + "..." : "(empty)";
                    AppendOutput(tab, $"  api_key:  {keyPreview}\n\n");
                    AppendOutput(tab, "  /vendor {\"base_url\":\"...\",\"model\":\"...\",\"api_key\":\"...\"}\n\n");
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
                            Dispatcher.BeginInvoke(() => AppendOutput(tab, "\n缺少必要字段，需要 base_url、model、api_key 三个非空字段\n\n", "#E8915A"));
                            return;
                        }
                        baseUrl = buEl.GetString()!;
                        model = mEl.GetString()!;
                        apiKey = akEl.GetString()!;
                    }
                    catch (System.Text.Json.JsonException)
                    {
                        Dispatcher.BeginInvoke(() => AppendOutput(tab, "\nJSON parse failed. Check format.\n\n", "#E8915A"));
                        return;
                    }

                    Log($"/vendor: validating {baseUrl} model={model}");
                    Dispatcher.BeginInvoke(() => AppendOutput(tab, $"\nValidating {baseUrl} ... ", "#AAAAAE"));
                    var (ok, msg) = await LLMClient.ValidateAsync(baseUrl, model, apiKey);
                    Log($"/vendor: validation result ok={ok} msg={msg}");

                    if (!ok)
                    {
                        Dispatcher.BeginInvoke(() => AppendOutput(tab, $"failed\n  {msg}\n\n", "#E8915A"));
                        return;
                    }
                    Dispatcher.BeginInvoke(() =>
                    {
                        AppendOutput(tab, "OK\n", "#6ECF6E");
                        Config.Save(baseUrl, model, apiKey);
                        if (tab.Session.Messages.Count == 0)
                        {
                            AppendOutput(tab, $"\nConfig saved → {baseUrl} / {model}\n", "#6ECF6E");
                            AppendOutput(tab, FormatSessionHeader(tab.Session));
                        }
                        else
                        {
                            AppendOutput(tab, $"\nConfig updated → {baseUrl} / {model}\n\n", "#6ECF6E");
                        }
                    });
                });
                return;
            }

            if (tab.IsInSessionMenu)
            {
                HandleSessionMenuInput(tab, text);
                return;
            }

            if (text == "/organize_agent_out")
            {
                OrganizeAgentOut(tab);
                return;
            }

            if (!Config.IsConfigured)
            {
                AppendOutput(tab, "\n请先使用 /vendor 配置 API\n\n", "#E8915A");
                return;
            }

            if (text == "/session")
            {
                EnterSessionMenu(tab);
                return;
            }

            if (text == "/new")
            {
                await CreateNewTabAsync();
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

                AppendOutput(tab, $"> /analyse\n", "#FFFFFF");
                _ = RunChatAsync(tab, prompt);
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

                AppendOutput(tab, $"> /analyse_st\n", "#FFFFFF");
                _ = RunChatAsync(tab, prompt);
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

                AppendOutput(tab, $"> /summary_to_panel\n", "#FFFFFF");
                _ = RunChatAsync(tab, prompt);
                return;
            }

            if (text == "/repair_archives")
            {
                RepairArchiveAnalysis(tab);
                return;
            }

            if (text.StartsWith("/name_archive ", StringComparison.OrdinalIgnoreCase))
            {
                var args = text.Substring("/name_archive ".Length)
                    .Split(new[] { ' ' }, 2, StringSplitOptions.RemoveEmptyEntries);
                if (args.Length < 2)
                {
                    AppendOutput(tab, "\nUsage: /name_archive exports/YYYY-MM/file.txt exports/YYYY-MM/file.meta.json\n\n", "#E8915A");
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

                AppendOutput(tab, $"> /name_archive {txtFile} {metaFile}\n", "#FFFFFF");
                _ = RunChatAsync(tab, prompt);
                return;
            }

            AppendOutput(tab, $"> {text}\n", "#FFFFFF");
            _ = RunChatAsync(tab, text);
        }

        private void OrganizeAgentOut(AgentTabState tab)
        {
            var outDir = Path.Combine(Config.DataDir, "agent-out");
            if (!Directory.Exists(outDir))
            {
                AppendOutput(tab, "\nNo agent-out directory found.\n\n", "#E8915A");
                return;
            }

            var moved = 0;
            var copied = 0;
            var skipped = 0;
            var errors = 0;
            foreach (var file in Directory.GetFiles(outDir, "*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(file);
                if (ShouldKeepAgentOutRootFile(name))
                {
                    skipped++;
                    continue;
                }

                var category = ClassifyAgentOutFile(name);
                if (string.IsNullOrWhiteSpace(category))
                {
                    skipped++;
                    continue;
                }

                var month = GetAgentOutMonth(file, name);
                var targetDir = category == "memory"
                    ? Path.Combine(outDir, category)
                    : Path.Combine(outDir, category, month);
                var target = GetUniquePath(Path.Combine(targetDir, name));

                try
                {
                    Directory.CreateDirectory(targetDir);
                    File.Move(file, target);
                    moved++;
                }
                catch (Exception ex)
                {
                    errors++;
                    Log($"OrganizeAgentOut move failed: {file} -> {target}: {ex.Message}");
                }
            }

            var memoryRoot = Path.Combine(outDir, "summary_task_exp_decay_memory.md");
            if (File.Exists(memoryRoot))
            {
                try
                {
                    var memoryDir = Path.Combine(outDir, "memory");
                    Directory.CreateDirectory(memoryDir);
                    var memoryCopy = Path.Combine(memoryDir, "summary_task_exp_decay_memory.md");
                    if (!File.Exists(memoryCopy) || File.GetLastWriteTime(memoryRoot) > File.GetLastWriteTime(memoryCopy))
                    {
                        File.Copy(memoryRoot, memoryCopy, true);
                        copied++;
                    }
                }
                catch (Exception ex)
                {
                    errors++;
                    Log($"OrganizeAgentOut memory copy failed: {ex.Message}");
                }
            }

            try
            {
                var indexed = WriteAgentOutIndex(outDir);
                AppendOutput(tab, $"\nagent-out organized. moved={moved}, copied={copied}, skipped={skipped}, indexed={indexed}, errors={errors}\n\n", errors == 0 ? "#6ECF6E" : "#E8915A");
            }
            catch (Exception ex)
            {
                Log($"OrganizeAgentOut index failed: {ex.Message}");
                AppendOutput(tab, $"\nagent-out organized but index failed: {ex.Message}\n\n", "#E8915A");
            }
        }

        private static bool ShouldKeepAgentOutRootFile(string name)
        {
            return name.Equals("panel.html", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("panel-template.html", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("summary_task_exp_decay_memory.md", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("summary_task_exp_decay_memory_template.md", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("index.json", StringComparison.OrdinalIgnoreCase);
        }

        private static string ClassifyAgentOutFile(string name)
        {
            var lower = name.ToLowerInvariant();
            if (lower.EndsWith("-report.md")) return "reports";
            if (lower.EndsWith("-extract.md")) return "extracts";
            if (lower.Contains("reminder") && lower.EndsWith(".md")) return "reminders";
            if (lower.Contains("response") && lower.EndsWith(".md")) return "responses";
            if (lower.Contains("summary") || lower.Contains("analysis")) return "analysis";
            if (lower.EndsWith(".md") || lower.EndsWith(".txt") || lower.EndsWith(".json") || lower.EndsWith(".py")) return "scratch";
            return "";
        }

        private static string GetAgentOutMonth(string file, string name)
        {
            var match = Regex.Match(name, @"20\d{2}[-_](0[1-9]|1[0-2])");
            if (match.Success)
                return match.Value.Replace('_', '-');
            return File.GetLastWriteTime(file).ToString("yyyy-MM");
        }

        private static string GetUniquePath(string target)
        {
            if (!File.Exists(target)) return target;
            var dir = Path.GetDirectoryName(target) ?? "";
            var stem = Path.GetFileNameWithoutExtension(target);
            var ext = Path.GetExtension(target);
            for (int i = 1; i < 1000; i++)
            {
                var candidate = Path.Combine(dir, $"{stem}-{i:000}{ext}");
                if (!File.Exists(candidate)) return candidate;
            }
            return Path.Combine(dir, $"{stem}-{Guid.NewGuid().ToString("N")[..6]}{ext}");
        }

        private static int WriteAgentOutIndex(string outDir)
        {
            var files = new List<object>();
            foreach (var file in Directory.GetFiles(outDir, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                var info = new FileInfo(file);
                var rel = Path.GetRelativePath(outDir, file).Replace('\\', '/');
                var first = rel.Contains('/') ? rel[..rel.IndexOf('/')] : "";
                var category = IsKnownAgentOutCategory(first) ? first : "root";
                files.Add(new
                {
                    path = rel,
                    category,
                    role = GetAgentOutRole(rel, category),
                    size = info.Length,
                    modified_at = info.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")
                });
            }

            var index = new
            {
                version = 1,
                generated_at = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                root_keep_files = new[]
                {
                    "panel.html",
                    "panel-template.html",
                    "summary_task_exp_decay_memory.md",
                    "summary_task_exp_decay_memory_template.md"
                },
                files
            };
            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(Path.Combine(outDir, "index.json"), JsonSerializer.Serialize(index, opts));
            return files.Count;
        }

        private static bool IsKnownAgentOutCategory(string category)
        {
            return category.Equals("reports", StringComparison.OrdinalIgnoreCase) ||
                   category.Equals("extracts", StringComparison.OrdinalIgnoreCase) ||
                   category.Equals("reminders", StringComparison.OrdinalIgnoreCase) ||
                   category.Equals("responses", StringComparison.OrdinalIgnoreCase) ||
                   category.Equals("analysis", StringComparison.OrdinalIgnoreCase) ||
                   category.Equals("scratch", StringComparison.OrdinalIgnoreCase) ||
                   category.Equals("memory", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetAgentOutRole(string rel, string category)
        {
            var name = Path.GetFileName(rel).ToLowerInvariant();
            if (name == "panel.html") return "bar-panel";
            if (name == "panel-template.html") return "bar-panel-template";
            if (name == "summary_task_exp_decay_memory.md") return category == "memory" ? "memory-copy" : "memory-root";
            if (name == "summary_task_exp_decay_memory_template.md") return "memory-template";
            if (name == "index.json") return "agent-out-index";
            return category;
        }

        private string RepairArchiveAnalysis(AgentTabState tab)
        {
            ArchiveRepairResult repairResult;
            try
            {
                repairResult = ArchiveRepair.RepairFiles();
                AppendOutput(tab, "\n" + repairResult + "\n", "#6ECF6E");
                foreach (var error in repairResult.Errors.Take(5))
                    AppendOutput(tab, $"Archive file repair error: {error}\n", "#E8915A");
                if (repairResult.Errors.Count > 5)
                    AppendOutput(tab, $"... {repairResult.Errors.Count - 5} more archive repair errors omitted\n", "#E8915A");
            }
            catch (Exception ex)
            {
                AppendOutput(tab, $"\nArchive file repair failed: {ex.Message}\n", "#E8915A");
            }

            var exportDir = Path.Combine(Config.DataDir, "exports");
            if (!Directory.Exists(exportDir))
            {
                AppendOutput(tab, "\nNo exports directory found.\n\n", "#E8915A");
                return "No exports directory found";
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
                        AppendOutput(tab, $"Archive missing txt_path: {metaRel}\n", "#E8915A");
                        continue;
                    }
                    pending.Add($"/name_archive {txtRel} {metaRel}");
                }
                catch (Exception ex)
                {
                    AppendOutput(tab, $"Archive scan error: {Path.GetFileName(metaPath)} {ex.Message}\n", "#E8915A");
                }
            }

            if (pending.Count == 0)
            {
                AppendOutput(tab, "\nArchives already have agent analysis.\n\n", "#6ECF6E");
                return "Archives already have agent analysis";
            }

            EnqueueInvokeForTab(tab, pending);
            AppendOutput(tab, $"\nQueued archive agent analysis in current session: {pending.Count}\n\n", "#6ECF6E");
            return $"Queued archive agent analysis in current session: {pending.Count}";
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

        private void EnterSessionMenu(AgentTabState tab)
        {
            tab.IsInSessionMenu = true;
            tab.Document.Blocks.Clear();
            AppendOutput(tab, "--- Sessions ---\n");
            var sessions = Memory.ListSessions();
            if (sessions.Count == 0)
            {
                AppendOutput(tab, "(no sessions)\n");
            }
            foreach (var (id, updatedAt, msgCount, title) in sessions)
            {
                var marker = id == tab.Session.Id ? " *" : "";
                var openTab = _tabs.FirstOrDefault(t => t.Session.Id == id);
                var status = openTab == null ? "closed" : (openTab.IsContextFull ? "full" : (openTab.IsBusy ? "busy" : "open"));
                var fi = new FileInfo(Path.Combine(Config.MemoryDir, $"{id}.json"));
                var created = fi.Exists ? fi.CreationTime : updatedAt;
                var titleText = string.IsNullOrWhiteSpace(title) ? "(未命名)" : title;
                AppendOutput(tab, $"  {id}  {status,-6}  {titleText}  {created:MM-dd HH:mm} ~ {updatedAt:MM-dd HH:mm}  {msgCount}msgs{marker}\n");
            }
            AppendOutput(tab, "\nEnter session id to switch, /new for new. Esc to cancel.\n");
        }

        private async void HandleSessionMenuInput(AgentTabState tab, string input)
        {
            if (input == "/new")
            {
                tab.IsInSessionMenu = false;
                await CreateNewTabAsync();
                return;
            }

            if (input == "/session")
            {
                EnterSessionMenu(tab);
                return;
            }

            var target = Memory.LoadOrCreate(input);
            if (target.Id != input && target.Messages.Count == 0)
            {
                AppendOutput(tab, $"Session '{input}' not found. Try again, /new, or Esc.\n");
                return;
            }

            tab.IsInSessionMenu = false;
            await Memory.EnsureNamedAsync(tab.Session);
            var targetTab = CreateTab(target, renderHistory: true, switchTo: true);
            targetTab.IsInSessionMenu = false;
        }

        private static string FormatSessionHeader(Session session)
        {
            var title = string.IsNullOrWhiteSpace(session.Title) ? "" : $"  {session.Title}";
            return $"Session: {session.Id}{title} ({session.Messages.Count} msgs)\n\n";
        }

        private void ResetSubtaskTail(AgentTabState tab)
        {
            tab.SubtaskTail = "";
            tab.SubtaskRun = null;
            tab.SubtaskPara = null;
        }

        private void AppendSubtaskProgress(AgentTabState tab, string? chunk)
        {
            if (chunk == null)
            {
                tab.SubtaskTail = "";
                tab.SubtaskPara = new Paragraph { Margin = new Thickness(0, 2, 0, 6) };
                tab.SubtaskRun = new Run("  │ ...")
                {
                    Foreground = (Brush)new BrushConverter().ConvertFromString("#5A5A7A")
                };
                tab.SubtaskPara.Inlines.Add(tab.SubtaskRun);
                tab.Document.Blocks.Add(tab.SubtaskPara);
                MarkTabOutputChanged(tab);
                return;
            }
            var clean = chunk.Replace("\n", "").Replace("\r", "").Replace("\t", "");
            tab.SubtaskTail += clean;
            const int maxShow = 20;
            if (tab.SubtaskTail.Length > maxShow * 2)
                tab.SubtaskTail = tab.SubtaskTail[^maxShow..];

            var show = tab.SubtaskTail.Length > maxShow ? tab.SubtaskTail[^maxShow..] : tab.SubtaskTail;

            if (tab.SubtaskRun == null)
            {
                tab.SubtaskPara = new Paragraph { Margin = new Thickness(0, 2, 0, 6), Tag = "tool" };
                tab.SubtaskRun = new Run($"  │ {show}")
                {
                    Foreground = (Brush)new BrushConverter().ConvertFromString("#5A5A7A")
                };
                tab.SubtaskPara.Inlines.Add(tab.SubtaskRun);
                tab.Document.Blocks.Add(tab.SubtaskPara);
            }
            else
            {
                tab.SubtaskRun.Text = $"  │ {show}";
            }
            MarkTabOutputChanged(tab);
        }

        private async Task RunChatAsync(AgentTabState tab, string input)
        {
            if (!CanAcceptInput(tab)) return;
            if (IsContextFull(tab, EstimateSessionTokens(tab.Session)))
            {
                MarkContextFull(tab);
                return;
            }
            tab.IsBusy = true;
            tab.HasError = false;
            tab.Cts = new CancellationTokenSource();
            if (tab == _activeTab)
            {
                UpdateInputState(tab);
            }
            ResetSubtaskTail(tab);
            RefreshAllTabs();

            var maxContextTokens = 0;
            try
            {
                await Runtime.RunAsync(
                    tab.Session,
                    input,
                    onOutput: chunk => Dispatcher.BeginInvoke(() => AppendOutput(tab, chunk)),
                    onToolStart: info => Dispatcher.BeginInvoke(() => AppendToolStart(tab, info)),
                    onToolDone: info => Dispatcher.BeginInvoke(() =>
                    {
                        AppendToolDone(tab, info);
                        RefreshTabButton(tab);
                    }),
                    onToolError: err => Dispatcher.BeginInvoke(() =>
                    {
                        tab.HasError = true;
                        AppendOutput(tab, $"  ✗ {err}\n", "#E8915A");
                    }),
                    onSubtaskProgress: chunk => Dispatcher.BeginInvoke(() => AppendSubtaskProgress(tab, chunk)),
                    ct: tab.Cts.Token,
                    onUsage: (promptTokens, completionTokens) =>
                    {
                        var used = promptTokens + completionTokens;
                        if (used > maxContextTokens)
                            maxContextTokens = used;
                    },
                    onRepairArchives: () =>
                    {
                        var result = "";
                        if (Dispatcher.CheckAccess())
                            result = RepairArchiveAnalysis(tab);
                        else
                            Dispatcher.Invoke(() => result = RepairArchiveAnalysis(tab));
                        return result;
                    });
            }
            catch (OperationCanceledException)
            {
                FixIncompleteToolCalls(tab.Session);
                Memory.Save(tab.Session);
                Dispatcher.BeginInvoke(() => AppendOutput(tab, "\n[cancelled]\n"));
            }
            catch (Exception ex)
            {
                tab.HasError = true;
                Dispatcher.BeginInvoke(() => AppendOutput(tab, $"\n[error] {ex.Message}\n", "#E8915A"));
            }
            finally
            {
                tab.Cts?.Dispose();
                tab.Cts = null;
            }

            Interlocked.Exchange(ref _lastOutputTick, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            Dispatcher.BeginInvoke(() =>
            {
                var contextTokens = maxContextTokens > 0 ? maxContextTokens : EstimateSessionTokens(tab.Session);
                tab.LastContextTokens = contextTokens;
                if (IsContextFull(tab, contextTokens))
                    MarkContextFull(tab, appendMessage: false);
                AppendOutput(tab, "\n\n");
                tab.IsBusy = false;
                _escCount = 0;
                UpdateInputState(tab);
                if (tab == _activeTab && CanAcceptInput(tab))
                    InputBox.Focus();
                RefreshAllTabs();
                TryDequeueInvoke();
            });
        }

        private static int ContextLimitTokens()
        {
            var model = (Config.Model ?? "").ToLowerInvariant();
            if (model.Contains("mimo"))
                return 1000000;
            if (model.Contains("gpt-4o") || model.Contains("gpt-4.1") || model.Contains("o4") || model.Contains("o3"))
                return 128000;
            if (model.Contains("deepseek"))
                return 64000;
            if (model.Contains("qwen3") || model.Contains("qwen2.5"))
                return 32768;
            return 32768;
        }

        private static int EstimateSessionTokens(Session session)
        {
            var chars = 0;
            foreach (var msg in session.Messages)
            {
                if (msg.Role == "display") continue;
                chars += msg.Role.Length + 8;
                chars += msg.Content?.Length ?? 0;
                if (msg.ToolCalls != null)
                {
                    foreach (var tc in msg.ToolCalls)
                        chars += (tc.Name?.Length ?? 0) + (tc.Arguments?.Length ?? 0) + 32;
                }
            }
            return Math.Max(0, chars / 3 + session.Messages.Count * 8 + 2500);
        }

        private static bool IsContextFull(AgentTabState tab, int usedTokens)
        {
            var limit = ContextLimitTokens();
            return usedTokens >= (int)(limit * 0.9);
        }

        private void MarkContextFull(AgentTabState tab, bool appendMessage = true)
        {
            if (tab.IsContextFull) return;
            tab.IsContextFull = true;
            if (appendMessage)
                AppendOutput(tab, "\n[模型上下文已满，请新建会话继续]\n", "#E8915A");
            UpdateInputState(tab);
            RefreshAllTabs();
        }

        private bool ShouldTreatAsContextFull(AgentTabState tab)
        {
            if (tab.IsContextFull) return true;
            var estimated = EstimateSessionTokens(tab.Session);
            if (!IsContextFull(tab, estimated)) return false;
            MarkContextFull(tab);
            return true;
        }

        private AgentTabState GetQueueContinuationTab(AgentTabState source)
        {
            var continuation = source.QueueContinuationTab;
            if (continuation != null && !continuation.IsContextFull)
                return continuation;

            continuation = CreateTab(Memory.LoadOrCreate(), renderHistory: false, switchTo: true);
            AppendOutput(continuation, $"CommitBall Agent Terminal v0.2.1\n");
            AppendOutput(continuation, FormatSessionHeader(continuation.Session));
            AppendOutput(continuation, $"[上一会话上下文已满，未执行的队列指令已转入此新会话]\n\n", "#E8915A");
            source.QueueContinuationTab = continuation;
            return continuation;
        }

        public void AppendOutput(string text, string? color = null)
        {
            AppendOutput(CurrentTab, text, color);
        }

        private void AppendOutput(AgentTabState tab, string text, string? color = null)
        {
            var doc = tab.Document;
            var para = doc.Blocks.LastBlock as Paragraph;
            if (para != null && para.Tag as string == "tool")
            {
                para = new Paragraph();
                doc.Blocks.Add(para);
            }
            if (para == null)
            {
                para = new Paragraph();
                doc.Blocks.Add(para);
            }
            AppendTextWithLinks(para, text, color);
            MarkTabOutputChanged(tab);
        }

        private static readonly Regex LinkRegex = new(
            @"https?://[^\s<>()]+|[A-Za-z]:\\[^\r\n\t<>|""]+",
            RegexOptions.Compiled);

        private void AppendTextWithLinks(Paragraph para, string text, string? color)
        {
            var brush = color == null
                ? null
                : new SolidColorBrush(
                    (Color)ColorConverter.ConvertFromString(color));
            int pos = 0;
            foreach (Match match in LinkRegex.Matches(text))
            {
                if (match.Index > pos)
                    para.Inlines.Add(MakeRun(text.Substring(pos, match.Index - pos), brush));

                var target = match.Value.TrimEnd('.', ',', ';', ')', ']');
                var trailing = match.Value.Substring(target.Length);
                var link = new Hyperlink(new Run(target))
                {
                    NavigateUri = MakeNavigateUri(target),
                    Foreground = brush ?? (Brush)new BrushConverter().ConvertFromString("#7AB7FF")
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

        private static Run MakeRun(string text, Brush? brush)
        {
            var run = new Run(text);
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
            AppendToolStart(CurrentTab, info);
        }

        private void AppendToolStart(AgentTabState tab, string info)
        {
            var doc = tab.Document;
            var para = new Paragraph { Margin = new Thickness(0, 6, 0, 0), Tag = "tool" };
            var run = new Run($"→ {info}")
            {
                Foreground = (Brush)new BrushConverter().ConvertFromString("#6A6A8A")
            };
            para.Inlines.Add(run);
            doc.Blocks.Add(para);
            MarkTabOutputChanged(tab);
        }

        private void AppendToolDone(string info)
        {
            AppendToolDone(CurrentTab, info);
        }

        private void AppendToolDone(AgentTabState tab, string info)
        {
            var doc = tab.Document;
            var para = new Paragraph { Margin = new Thickness(0, 4, 0, 0), Tag = "tool" };
            var run = new Run($"[tool: {info}]")
            {
                Foreground = (Brush)new BrushConverter().ConvertFromString("#6A6A8A")
            };
            para.Inlines.Add(run);
            doc.Blocks.Add(para);
            MarkTabOutputChanged(tab);
        }

        public void AppendOutputLine(string text, string? color = null)
        {
            AppendOutput(CurrentTab, text + "\n", color);
        }

        private void MarkTabOutputChanged(AgentTabState tab)
        {
            if (tab != _activeTab)
                tab.HasUnread = true;
            RefreshTabButton(tab);
            if (tab == _activeTab)
                OutputBox.ScrollToEnd();
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
                    _invokeQueue.Enqueue(new QueuedInvoke { Input = input });
            }
            Dispatcher.BeginInvoke((Action)TryDequeueInvoke);
        }

        private void EnqueueInvokeForTab(AgentTabState tab, IEnumerable<string> inputs)
        {
            lock (_invokeQueue)
            {
                foreach (var input in inputs)
                {
                    if (string.IsNullOrWhiteSpace(input)) continue;
                    _invokeQueue.Enqueue(new QueuedInvoke { Target = tab, Input = input });
                }
            }
            Dispatcher.BeginInvoke((Action)TryDequeueInvoke);
        }

        public void EnqueueExternalInvoke(string[] inputs)
        {
            var target = _tabs.FirstOrDefault(CanAcceptInput);
            if (target == null)
            {
                target = CreateTab(Memory.LoadOrCreate(), renderHistory: false, switchTo: true);
                AppendOutput(target, $"CommitBall Agent Terminal v0.2.1\n");
                AppendOutput(target, FormatSessionHeader(target.Session));
            }
            else
            {
                SwitchToTab(target);
            }

            var normalized = inputs
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();

            lock (_invokeQueue)
            {
                foreach (var input in normalized)
                    _invokeQueue.Enqueue(new QueuedInvoke { Target = target, Input = input });
            }
            Dispatcher.BeginInvoke((Action)TryDequeueInvoke);
        }

        private void TryDequeueInvoke()
        {
            while (true)
            {
                QueuedInvoke? item = null;
                AgentTabState? tab = null;
                lock (_invokeQueue)
                {
                    var count = _invokeQueue.Count;
                    for (int i = 0; i < count; i++)
                    {
                        var candidate = _invokeQueue.Dequeue();
                        var candidateTab = candidate.Target ?? _activeTab;
                        if (candidateTab != null && ShouldTreatAsContextFull(candidateTab))
                            candidateTab = GetQueueContinuationTab(candidateTab);
                        if (candidateTab != null && CanAcceptInput(candidateTab))
                        {
                            item = candidate;
                            tab = candidateTab;
                            break;
                        }
                        _invokeQueue.Enqueue(candidate);
                    }
                }

                if (item == null || tab == null) return;
                Log($"Invoke dequeue: {item.Input.Substring(0, Math.Min(item.Input.Length, 40))}");
                ProcessInput(tab, item.Input);
            }
        }

        public new void Show()
        {
            base.Show();
            Activate();
            if (_activeTab != null)
                SwitchToTab(_activeTab);
            InputBox.Focus();
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            SetForegroundWindow(hwnd);
        }

        public new void Hide()
        {
            Log("Hide called");
            if (_activeTab != null)
                _activeTab.InputDraft = InputBox.Text;
            InputBox.Clear();
            base.Hide();
        }
    }
}
