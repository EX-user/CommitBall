using System.Windows;
using System.Windows.Media;

namespace CommitBall_BallUiLab;

public enum BallMode
{
    Idle,
    Recording,
    NoAdmin
}

public enum BallEdge
{
    None,
    Left,
    Right,
    Top,
    Bottom
}

public sealed record BallRuntimeState(
    BallMode Mode,
    bool EyeEnabled,
    bool IsMouseIdle,
    string? BubbleText);

public sealed record BallCoreFrame(
    BallRuntimeState State,
    Point NormalizedCursor,
    bool HasCursor,
    string Description);

public interface IBallCoreBackend
{
    event EventHandler<BallCoreFrame>? FrameProduced;
    bool IsRunning { get; }
    void Start();
    void Stop();
    void Step();
}

public readonly record struct BallInputSnapshot(
    Point Cursor,
    bool HasCursor,
    Rect Bounds);

public sealed record BallAnimationFrame(
    double Pulse,
    double EyeYaw,
    double EyePitch,
    double Morph);

public sealed record BallBubbleStyle(
    Color Background,
    Color Border,
    Color Text,
    double CornerRadius);

public interface IBallAnimator
{
    BallAnimationFrame Tick(TimeSpan now, TimeSpan delta, BallRuntimeState state, BallInputSnapshot input);
    void RequestHalfBlink();
    void Reset();
}

public interface IBallSkin
{
    string Id { get; }
    string DisplayName { get; }
    void Render(DrawingContext dc, Rect bounds, BallRuntimeState state, BallAnimationFrame frame);
    BallBubbleStyle GetBubbleStyle(BallRuntimeState state);
}

public interface IBallSkinCatalog
{
    IReadOnlyList<IBallSkin> Skins { get; }
    IBallSkin Get(string id);
}

public interface IBallCommandSink
{
    void SetMode(BallMode mode);
    void SetEyeEnabled(bool enabled);
    void ShowBubble(string text);
    void ClearBubble();
}

public interface IBallHostCommands
{
    void OpenDataDirectory();
    void OpenLiveText();
    void OpenBarLocked();
    void OpenAgent();
    void InvokeAgentAnalysis();
    void ExitCommitBall();
}

public sealed record BallHostStatus(
    string RecordingStatus,
    string DbInfo,
    string BarStatus,
    string AgentStatus);

public interface IBallStatusProvider
{
    BallHostStatus GetStatus();
}
