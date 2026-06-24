using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace CommitBallBar
{
    public partial class BarWindow : Window
    {
        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        private bool _locked = false;
        private bool _panelEnabled = true;
        private IntPtr _hwnd;
        private PanelWindow? _panelWindow;
        private List<string> _history = new List<string>();
        private int _historyIndex = -1;
        private bool _suppressTextChange = false;
        private int _prefixIndex = -1;
        private System.Windows.Threading.DispatcherTimer? _toastTimer;
        private static readonly string StatusPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "bar-status");

        private static readonly (string label, string prefix)[] Prefixes = new[]
        {
            ("代办", "[直达代办]用户明确希望注册代办事项，内容为："),
            ("配置", "[直达配置]用户正在补充事实性信息，场景信息或长期偏好，内容为："),
            ("指令", "[直达指令]用户正在输入直接指令，请确保被处理，内容为："),
        };

        public BarWindow()
        {
            InitializeComponent();
            PositionWindow();
            InputBox.LostKeyboardFocus += InputBox_LostKeyboardFocus;
            Deactivated += (_, _) => ScheduleAutoHideCheck(180);
            WriteStatus("hidden");
        }

        private void WriteStatus(string status)
        {
            try
            {
                var dir = Path.GetDirectoryName(StatusPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(StatusPath, status);
            }
            catch { }
        }

        private void InputBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (!_locked && Visibility == Visibility.Visible)
                ScheduleAutoHideCheck(150);
        }

        private void ScheduleAutoHideCheck(int delayMs)
        {
            var delay = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delayMs) };
            delay.Tick += (s, _) =>
            {
                delay.Stop();
                if (_locked || Visibility != Visibility.Visible) return;
                bool barFocus = IsKeyboardFocusWithin || IsActive;
                bool panelFocus = _panelWindow?.IsKeyboardFocusWithin == true || _panelWindow?.IsActive == true;
                if (!barFocus && !panelFocus)
                    HideBar();
            };
            delay.Start();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            _hwnd = new WindowInteropHelper(this).Handle;
        }

        private void PositionWindow()
        {
            var workArea = SystemParameters.WorkArea;
            Width = Math.Max(480, Math.Min(680, workArea.Width * 0.3));
            Left = (workArea.Width - Width) / 2 + workArea.Left;
            Top = workArea.Height * 3 / 4 - ActualHeight / 2;
        }

        public void ShowBar(bool locked = false)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => ShowBar(locked));
                return;
            }

            if (locked)
                SetLocked(true);

            if (Visibility == Visibility.Visible)
            {
                WriteStatus(_locked ? "locked" : "visible");
                BringToFront();
                InputBox.Focus();
                return;
            }

            InputBox.Clear();
            _historyIndex = -1;
            ResetPrefix();
            Visibility = Visibility.Visible;
            Show();
            _hwnd = new WindowInteropHelper(this).Handle;
            Activate();
            InputBox.Focus();
            if (_panelEnabled)
                ShowPanel();
            BringToFront();
            WriteStatus(_locked ? "locked" : "visible");
        }

        private void SetLocked(bool locked)
        {
            _locked = locked;
            LockBtn.Content = _locked ? "🔒" : "🔓";
            LockBtn.Foreground = _locked
                ? (Brush)new BrushConverter().ConvertFromString("#3B82F6")
                : (Brush)new BrushConverter().ConvertFromString("#AAAAAE");
            HintText.Text = _locked ? "Esc 关闭 | Enter 提交并继续" : "Esc 关闭 | 键入后 Enter 提交";
            if (Visibility == Visibility.Visible)
                WriteStatus(_locked ? "locked" : "visible");
        }

        private void ShowPanel()
        {
            App.WriteLog($"ShowPanel: PanelExists={PanelWindow.PanelExists()}");
            if (!PanelWindow.PanelExists()) return;
            if (_panelWindow == null)
            {
                _panelWindow = new PanelWindow();
                _panelWindow.Show();
                _panelWindow.Hide();
            }
            _panelWindow.PositionAbove(Left, Width, Top);
            _panelWindow.ShowPanel();
        }

        public void RefreshPanel()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(RefreshPanel);
                return;
            }
            if (_panelWindow == null)
            {
                if (Visibility == Visibility.Visible && _panelEnabled)
                    ShowPanel();
                return;
            }
            _panelWindow.RefreshPanel();
        }

        private void HideBar()
        {
            _panelWindow?.HidePanel();
            InputBox.Clear();
            _historyIndex = -1;
            ResetPrefix();
            Visibility = Visibility.Hidden;
            Hide();
            WriteStatus("hidden");
        }

        private void BringToFront()
        {
            var fg = GetForegroundWindow();
            var fgThread = GetWindowThreadProcessId(fg, out _);
            var myThread = GetCurrentThreadId();
            var attached = false;
            if (fgThread != myThread && fgThread != 0)
                attached = AttachThreadInput(myThread, fgThread, true);
            SetForegroundWindow(_hwnd);
            if (attached)
                AttachThreadInput(myThread, fgThread, false);
        }

        private void ResetPrefix()
        {
            _prefixIndex = -1;
            PrefixTag.Visibility = Visibility.Collapsed;
            CommandToast.Visibility = Visibility.Collapsed;
            _toastTimer?.Stop();
            InputBox.Margin = new Thickness(16, 0, 0, 0);
            HintText.Margin = new Thickness(18, 0, 18, 0);
        }

        private void InputBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Tab)
            {
                e.Handled = true;
                _prefixIndex++;
                if (_prefixIndex >= Prefixes.Length)
                    ResetPrefix();
                else
                {
                    var p = Prefixes[_prefixIndex];
                    PrefixTagText.Text = p.label;
                    PrefixTag.Visibility = Visibility.Visible;
                    PrefixTag.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    var tagWidth = PrefixTag.DesiredSize.Width;
                    InputBox.Margin = new Thickness(16 + tagWidth + 6, 0, 0, 0);
                    HintText.Margin = new Thickness(18 + tagWidth + 6, 0, 18, 0);
                }
                InputBox.Focus();
                return;
            }

            if (e.Key == Key.Up)
            {
                if (InputBox.Text.Length == 0 && _historyIndex == -1 && _history.Count > 0)
                {
                    _historyIndex = _history.Count - 1;
                    _suppressTextChange = true;
                    InputBox.Text = _history[_historyIndex];
                    _suppressTextChange = false;
                    InputBox.CaretIndex = InputBox.Text.Length;
                    e.Handled = true;
                    App.WriteLog($"History Up: index={_historyIndex}, count={_history.Count}");
                }
                else if (_historyIndex > 0)
                {
                    _historyIndex--;
                    _suppressTextChange = true;
                    InputBox.Text = _history[_historyIndex];
                    _suppressTextChange = false;
                    InputBox.CaretIndex = InputBox.Text.Length;
                    e.Handled = true;
                    App.WriteLog($"History Up: index={_historyIndex}");
                }
            }
            else if (e.Key == Key.Down)
            {
                if (_historyIndex >= 0)
                {
                    _historyIndex++;
                    if (_historyIndex >= _history.Count)
                    {
                        _historyIndex = -1;
                        _suppressTextChange = true;
                        InputBox.Clear();
                        _suppressTextChange = false;
                        App.WriteLog("History Down: exit browsing");
                    }
                    else
                    {
                        _suppressTextChange = true;
                        InputBox.Text = _history[_historyIndex];
                        _suppressTextChange = false;
                        InputBox.CaretIndex = InputBox.Text.Length;
                        App.WriteLog($"History Down: index={_historyIndex}");
                    }
                    e.Handled = true;
                }
            }
        }

        private void InputBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                var rawText = InputBox.Text.Trim();
                if (!string.IsNullOrEmpty(rawText))
                {
                    AddHistory(rawText);
                    if (HandleCommand(rawText))
                    {
                        InputBox.Clear();
                        InputBox.Focus();
                        return;
                    }
                    var text = _prefixIndex >= 0 ? Prefixes[_prefixIndex].prefix + rawText : rawText;
                    SaveNote(text);
                }
                if (_locked)
                {
                    InputBox.Clear();
                    InputBox.Focus();
                }
                else
                {
                    HideBar();
                }
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                HideBar();
            }
        }

        private void InputBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            HintText.Visibility = string.IsNullOrEmpty(InputBox.Text) ? Visibility.Visible : Visibility.Collapsed;
            if (_suppressTextChange) return;
            if (_historyIndex >= 0)
                _historyIndex = -1;
        }

        private void AddHistory(string rawText)
        {
            if (_history.Count == 0 || _history[_history.Count - 1] != rawText)
                _history.Add(rawText);
            _historyIndex = -1;
            App.WriteLog($"History add: count={_history.Count}, text={rawText.Substring(0, Math.Min(rawText.Length, 40))}");
        }

        private void DragBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
                DragMove();
        }

        private void LockBtn_Click(object sender, RoutedEventArgs e)
        {
            SetLocked(!_locked);
        }

        private void PanelBtn_Click(object sender, RoutedEventArgs e)
        {
            _panelEnabled = !_panelEnabled;
            PanelBtn.Foreground = _panelEnabled
                ? (Brush)new BrushConverter().ConvertFromString("#3B82F6")
                : (Brush)new BrushConverter().ConvertFromString("#AAAAAE");
            if (_panelEnabled)
                ShowPanel();
            else
                _panelWindow?.HidePanel();
            Dispatcher.BeginInvoke(new Action(() => InputBox.Focus()));
        }

        private void SaveNote(string text)
        {
            var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "notes");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, DateTime.Now.ToString("yyyy-MM-dd") + ".txt");
            var line = DateTime.Now.ToString("HH:mm:ss") + "  " + text + Environment.NewLine;
            File.AppendAllText(path, line, System.Text.Encoding.UTF8);
            SendToCommitBall(text);
        }

        private bool HandleCommand(string rawText)
        {
            var isTriggerCommand = rawText.Equals("/trigger", StringComparison.OrdinalIgnoreCase) ||
                rawText.StartsWith("/trigger ", StringComparison.OrdinalIgnoreCase);
            var isEyeCommand = rawText.Equals("/eye", StringComparison.OrdinalIgnoreCase) ||
                rawText.StartsWith("/eye ", StringComparison.OrdinalIgnoreCase);
            if ((isTriggerCommand || isEyeCommand) && !IsDirectCommandMode())
            {
                ShowCommandToast("先切到「指令」模式", false);
                App.WriteLog("Command rejected: not in direct command mode");
                return true;
            }

            if (isEyeCommand)
            {
                var arg = rawText.Length > "/eye".Length
                    ? rawText.Substring("/eye".Length).Trim().ToLowerInvariant()
                    : "toggle";
                string mode;
                string toast;
                if (arg == "on" || arg == "open" || arg == "开启")
                {
                    mode = "ON";
                    toast = "眼睛模式: 开启";
                }
                else if (arg == "off" || arg == "close" || arg == "关闭")
                {
                    mode = "OFF";
                    toast = "眼睛模式: 关闭";
                }
                else if (arg == "toggle" || arg.Length == 0)
                {
                    mode = "TOGGLE";
                    toast = "眼睛模式已切换";
                }
                else
                {
                    ShowCommandToast("用法: /eye on|off|toggle", false);
                    App.WriteLog($"Eye command invalid: {arg}");
                    return true;
                }

                SendCommandToCommitBall("SET_EYE_MODE " + mode);
                ShowCommandToast(toast, true);
                App.WriteLog($"Eye command sent: {mode}");
                return true;
            }

            if (rawText == "/trigger")
            {
                ShowCommandToast("用法: /trigger ;cb", false);
                App.WriteLog("Trigger command: missing value");
                return true;
            }
            if (!rawText.StartsWith("/trigger ", StringComparison.OrdinalIgnoreCase))
                return false;

            var trigger = rawText.Substring("/trigger ".Length).Trim();
            if (!IsValidTrigger(trigger))
            {
                ShowCommandToast("触发词格式无效", false);
                App.WriteLog($"Trigger command invalid: {trigger}");
                return true;
            }

            SendCommandToCommitBall("SET_TRIGGER " + trigger);
            ShowCommandToast($"唤醒序列: {trigger}", true);
            App.WriteLog($"Trigger command sent: {trigger}");
            return true;
        }

        private void ShowCommandToast(string text, bool success)
        {
            CommandToastText.Text = text;
            CommandToast.Background = (Brush)new BrushConverter().ConvertFromString(success ? "#EAF2FF" : "#FFF2E8");
            CommandToastText.Foreground = (Brush)new BrushConverter().ConvertFromString(success ? "#2563EB" : "#C35A14");
            CommandToast.Visibility = Visibility.Visible;

            _toastTimer?.Stop();
            _toastTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2200) };
            _toastTimer.Tick += (_, _) =>
            {
                _toastTimer?.Stop();
                CommandToast.Visibility = Visibility.Collapsed;
            };
            _toastTimer.Start();
        }

        private bool IsDirectCommandMode()
        {
            return _prefixIndex >= 0 &&
                _prefixIndex < Prefixes.Length &&
                Prefixes[_prefixIndex].label == "指令";
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

        private void SendCommandToCommitBall(string command)
        {
            try
            {
                using (var pipe = new System.IO.Pipes.NamedPipeClientStream(".", "CommitBall-direct", System.IO.Pipes.PipeDirection.Out))
                {
                    pipe.Connect(1000);
                    var bytes = System.Text.Encoding.UTF8.GetBytes("CMD " + command);
                    pipe.Write(bytes, 0, bytes.Length);
                    App.WriteLog("Sent command to CommitBall: " + command);
                }
            }
            catch (Exception ex)
            {
                App.WriteLog("SendCommandToCommitBall failed: " + ex.Message);
            }
        }

        private void SendToCommitBall(string text)
        {
            try
            {
                using (var pipe = new System.IO.Pipes.NamedPipeClientStream(".", "CommitBall-direct", System.IO.Pipes.PipeDirection.Out))
                {
                    pipe.Connect(1000);
                    var bytes = System.Text.Encoding.UTF8.GetBytes(text);
                    pipe.Write(bytes, 0, bytes.Length);
                    App.WriteLog("Sent to CommitBall: " + text.Substring(0, Math.Min(text.Length, 40)));
                }
            }
            catch (Exception ex)
            {
                App.WriteLog("SendToCommitBall failed: " + ex.Message);
            }
        }
    }
}
