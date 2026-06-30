using System.Windows;

namespace CommitBallBallShell;

public partial class App : Application
{
    private PipeBallBackend? _backend;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var parentPid = ParseParentPid(e.Args);
        _backend = new PipeBallBackend(parentPid);
        var window = new BallWindow(_backend);
        window.Show();
        _backend.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _backend?.Dispose();
        base.OnExit(e);
    }

    private static int? ParseParentPid(string[] args)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--parent-pid", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(args[i + 1], out var pid))
            {
                return pid;
            }
        }

        return null;
    }
}
