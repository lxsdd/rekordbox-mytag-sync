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
                "WriteConsentTextBox",
                "WriteConsentChallengeTextBlock",
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

            // Real production elements must be understandable through WPF
            // accessibility APIs, even when visible labels are in another tab
            // or a user navigates entirely with the keyboard.
            foreach (var (elementName, expectedName) in new[]
            {
                ("BridgeDirectoryTextBox", "foobar2000 Bridge snapshot directory"),
                ("TargetDatabaseTextBox", "rekordbox master.db database path"),
                ("PathProposalsGrid", "Unverified folder root mapping proposals"),
                ("WriteConsentTextBox", "One-operation database write confirmation")
            })
            {
                if (window.FindName(elementName) is not System.Windows.FrameworkElement element ||
                    System.Windows.Automation.AutomationProperties.GetName(element) != expectedName ||
                    string.IsNullOrWhiteSpace(
                        System.Windows.Automation.AutomationProperties.GetHelpText(element)))
                    throw new InvalidOperationException(
                        $"The production WPF UI is missing an accessible name/description on '{elementName}'.");
            }

            foreach (var (buttonName, expectedCaption) in new[]
            {
                ("DiscoverSourceButton", "Find sources"),
                ("InspectSourceButton", "Inspect selected"),
                ("DiscoverTargetButton", "Find libraries")
            })
            {
                if (window.FindName(buttonName) is not System.Windows.Controls.Button button ||
                    !string.Equals(button.Content?.ToString(), expectedCaption, StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(button.ToolTip?.ToString()))
                    throw new InvalidOperationException(
                        $"The discovery action '{buttonName}' is not clear or lacks explanatory help.");
            }

            foreach (var statusName in new[]
            {
                "OperationStatusTextBlock", "PathAnalysisStatusTextBlock",
                "PathVerificationStatusTextBlock", "PreviewRunStatusTextBlock"
            })
            {
                if (window.FindName(statusName) is not System.Windows.Controls.TextBlock status ||
                    System.Windows.Automation.AutomationProperties.GetLiveSetting(status) !=
                        System.Windows.Automation.AutomationLiveSetting.Polite)
                    throw new InvalidOperationException(
                        $"The workflow result status '{statusName}' lacks an accessible live-region setting.");
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
                if (!table.EnableRowVirtualization || !table.EnableColumnVirtualization ||
                    !System.Windows.Controls.ScrollViewer.GetCanContentScroll(table))
                    throw new InvalidOperationException(
                        $"Production WPF table '{name}' must virtualize both rows and columns at 52,784+ library scale.");
                table.ApplyTemplate();
                var mode = System.Windows.Controls.ScrollViewer.GetHorizontalScrollBarVisibility(table);
                if (mode != System.Windows.Controls.ScrollBarVisibility.Auto &&
                    mode != System.Windows.Controls.ScrollBarVisibility.Visible)
                    throw new InvalidOperationException(
                        $"Production table '{name}' disables horizontal scrolling.");
            }

            // Real WPF Source discovery must reject a V3 state advertising
            // an older V2 payload. This is synthetic temporary input only.
            var schemaSmokeDir = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "MyTagSync-source-schema-" +
                Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(schemaSmokeDir);
            try
            {
                System.IO.File.WriteAllText(
                    System.IO.Path.Combine(schemaSmokeDir, BridgeSourceDiscovery.StateFileName),
                    "schema_version\t3\ngeneration\t1\ncomplete\t1\n" +
                    "item_count\t0\nlast_change_utc\t2026-10-19T00:00:00Z\n");
                using (var file = System.IO.File.Create(System.IO.Path.Combine(
                           schemaSmokeDir, BridgeSourceDiscovery.SnapshotFileName)))
                using (var zip = new System.IO.Compression.GZipStream(
                           file, System.IO.Compression.CompressionLevel.Fastest))
                using (var writer = new System.IO.StreamWriter(zip))
                    writer.WriteLine(
                        "path\tsubsong\tartist\tartists\ttitle\toriginal_title\tremixed_by\t" +
                        "album\talbum_artist\ttrack_number\ttotal_tracks\tdisc_number\t" +
                        "total_discs\tdate\tgenre\tstyle\tbpm\tlabel\tcatalog_number\t" +
                        "duration_seconds\tisrc\tcodec\tbitrate\ttag_fingerprint\textra_metadata_json");
                var candidate = BridgeSourceDiscovery.Inspect(schemaSmokeDir);
                if (candidate.Safe || candidate.Error is null ||
                    !candidate.Error.Contains("schema", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException(
                        "WPF Source discovery allowed V3 state / V2 payload mismatch.");
            }
            finally
            {
                System.IO.Directory.Delete(schemaSmokeDir, recursive: true);
            }

            // DATE/Year's real WPF mapping evaluator must honor all
            // producer V3 DATE/YEAR values, not just one scalar.
            var yearValues = MappingEngine.Apply(
                new MappingRule("DATE", "Year", TransformKind.YearFromDate),
                new[] { "2001-01-01", "2002-02-02" });
            if (!yearValues.SequenceEqual(new[] { "2001", "2002" }))
                throw new InvalidOperationException(
                    "Production WPF Year mapping dropped Bridge V3 Core multivalues.");

            // The source-value regex filter must be an editable production
            // mapping column, not just a core API option hidden from users.
            var filterColumn = window.MappingGrid.Columns
                .OfType<System.Windows.Controls.DataGridTextColumn>()
                .FirstOrDefault(c => string.Equals(c.Header?.ToString(),
                    "Source filter (regex)", StringComparison.Ordinal));
            if (filterColumn?.Binding is not System.Windows.Data.Binding filterBinding ||
                filterBinding.Path?.Path != "FilterPattern")
                throw new InvalidOperationException(
                    "Production WPF mapping grid is missing the source filter editor.");
            var excludeColumn = window.MappingGrid.Columns
                .OfType<System.Windows.Controls.DataGridCheckBoxColumn>()
                .FirstOrDefault(c => string.Equals(c.Header?.ToString(),
                    "Exclude matches", StringComparison.Ordinal));
            if (excludeColumn?.Binding is not System.Windows.Data.Binding excludeBinding ||
                excludeBinding.Path?.Path != "FilterExclude")
                throw new InvalidOperationException(
                    "Production WPF mapping grid is missing the source filter exclusion toggle.");


            // Exercise the ACTUAL production MappingRow and WPF binding paths:
            // edit both controls, persist/reload settings, verify preview drift.
            var row = new MainWindow.MappingRow
            {
                SourceField = "GENRE",
                TargetMyTag = "Genre",
                FilterPattern = "^House$",
                FilterExclude = false
            };
            var originalRule = row.ToRule();
            var regexEditor = new System.Windows.Controls.TextBox { DataContext = row };
            System.Windows.Data.BindingOperations.SetBinding(
                regexEditor, System.Windows.Controls.TextBox.TextProperty,
                new System.Windows.Data.Binding(filterBinding.Path.Path)
                {
                    Mode = System.Windows.Data.BindingMode.TwoWay,
                    UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.Explicit
                });
            regexEditor.Text = "^Techno$";
            regexEditor.GetBindingExpression(
                System.Windows.Controls.TextBox.TextProperty)!.UpdateSource();
            var excludeEditor = new System.Windows.Controls.CheckBox { DataContext = row };
            System.Windows.Data.BindingOperations.SetBinding(
                excludeEditor,
                System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
                new System.Windows.Data.Binding(excludeBinding.Path.Path)
                {
                    Mode = System.Windows.Data.BindingMode.TwoWay,
                    UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.Explicit
                });
            excludeEditor.IsChecked = true;
            excludeEditor.GetBindingExpression(
                System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty)!.UpdateSource();
            if (row.FilterPattern != "^Techno$" || !row.FilterExclude)
                throw new InvalidOperationException(
                    "WPF filter editors did not update the production mapping row.");

            var filterSettingsPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "MyTagSync-filter-smoke-" +
                Guid.NewGuid().ToString("N") + ".json");
            try
            {
                AppSettingsStore.SaveAtomic(filterSettingsPath,
                    new AppSettings(Mappings: new[] { row.ToRule() }));
                var restoredRule = AppSettingsStore.Load(filterSettingsPath)
                    .EffectiveMappings.Single();
                if (restoredRule.FilterPattern != "^Techno$" || !restoredRule.FilterExclude ||
                    !MappingEngine.Apply(restoredRule, new[] { "House", "Techno" })
                        .SequenceEqual(new[] { "House" }))
                    throw new InvalidOperationException(
                        "WPF filter edits failed persistence/reload or multivalue evaluation.");
                var originalPreview = PreviewEngine.Create(new PreviewRequest(
                    "wpf-filter-fixture", Array.Empty<BridgeTrack>(),
                    new[] { originalRule }, Array.Empty<RekordboxTrackSnapshot>(),
                    Array.Empty<ManagedAssignment>()));
                var changedPreview = PreviewEngine.Create(new PreviewRequest(
                    "wpf-filter-fixture", Array.Empty<BridgeTrack>(),
                    new[] { restoredRule }, Array.Empty<RekordboxTrackSnapshot>(),
                    Array.Empty<ManagedAssignment>()));
                if (originalPreview.FingerprintSha256 == changedPreview.FingerprintSha256)
                    throw new InvalidOperationException(
                        "WPF filter edit and reload failed to invalidate prior Preview.");
            }
            finally
            {
                if (System.IO.File.Exists(filterSettingsPath))
                    System.IO.File.Delete(filterSettingsPath);
            }

            // The production UI can retain only a typed, physically scoped
            // preview and MUST remain incapable of writing to real master.db
            // until the user separately approves a hardware write test.
            if (window.ApplyButton.IsEnabled || window.RestoreButton.IsEnabled)
                throw new InvalidOperationException(
                    "WPF without locally typed consent unexpectedly enabled live Apply/Restore.");
            var consentText = window.WriteConsentTextBox;
            if (consentText.Text.Length != 0 ||
                window.WriteConsentChallengeTextBlock.Text.Length == 0)
                throw new InvalidOperationException(
                    "WPF one-operation consent must start blank with visible explanation.");
            consentText.Text = "APPLY fake-db fake-proof";
            if (window.ApplyButton.IsEnabled || window.RestoreButton.IsEnabled)
                throw new InvalidOperationException(
                    "Unqualified WPF target accepted a forged local write confirmation.");

            // A changed target library must revoke all previously captured
            // physical approval and effective-mapping state. Simulate this
            // in WPF without opening any real rekordbox database.
            var mappingApprovalField = typeof(MainWindow).GetField(
                "_approvedPreviewMappings",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);
            var physicalApprovalField = typeof(MainWindow).GetField(
                "_approvedPhysicalAuthority",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);
            var invalidateTarget = typeof(MainWindow).GetMethod(
                "InvalidateDatabaseAccess",
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Instance);
            if (mappingApprovalField is null || physicalApprovalField is null ||
                invalidateTarget is null)
                throw new InvalidOperationException(
                    "Physical approval revocation hook is missing from WPF.");
            mappingApprovalField.SetValue(window,
                new[] { new MappingRule("GENRE", "Genre") });
            invalidateTarget.Invoke(window, new object[] { "WPF smoke target changed." });
            if (consentText.Text.Length != 0 ||
                mappingApprovalField.GetValue(window) is not null ||
                physicalApprovalField.GetValue(window) is not null ||
                window.ApplyButton.IsEnabled || window.RestoreButton.IsEnabled)
                throw new InvalidOperationException(
                    "WPF target change did not revoke previously approved mapping/physical scope.");

            // Real WPF physical target status: byte-identical copies are NOT aliases.
            var uiDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "MyTagSync-physical-target-ui-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(uiDir);
            try
            {
                var originalDb = System.IO.Path.Combine(uiDir, "master.db");
                var copiedDb = System.IO.Path.Combine(uiDir, "copy.db");
                System.IO.File.WriteAllText(originalDb, "synthetic-ui-db");
                System.IO.File.Copy(originalDb, copiedDb);
                var physical = PhysicalDatabasePath.ResolveExisting(originalDb);
                var fake = new RekordboxDatabaseSnapshot(
                    new RekordboxDatabaseIdentity("ui-fixture", "fixture-v1",
                        physical, new System.IO.FileInfo(originalDb).Length,
                        System.IO.File.GetLastWriteTimeUtc(originalDb), "fixture"),
                    Array.Empty<RekordboxTrackSnapshot>(),
                    Array.Empty<RekordboxMyTagDefinition>());
                var caption = global::RekordboxMyTagSync.MainWindow.DescribeQualifiedTarget(fake, originalDb);
                if (!caption.Contains(physical, StringComparison.OrdinalIgnoreCase) ||
                    !caption.Contains("Physical master.db:", StringComparison.Ordinal) ||
                    !caption.Contains("no live writes", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("WPF physical target status is incomplete.");
                var copyBlocked = false;
                try { _ = global::RekordboxMyTagSync.MainWindow.DescribeQualifiedTarget(fake, copiedDb); }
                catch (System.IO.InvalidDataException) { copyBlocked = true; }
                if (!copyBlocked)
                    throw new InvalidOperationException(
                        "WPF target status accepted an unrelated copied database.");
            }
            finally
            {
                System.IO.Directory.Delete(uiDir, recursive: true);
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

            // Validate the real Source/Target WPF alias columns against
            // the same whole-volume mapping semantics used by Preview.
            if (window.PathAliasGrid.Columns.Count != 2 ||
                window.PathAliasGrid.Columns[0].Header?.ToString() != "Bridge/source root" ||
                window.PathAliasGrid.Columns[1].Header?.ToString() != "rekordbox/target root" ||
                !WindowsPathMatcher.Equivalent(
                    @"Z:\Music\Singles\Track.mp3",
                    @"R:\Music\Singles\Track.mp3",
                    new[] { new PathAlias(@"Z:\", @"R:\") }))
                throw new InvalidOperationException(
                    "WPF alias controls or whole-volume path resolution are inconsistent.");

            var twoRoots = new[]
            {
                new PathAlias(@"Z:\Music", @"R:\Music"),
                new PathAlias(@"S:\Archive", @"T:\Archive")
            };
            if (!WindowsPathMatcher.Equivalent(
                    @"Z:\Music\Track.mp3", @"R:\Music\Track.mp3", twoRoots) ||
                !WindowsPathMatcher.Equivalent(
                    @"S:\Archive\Track.mp3", @"T:\Archive\Track.mp3", twoRoots) ||
                WindowsPathMatcher.Equivalent(
                    @"S:\Archive\Track.mp3", @"R:\Music\Track.mp3", twoRoots))
                throw new InvalidOperationException(
                    "WPF configured aliases must support independent roots without cross-matches.");

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
            if (window.FindName("ExportPhysicalExceptionsButton") is not
                    System.Windows.Controls.Button exportEight ||
                exportEight.IsEnabled ||
                string.IsNullOrWhiteSpace(exportEight.ToolTip?.ToString()) ||
                typeof(MainWindow).GetMethod("ExportPhysicalExceptionsAsync",
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance) is null)
                throw new InvalidOperationException(
                    "WPF CSV export must exist but stay disabled before inspection.");
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
