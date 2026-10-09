using System.Windows;
using RekordboxMyTagSync.Core;

namespace RekordboxMyTagSync;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Any(arg =>
                string.Equals(arg, "--ui-smoke", StringComparison.OrdinalIgnoreCase)))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            RunUiSmoke();
            Shutdown(0);
            return;
        }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    private static void RunUiSmoke()
    {
        var window = new MainWindow { Tag = "ui-smoke" };
        try
        {
            foreach (var name in new[]
            {
                "BridgeDirectoryTextBox",
                "SourceCandidatesGrid",
                "TargetDatabaseTextBox",
                "BrowseTargetButton",
                "ValidateDatabaseAccessButton",
                "MappingGrid",
                "PathAliasGrid",
                "BuildPreviewButton",
                "PreviewGrid",
                "ApplyButton",
                "RestoreButton",
                "SaveSettingsButton",
                "DiagnosticsTextBox",
                "SourceInspectionStatusTextBlock",
                "TargetDiscoveryStatusTextBlock",
                "DatabaseAccessStatusTextBlock",
                "OperationStatusTextBlock",
                "OperationProgressBar"
            })
            {
                if (window.FindName(name) is null)
                    throw new InvalidOperationException(
                        $"Required WPF workflow element '{name}' is missing.");
            }

            if (window.Icon is null ||
                !string.Equals(window.Title, "rekordbox MyTagSync", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    "The production app title or native window icon is missing.");

            // Regression gate: the same placement code used on closing/reopening
            // the real WPF window preserves size, position and maximize state.
            var placementPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "MyTagSync-ui-smoke-" +
                Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var workArea = SystemParameters.WorkArea;
                window.Left = workArea.Left + 20;
                window.Top = workArea.Top + 20;
                window.Width = Math.Max(1200, window.MinWidth);
                window.Height = Math.Max(760, window.MinHeight);
                WindowPlacementStore.Save(window, placementPath);
                var reopened = new MainWindow { Tag = "ui-smoke" };
                try
                {
                    WindowPlacementStore.Restore(reopened, placementPath);
                    if (Math.Abs(reopened.Left - window.Left) > 0.5 ||
                        Math.Abs(reopened.Top - window.Top) > 0.5 ||
                        Math.Abs(reopened.Width - window.Width) > 0.5 ||
                        Math.Abs(reopened.Height - window.Height) > 0.5)
                        throw new InvalidOperationException(
                            "Saved window geometry was not restored on the production window.");
                }
                finally { reopened.Close(); }
            }
            finally
            {
                if (System.IO.File.Exists(placementPath))
                    System.IO.File.Delete(placementPath);
            }
        }
        finally
        {
            window.Close();
        }
    }
}
