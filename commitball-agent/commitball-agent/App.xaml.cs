using System.Windows;

namespace CommitBallAgent
{
    public partial class App : Application
    {
        private AgentWindow? _window;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            _window = new AgentWindow();
            _window.Show();
        }
    }
}
