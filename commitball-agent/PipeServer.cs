using System;
using System.IO.Pipes;
using System.Text.Json;
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
                                if (string.IsNullOrWhiteSpace(msg))
                                {
                                    AgentWindow.Log("PipeServer: empty message");
                                    continue;
                                }
                                AgentWindow.Log($"PipeServer: received {msg.Substring(0, Math.Min(msg.Length, 160))}");
                                if (msg == "SHOW")
                                    _window.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => _window.Show()));
                                else if (msg == "QUIT")
                                {
                                    _window.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => Application.Current.Shutdown()));
                                    break;
                                }
                                else if (msg.StartsWith("INVOKE_BAR "))
                                {
                                    try
                                    {
                                        var json = msg.Substring("INVOKE_BAR ".Length);
                                        var inputs = JsonSerializer.Deserialize<string[]>(json);
                                        if (inputs != null && inputs.Length > 0)
                                        {
                                            AgentWindow.Log($"PipeServer: dispatch INVOKE_BAR count={inputs.Length}");
                                            _window.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => _window.EnqueueBarInvoke(inputs)));
                                        }
                                        else
                                        {
                                            AgentWindow.Log("PipeServer: INVOKE_BAR decoded empty inputs");
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        AgentWindow.Log($"PipeServer: INVOKE_BAR parse failed: {ex.Message}");
                                    }
                                }
                                else if (msg.StartsWith("INVOKE_NEW "))
                                {
                                    try
                                    {
                                        var json = msg.Substring("INVOKE_NEW ".Length);
                                        var inputs = JsonSerializer.Deserialize<string[]>(json);
                                        if (inputs != null && inputs.Length > 0)
                                        {
                                            AgentWindow.Log($"PipeServer: dispatch INVOKE_NEW count={inputs.Length}");
                                            _window.Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => _window.EnqueueNewSessionInvoke(inputs)));
                                        }
                                        else
                                        {
                                            AgentWindow.Log("PipeServer: INVOKE_NEW decoded empty inputs");
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        AgentWindow.Log($"PipeServer: INVOKE_NEW parse failed: {ex.Message}");
                                    }
                                }
                                else
                                {
                                    AgentWindow.Log($"PipeServer: unknown message '{msg.Substring(0, Math.Min(msg.Length, 80))}'");
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        AgentWindow.Log($"PipeServer loop error: {ex.Message}");
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
