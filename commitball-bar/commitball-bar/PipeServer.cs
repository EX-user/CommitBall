using System;
using System.IO.Pipes;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace CommitBallBar
{
    class PipeServer : IDisposable
    {
        private readonly BarWindow _bar;
        private const string PipeName = "CommitBall-bar";
        private Thread _thread;
        private volatile bool _running = true;

        public PipeServer(BarWindow bar)
        {
            _bar = bar;
        }

        public void Start()
        {
            _thread = new Thread(() =>
            {
                while (_running)
                {
                    try
                    {
                        using (var server = new NamedPipeServerStream(PipeName, PipeDirection.In))
                        {
                            server.WaitForConnection();
                            using (var reader = new System.IO.StreamReader(server))
                            {
                                var msg = reader.ReadLine()?.Trim();
                                if (msg == "SHOW")
                                    _bar.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => _bar.ShowBar()));
                                else if (msg == "QUIT")
                                {
                                    _bar.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => Application.Current.Shutdown()));
                                    break;
                                }
                            }
                        }
                    }
                    catch
                    {
                        Thread.Sleep(1000);
                    }
                }
            });
            _thread.IsBackground = true;
            _thread.Start();
        }

        public void Dispose()
        {
            _running = false;
        }
    }
}
