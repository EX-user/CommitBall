using System;
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

        public BarWindow()
        {
            InitializeComponent();
            PositionWindow();
            InputBox.LostKeyboardFocus += InputBox_LostKeyboardFocus;
        }

        private void InputBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (!_locked && Visibility == Visibility.Visible)
            {
                var delay = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
                delay.Tick += (s, _) =>
                {
                    delay.Stop();
                    if (!_locked && Visibility == Visibility.Visible)
                    {
                        bool barFocus = IsKeyboardFocusWithin;
                        bool panelFocus = _panelWindow?.IsKeyboardFocusWithin == true;
                        if (!barFocus && !panelFocus)
                            HideBar();
                    }
                };
                delay.Start();
            }
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

        public void ShowBar()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => ShowBar());
                return;
            }

            if (Visibility == Visibility.Visible) return;

            InputBox.Clear();
            Visibility = Visibility.Visible;
            Show();
            _hwnd = new WindowInteropHelper(this).Handle;
            Activate();
            InputBox.Focus();
            if (_panelEnabled)
                ShowPanel();
            BringToFront();
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

        private void HideBar()
        {
            _panelWindow?.HidePanel();
            InputBox.Clear();
            Visibility = Visibility.Hidden;
            Hide();
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

        private void InputBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                var text = InputBox.Text.Trim();
                if (!string.IsNullOrEmpty(text))
                    SaveNote(text);
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
        }

        private void LockBtn_Click(object sender, RoutedEventArgs e)
        {
            _locked = !_locked;
            LockBtn.Content = _locked ? "🔒" : "🔓";
            LockBtn.Foreground = _locked
                ? (Brush)new BrushConverter().ConvertFromString("#3B82F6")
                : (Brush)new BrushConverter().ConvertFromString("#AAAAAE");
            HintText.Text = _locked ? "Esc 关闭 | Enter 提交并继续" : "Esc 关闭 | 键入后 Enter 提交";
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
            var path = Path.Combine(dir, DateTime.Now.ToString("yyyy-MM-dd_HHmmss") + ".txt");
            File.WriteAllText(path, text, System.Text.Encoding.UTF8);
            SendToCommitBall(text);
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
