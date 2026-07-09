using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace CommitBallBallShell;

public sealed class BallWindow : Window
{
    private const double BallSize = 86.0;
    private const double WindowWidth = 124.0;
    private const double WindowHeight = 124.0;
    private readonly BallSurface _surface;
    private readonly PipeBallBackend _backend;
    private readonly DispatcherTimer _topmostTimer;
    private readonly DispatcherTimer _bubbleTimer;
    private BubbleWindow? _bubbleWindow;
    private BallEdge _snappedEdge = BallEdge.None;
    private ContextMenu? _openMenu;
    private IntPtr _openMenuHwnd = IntPtr.Zero;
    private IntPtr _menuMouseHook = IntPtr.Zero;
    private LowLevelMouseProc? _menuMouseProc;
    private IntPtr _hwnd = IntPtr.Zero;

    public BallWindow(PipeBallBackend backend)
    {
        _backend = backend;
        Width = WindowWidth;
        Height = WindowHeight;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Title = "CommitBall-BallShell";

        _surface = new BallSurface(backend);
        Content = _surface;

        _topmostTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _topmostTimer.Tick += (_, _) => EnsureTopmost();
        _bubbleTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _bubbleTimer.Tick += (_, _) => UpdateBubbleWindow();
        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) =>
        {
            RestoreInitialPosition();
            EnsureTopmost();
            _topmostTimer.Start();
            _bubbleTimer.Start();
            UpdateBubbleWindow();
        };
        Unloaded += (_, _) =>
        {
            _topmostTimer.Stop();
            _bubbleTimer.Stop();
            UninstallMenuMouseHook();
            CloseBubbleWindow();
        };
        Deactivated += (_, _) => EnsureTopmost();
        LocationChanged += (_, _) =>
        {
            UpdateVisibleViewport();
            UpdateBubbleWindow();
        };
        SizeChanged += (_, _) =>
        {
            UpdateVisibleViewport();
            UpdateBubbleWindow();
        };
        Closed += (_, _) =>
        {
            CloseBubbleWindow();
            CloseContextMenu();
            _backend.ReportWindowState(Left, Top, _snappedEdge, IsVisible, GetLegacyBallTopLeft(), _surface.SkinId);
        };
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        _hwnd = hwnd;
        var exStyle = GetWindowLong(hwnd, GwlExstyle);
        SetWindowLong(hwnd, GwlExstyle, exStyle | WsExToolWindow | WsExNoActivate);
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        EnsureTopmost();
    }

    private void EnsureTopmost()
    {
        Topmost = true;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        SetWindowPos(
            hwnd,
            HwndTopmost,
            0,
            0,
            0,
            0,
            SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
    }

    private void RestoreInitialPosition()
    {
        var work = GetPrimaryWorkArea();
        var saved = _backend.LoadWindowState();
        if (!string.IsNullOrWhiteSpace(saved?.SkinId))
        {
            _surface.SetSkin(saved.SkinId);
        }

        var legacy = _backend.LoadLegacyBallPosition();
        if (legacy is not null)
        {
            SetSnappedEdge(legacy.Edge);
            PositionFromLegacyBallTopLeft(DevicePointToDip(new Point(legacy.X, legacy.Y)));
            if (_snappedEdge == BallEdge.None)
            {
                ClampWindowInsideWorkArea();
            }
            else
            {
                PositionWindowForSnappedBall(work);
            }
            UpdateVisibleViewport();
            return;
        }

        if (saved is not null && TryParseEdge(saved.Edge, out var edge))
        {
            SetSnappedEdge(edge);
            Left = saved.X;
            Top = saved.Y;
            if (edge == BallEdge.None)
            {
                ClampWindowInsideWorkArea();
            }
            else
            {
                PositionWindowForSnappedBall(work);
            }
        }
        else
        {
            Left = work.Right - Width - 24;
            Top = work.Bottom - Height - 24;
        }
        UpdateVisibleViewport();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WmNchittest:
            {
                var point = ScreenPointFromLParam(lParam);
                var local = PointFromScreen(point);
                if (_surface.IsPointInsideBall(local))
                {
                    handled = true;
                    return new IntPtr(Htcaption);
                }

                handled = true;
                return new IntPtr(Htnowhere);
            }
            case WmNclbuttondown:
                if (wParam.ToInt32() == Htcaption)
                {
                    _surface.RequestHalfBlink();
                }
                break;
            case WmNcrbuttonup:
                OpenContextMenu();
                handled = true;
                break;
            case WmExitsizemove:
                SnapToNearestEdge();
                _backend.ReportWindowState(Left, Top, _snappedEdge, IsVisible, GetLegacyBallTopLeft(), _surface.SkinId);
                handled = true;
                break;
        }

        return IntPtr.Zero;
    }

    private void SnapToNearestEdge()
    {
        var work = GetPrimaryWorkArea();
        if (TryGetCursorPointDip(out var cursor))
        {
            var cursorThreshold = DeviceDistanceToDip(96);
            var cursorDistances = new[]
            {
                (Edge: BallEdge.Left, Distance: Math.Abs(cursor.X - work.Left)),
                (Edge: BallEdge.Right, Distance: Math.Abs(work.Right - cursor.X)),
                (Edge: BallEdge.Top, Distance: Math.Abs(cursor.Y - work.Top)),
                (Edge: BallEdge.Bottom, Distance: Math.Abs(work.Bottom - cursor.Y))
            };
            var cursorBest = cursorDistances.OrderBy(x => x.Distance).First();
            if (cursorBest.Distance <= cursorThreshold)
            {
                SetSnappedEdge(cursorBest.Edge);
                PositionWindowForSnappedBall(work);
                return;
            }
        }

        var ball = GetScreenBallBounds();
        var threshold = DeviceDistanceToDip(32);
        var distances = new[]
        {
            (Edge: BallEdge.Left, Distance: Math.Abs(ball.Left - work.Left)),
            (Edge: BallEdge.Right, Distance: Math.Abs(work.Right - ball.Right)),
            (Edge: BallEdge.Top, Distance: Math.Abs(ball.Top - work.Top)),
            (Edge: BallEdge.Bottom, Distance: Math.Abs(work.Bottom - ball.Bottom))
        };
        var best = distances.OrderBy(x => x.Distance).First();
        if (best.Distance > threshold)
        {
            UnsnapPreservingBallPosition();
            return;
        }

        SetSnappedEdge(best.Edge);
        PositionWindowForSnappedBall(work);
    }

    private void PositionWindowForSnappedBall(Rect work)
    {
        var localCenter = _surface.BallCenter;
        switch (_snappedEdge)
        {
            case BallEdge.Left:
                Left = work.Left - localCenter.X;
                Top = Math.Clamp(Top, work.Top, work.Bottom - Height);
                break;
            case BallEdge.Right:
                Left = work.Right - localCenter.X;
                Top = Math.Clamp(Top, work.Top, work.Bottom - Height);
                break;
            case BallEdge.Top:
                Top = work.Top - localCenter.Y;
                Left = Math.Clamp(Left, work.Left, work.Right - Width);
                break;
            case BallEdge.Bottom:
                Top = work.Bottom - localCenter.Y;
                Left = Math.Clamp(Left, work.Left, work.Right - Width);
                break;
        }
        UpdateVisibleViewport();
    }

    private void UnsnapPreservingBallPosition()
    {
        var screenBall = GetScreenBallBounds();
        var ballTopLeft = new Point(screenBall.Left, screenBall.Top);
        SetSnappedEdge(BallEdge.None);
        PositionFromLegacyBallTopLeft(ballTopLeft);
        ClampBallInsideWorkArea();
    }

    private void ClampBallInsideWorkArea()
    {
        var work = GetPrimaryWorkArea();
        var ball = GetScreenBallBounds();
        var dx = 0.0;
        var dy = 0.0;
        if (ball.Left < work.Left)
            dx = work.Left - ball.Left;
        else if (ball.Right > work.Right)
            dx = work.Right - ball.Right;
        if (ball.Top < work.Top)
            dy = work.Top - ball.Top;
        else if (ball.Bottom > work.Bottom)
            dy = work.Bottom - ball.Bottom;
        Left += dx;
        Top += dy;
        UpdateVisibleViewport();
    }

    private void ClampWindowInsideWorkArea()
    {
        var work = GetPrimaryWorkArea();
        Left = Math.Clamp(Left, work.Left, Math.Max(work.Left, work.Right - Width));
        Top = Math.Clamp(Top, work.Top, Math.Max(work.Top, work.Bottom - Height));
        UpdateVisibleViewport();
    }

    private void SetSnappedEdge(BallEdge edge)
    {
        _snappedEdge = edge;
        _surface.SnappedEdge = edge;
    }

    private static bool TryParseEdge(string? value, out BallEdge edge)
    {
        edge = BallEdge.None;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        return Enum.TryParse(value, true, out edge);
    }

    private void UpdateVisibleViewport()
    {
        var work = GetPrimaryWorkArea();
        var left = Math.Max(0, work.Left - Left);
        var top = Math.Max(0, work.Top - Top);
        var right = Math.Min(Width, work.Right - Left);
        var bottom = Math.Min(Height, work.Bottom - Top);
        if (right <= left || bottom <= top)
        {
            _surface.VisibleViewport = new Rect(0, 0, Width, Height);
            return;
        }

        _surface.VisibleViewport = new Rect(left, top, right - left, bottom - top);
    }

    private void UpdateBubbleWindow()
    {
        var text = _backend.State.BubbleText;
        if (string.IsNullOrWhiteSpace(text))
        {
            CloseBubbleWindow();
            return;
        }

        var work = GetPrimaryWorkArea();
        var anchor = GetScreenBallBounds();
        _bubbleWindow ??= new BubbleWindow();
        if (!_bubbleWindow.IsVisible)
        {
            _bubbleWindow.Show();
        }
        _bubbleWindow.UpdateBubble(work, anchor, text, _surface.GetBubbleStyle(_backend.State));
    }

    private void CloseBubbleWindow()
    {
        if (_bubbleWindow is null)
        {
            return;
        }

        _bubbleWindow.Close();
        _bubbleWindow = null;
    }

    private void PositionFromLegacyBallTopLeft(Point ballTopLeft)
    {
        var ball = _surface.BallBounds();
        Left = ballTopLeft.X - ball.Left;
        Top = ballTopLeft.Y - ball.Top;
    }

    private Rect GetScreenBallBounds()
    {
        var ball = _surface.BallBounds();
        return new Rect(Left + ball.Left, Top + ball.Top, ball.Width, ball.Height);
    }

    private Point GetLegacyBallTopLeft()
    {
        var ball = GetScreenBallBounds();
        return DipPointToDevice(new Point(ball.Left, ball.Top));
    }

    private void OpenContextMenu()
    {
        CloseContextMenu();

        _surface.RequestHalfBlink();
        var menu = new ContextMenu
        {
            StaysOpen = false,
            Focusable = false,
            Placement = PlacementMode.AbsolutePoint
        };
        if (TryGetCursorPointDip(out var cursor))
        {
            menu.HorizontalOffset = cursor.X;
            menu.VerticalOffset = cursor.Y;
        }
        _openMenu = menu;
        var status = _backend.GetStatus();
        AddStatus(menu, $"记录：{status.RecordingStatus}");
        AddStatus(menu, $"数据库：{status.DbInfo}");
        AddStatus(menu, $"Bar：{status.BarStatus}");
        AddStatus(menu, $"Agent：{status.AgentStatus}");
        menu.Items.Add(new Separator());
        menu.Items.Add(CommandItem("打开数据目录", "open_data_directory"));
        menu.Items.Add(CommandItem("打开当前记录文本", "open_live_text"));
        menu.Items.Add(new Separator());
        menu.Items.Add(CommandItem("打开并锁定 Bar", "open_bar_locked"));
        menu.Items.Add(CommandItem("打开 Agent", "open_agent"));
        menu.Items.Add(CommandItem("分析当前状态", "invoke_agent_analysis"));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateSkinMenu());
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateHelpMenu());
        menu.Items.Add(new Separator());
        menu.Items.Add(CommandItem("退出 CommitBall", "exit_commitball"));
        menu.PlacementTarget = this;
        menu.Opened += (_, _) =>
        {
            _openMenuHwnd = ((HwndSource?)PresentationSource.FromVisual(menu))?.Handle ?? IntPtr.Zero;
            InstallMenuMouseHook();
        };
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_openMenu, menu))
            {
                _openMenu = null;
            }
            _openMenuHwnd = IntPtr.Zero;
            UninstallMenuMouseHook();
        };
        menu.IsOpen = true;
    }

    private void CloseContextMenu()
    {
        if (_openMenu?.IsOpen == true)
        {
            _openMenu.IsOpen = false;
        }
    }

    private void InstallMenuMouseHook()
    {
        if (_menuMouseHook != IntPtr.Zero)
        {
            return;
        }

        _menuMouseProc = MenuMouseHookProc;
        _menuMouseHook = SetWindowsHookEx(WhMouseLl, _menuMouseProc, IntPtr.Zero, 0);
    }

    private void UninstallMenuMouseHook()
    {
        if (_menuMouseHook == IntPtr.Zero)
        {
            return;
        }

        UnhookWindowsHookEx(_menuMouseHook);
        _menuMouseHook = IntPtr.Zero;
        _menuMouseProc = null;
    }

    private IntPtr MenuMouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && IsMouseButtonDownMessage(wParam.ToInt32()))
        {
            var hook = Marshal.PtrToStructure<MouseHookStruct>(lParam);
            var point = new NativePoint { X = hook.Point.X, Y = hook.Point.Y };
            if (!IsPointInsideMenuOrBall(point))
            {
                Dispatcher.BeginInvoke(() =>
                {
                    if (_openMenu?.IsOpen == true)
                    {
                        _openMenu.IsOpen = false;
                    }
                });
            }
        }

        return CallNextHookEx(_menuMouseHook, nCode, wParam, lParam);
    }

    private static bool IsMouseButtonDownMessage(int msg)
    {
        return msg is WmLbuttondown or WmRbuttondown or WmMbuttondown;
    }

    private bool IsPointInsideMenuOrBall(NativePoint cursor)
    {
        if (_openMenuHwnd != IntPtr.Zero &&
            GetWindowRect(_openMenuHwnd, out var menuRect) &&
            Contains(menuRect, cursor))
        {
            return true;
        }

        var ball = DipRectToDevice(GetScreenBallBounds());
        if (Contains(ball, cursor))
        {
            return true;
        }

        var hwndAtPoint = WindowFromPoint(cursor);
        if (hwndAtPoint == IntPtr.Zero || hwndAtPoint == _hwnd)
        {
            return false;
        }

        GetWindowThreadProcessId(hwndAtPoint, out var processId);
        return processId == Environment.ProcessId;
    }

    private static void AddStatus(ContextMenu menu, string text)
    {
        menu.Items.Add(new MenuItem { Header = text, IsEnabled = false, FontWeight = FontWeights.SemiBold });
    }

    private MenuItem CommandItem(string text, string command)
    {
        var item = new MenuItem { Header = text };
        item.Click += (_, _) => _backend.SendCommand(command);
        return item;
    }

    private MenuItem CreateSkinMenu()
    {
        var root = new MenuItem { Header = "皮肤" };
        foreach (var skin in _surface.Skins)
        {
            var item = new MenuItem { Header = skin.DisplayName, Tag = skin.Id, IsCheckable = true, IsChecked = skin.Id == _surface.SkinId };
            item.Click += (_, _) => SelectSkin((string)item.Tag);
            root.Items.Add(item);
        }
        return root;
    }

    private void SelectSkin(string id)
    {
        _surface.SetSkin(id);
        UpdateBubbleWindow();
        _backend.ReportWindowState(Left, Top, _snappedEdge, IsVisible, GetLegacyBallTopLeft(), _surface.SkinId);
    }

    private static MenuItem CreateHelpMenu()
    {
        var help = new MenuItem { Header = "帮助" };
        foreach (var line in new[]
        {
            "CapsLock 四连击开始或停止记录。",
            "Bar 用于输入自然语言指令，打开时默认锁定。",
            "右键菜单可打开数据、Bar、Agent 和触发分析。",
            "气泡用于显示配置变更、Agent 回复和归档提示。"
        })
        {
            help.Items.Add(new MenuItem { Header = line, IsEnabled = false });
        }
        return help;
    }

    private const int GwlExstyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int WmNchittest = 0x0084;
    private const int WmNclbuttondown = 0x00A1;
    private const int WmNcrbuttonup = 0x00A5;
    private const int WmExitsizemove = 0x0232;
    private const int Htnowhere = 0;
    private const int Htcaption = 2;
    private const int SpiGetworkarea = 0x0030;
    private const int SmCxscreen = 0;
    private const int SmCyscreen = 1;
    private const int WhMouseLl = 14;
    private const int WmLbuttondown = 0x0201;
    private const int WmRbuttondown = 0x0204;
    private const int WmMbuttondown = 0x0207;
    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr hwndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(int action, int param, out NativeRect rect, int update);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    private static bool Contains(NativeRect rect, NativePoint point)
    {
        return point.X >= rect.Left &&
               point.X < rect.Right &&
               point.Y >= rect.Top &&
               point.Y < rect.Bottom;
    }

    private static Point ScreenPointFromLParam(IntPtr lParam)
    {
        var value = lParam.ToInt64();
        var x = unchecked((short)(value & 0xffff));
        var y = unchecked((short)((value >> 16) & 0xffff));
        return new Point(x, y);
    }

    private Rect GetPrimaryWorkArea()
    {
        if (SystemParametersInfo(SpiGetworkarea, 0, out var rect, 0))
        {
            var topLeft = DevicePointToDip(new Point(rect.Left, rect.Top));
            var bottomRight = DevicePointToDip(new Point(rect.Right, rect.Bottom));
            return new Rect(topLeft, bottomRight);
        }

        return SystemParameters.WorkArea;
    }

    private Point DevicePointToDip(Point point)
    {
        var source = PresentationSource.FromVisual(this);
        return source?.CompositionTarget?.TransformFromDevice.Transform(point) ?? point;
    }

    private Point DipPointToDevice(Point point)
    {
        var source = PresentationSource.FromVisual(this);
        return source?.CompositionTarget?.TransformToDevice.Transform(point) ?? point;
    }

    private NativeRect DipRectToDevice(Rect rect)
    {
        var topLeft = DipPointToDevice(new Point(rect.Left, rect.Top));
        var bottomRight = DipPointToDevice(new Point(rect.Right, rect.Bottom));
        return new NativeRect
        {
            Left = (int)Math.Floor(topLeft.X),
            Top = (int)Math.Floor(topLeft.Y),
            Right = (int)Math.Ceiling(bottomRight.X),
            Bottom = (int)Math.Ceiling(bottomRight.Y)
        };
    }

    private double DeviceDistanceToDip(double value)
    {
        var source = PresentationSource.FromVisual(this);
        return source?.CompositionTarget?.TransformFromDevice.M11 * value ?? value;
    }

    private bool TryGetCursorPointDip(out Point point)
    {
        if (GetCursorPos(out var nativePoint))
        {
            point = DevicePointToDip(new Point(nativePoint.X, nativePoint.Y));
            return true;
        }

        point = default;
        return false;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out int processId);

    private delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseHookStruct
    {
        public NativePoint Point;
        public int MouseData;
        public int Flags;
        public int Time;
        public IntPtr ExtraInfo;
    }
}

public sealed class BubbleWindow : Window
{
    private readonly BubbleSurface _surface = new();

    public BubbleWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;
        Title = "CommitBall-Bubble";
        Content = _surface;
        SourceInitialized += OnSourceInitialized;
    }

    public void UpdateBubble(Rect work, Rect anchor, string text, BallBubbleStyle style)
    {
        Left = work.Left;
        Top = work.Top;
        Width = work.Width;
        Height = work.Height;
        _surface.Update(new Rect(0, 0, work.Width, work.Height), Offset(anchor, -work.Left, -work.Top), text, style);
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        var exStyle = GetWindowLong(hwnd, GwlExstyle);
        SetWindowLong(hwnd, GwlExstyle, exStyle | WsExToolWindow | WsExNoActivate | WsExTransparent);
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpNoOwnerZOrder);
    }

    private static Rect Offset(Rect rect, double dx, double dy)
    {
        return new Rect(rect.Left + dx, rect.Top + dy, rect.Width, rect.Height);
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmNchittest)
        {
            handled = true;
            return new IntPtr(Httransparent);
        }

        return IntPtr.Zero;
    }

    private const int GwlExstyle = -20;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExTransparent = 0x00000020;
    private const int WsExNoActivate = 0x08000000;
    private const int WmNchittest = 0x0084;
    private const int Httransparent = -1;
    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr hwndInsertAfter, int x, int y, int cx, int cy, uint flags);
}

public sealed class BubbleSurface : FrameworkElement
{
    private Rect _viewport;
    private Rect _anchor;
    private string _text = "";
    private BallBubbleStyle _style = BasicBallRenderer.DefaultBubbleStyle;

    public void Update(Rect viewport, Rect anchor, string text, BallBubbleStyle style)
    {
        _viewport = viewport;
        _anchor = anchor;
        _text = text;
        _style = style;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (string.IsNullOrWhiteSpace(_text))
        {
            return;
        }

        BasicBallRenderer.RenderBubble(dc, _viewport, _anchor, _text, 1.0, _style);
    }
}

public sealed class BallSurface : FrameworkElement
{
    private static readonly BallAnimationFrame StaticFrame = new(0, 0, 0, 0);
    private readonly PipeBallBackend _backend;
    private readonly IBallAnimator _animator = new SpringBallAnimator();
    private readonly BallSkinCatalog _catalog = new();
    private readonly DispatcherTimer _timer;
    private BallAnimationFrame _frame = StaticFrame;
    private TimeSpan _lastTick;
    private Point _cursor;
    private bool _hasCursor;
    private Point _lastScreenCursor;
    private bool _hasLastScreenCursor;
    private DateTime _lastMouseMoveAt = DateTime.UtcNow;
    private IBallSkin _skin;
    private BallEdge _snappedEdge = BallEdge.None;
    private Rect _visibleViewport;

    public BallSurface(PipeBallBackend backend)
    {
        _backend = backend;
        _skin = _catalog.Skins[0];
        SnapsToDevicePixels = true;
        Focusable = false;
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        _timer.Tick += OnTick;
        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => _timer.Stop();
        MouseMove += (_, e) =>
        {
            UpdateCursorFromScreen();
        };
    }

    public IReadOnlyList<IBallSkin> Skins => _catalog.Skins;
    public string SkinId => _skin.Id;
    public Point BallCenter
    {
        get
        {
            var bounds = BallBounds();
            return new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
        }
    }

    public BallEdge SnappedEdge
    {
        get => _snappedEdge;
        set
        {
            if (_snappedEdge == value)
            {
                return;
            }

            _snappedEdge = value;
            InvalidateVisual();
        }
    }

    public Rect VisibleViewport
    {
        get => _visibleViewport.IsEmpty || _visibleViewport.Width <= 0 || _visibleViewport.Height <= 0
            ? new Rect(0, 0, ActualWidth, ActualHeight)
            : _visibleViewport;
        set
        {
            _visibleViewport = value;
            InvalidateVisual();
        }
    }

    public void SetSkin(string id)
    {
        _skin = _catalog.Get(id);
        _animator.Reset();
        InvalidateVisual();
    }

    public void RequestHalfBlink()
    {
        _animator.RequestHalfBlink();
        InvalidateVisual();
    }

    public BallBubbleStyle GetBubbleStyle(BallRuntimeState state)
    {
        return _skin.GetBubbleStyle(state);
    }

    public bool IsPointInsideBall(Point point)
    {
        var ball = BallBounds();
        var center = new Point(ball.Left + ball.Width / 2, ball.Top + ball.Height / 2);
        var radius = Math.Min(ball.Width, ball.Height) / 2;
        var dx = point.X - center.X;
        var dy = point.Y - center.Y;
        return dx * dx + dy * dy <= radius * radius;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var viewport = VisibleViewport;
        var ball = BallBounds();
        var state = CurrentState();
        try
        {
            _skin.Render(dc, ball, state, ShouldAnimate(state) ? _frame : StaticFrame);

        }
        catch
        {
            BasicBallRenderer.Render(dc, ball, state, StaticFrame);
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var now = TimeSpan.FromMilliseconds(Environment.TickCount64);
        var delta = _lastTick == TimeSpan.Zero ? TimeSpan.FromMilliseconds(16) : now - _lastTick;
        _lastTick = now;
        UpdateCursorFromScreen();
        _frame = _animator.Tick(now, delta, CurrentState(), CurrentInput());
        InvalidateVisual();
    }

    private BallRuntimeState CurrentState()
    {
        var state = _backend.State;
        var mouseIdle = !_hasCursor || (DateTime.UtcNow - _lastMouseMoveAt).TotalSeconds >= 6.0;
        return state with { IsMouseIdle = mouseIdle };
    }

    private BallInputSnapshot CurrentInput()
    {
        return new BallInputSnapshot(_cursor, _hasCursor, BallBounds());
    }

    private void UpdateCursorFromScreen()
    {
        if (!GetCursorPos(out var pt))
        {
            _hasCursor = false;
            return;
        }

        var screen = new Point(pt.X, pt.Y);
        if (!_hasLastScreenCursor ||
            Math.Abs(screen.X - _lastScreenCursor.X) >= 1.0 ||
            Math.Abs(screen.Y - _lastScreenCursor.Y) >= 1.0)
        {
            _lastScreenCursor = screen;
            _hasLastScreenCursor = true;
            _lastMouseMoveAt = DateTime.UtcNow;
        }

        _cursor = PointFromScreen(screen);
        _hasCursor = true;
    }

    public Rect BallBounds()
    {
        const double size = 86.0;
        var x = _snappedEdge switch
        {
            BallEdge.Right => ActualWidth - size - 12,
            BallEdge.Top or BallEdge.Bottom => (ActualWidth - size) / 2,
            _ => 12
        };
        var y = _snappedEdge switch
        {
            BallEdge.Top => 12,
            BallEdge.Left or BallEdge.Right => (ActualHeight - size) / 2,
            _ => ActualHeight - size - 12
        };
        return new Rect(x, y, size, size);
    }

    private static bool ShouldAnimate(BallRuntimeState state)
    {
        return state.Mode == BallMode.Recording && state.EyeEnabled;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }
}
