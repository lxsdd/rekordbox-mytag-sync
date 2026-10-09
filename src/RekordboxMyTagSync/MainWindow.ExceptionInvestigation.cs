using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using RekordboxMyTagSync.Core;

namespace RekordboxMyTagSync;

public partial class MainWindow
{
    private async Task InspectPhysicalExceptionsAsync()
    {
        if (_operationBusy) return;
        if (!_sourceSafe || _bridgeSnapshot is null ||
            !_targetSafe || !_databaseAccessQualified ||
            _databaseSnapshot is null || _databaseAccess is null)
        {
            PhysicalExceptionsStatusTextBlock.Text =
                "Blocked: Inspect Source and Validate Target before examining physical exceptions.";
            AppendDiagnostic(PhysicalExceptionsStatusTextBlock.Text);
            return;
        }

        var db = _databaseSnapshot;
        var access = _databaseAccess;
        var source = _bridgeSnapshot;
        var fingerprint = JsonSerializer.Serialize(BuildSettingsFromUi());
        PhysicalExceptionsGrid.ItemsSource = null;
        PhysicalExceptionsStatusTextBlock.Text =
            "Inspecting duplicate paths and missing bridge matches (read-only, without full file-ID scan)…";
        SetOperationStatus("Inspecting only physical rekordbox exceptions — read-only…", busy: true);
        UpdateWorkflowGate();
        await Dispatcher.Yield(DispatcherPriority.Background);
        try
        {
            var result = await Task.Run(() =>
            {
                if (RekordboxProcessGuard.IsRunning())
                    throw new InvalidOperationException("Close rekordbox before examining its database.");
                var analysis = PathMatchAdvisor.Analyze(
                    source.Tracks, db.Tracks, []);
                if (analysis.Proposals.Count != 1)
                    throw new InvalidDataException(
                        "Exactly one unique root proposal is required for exception diagnosis.");
                var proposal = analysis.Proposals[0];
                var root = new PathAlias(proposal.SourceRoot, proposal.TargetRoot);
                var cases = PhysicalExceptionInvestigator.Identify(source.Tracks, db.Tracks, root);
                return PhysicalExceptionInvestigator.InspectReadOnly(
                    db.Identity.CanonicalPath, access.Key, cases);
            });
            if (!ReferenceEquals(source, _bridgeSnapshot) ||
                !ReferenceEquals(db, _databaseSnapshot) ||
                !ReferenceEquals(access, _databaseAccess) ||
                !_sourceSafe || !_targetSafe || !_databaseAccessQualified ||
                !string.Equals(fingerprint, JsonSerializer.Serialize(BuildSettingsFromUi()),
                    StringComparison.Ordinal))
                throw new InvalidDataException(
                    "Source, database or settings changed during exception investigation.");

            PhysicalExceptionsGrid.ItemsSource = result;
            var samePath = result.Count(x => x.Reason == "SAME_REKORDBOX_PATH");
            var absent = result.Count(x => x.Reason == "MISSING_FROM_BRIDGE_EXPORT");
            PhysicalExceptionsStatusTextBlock.Text =
                $"{result.Count} physical exception ContentIDs: {samePath} with duplicate rekordbox paths, " +
                $"{absent} absent from foobar BRIDGE PATHS. Playlist/Cue/MyTag row counts are shown " +
                "where directly queryable; unavailable data is NOT counted as zero. " +
                "No music files, tag assignments, aliases or DB rows changed.";
            AppendDiagnostic(PhysicalExceptionsStatusTextBlock.Text);
            SetOperationStatus("Targeted read-only exception investigation complete; no files changed.");
        }
        catch (Exception ex)
        {
            PhysicalExceptionsGrid.ItemsSource = null;
            PhysicalExceptionsStatusTextBlock.Text = "Investigation blocked: " + ex.Message;
            AppendDiagnostic(PhysicalExceptionsStatusTextBlock.Text);
            SetOperationStatus(PhysicalExceptionsStatusTextBlock.Text, error: true);
        }
        finally
        {
            OperationProgressBar.Visibility = Visibility.Collapsed;
            UpdateWorkflowGate();
        }
    }
}
