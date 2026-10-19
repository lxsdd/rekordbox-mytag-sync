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
            try
            {
                RunUiSmoke();
                Shutdown(0);
            }
            catch (Exception exception)
            {
                // Windows GUI executables have no usable CI stderr stream.
                // Write ONLY in explicit --ui-smoke mode and let the runner
                // print the failure for targeted regression repairs.
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                        "RekordboxMyTagSync-ui-smoke-error.txt"),
                    exception.ToString());
                Shutdown(1);
            }
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
                "AnalyzePathsButton",
                "VerifyRootSampleButton",
                "VerifyPhysicalIdsButton",
                "CancelPhysicalIdsButton",
                "PhysicalIdentityStatusTextBlock",
                "PathVerificationStatusTextBlock",
                "PathAnalysisStatusTextBlock",
                "PathProposalsGrid",
                "BuildPreviewButton",
                "BuildVerifiedPreviewButton",
                "CancelVerifiedPreviewButton",
                "PreviewRunStatusTextBlock",
                "PreviewInvestigationStatusTextBlock",
                "PreviewResultsTabs",
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

            // Production table templates must never silently clip columns.
            // In particular Path matching needs horizontal overflow for
            // long Z:/R: source and destination paths at normal window width.
            var tableNames = new[]
            {
                "SourceCandidatesGrid", "PathAliasGrid", "TargetCandidatesGrid",
                "PathProposalsGrid", "MappingGrid", "PreviewGrid",
                "PreviewInvestigationGrid", "PhysicalExceptionsGrid"
            };
            foreach (var name in tableNames)
            {
                if (window.FindName(name) is not System.Windows.Controls.DataGrid table)
                    throw new InvalidOperationException($"Missing production table '{name}'.");
                if (!HorizontalScrollSupport.IsEnabled(table))
                    throw new InvalidOperationException(
                        $"Production table '{name}' is missing DJ Library precision-touchpad horizontal routing.");
                table.ApplyTemplate();
                var mode = System.Windows.Controls.ScrollViewer.GetHorizontalScrollBarVisibility(table);
                if (mode != System.Windows.Controls.ScrollBarVisibility.Auto &&
                    mode != System.Windows.Controls.ScrollBarVisibility.Visible)
                    throw new InvalidOperationException(
                        $"Production table '{name}' disables horizontal scrolling.");
            }

            // Exercise the production column-width persistence against actual
            // WPF DataGrid columns without touching user profile settings.
            var widthsPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "MyTagSync-grid-smoke-" +
                Guid.NewGuid().ToString("N") + ".json");
            try
            {
                window.MappingGrid.Columns[0].Width =
                    new System.Windows.Controls.DataGridLength(
                        237, System.Windows.Controls.DataGridLengthUnitType.Pixel);
                var originalGrids = new Dictionary<string, System.Windows.Controls.DataGrid>
                {
                    [window.MappingGrid.Name] = window.MappingGrid,
                    [window.PreviewGrid.Name] = window.PreviewGrid
                };
                GridColumnWidthStore.Save(originalGrids, widthsPath);

                var reopenedGridWindow = new MainWindow { Tag = "ui-smoke" };
                try
                {
                    var reopenedGrids = new Dictionary<string, System.Windows.Controls.DataGrid>
                    {
                        [reopenedGridWindow.MappingGrid.Name] = reopenedGridWindow.MappingGrid,
                        [reopenedGridWindow.PreviewGrid.Name] = reopenedGridWindow.PreviewGrid
                    };
                    GridColumnWidthStore.Restore(reopenedGrids, widthsPath);
                    if (Math.Abs(reopenedGridWindow.MappingGrid.Columns[0].Width.Value - 237) > 0.5)
                        throw new InvalidOperationException(
                            "User-resized WPF DataGrid column width was not restored on reopening.");

                    System.IO.File.WriteAllText(widthsPath, "{invalid-json");
                    GridColumnWidthStore.Restore(reopenedGrids, widthsPath);
                    if (Math.Abs(reopenedGridWindow.MappingGrid.Columns[0].Width.Value - 237) > 0.5)
                        throw new InvalidOperationException(
                            "Malformed optional column-width settings corrupted the current layout.");
                }
                finally { reopenedGridWindow.Close(); }
            }
            finally
            {
                if (System.IO.File.Exists(widthsPath))
                    System.IO.File.Delete(widthsPath);
            }

            var physicalTable = window.PhysicalExceptionsGrid;
            if (physicalTable.Columns.Count != 13 || physicalTable.FrozenColumnCount != 2 ||
                physicalTable.Columns[0].Header?.ToString() != "Reason" ||
                physicalTable.Columns[1].Header?.ToString() != "Content ID" ||
                physicalTable.Columns[9].Header?.ToString() != "Beatgrid refs" ||
                physicalTable.Columns[9] is not System.Windows.Controls.DataGridBoundColumn beatgridColumn ||
                beatgridColumn.Binding is not System.Windows.Data.Binding beatgridBinding ||
                beatgridBinding.Path?.Path != "BeatgridReferences")
                throw new InvalidOperationException(
                    "Targeted exception grid must preserve Reason/Content ID and bind all thirteen evidence columns, including beatgrid.");
            if (window.FindName("InvestigatePhysicalExceptionsButton") is not
                    System.Windows.Controls.Button inspectEight ||
                string.IsNullOrWhiteSpace(inspectEight.ToolTip?.ToString()))
                throw new InvalidOperationException("Targeted physical exception button has no explanation.");
            inspectEight.RaiseEvent(new RoutedEventArgs(
                System.Windows.Controls.Primitives.ButtonBase.ClickEvent, inspectEight));
            if (!window.PhysicalExceptionsStatusTextBlock.Text.Contains("Blocked",
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Unqualified physical exception inspection must fail closed and report its prerequisites.");

            // The single root mapping must not be presented as one
            // unexplained leftover track. The examples are illustrative.
            var guidance = window.PathProposalsGrid;
            var exampleHeader = guidance.Columns[4].Header?.ToString() ?? string.Empty;
            if (!exampleHeader.Contains("Illustrative", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Path proposal example still looks like a single unmatched track.");

            // Investigation columns have long file paths / raw DB row IDs.
            // Keep classification and ContentId visible during horizontal
            // touchpad or scrollbar navigation.
            var investigation = window.PreviewInvestigationGrid;
            if (investigation.FrozenColumnCount != 2 ||
                investigation.Columns.Count != 5 ||
                investigation.Columns[0].SortMemberPath != "ReasonCode" &&
                investigation.Columns[0].Header?.ToString() != "Reason" ||
                investigation.Columns[1].Header?.ToString() != "Content ID")
                throw new InvalidOperationException(
                    $"Investigation must freeze reason and Content ID: frozen={investigation.FrozenColumnCount}, " +
                    $"columns={investigation.Columns.Count}, reasonColumn={investigation.Columns[0].Header}, " +
                    $"sortMember={investigation.Columns[0].SortMemberPath}, " +
                    $"idColumn={investigation.Columns[1].Header}.");
            if (investigation.Columns[1] is not System.Windows.Controls.DataGridTextColumn pinnedId ||
                pinnedId.CellStyle is null || pinnedId.HeaderStyle is null ||
                !pinnedId.CellStyle.Setters.OfType<Setter>().Any(s =>
                    s.Property == System.Windows.Controls.Control.BorderThicknessProperty &&
                    s.Value is Thickness border && border.Right >= 2))
                throw new InvalidOperationException(
                    "Pinned Content ID column has no visible, theme-aware vertical boundary.");

            var display = InvestigationDisplayRow.FromPreview(
                new PreviewDetail(PreviewDetailKind.Unmatched, "C1", @"R:\Music\Track.mp3", null,
                    "AMBIGUOUS_SOURCE_PATH: virtual source siblings"));
            if (display.ReasonCode != "AMBIGUOUS_SOURCE_PATH" ||
                display.ContentId != "C1")
                throw new InvalidOperationException(
                    "Investigation reason code / ContentId display regression.");

            // Exercise the production PreviewEngine conflict model through the
            // actual Preview DataGrid (not the separate excluded-track table).
            // This synthetic case previously threw while appending a conflict
            // during enumeration of the Add detail.
            var syntheticPath = @"R:\\Music\\PreviewAmbiguous.mp3";
            var syntheticCore = new Dictionary<string, IReadOnlyList<string>>(
                StringComparer.OrdinalIgnoreCase);
            var syntheticExtra = new Dictionary<string, IReadOnlyList<string>>(
                StringComparer.OrdinalIgnoreCase)
            {
                ["MOOD"] = new[] { "Bright" }
            };
            var ambiguousPreview = PreviewEngine.Create(new PreviewRequest(
                "dbid:ui-smoke-ambiguous",
                new[] { new BridgeTrack(syntheticPath, 0, syntheticCore, syntheticExtra) },
                new[] { new MappingRule("MOOD", "Mood") },
                new[] { new RekordboxTrackSnapshot("C-ui", syntheticPath,
                    Array.Empty<MyTagAssignment>()) },
                Array.Empty<ManagedAssignment>(),
                MyTagDefinitions: new[]
                {
                    new RekordboxMyTagDefinition("G1", "Mood", null, 0, 0),
                    new RekordboxMyTagDefinition("G2", "Mood", null, 1, 0)
                }));
            if (ambiguousPreview.IsValid || ambiguousPreview.Counts.Conflicts != 1)
                throw new InvalidOperationException(
                    "Ambiguous MyTag definitions did not produce one fail-closed preview conflict.");
            window.PreviewGrid.ItemsSource = ambiguousPreview.Details;
            var previewConflict = window.PreviewGrid.Items
                .OfType<PreviewDetail>()
                .SingleOrDefault(x => x.Kind == PreviewDetailKind.Conflict);
            if (previewConflict?.ContentId != "C-ui" ||
                !previewConflict.Message.Contains("ambiguous", StringComparison.OrdinalIgnoreCase) ||
                window.PreviewGrid.Columns.Count != 5 ||
                window.PreviewGrid.Columns[4] is not
                    System.Windows.Controls.DataGridTextColumn messageColumn ||
                messageColumn.Binding is not System.Windows.Data.Binding messageBinding ||
                messageBinding.Path?.Path != nameof(PreviewDetail.Message))
                throw new InvalidOperationException(
                    "The real WPF Preview grid does not expose the fail-closed MyTag conflict.");
            window.PreviewGrid.ItemsSource = null;

            var proposals = window.PathProposalsGrid;
            if (proposals.Columns.Count != 6 ||
                proposals.Columns[4].MinWidth < 500 ||
                proposals.Columns[5].MinWidth < 500 ||
                proposals.Columns[0].Width.IsStar ||
                proposals.Columns[1].Width.IsStar)
                throw new InvalidOperationException(
                    "Path proposals cannot horizontally overflow to reveal complete R:/Z: examples.");

            // Click the real production button with no inspected inputs.
            // The actual event wiring must produce an immediate, persistent
            // blocked explanation rather than an inert/no-op control.
            if (window.FindName("AnalyzePathsButton") is not System.Windows.Controls.Button analyze ||
                string.IsNullOrWhiteSpace(analyze.ToolTip?.ToString()))
                throw new InvalidOperationException("Path diagnostics action/tooltip is not wired.");
            analyze.RaiseEvent(new RoutedEventArgs(
                System.Windows.Controls.Primitives.ButtonBase.ClickEvent, analyze));
            if (!window.PathAnalysisStatusTextBlock.Text.Contains("blocked", StringComparison.OrdinalIgnoreCase) ||
                !window.PathAnalysisStatusTextBlock.Text.Contains("inspect", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Production path-analysis button did not report its blocked prerequisite on click.");

            if (window.FindName("VerifyRootSampleButton") is not System.Windows.Controls.Button checkFiles ||
                string.IsNullOrWhiteSpace(checkFiles.ToolTip?.ToString()))
                throw new InvalidOperationException(
                    "Production read-only file sample button or scope tooltip is missing.");
            checkFiles.RaiseEvent(new RoutedEventArgs(
                System.Windows.Controls.Primitives.ButtonBase.ClickEvent, checkFiles));
            if (!window.PathVerificationStatusTextBlock.Text.Contains("blocked", StringComparison.OrdinalIgnoreCase) ||
                !window.PathVerificationStatusTextBlock.Text.Contains("select", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Production sample button did not display its blocked prerequisite.");

            if (window.FindName("VerifyPhysicalIdsButton") is not System.Windows.Controls.Button allIds ||
                string.IsNullOrWhiteSpace(allIds.ToolTip?.ToString()))
                throw new InvalidOperationException(
                    "Production whole-library read-only file-ID control or scope tooltip is missing.");
            allIds.RaiseEvent(new RoutedEventArgs(
                System.Windows.Controls.Primitives.ButtonBase.ClickEvent, allIds));
            if (!window.PhysicalIdentityStatusTextBlock.Text.Contains("blocked", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Production physical identity button lacks persistent blocked-click feedback.");
            if (window.FindName("CancelPhysicalIdsButton") is not System.Windows.Controls.Button cancelIds ||
                string.IsNullOrWhiteSpace(cancelIds.ToolTip?.ToString()))
                throw new InvalidOperationException("Physical identity cancellation control missing.");

            if (window.FindName("BuildPreviewButton") is not System.Windows.Controls.Button preview ||
                string.IsNullOrWhiteSpace(preview.ToolTip?.ToString()))
                throw new InvalidOperationException(
                    "Production read-only preview action/tooltip is missing.");
            // The legacy fresh-preview button must share the exact same
            // physical file-ID gate as Auto-match. No path-only preview is
            // allowed to leave a fresh/approved mutable state behind.
            preview.RaiseEvent(new RoutedEventArgs(
                System.Windows.Controls.Primitives.ButtonBase.ClickEvent, preview));
            if (!window.PreviewRunStatusTextBlock.Text.Contains("Verified preview blocked:",
                    StringComparison.OrdinalIgnoreCase) ||
                !window.PreviewRunStatusTextBlock.Text.Contains("Inspect",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Build fresh preview bypassed the production verified identity requirement.");
            if (window.FindName("BuildVerifiedPreviewButton") is not System.Windows.Controls.Button verified ||
                string.IsNullOrWhiteSpace(verified.ToolTip?.ToString()))
                throw new InvalidOperationException(
                    "Production auto-match preview action or read-only explanation is missing.");
            verified.RaiseEvent(new RoutedEventArgs(
                System.Windows.Controls.Primitives.ButtonBase.ClickEvent, verified));
            if (!window.PreviewRunStatusTextBlock.Text.Contains("blocked", StringComparison.OrdinalIgnoreCase) ||
                !window.PreviewRunStatusTextBlock.Text.Contains("Inspect", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Verified preview button must explain its missing source/target qualification.");
            if (window.FindName("CancelVerifiedPreviewButton") is not System.Windows.Controls.Button)
                throw new InvalidOperationException("Read-only verified preview cancellation control missing.");
            if (window.ApplyButton.IsEnabled || window.RestoreButton.IsEnabled)
                throw new InvalidOperationException("Read-only build must not enable database mutations.");
            window.ApplyButton.RaiseEvent(new RoutedEventArgs(
                System.Windows.Controls.Primitives.ButtonBase.ClickEvent, window.ApplyButton));
            if (!window.ApplyStatusTextBox.Text.Contains("blocked", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Direct production Apply handler must fail closed even when clicked programmatically.");

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
