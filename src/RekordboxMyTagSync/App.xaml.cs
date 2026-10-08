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

            RunUiSmoke();
            Shutdown(0);
            return;
        }

        base.OnStartup(e);
    }

    private static void RunUiSmoke()
    {
        var window = new MainWindow();
        try
        {
            foreach (var name in new[]
            {
                "BridgeDirectoryTextBox",
                "SourceCandidatesGrid",
                "TargetDatabaseTextBox",
                "ValidateDatabaseAccessButton",
                "MappingGrid",
                "PathAliasGrid",
                "BuildPreviewButton",
                "PreviewGrid",
                "ApplyButton",
                "RestoreButton",
                "SaveSettingsButton",
                "DiagnosticsTextBox"
            })
            {
                if (window.FindName(name) is null)
                    throw new InvalidOperationException(
                        $"Required WPF workflow element '{name}' is missing.");
            }
        }
        finally
        {
            window.Close();
        }
    }
}
