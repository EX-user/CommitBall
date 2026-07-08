using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
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
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(
            IntPtr hWnd,
            IntPtr hWndInsertAfter,
            int x,
            int y,
            int cx,
            int cy,
            uint uFlags);

        private static readonly IntPtr HwndTopmost = new(-1);
        private const int SwShownormal = 1;
        private const uint SwpNoSize = 0x0001;
        private const uint SwpShowWindow = 0x0040;

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
            public enum TabKind
            {
                Normal,
                BarCommand
            }

            public Session Session { get; }
            public TabKind Kind { get; set; }
            public FlowDocument Document { get; } = CreateOutputDocument();
            public CancellationTokenSource? Cts { get; set; }
            public bool IsBusy { get; set; }
            public bool IsContextFull { get; set; }
            public bool IsInSessionMenu { get; set; }
            public bool HasUnread { get; set; }
            public bool HasError { get; set; }
            public int LastContextTokens { get; set; }
            public bool LastContextTokensAreActual { get; set; }
            public string InputDraft { get; set; } = "";
            public string SubtaskTail { get; set; } = "";
            public Run? SubtaskRun { get; set; }
            public Paragraph? SubtaskPara { get; set; }
            public Button? TabButton { get; set; }
            public AgentTabState? QueueContinuationTab { get; set; }

            public AgentTabState(Session session, TabKind kind)
            {
                Session = session;
                Kind = kind;
            }
        }

        private readonly List<AgentTabState> _tabs = new();
        private AgentTabState? _activeTab;
        private long _lastOutputTick;
        private int _escCount;
        private long _firstEscTick;
        private sealed class QueuedInvoke
        {
            public enum InvokeKind
            {
                BarCommand,
                NewSession
            }

            public AgentTabState? Target { get; init; }
            public string Input { get; init; } = "";
            public InvokeKind Kind { get; init; }
            public bool LockAgentOutWrites { get; init; }
        }
        private readonly Queue<QueuedInvoke> _invokeQueue = new();
        private AgentTabState? _barCommandTab;

        private AgentTabState CurrentTab => _activeTab ?? throw new InvalidOperationException("No active Agent tab");

        public AgentWindow()
        {
            InitializeComponent();
            WriteStatus("idle");
            PositionWindow();
            OutputBox.CommandBindings.Add(new CommandBinding(ApplicationCommands.Copy, OnOutputCopy));

            Session initialSession;
            var sessions = Memory.ListSessions(includeBarCommand: false);
            if (sessions.Count > 0)
                initialSession = Memory.LoadOrCreate(sessions[0].Id);
            else
                initialSession = Memory.CreateNew();
            var initialTab = CreateTab(initialSession, renderHistory: Config.IsConfigured);
            SwitchToTab(initialTab);

            var latestBarSession = Memory.LoadLatestBarCommandSession();
            if (latestBarSession != null)
                _barCommandTab = CreateTab(
                    latestBarSession,
                    renderHistory: Config.IsConfigured,
                    switchTo: false,
                    kind: AgentTabState.TabKind.BarCommand);

            if (!Config.IsConfigured)
            {
                AppendOutput("CommitBall Agent Terminal v0.2.3\n\n", "#FFFFFF");
                AppendOutput("未检测到 API 配置。请使用 /vendor 命令配置：\n\n", "#E8915A");
                AppendOutput("  /vendor {\"base_url\":\"...\",\"model\":\"...\",\"api_key\":\"...\"}\n\n");
                AppendOutput("常用提供商：\n");
                AppendOutput("  DeepSeek:    base_url=https://api.deepseek.com   model=deepseek-chat\n");
                AppendOutput("  OpenAI:      base_url=https://api.openai.com      model=gpt-4o-mini\n");
                AppendOutput("  SiliconFlow: base_url=https://api.siliconflow.cn   model=Qwen/Qwen3-8B\n\n");
                AppendOutput("  BigModel:    base_url=https://open.bigmodel.cn/api/paas/v4/   model=glm-5.2\n\n");
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

        private AgentTabState CreateTab(Session session, bool renderHistory = true, bool switchTo = false, AgentTabState.TabKind kind = AgentTabState.TabKind.Normal)
        {
            if (Memory.IsBarCommandSession(session))
                kind = AgentTabState.TabKind.BarCommand;
            if (kind == AgentTabState.TabKind.BarCommand)
                session.Purpose = Memory.PurposeBarCommand;

            var existing = _tabs.FirstOrDefault(t => t.Session.Id == session.Id);
            if (existing != null)
            {
                existing.Kind = kind;
                RebuildTabStrip();
                RefreshTabButton(existing);
                if (switchTo) SwitchToTab(existing);
                return existing;
            }

            var tab = new AgentTabState(session, kind);
            _tabs.Add(tab);
            if (renderHistory)
                RenderSession(tab);
            CreateTabButton(tab);
            RebuildTabStrip();
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
            RefreshTabButton(tab);
        }

        private void RebuildTabStrip()
        {
            TabsPanel.Children.Clear();
            var barTabs = _tabs.Where(t => t.Kind == AgentTabState.TabKind.BarCommand).ToList();
            var normalTabs = _tabs.Where(t => t.Kind == AgentTabState.TabKind.Normal).ToList();

            if (barTabs.Count > 0)
            {
                TabsPanel.Children.Add(CreateTabGroupLabel("Bar 指令"));
                foreach (var tab in barTabs)
                    if (tab.TabButton != null) TabsPanel.Children.Add(tab.TabButton);
                TabsPanel.Children.Add(CreateTabSeparator());
            }

            TabsPanel.Children.Add(CreateTabGroupLabel("会话"));
            foreach (var tab in normalTabs)
                if (tab.TabButton != null) TabsPanel.Children.Add(tab.TabButton);
        }

        private static TextBlock CreateTabGroupLabel(string text)
        {
            return new TextBlock
            {
                Text = text,
                Foreground = (Brush)new BrushConverter().ConvertFromString("#6F7898"),
                FontSize = 11,
                FontFamily = new FontFamily("Cascadia Code, Consolas"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 6, 0)
            };
        }

        private static Border CreateTabSeparator()
        {
            return new Border
            {
                Width = 1,
                Height = 16,
                Background = (Brush)new BrushConverter().ConvertFromString("#3D4058"),
                Margin = new Thickness(4, 3, 8, 3)
            };
        }

        private void RefreshTabButton(AgentTabState tab)
        {
            if (tab.TabButton == null) return;
            var title = !string.IsNullOrWhiteSpace(tab.Session.Title) ? tab.Session.Title! : tab.Session.Id;
            if (title.Length > 16) title = title[..16];
            if (tab.Kind == AgentTabState.TabKind.BarCommand)
                title = "指令 " + title;
            var prefix = (tab.IsBusy || tab.IsContextFull) ? "● " : (tab.HasUnread ? "• " : "");
            tab.TabButton.Content = prefix + title;
            var state = tab.IsContextFull ? "context full" : (tab.IsBusy ? "busy" : "idle");
            var kind = tab.Kind == AgentTabState.TabKind.BarCommand ? "Bar 指令会话" : "普通会话";
            tab.TabButton.ToolTip = $"{kind}\n{tab.Session.Id}\n{tab.Session.Title}\n{state}\n{FormatContextUsage(tab)}\n右键关闭标签";
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
            RefreshContextUsage(tab);
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
            AppendOutput(tab, $"CommitBall Agent Terminal v0.2.3\n");
            AppendOutput(tab, FormatSessionHeader(tab.Session));
            for (int i = 0; i < tab.Session.Messages.Count; i++)
            {
                var msg = tab.Session.Messages[i];
                if (msg.Role == "user")
                    AppendOutput(tab, $"> {msg.Content}\n", "#FFFFFF");
                else if (msg.Role == "display")
                {
                    Log($"Loading display: {msg.DisplayType ?? "tool_done"} {msg.Content}");
                    RenderDisplayMessage(tab, msg);
                }
                else if (msg.Role == "assistant" && !string.IsNullOrEmpty(msg.Content))
                {
                    var nextIsTurnEnd = i + 1 < tab.Session.Messages.Count &&
                        tab.Session.Messages[i + 1].Role == "display" &&
                        tab.Session.Messages[i + 1].DisplayType == "turn_end";
                    AppendOutput(tab, nextIsTurnEnd ? msg.Content : $"{msg.Content}\n\n");
                }
            }
            RefreshContextUsage(tab, forceEstimate: true);
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
            var tab = CreateTab(Memory.CreateNew(), renderHistory: false, switchTo: true);
            AppendOutput(tab, $"CommitBall Agent Terminal v0.2.3\n");
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
            _tabs.Remove(tab);
            if (_barCommandTab == tab)
                _barCommandTab = null;
            tab.Cts?.Dispose();
            tab.Cts = null;
            RebuildTabStrip();

            if (_tabs.Count == 0)
            {
                var newTab = CreateTab(Memory.CreateNew(), renderHistory: false, switchTo: true);
                AppendOutput(newTab, $"CommitBall Agent Terminal v0.2.3\n");
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

        private void EnsureWindowInWorkArea()
        {
            var workArea = SystemParameters.WorkArea;
            Width = Math.Max(480, Math.Min(680, Math.Min(Width, workArea.Width * 0.9)));
            Height = Math.Max(360, Math.Min(480, Math.Min(Height, workArea.Height * 0.9)));

            if (double.IsNaN(Left) || double.IsNaN(Top) ||
                Left + Width < workArea.Left + 80 ||
                Top + Height < workArea.Top + 80 ||
                Left > workArea.Right - 80 ||
                Top > workArea.Bottom - 80)
            {
                Left = (workArea.Width - Width) / 2 + workArea.Left;
                Top = workArea.Top + workArea.Height * 0.1;
                Log($"Show repositioned to visible area left={Left:F0} top={Top:F0} width={Width:F0} height={Height:F0}");
            }
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
                    LeaveSessionMenu(tab);
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
            PrepareForShutdown();
        }

        public void PrepareForShutdown()
        {
            foreach (var tab in _tabs.ToArray())
            {
                if (tab.IsBusy)
                {
                    Log($"PrepareForShutdown: cancel busy session {tab.Session.Id}");
                    tab.Cts?.Cancel();
                    FixIncompleteToolCalls(tab.Session);
                }
                Memory.Save(tab.Session);
            }
            WriteStatus("idle");
            Log($"PrepareForShutdown complete tabs={_tabs.Count}");
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

        private async void ProcessInput(AgentTabState tab, string text, bool lockAgentOutWrites = false)
        {
            if (text == "/help" || text == "/vendor" || text.StartsWith("/vendor "))
            {
                if (text == "/help")
                {
                    AppendOutput(tab, "\nCommands:\n", "#FFFFFF");
                    AppendOutput(tab, "  /help      Show this help\n");
                    AppendOutput(tab, "  /new       Create a new session\n");
                    AppendOutput(tab, "  /session   List and switch sessions\n");
                    AppendOutput(tab, "  /summary_to_panel Analyse + panel in one pass (single task)\n");
                    AppendOutput(tab, "  /repair_archives  Repair archive files and guide meta analysis\n");
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

            if (text == "/summary_to_panel")
            {
                var promptFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "summary_to_panel-prompt.md");
                string prompt;
                if (File.Exists(promptFile))
                    prompt = File.ReadAllText(promptFile);
                else
                    prompt = "Error: summary_to_panel-prompt.md not found";

                AppendPromptInput(tab, prompt);
                _ = RunChatAsync(tab, prompt, lockAgentOutWrites: true);
                return;
            }

            if (text == "/repair_archives")
            {
                var prompt =
                    "现在开始 CommitBall 归档修复和 meta 模型分析任务。\n" +
                    "请严格按以下步骤执行：\n" +
                    "1. 先调用 repair_archives 工具。这个工具只做机器修复：扫描 data/sessions；只生成缺失的 .txt、.raw.txt、meta.json 和 _clusters/ 文件；已存在的派生产物不会覆盖。这个工具不做模型总结。\n" +
                    "2. 使用 list 工具查看 exports/，递归定位所有 *.meta.json；必要时按 YYYY-MM 子目录逐个 list。\n" +
                    "3. 逐个检查 meta 是否已经有模型总结信息。判定为已完成的条件是：顶层 summary_source 为 agent 且 summary 非空；如果 clusters 存在，重要 cluster 也应有 summary_source=agent 且 agent_summary 非空。\n" +
                    "4. 对没有完成模型总结的 meta，调用 subtask。每个 subtask 负责一个 meta 文件：读取该 meta，读取 txt_path 指向的导出文本，读取 clusters 中重要 cluster 的 txt_path，然后调用 update_meta 更新 title、work_tags、summary 和必要的 cluster_summaries。\n" +
                    "5. subtask 必须基于 read 读到的内容总结，不要猜测不存在的事实；不要用 write 覆盖 meta，只能用 update_meta。\n" +
                    "6. 所有待处理 meta 都完成后，给出简短结果：机器修复结果、检查了多少 meta、更新了多少 meta、跳过了多少已完成 meta。\n";

                AppendPromptInput(tab, prompt);
                _ = RunChatAsync(tab, prompt, lockAgentOutWrites: lockAgentOutWrites);
                return;
            }

            AppendOutput(tab, $"> {text}\n", "#FFFFFF");
            _ = RunChatAsync(tab, text, lockAgentOutWrites: lockAgentOutWrites);
        }

        private void AppendPromptInput(AgentTabState tab, string prompt)
        {
            AppendOutput(tab, $"> {prompt}\n", "#FFFFFF");
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

            try
            {
                var indexed = WriteAgentOutIndex(outDir);
                AppendOutput(tab, $"\nagent-out organized. moved={moved}, skipped={skipped}, indexed={indexed}, errors={errors}\n\n", errors == 0 ? "#6ECF6E" : "#E8915A");
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
                   name.Equals("summary_task_exp_decay_memory_template.md", StringComparison.OrdinalIgnoreCase) ||
                   name.Equals("index.json", StringComparison.OrdinalIgnoreCase);
        }

        private static string ClassifyAgentOutFile(string name)
        {
            var lower = name.ToLowerInvariant();
            if (lower == "summary_task_exp_decay_memory.md") return "memory";
            if (lower.EndsWith("-report.md")) return "reports";
            if (lower.EndsWith("-extract.md")) return "extracts";
            if (lower.Contains("reminder") && lower.EndsWith(".md")) return "scratch";
            if (lower.Contains("response") && lower.EndsWith(".md")) return "scratch";
            if (lower.Contains("summary") || lower.Contains("analysis")) return "scratch";
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
                    "summary_task_exp_decay_memory_template.md",
                    "index.json"
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
                   category.Equals("scratch", StringComparison.OrdinalIgnoreCase) ||
                   category.Equals("memory", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetAgentOutRole(string rel, string category)
        {
            var name = Path.GetFileName(rel).ToLowerInvariant();
            if (name == "panel.html") return "bar-panel";
            if (name == "panel-template.html") return "bar-panel-template";
            if (name == "summary_task_exp_decay_memory.md") return category == "memory" ? "memory-main" : category;
            if (name == "summary_task_exp_decay_memory_template.md") return "memory-template";
            if (name == "index.json") return "agent-out-index";
            return category;
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
                LeaveSessionMenu(tab);
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

            LeaveSessionMenu(tab);
            await Memory.EnsureNamedAsync(tab.Session);
            var targetTab = OpenSessionFromMenu(target);
            targetTab.IsInSessionMenu = false;
        }

        private void LeaveSessionMenu(AgentTabState tab)
        {
            if (!tab.IsInSessionMenu) return;
            tab.IsInSessionMenu = false;
            tab.Document.Blocks.Clear();
            RenderSession(tab);
        }

        private AgentTabState OpenSessionFromMenu(Session session)
        {
            if (!Memory.IsBarCommandSession(session))
                return CreateTab(session, renderHistory: true, switchTo: true);

            var currentBar = _barCommandTab;
            if (currentBar != null && _tabs.Contains(currentBar) && currentBar.Session.Id != session.Id)
                CloseTab(currentBar);

            var existing = _tabs.FirstOrDefault(t => t.Session.Id == session.Id);
            if (existing != null)
            {
                existing.Kind = AgentTabState.TabKind.BarCommand;
                _barCommandTab = existing;
                SwitchToTab(existing);
                RefreshAllTabs();
                return existing;
            }

            var tab = CreateTab(session, renderHistory: true, switchTo: true, kind: AgentTabState.TabKind.BarCommand);
            _barCommandTab = tab;
            return tab;
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

        private async Task RunChatAsync(AgentTabState tab, string input, bool lockAgentOutWrites = false)
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
            RefreshContextUsage(tab, forceEstimate: true);
            RefreshAllTabs();

            var maxContextTokens = 0;
            IDisposable? agentOutWriteLease = null;
            try
            {
                if (lockAgentOutWrites)
                {
                    AppendOutput(tab, "[等待 agent-out 写入锁]\n", "#AAAAAE");
                    agentOutWriteLease = await Task.Run(() => Tools.AcquireAgentOutWriteLease(tab.Session.Id), tab.Cts.Token);
                    AppendOutput(tab, "[agent-out 写入锁已获取，summary_to_panel 执行期间其他会话写入会失败]\n", "#AAAAAE");
                }

                await Runtime.RunAsync(
                    tab.Session,
                    input,
                    onOutput: chunk => Dispatcher.BeginInvoke(() => AppendOutput(tab, chunk)),
                    onToolStart: info => Dispatcher.BeginInvoke(() => AppendToolStart(tab, info)),
                    onToolDone: info => Dispatcher.BeginInvoke(() =>
                    {
                        AppendToolDone(tab, info);
                        RefreshContextUsage(tab, forceEstimate: true);
                        RefreshTabButton(tab);
                    }),
                    onToolError: err => Dispatcher.BeginInvoke(() =>
                    {
                        tab.HasError = true;
                        AppendOutput(tab, $"  ✗ {err}\n", "#E8915A");
                        RefreshContextUsage(tab, forceEstimate: true);
                    }),
                    onSubtaskProgress: chunk => Dispatcher.BeginInvoke(() => AppendSubtaskProgress(tab, chunk)),
                    ct: tab.Cts.Token,
                    onUsage: (promptTokens, completionTokens) =>
                    {
                        var used = promptTokens + completionTokens;
                        if (used > maxContextTokens)
                            maxContextTokens = used;
                        Dispatcher.BeginInvoke(() =>
                        {
                            tab.LastContextTokens = used;
                            tab.LastContextTokensAreActual = true;
                            RefreshContextUsageDisplay(tab);
                            RefreshTabButton(tab);
                        });
                    });
            }
            catch (OperationCanceledException)
            {
                FixIncompleteToolCalls(tab.Session);
                AddDisplay(tab.Session, "cancelled", "\n[cancelled]\n");
                Memory.Save(tab.Session);
                Dispatcher.BeginInvoke(() => AppendOutput(tab, "\n[cancelled]\n"));
            }
            catch (Exception ex)
            {
                tab.HasError = true;
                var errorText = $"\n[error] {ex.Message}\n";
                AddDisplay(tab.Session, "error", errorText);
                Memory.Save(tab.Session);
                Dispatcher.BeginInvoke(() => AppendOutput(tab, errorText, "#E8915A"));
            }
            finally
            {
                agentOutWriteLease?.Dispose();
                tab.Cts?.Dispose();
                tab.Cts = null;
            }

            Interlocked.Exchange(ref _lastOutputTick, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

            Dispatcher.BeginInvoke(() =>
            {
                var contextTokens = maxContextTokens > 0 ? maxContextTokens : EstimateSessionTokens(tab.Session);
                tab.LastContextTokens = contextTokens;
                tab.LastContextTokensAreActual = maxContextTokens > 0;
                if (IsContextFull(tab, contextTokens))
                    MarkContextFull(tab, appendMessage: false);
                AddDisplay(tab.Session, "turn_end", "\n\n");
                Memory.Save(tab.Session);
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

        private static double ContextUsageRatio(AgentTabState tab)
        {
            var limit = Math.Max(1, ContextLimitTokens());
            var used = tab.LastContextTokens > 0 ? tab.LastContextTokens : EstimateSessionTokens(tab.Session);
            return Math.Clamp((double)used / limit, 0.0, 9.99);
        }

        private static string FormatContextUsage(AgentTabState tab)
        {
            var limit = Math.Max(1, ContextLimitTokens());
            var used = tab.LastContextTokens > 0 ? tab.LastContextTokens : EstimateSessionTokens(tab.Session);
            var pct = Math.Clamp((int)Math.Round(used * 100.0 / limit), 0, 999);
            var kind = tab.LastContextTokensAreActual ? "实际" : "估算";
            return $"上下文 {pct}% ({kind}, {used:n0}/{limit:n0})";
        }

        private void RefreshContextUsage(AgentTabState tab, bool forceEstimate = false)
        {
            if (forceEstimate || tab.LastContextTokens <= 0)
            {
                tab.LastContextTokens = EstimateSessionTokens(tab.Session);
                tab.LastContextTokensAreActual = false;
            }
            RefreshContextUsageDisplay(tab);
            RefreshTabButton(tab);
        }

        private void RefreshContextUsageDisplay(AgentTabState tab)
        {
            if (tab != _activeTab) return;
            ContextUsageText.Text = FormatContextUsage(tab);
            var ratio = ContextUsageRatio(tab);
            var color = ratio >= 0.9 ? "#E8915A" : (ratio >= 0.75 ? "#E6C56E" : "#8E96B5");
            ContextUsageText.Foreground = (Brush)new BrushConverter().ConvertFromString(color);
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

            continuation = CreateTab(Memory.CreateNew(), renderHistory: false, switchTo: true);
            AppendOutput(continuation, $"CommitBall Agent Terminal v0.2.3\n");
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
            RefreshContextUsage(tab, forceEstimate: true);
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
            RefreshContextUsage(tab, forceEstimate: true);
        }

        private void RenderDisplayMessage(AgentTabState tab, Message msg)
        {
            var type = string.IsNullOrWhiteSpace(msg.DisplayType) ? "tool_done" : msg.DisplayType;
            switch (type)
            {
                case "tool_start":
                    AppendToolStart(tab, msg.Content);
                    break;
                case "tool_done":
                    AppendToolDone(tab, msg.Content);
                    break;
                case "tool_error_detail":
                    AppendOutput(tab, $"  ✗ {msg.Content}\n", "#E8915A");
                    break;
                case "subtask_progress_start":
                    AppendSubtaskProgress(tab, null);
                    break;
                case "subtask_progress":
                    AppendSubtaskProgress(tab, msg.Content);
                    break;
                case "cancelled":
                    AppendOutput(tab, msg.Content, "#AAAAAE");
                    break;
                case "error":
                    AppendOutput(tab, msg.Content, "#E8915A");
                    break;
                case "notice":
                case "turn_end":
                    AppendOutput(tab, msg.Content);
                    break;
                default:
                    AppendToolDone(tab, msg.Content);
                    break;
            }
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

        private void EnqueueInvokeForTab(AgentTabState tab, IEnumerable<string> inputs)
        {
            lock (_invokeQueue)
            {
                foreach (var input in inputs)
                {
                    if (string.IsNullOrWhiteSpace(input)) continue;
                    _invokeQueue.Enqueue(new QueuedInvoke { Target = tab, Input = input, LockAgentOutWrites = input == "/summary_to_panel" });
                }
            }
            Dispatcher.BeginInvoke((Action)TryDequeueInvoke);
        }

        public void EnqueueBarInvoke(string[] inputs)
        {
            var normalized = inputs.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            if (normalized.Count == 0) return;

            var target = GetOrCreateBarCommandTab(switchTo: true);
            NotifyBar("Agent 指令已排队");
            lock (_invokeQueue)
            {
                foreach (var input in normalized)
                    _invokeQueue.Enqueue(new QueuedInvoke
                    {
                        Target = target,
                        Input = input,
                        Kind = QueuedInvoke.InvokeKind.BarCommand,
                        LockAgentOutWrites = input == "/summary_to_panel"
                    });
            }
            Dispatcher.BeginInvoke((Action)TryDequeueInvoke);
        }

        public void EnqueueNewSessionInvoke(string[] inputs)
        {
            var normalized = inputs.Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            if (normalized.Count == 0) return;

            var target = CreateTab(Memory.CreateNew(), renderHistory: false, switchTo: true);
            AppendOutput(target, $"CommitBall Agent Terminal v0.2.3\n");
            AppendOutput(target, FormatSessionHeader(target.Session));
            AppendOutput(target, "[Core 指令新会话]\n\n", "#AAAAAE");

            lock (_invokeQueue)
            {
                foreach (var input in normalized)
                    _invokeQueue.Enqueue(new QueuedInvoke
                    {
                        Target = target,
                        Input = input,
                        Kind = QueuedInvoke.InvokeKind.NewSession,
                        LockAgentOutWrites = input == "/summary_to_panel"
                    });
            }
            Dispatcher.BeginInvoke((Action)TryDequeueInvoke);
        }

        private AgentTabState GetOrCreateBarCommandTab(bool switchTo)
        {
            if (_barCommandTab != null && _tabs.Contains(_barCommandTab) && !_barCommandTab.IsContextFull)
            {
                if (switchTo) SwitchToTab(_barCommandTab);
                return _barCommandTab;
            }

            if (_barCommandTab != null && _tabs.Contains(_barCommandTab) && _barCommandTab.IsContextFull && !_barCommandTab.IsBusy)
                CloseTab(_barCommandTab);

            _barCommandTab = CreateTab(Memory.CreateNew(Memory.PurposeBarCommand), renderHistory: false, switchTo: switchTo, kind: AgentTabState.TabKind.BarCommand);
            AppendOutput(_barCommandTab, $"CommitBall Agent Terminal v0.2.3\n");
            AppendOutput(_barCommandTab, FormatSessionHeader(_barCommandTab.Session));
            AppendOutput(_barCommandTab, "[Bar 指令专用会话]\n\n", "#AAAAAE");
            return _barCommandTab;
        }

        private static void NotifyBar(string text)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(".", "CommitBall-direct", PipeDirection.Out);
                pipe.Connect(250);
                var safe = text.Replace("\r", " ").Replace("\n", " ").Trim();
                var bytes = System.Text.Encoding.UTF8.GetBytes("CMD BAR_NOTICE " + safe);
                pipe.Write(bytes, 0, bytes.Length);
            }
            catch
            {
                // Bar may be hidden or restarting; this is only user feedback.
            }
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
                        if (candidate.Kind == QueuedInvoke.InvokeKind.BarCommand)
                        {
                            if (candidateTab == null || candidateTab.IsContextFull || ShouldTreatAsContextFull(candidateTab))
                                candidateTab = GetOrCreateBarCommandTab(switchTo: false);
                        }
                        else if (candidateTab != null && ShouldTreatAsContextFull(candidateTab))
                        {
                            candidateTab = GetQueueContinuationTab(candidateTab);
                        }
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
                if (item.Kind == QueuedInvoke.InvokeKind.BarCommand)
                    NotifyBar("Agent 正在处理指令");
                ProcessInput(tab, item.Input, item.LockAgentOutWrites);
            }
        }

        public new void Show()
        {
            Log($"Show begin visible={IsVisible} visibility={Visibility} state={WindowState} left={Left:F0} top={Top:F0} width={Width:F0} height={Height:F0}");
            EnsureWindowInWorkArea();
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;
            Visibility = Visibility.Visible;
            base.Show();
            Topmost = true;
            if (_activeTab != null)
                SwitchToTab(_activeTab);
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                ShowWindow(hwnd, SwShownormal);
                SetWindowPos(hwnd, HwndTopmost, (int)Left, (int)Top, 0, 0, SwpNoSize | SwpShowWindow);
            }
            Activate();
            InputBox.Focus();
            if (hwnd != IntPtr.Zero)
                SetForegroundWindow(hwnd);
            Log($"Show end visible={IsVisible} visibility={Visibility} state={WindowState} active={IsActive}");
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
