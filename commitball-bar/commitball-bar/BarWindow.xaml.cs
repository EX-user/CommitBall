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

        public BarWindow()
        {
            InitializeComponent();
            PositionWindow();
        }

        private void PositionWindow()
        {
            var screenWidth = SystemParameters.PrimaryScreenWidth;
            var screenHeight = SystemParameters.PrimaryScreenHeight;
            var workArea = SystemParameters.WorkArea;

            Left = (screenWidth - Width) / 2;
            Top = workArea.Height * 3 / 4 - ActualHeight / 2;
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            var source = HwndSource.FromHwnd(hwnd);
            source.AddHook(WndProc);
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            return IntPtr.Zero;
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
            Activate();
            InputBox.Focus();
            BringToFront();
        }

        private void HideBar()
        {
            InputBox.Clear();
            Visibility = Visibility.Hidden;
            Hide();
        }

        private void BringToFront()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            SetForegroundWindow(hwnd);
        }

        private void InputBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                var text = InputBox.Text.Trim();
                if (!string.IsNullOrEmpty(text))
                    SaveNote(text);
                HideBar();
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
