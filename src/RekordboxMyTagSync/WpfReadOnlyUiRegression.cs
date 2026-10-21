using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using RekordboxMyTagSync.Core;

namespace RekordboxMyTagSync;

/// <summary>
/// Checks real WPF controls (not standalone mocks) on ephemeral hosted Windows.
/// Offscreen Measure/Arrange is not a physical-monitor DPI or screenshot test.
/// </summary>
internal static class WpfReadOnlyUiRegression
{
    public static void Run(MainWindow window)
    {
        if (window.FindName("WorkflowTabs") is not TabControl tabs ||
            window.Content is not FrameworkElement root ||
            tabs.Items.Count < 2)
            throw new InvalidOperationException(
                "Production workflow tabs/layout root are unavailable for WPF regression.");

        var previousTab = tabs.SelectedIndex;
        try
        {
            foreach (var (width, height) in new[]
            {
                (900.0, 600.0),
                (1008.0, 704.0),
                (1050.0, 700.0),
                (1280.0, 820.0)
            })
            {
                tabs.SelectedIndex = 0; // Source tab: real TextBox + two Buttons.
                Measure(root, width, height);
                AssertSingleRow(window, root, tabs, "Source",
                    "BridgeDirectoryTextBox", "DiscoverSourceButton",
                    "InspectSourceButton");

                tabs.SelectedIndex = 1; // Target tab: real TextBox + two Buttons.
                Measure(root, width, height);
                AssertSingleRow(window, root, tabs, "Target Library",
                    "TargetDatabaseTextBox", "DiscoverTargetButton",
                    "BrowseTargetButton");

                tabs.SelectedIndex = 2;
                Measure(root, width, height);
                AssertToolbar(window, root, tabs, "Path matching",
                    "AnalyzePathsButton", "VerifyRootSampleButton",
                    "VerifyPhysicalIdsButton", "CancelPhysicalIdsButton");
                tabs.SelectedIndex = 4;
                Measure(root, width, height);
                AssertToolbar(window, root, tabs, "Preview / Changes",
                    "BuildPreviewButton", "BuildVerifiedPreviewButton",
                    "CancelVerifiedPreviewButton");
            }

            // Click the actual production discovery handler, not a mock.
            // The path is deliberately missing and must remain unsafe. Nothing
            // under a real user profile or live rekordbox database is opened.
            var missing = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "MyTagSync-missing-source-" +
                Guid.NewGuid().ToString("N"));
            if (System.IO.Directory.Exists(missing))
                throw new InvalidOperationException("Synthetic missing path unexpectedly exists.");
            window.BridgeDirectoryTextBox.Text = missing;
            if (window.FindName("DiscoverSourceButton") is not Button discover)
                throw new InvalidOperationException("Find sources button is unavailable.");
            discover.RaiseEvent(new RoutedEventArgs(
                System.Windows.Controls.Primitives.ButtonBase.ClickEvent, discover));
            if (!window.SourceInspectionStatusTextBlock.Text.StartsWith(
                    "Discovered ", StringComparison.Ordinal) ||
                window.SourceCandidatesGrid.ItemsSource is not
                    IEnumerable<BridgeSourceCandidate> sources ||
                !sources.Any(x =>
                    string.Equals(x.DirectoryPath, System.IO.Path.GetFullPath(missing),
                        StringComparison.OrdinalIgnoreCase) &&
                    !x.Safe &&
                    x.Error is not null &&
                    x.Error.Contains("missing", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(
                    "Production Find sources click produced no visible, fail-closed result.");
            if (window.ApplyButton.IsEnabled || window.RestoreButton.IsEnabled)
                throw new InvalidOperationException(
                    "Failed synthetic source discovery unexpectedly permitted a database write.");

            // A recipient candidate must NEVER accept live writes, including
            // direct programmatic Clicks that bypass WPF IsEnabled.
            if (!MainWindow.LiveWritesHardLocked ||
                window.WriteConsentTextBox.IsEnabled ||
                !window.WriteConsentChallengeTextBlock.Text.Contains(
                    "permanently disabled", StringComparison.OrdinalIgnoreCase) ||
                !window.PreviewStatusTextBlock.Text.Contains(
                    "READ-ONLY", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Owner-only candidate lost its irreversible read-only UI hold.");
            window.WriteConsentTextBox.Text = "SYNTHETIC-WRONG-CONSENT";
            window.ApplyButton.RaiseEvent(new RoutedEventArgs(
                System.Windows.Controls.Primitives.ButtonBase.ClickEvent,
                window.ApplyButton));
            if (window.ApplyStatusTextBox.Text != MainWindow.LiveWriteHoldMessage ||
                !string.IsNullOrEmpty(window.WriteConsentTextBox.Text))
                throw new InvalidOperationException(
                    "Direct Apply Click escaped the read-only candidate hold.");
            window.WriteConsentTextBox.Text = "SYNTHETIC-WRONG-CONSENT";
            window.RestoreButton.RaiseEvent(new RoutedEventArgs(
                System.Windows.Controls.Primitives.ButtonBase.ClickEvent,
                window.RestoreButton));
            if (window.ApplyStatusTextBox.Text != MainWindow.LiveWriteHoldMessage ||
                !string.IsNullOrEmpty(window.WriteConsentTextBox.Text) ||
                window.ApplyButton.IsEnabled || window.RestoreButton.IsEnabled)
                throw new InvalidOperationException(
                    "Direct Restore Click escaped the read-only candidate hold.");
            
            // A click on Inspect with no selected source must always produce a
            // visible, *synchronous* failure and restore the clickable action.
            // This exercises the actual Click wiring without touching a real
            // foobar snapshot, starting an async I/O operation, or pumping the
            // WPF dispatcher behind the smoke-test runner.
            window.BridgeDirectoryTextBox.Text = string.Empty;
            if (window.FindName("InspectSourceButton") is not Button inspect ||
                !inspect.IsEnabled)
                throw new InvalidOperationException(
                    "Inspect selected must be available before qualification.");
            inspect.RaiseEvent(new RoutedEventArgs(
                System.Windows.Controls.Primitives.ButtonBase.ClickEvent, inspect));
            if (!window.SourceInspectionStatusTextBlock.Text.StartsWith(
                    "Source verification failed:", StringComparison.Ordinal) ||
                !window.SourceInspectionStatusTextBlock.Text.Contains(
                    "No bridge source directory", StringComparison.OrdinalIgnoreCase) ||
                !window.OperationStatusTextBlock.Text.StartsWith(
                    "Inspect failed", StringComparison.Ordinal) ||
                window.OperationProgressBar.Visibility != Visibility.Collapsed ||
                !inspect.IsEnabled ||
                window.ApplyButton.IsEnabled || window.RestoreButton.IsEnabled)
                throw new InvalidOperationException(
                    "Inspect selected click was silent, remained stuck busy, " +
                    "or permitted a write without a verified source.");

            // No more silently ignored clicks: the real discovery controls
            // must visibly disable during an existing read-only operation and
            // become available again after it completes. This is intentionally
            // synthetic; there is no actual disk or database operation.
            var discoveryActions = new[]
            {
                "DiscoverSourceButton", "InspectSourceButton",
                "DiscoverTargetButton", "BrowseTargetButton",
                "ValidateDatabaseAccessButton", "RefreshDiagnosticsButton"
            }.Select(name => window.FindName(name) as Button ??
                throw new InvalidOperationException(
                    "Missing busy-aware production action: " + name)).ToArray();
            window.SetOperationStatus("Synthetic busy-state UI check", busy: true);
            window.UpdateWorkflowGate();
            try
            {
                if (discoveryActions.Any(action => action.IsEnabled) ||
                    window.ApplyButton.IsEnabled || window.RestoreButton.IsEnabled)
                    throw new InvalidOperationException(
                        "A read-only busy operation left a misleading clickable " +
                        "discovery action or a dangerous write action enabled.");
            }
            finally
            {
                window.SetOperationStatus("Ready — synthetic busy check complete.");
                window.UpdateWorkflowGate();
            }
            if (discoveryActions.Any(action => !action.IsEnabled) ||
                window.OperationProgressBar.Visibility != Visibility.Collapsed)
                throw new InvalidOperationException(
                    "Discovery actions did not recover from the busy operation.");
        }
        finally
        {
            tabs.SelectedIndex = previousTab;
        }
    }

    private static void Measure(FrameworkElement root, double width, double height)
    {
        root.InvalidateMeasure();
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
    }

    private static Rect Bounds(FrameworkElement element, FrameworkElement root)
    {
        if (element.ActualWidth < 8 || element.ActualHeight < 8 ||
            !element.IsVisible && element.Visibility != Visibility.Visible)
            throw new InvalidOperationException(
                "WPF control missing usable size or visibility: " + element.Name);

        GeneralTransform transform;
        try { transform = element.TransformToAncestor(root); }
        catch (InvalidOperationException ex)
        {
            throw new InvalidOperationException(
                "WPF control is not rendered in the selected workflow tab: " +
                element.Name, ex);
        }
        return transform.TransformBounds(new Rect(
            0, 0, element.ActualWidth, element.ActualHeight));
    }

    private static void AssertSingleRow(
        MainWindow window, FrameworkElement root, TabControl tabs,
        string area, params string[] elementNames)
    {
        var tabBounds = Bounds(tabs, root);
        var controls = elementNames.Select(name =>
            window.FindName(name) as FrameworkElement ??
            throw new InvalidOperationException("Missing WPF layout control: " + name))
            .ToArray();
        var rects = controls.Select(control => Bounds(control, root)).ToArray();
        var centerY = rects[0].Top + rects[0].Height / 2;
        for (var i = 0; i < rects.Length; i++)
        {
            var rect = rects[i];
            if (Math.Abs(rect.Top + rect.Height / 2 - centerY) > 4)
                throw new InvalidOperationException(
                    area + ": input and buttons have different vertical centers: " +
                    elementNames[i]);
            if (rect.Left < tabBounds.Left - 2 ||
                rect.Right > tabBounds.Right + 2 ||
                rect.Top < tabBounds.Top - 2 ||
                rect.Bottom > tabBounds.Bottom + 2)
                throw new InvalidOperationException(
                    area + ": WPF control extends beyond workflow tab: " +
                    elementNames[i]);
            if (i > 0 && rect.Left < rects[i - 1].Right - 1)
                throw new InvalidOperationException(
                    area + ": WPF controls overlap: " + elementNames[i - 1] +
                    " and " + elementNames[i]);
        }
    }
    private static void AssertToolbar(
        MainWindow window, FrameworkElement root, TabControl tabs,
        string area, params string[] names)
    {
        var bounds = Bounds(tabs, root);
        var rects = names.Select(name => Bounds(
            window.FindName(name) as FrameworkElement ??
            throw new InvalidOperationException("Missing WPF action: " + name),
            root)).ToArray();
        for (var i = 0; i < rects.Length; i++)
        {
            var rect = rects[i];
            if (rect.Left < bounds.Left - 2 || rect.Right > bounds.Right + 2 ||
                rect.Top < bounds.Top - 2 || rect.Bottom > bounds.Bottom + 2)
                throw new InvalidOperationException(
                    area + ": action clipped on narrow desktop: " + names[i]);
            for (var j = 0; j < i; j++)
            {
                var overlap = Rect.Intersect(rect, rects[j]);
                if (!overlap.IsEmpty && overlap.Width > 1 && overlap.Height > 1)
                    throw new InvalidOperationException(
                        area + ": overlapping actions " + names[i] + "/" + names[j]);
            }
        }
    }

}
