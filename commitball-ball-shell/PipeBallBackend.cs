using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace CommitBall_BallUiLab;

public sealed class PipeBallBackend : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly int? _parentPid;
    private readonly string _windowStatePath = Path.Combine(AppContext.BaseDirectory, "data", "ball-shell-state.json");
    private readonly string _legacyPositionPath = Path.Combine(AppContext.BaseDirectory, "commitball.pos");
    private BallHostStatus _status = new("未知", "未知", "未知", "未知");
    private int _bubbleVersion;

    public PipeBallBackend(int? parentPid)
    {
        _parentPid = parentPid;
    }

    public BallRuntimeState State { get; private set; } = new(BallMode.Idle, true, false, null);

    public void Start()
    {
        _ = Task.Run(ListenLoopAsync);
        if (_parentPid is not null)
        {
            _ = Task.Run(WatchParentAsync);
        }
    }

    public BallHostStatus GetStatus() => _status;

    public BallWindowState? LoadWindowState()
    {
        try
        {
            if (!File.Exists(_windowStatePath))
            {
                return null;
            }

            var json = File.ReadAllText(_windowStatePath, Encoding.UTF8);
            return JsonSerializer.Deserialize<BallWindowState>(json);
        }
        catch
        {
            return null;
        }
    }

    public LegacyBallPosition? LoadLegacyBallPosition()
    {
        try
        {
            if (!File.Exists(_legacyPositionPath))
            {
                return null;
            }

            var text = File.ReadAllText(_legacyPositionPath, Encoding.UTF8);
            var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3 ||
                !int.TryParse(parts[0], out var x) ||
                !int.TryParse(parts[1], out var y) ||
                !int.TryParse(parts[2], out var edgeValue))
            {
                return null;
            }

            var edge = Enum.IsDefined(typeof(BallEdge), edgeValue) ? (BallEdge)edgeValue : BallEdge.None;
            return new LegacyBallPosition(x, y, edge);
        }
        catch
        {
            return null;
        }
    }

    public void SendCommand(string name)
    {
        SendDirectCommand($"UI_COMMAND {name}");
    }

    public void ReportWindowState(double x, double y, BallEdge edge, bool visible, Point? legacyBallTopLeft = null)
    {
        var payload = JsonSerializer.Serialize(new BallWindowState(x, y, edge.ToString().ToLowerInvariant(), visible));
        SaveWindowState(payload);
        if (legacyBallTopLeft is not null)
        {
            SaveLegacyBallPosition(legacyBallTopLeft.Value, edge);
        }
        SendDirectCommand($"UI_WINDOW_STATE {payload}");
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }

    private async Task ListenLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream("CommitBall-BallShell", PipeDirection.In, 4, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
                using var reader = new StreamReader(server, Encoding.UTF8);
                while (!_cts.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line is null)
                    {
                        break;
                    }
                    Application.Current.Dispatcher.Invoke(() => ApplyMessage(line));
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                await Task.Delay(250, _cts.Token).ConfigureAwait(false);
            }
        }
    }

    private async Task WatchParentAsync()
    {
        try
        {
            var parent = Process.GetProcessById(_parentPid!.Value);
            await parent.WaitForExitAsync(_cts.Token).ConfigureAwait(false);
            Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown(0));
        }
        catch
        {
            Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown(0));
        }
    }

    private void ApplyMessage(string line)
    {
        var space = line.IndexOf(' ');
        var verb = space >= 0 ? line[..space] : line;
        var json = space >= 0 ? line[(space + 1)..] : "{}";
        switch (verb)
        {
            case "STATE":
                ApplyState(json);
                break;
            case "BUBBLE":
                ApplyBubble(json);
                break;
            case "CLEAR_BUBBLE":
                State = State with { BubbleText = null };
                break;
            case "STATUS":
                ApplyStatus(json);
                break;
            case "SHUTDOWN":
                Application.Current.Shutdown(0);
                break;
        }
    }

    private void ApplyState(string json)
    {
        var msg = JsonSerializer.Deserialize<StateMessage>(json);
        if (msg is null)
        {
            return;
        }

        var mode = msg.NoAdmin ? BallMode.NoAdmin : msg.Mode switch
        {
            "recording" => BallMode.Recording,
            "noadmin" => BallMode.NoAdmin,
            _ => BallMode.Idle
        };
        State = new BallRuntimeState(mode, msg.EyeEnabled, msg.IsMouseIdle, State.BubbleText);
    }

    private void ApplyBubble(string json)
    {
        var msg = JsonSerializer.Deserialize<BubbleMessage>(json);
        if (!string.IsNullOrWhiteSpace(msg?.Text))
        {
            var version = ++_bubbleVersion;
            State = State with { BubbleText = msg.Text };
            _ = Task.Run(async () =>
            {
                await Task.Delay(2600).ConfigureAwait(false);
                Application.Current.Dispatcher.Invoke(() =>
                {
                    if (_bubbleVersion == version)
                    {
                        State = State with { BubbleText = null };
                    }
                });
            });
        }
    }

    private void ApplyStatus(string json)
    {
        var msg = JsonSerializer.Deserialize<StatusMessage>(json);
        if (msg is not null)
        {
            _status = new BallHostStatus(msg.Recording ?? "未知", msg.Db ?? "未知", msg.Bar ?? "未知", msg.Agent ?? "未知");
        }
    }

    private static void SendDirectCommand(string command)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", "CommitBall-direct", PipeDirection.Out);
            pipe.Connect(500);
            var bytes = Encoding.UTF8.GetBytes("CMD " + command + "\r\n");
            pipe.Write(bytes, 0, bytes.Length);
        }
        catch
        {
            // UI commands are best-effort. Core status will stay unchanged if delivery fails.
        }
    }

    private sealed record StateMessage(string Mode, bool EyeEnabled, bool IsMouseIdle, bool NoAdmin);
    private sealed record BubbleMessage(string Text);
    private sealed record StatusMessage(string? Recording, string? Db, string? Bar, string? Agent);

    public sealed record BallWindowState(double X, double Y, string Edge, bool Visible);
    public sealed record LegacyBallPosition(int X, int Y, BallEdge Edge);

    private void SaveWindowState(string payload)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_windowStatePath)!);
            File.WriteAllText(_windowStatePath, payload, Encoding.UTF8);
        }
        catch
        {
            // Core receives the same state as a fallback; local persistence is best-effort.
        }
    }

    private void SaveLegacyBallPosition(Point ballTopLeft, BallEdge edge)
    {
        try
        {
            var x = (int)Math.Round(ballTopLeft.X);
            var y = (int)Math.Round(ballTopLeft.Y);
            File.WriteAllText(_legacyPositionPath, $"{x} {y} {(int)edge}", Encoding.ASCII);
        }
        catch
        {
            // The JSON state and Core fallback still preserve position if this write fails.
        }
    }
}
