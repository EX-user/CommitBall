using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;

namespace CommitBallBar
{
    public partial class App : Application
    {
        private BarWindow _bar;
        private PipeServer _pipe;
        private Mutex _mutex;
        private static readonly string LogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "log", "bar.log");

        public App()
        {
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                WriteLog("FATAL: " + e.ExceptionObject);
            };
            DispatcherUnhandledException += (s, e) =>
            {
                WriteLog("UI: " + e.Exception);
                e.Handled = true;
            };
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            WriteLog("OnStartup begin");
            base.OnStartup(e);

            bool created;
            try
            {
                _mutex = new Mutex(true, "CommitBallBarMutex", out created);
            }
            catch (Exception ex)
            {
                WriteLog("Mutex error: " + ex.Message);
                created = true;
            }

            if (!created)
            {
                WriteLog("Another instance exists, exit");
                Shutdown();
                return;
            }

            try
            {
                StartParentWatcher(e.Args);
                _bar = new BarWindow();
                WriteLog("BarWindow created");
                _pipe = new PipeServer(_bar);
                _pipe.Start();
                WriteLog("PipeServer started, running");
            }
            catch (Exception ex)
            {
                WriteLog("ERROR: " + ex);
                throw;
            }
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
                    WriteLog($"Parent watcher started: pid={parentPid}");
                    parent.WaitForExit();
                    WriteLog("Parent exited, shutting down Bar");
                    Dispatcher.BeginInvoke(new Action(() => Shutdown()));
                }
                catch (Exception ex)
                {
                    WriteLog("Parent watcher failed: " + ex.Message);
                }
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
            WriteLog("OnExit");
            _pipe?.Dispose();
            if (_mutex != null)
            {
                try { _mutex.ReleaseMutex(); } catch { }
                _mutex.Dispose();
            }
            base.OnExit(e);
        }

        public static void WriteLog(string msg)
        {
            try
            {
                var dir = Path.GetDirectoryName(LogPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss.fff}] {msg}\n");
            }
            catch { }
        }
    }
}
