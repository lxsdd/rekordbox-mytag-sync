using System.Windows;

namespace RekordboxMyTagSync;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.Any(arg =>
                string.Equals(arg, "--ui-smoke", StringComparison.OrdinalIgnoreCase)))
        {
            StartupUri = null;
            base.OnStartup(e);

            _ = new MainWindow();
            Shutdown(0);
            return;
        }

        base.OnStartup(e);
    }
}
