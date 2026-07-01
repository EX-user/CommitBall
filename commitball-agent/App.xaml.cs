using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Windows;

namespace CommitBallAgent
{
    public partial class App : Application
    {
        private AgentWindow? _window;
        private PipeServer? _pipe;
        private Mutex? _mutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            bool created;
            try
            {
                _mutex = new Mutex(true, "CommitBallAgentMutex", out created);
            }
            catch
            {
                created = true;
            }

            if (!created)
            {
                try
                {
                    using var client = new NamedPipeClientStream(".", "CommitBall-Agent", PipeDirection.Out);
                    client.Connect(1000);
                    var bytes = Encoding.UTF8.GetBytes("SHOW\n");
                    client.Write(bytes, 0, bytes.Length);
                }
                catch { }
                Shutdown();
                return;
            }

            Config.Load();
            Tools.ClearSummaryStatus();

            StartParentWatcher(e.Args);
            _window = new AgentWindow();
            _pipe = new PipeServer(_window);
            _pipe.Start();
        }

        private void StartParentWatcher(string[] args)
        {
            var parentPid = ParseParentPid(args);
            if (parentPid <= 0) return;

            var watcher = new Thread(() =>
            {
                try
                {
                    using var parent = Process.GetProcessById(parentPid);
                    parent.WaitForExit();
                    Dispatcher.BeginInvoke(new Action(() => Shutdown()));
                }
                catch { }
            });
            watcher.IsBackground = true;
            watcher.Start();
        }

        private static int ParseParentPid(string[] args)
        {
            for (int i = 0; i + 1 < args.Length; i++)
            {
                if (args[i] == "--parent-pid" && int.TryParse(args[i + 1], out var pid))
                    return pid;
            }
            return 0;
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Tools.ClearSummaryStatus();
            _pipe?.Dispose();
            if (_mutex != null)
            {
                try { _mutex.ReleaseMutex(); } catch { }
                _mutex.Dispose();
            }
            base.OnExit(e);
        }
    }
}
