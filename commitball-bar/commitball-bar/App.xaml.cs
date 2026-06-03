using System;
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
