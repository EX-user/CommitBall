using System;
using System.IO.Pipes;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace CommitBallAgent
{
    class PipeServer : IDisposable
    {
        private readonly AgentWindow _window;
        private const string PipeName = "CommitBall-Agent";
        private Thread _thread;
        private volatile bool _running = true;

        public PipeServer(AgentWindow window)
        {
            _window = window;
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
                                    _window.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => _window.Show()));
                                else if (msg == "QUIT")
                                {
                                    _window.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => Application.Current.Shutdown()));
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
