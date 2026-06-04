using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace CommitBallBar
{
    public partial class PanelWindow : Window
    {
        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWCP_ROUND = 2;

        private static readonly string PanelPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "agent-out", "panel.html");
        private bool _webViewInitialized = false;
        private double _barTop;
        private DateTime _lastWriteTime;
        private System.Windows.Threading.DispatcherTimer? _watchTimer;

        public static bool PanelExists()
        {
            return File.Exists(PanelPath);
        }

        public PanelWindow()
        {
            InitializeComponent();
        }

        private void ApplyRoundedCorners()
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int corner = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        }

        public void PositionAbove(double barLeft, double barWidth, double barTop)
        {
            _barTop = barTop;
            var workArea = SystemParameters.WorkArea;
            Width = barWidth;
            Left = barLeft;
            Height = Math.Max(200, Math.Min(Width * 0.4, workArea.Height * 0.25));
            Top = barTop - Height - 8;
            App.WriteLog($"PositionAbove: barLeft={barLeft}, barWidth={barWidth}, barTop={barTop}, panelH={Height}, panelTop={Top}");
        }

        private async void CoreWebView2_NavigationCompleted(object sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e)
        {
            App.WriteLog($"NavigationCompleted: IsSuccess={e.IsSuccess}");
            if (!e.IsSuccess) return;
            try
            {
                var result = await WebView.CoreWebView2.ExecuteScriptAsync("JSON.stringify({h:document.body.scrollHeight,w:document.body.scrollWidth})");
                App.WriteLog($"NavigationCompleted: JS result={result}");
                var outer = System.Text.Json.JsonDocument.Parse(result);
                var inner = System.Text.Json.JsonDocument.Parse(outer.RootElement.GetString());
                var contentH = inner.RootElement.GetProperty("h").GetDouble();
                var maxH = Width * 0.4;
                var newHeight = Math.Max(120, Math.Min(contentH + 12, maxH));
                Height = newHeight;
                Top = _barTop - newHeight - 8;
                App.WriteLog($"Panel auto-resize: contentH={contentH}, maxH={maxH}, newH={newHeight}, barTop={_barTop}, newTop={Top}");
            }
            catch (Exception ex)
            {
                App.WriteLog($"Panel auto-resize failed: {ex.Message}\n{ex.StackTrace}");
            }
        }

        public async void ShowPanel()
        {
            App.WriteLog($"ShowPanel: path={PanelPath} exists={File.Exists(PanelPath)}");
            if (!File.Exists(PanelPath)) return;
            try
            {
                if (!_webViewInitialized)
                {
                    var userDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CommitBall", "WebView2");
                    var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, userDataDir);
                    await WebView.EnsureCoreWebView2Async(env);
                    WebView.CoreWebView2.NavigationCompleted += CoreWebView2_NavigationCompleted;
                    _webViewInitialized = true;
                    App.WriteLog("ShowPanel: WebView2 initialized");
                }
                var uri = new Uri("file:///" + PanelPath.Replace('\\', '/')).AbsoluteUri;
                App.WriteLog($"ShowPanel: navigating to {uri}");
                WebView.CoreWebView2.Navigate(uri);
                StartWatch();
            }
            catch (Exception ex)
            {
                App.WriteLog($"ShowPanel: WebView2 failed: {ex.Message}");
            }
            Show();
            ApplyRoundedCorners();
            App.WriteLog($"ShowPanel: window shown at Left={Left} Top={Top} W={Width} H={Height}");
        }

        public void HidePanel()
        {
            StopWatch();
            Hide();
        }

        private void StartWatch()
        {
            StopWatch();
            try { _lastWriteTime = File.GetLastWriteTime(PanelPath); } catch { return; }
            _watchTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(30)
            };
            _watchTimer.Tick += (s, e) =>
            {
                try
                {
                    if (!File.Exists(PanelPath)) return;
                    var current = File.GetLastWriteTime(PanelPath);
                    if (current != _lastWriteTime)
                    {
                        _lastWriteTime = current;
                        App.WriteLog($"Panel file changed, reloading");
                        WebView.CoreWebView2.Navigate(new Uri("file:///" + PanelPath.Replace('\\', '/')).AbsoluteUri);
                    }
                }
                catch { }
            };
            _watchTimer.Start();
        }

        private void StopWatch()
        {
            if (_watchTimer != null)
            {
                _watchTimer.Stop();
                _watchTimer = null;
            }
        }

        private void DragBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                DragMove();
                App.WriteLog($"Panel dragged to Left={Left} Top={Top}");
            }
        }
    }
}
