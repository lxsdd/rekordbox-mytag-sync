using System.IO;
using Microsoft.Win32;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using RekordboxMyTagSync.Core;

namespace RekordboxMyTagSync;

public partial class MainWindow
{
    private IReadOnlyList<PhysicalExceptionRow>? _reportRows;
    private BridgeSourceSnapshot? _reportSource;
    private RekordboxDatabaseSnapshot? _reportTarget;
    private RekordboxResolvedDatabaseAccess? _reportAccess;
    private string? _reportSettings;
    private string? _reportDatabaseHash;

    private void RevokeExceptionReport()
    {
        _reportRows = null;
        _reportSource = null;
        _reportTarget = null;
        _reportAccess = null;
        _reportSettings = null;
        _reportDatabaseHash = null;
        PhysicalExceptionsGrid.ItemsSource = null;
        ExportPhysicalExceptionsButton.IsEnabled = false;
    }

    private async Task ExportPhysicalExceptionsAsync()
    {
        if (_operationBusy) return;
        var rows = _reportRows;
        var source = _reportSource;
        var target = _reportTarget;
        var access = _reportAccess;
        var settings = _reportSettings;
        var recordedDatabaseHash = _reportDatabaseHash;
        if (rows is null || source is null || target is null || access is null ||
            settings is null || recordedDatabaseHash is null ||
            !_sourceSafe || !_targetSafe || !_databaseAccessQualified ||
            RekordboxProcessGuard.IsRunning())
        {
            PhysicalExceptionsStatusTextBlock.Text = "Export blocked: inspect a safe Source and Target again.";
            return;
        }
        var dialog = new SaveFileDialog
        {
            Title = "Export inspected physical exceptions",
            Filter = "CSV report (*.csv)|*.csv",
            DefaultExt = ".csv",
            AddExtension = true,
            FileName = "mytag-physical-exceptions.csv",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true) return;
        SetOperationStatus("Revalidating read-only exception evidence…", busy: true);
        UpdateWorkflowGate();
        try
        {
            await Task.Run(() =>
            {
                if (RekordboxProcessGuard.IsRunning())
                    throw new InvalidOperationException("rekordbox/rbxport is running.");
                var stable = BridgeSourceDiscovery.ReadStable(source.DirectoryPath);
                if (!Equals(stable.State, source.State))
                    throw new InvalidDataException("Bridge publication changed since inspection.");
                PhysicalExceptionReportCsv.AssertBridgePathsUnchanged(
                    source.Tracks, stable.Tracks);
                var file = new FileInfo(target.Identity.CanonicalPath);
                if (!file.Exists || file.Length != target.Identity.FileLength ||
                    file.LastWriteTimeUtc != target.Identity.LastWriteUtc)
                    throw new InvalidDataException("Database changed since inspection.");
                if (!string.Equals(recordedDatabaseHash,
                        RekordboxRollingBackup.ComputeFileSha256(file.FullName),
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        "Encrypted database bytes changed since exception inspection.");
            });
            if (!string.Equals(recordedDatabaseHash, _reportDatabaseHash,
                    StringComparison.OrdinalIgnoreCase) ||
                !ReferenceEquals(rows, _reportRows) ||
                !ReferenceEquals(source, _bridgeSnapshot) ||
                !ReferenceEquals(target, _databaseSnapshot) ||
                !ReferenceEquals(access, _databaseAccess) ||
                !_sourceSafe || !_targetSafe || !_databaseAccessQualified ||
                RekordboxProcessGuard.IsRunning() ||
                !string.Equals(settings, JsonSerializer.Serialize(BuildSettingsFromUi()),
                    StringComparison.Ordinal))
                throw new InvalidDataException("Inspection scope changed before CSV export.");
            PhysicalExceptionReportCsv.WriteAtomic(dialog.FileName, rows);
            PhysicalExceptionsStatusTextBlock.Text =
                $"Exported {rows.Count} exceptional ContentIDs to local CSV. No DB or media writes.";
            AppendDiagnostic($"Physical exception CSV exported: {rows.Count} ContentIDs.");
            SetOperationStatus("Read-only CSV report saved. No database changes.");
        }
        catch (Exception e)
        {
            RevokeExceptionReport();
            PhysicalExceptionsStatusTextBlock.Text = "Export blocked: " + e.Message;
            AppendDiagnostic(PhysicalExceptionsStatusTextBlock.Text);
            SetOperationStatus(PhysicalExceptionsStatusTextBlock.Text, error: true);
        }
        finally { UpdateWorkflowGate(); }
    }

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

        RevokeExceptionReport();
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
                var inspectedDbHash = RekordboxRollingBackup.ComputeFileSha256(
                    db.Identity.CanonicalPath);
                // Recheck stable bridge publication without inspecting 40k audio
                // files. A newly exported foobar snapshot must not be mistaken
                // for the previously selected source.
                var stable = BridgeSourceDiscovery.ReadStable(source.DirectoryPath);
                if (!Equals(stable.State, source.State))
                    throw new InvalidDataException(
                        "foobar bridge generation changed; click Source → Inspect again.");
                var analysis = PathMatchAdvisor.Analyze(
                    stable.Tracks, db.Tracks, []);
                if (analysis.Proposals.Count != 1)
                    throw new InvalidDataException(
                        "Exactly one unique root proposal is required for exception diagnosis.");
                var proposal = analysis.Proposals[0];
                var root = new PathAlias(proposal.SourceRoot, proposal.TargetRoot);
                var cases = PhysicalExceptionInvestigator.Identify(stable.Tracks, db.Tracks, root);
                var inspectedRows = PhysicalExceptionInvestigator.InspectReadOnly(
                    db.Identity.CanonicalPath, access.Key, cases);
                if (!string.Equals(inspectedDbHash,
                        RekordboxRollingBackup.ComputeFileSha256(
                            db.Identity.CanonicalPath),
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        "Database bytes changed during exception inspection.");
                return (Rows: inspectedRows, DatabaseHash: inspectedDbHash);
            });
            if (!ReferenceEquals(source, _bridgeSnapshot) ||
                !ReferenceEquals(db, _databaseSnapshot) ||
                !ReferenceEquals(access, _databaseAccess) ||
                !_sourceSafe || !_targetSafe || !_databaseAccessQualified ||
                !string.Equals(fingerprint, JsonSerializer.Serialize(BuildSettingsFromUi()),
                    StringComparison.Ordinal))
                throw new InvalidDataException(
                    "Source, database or settings changed during exception investigation.");

            PhysicalExceptionsGrid.ItemsSource = result.Rows;
            _reportRows = result.Rows;
            _reportDatabaseHash = result.DatabaseHash;
            _reportSource = source;
            _reportTarget = db;
            _reportAccess = access;
            _reportSettings = fingerprint;
            var samePath = result.Rows.Count(x => x.Reason == "SAME_REKORDBOX_PATH");
            var absent = result.Rows.Count(x => x.Reason == "MISSING_FROM_BRIDGE_EXPORT");
            PhysicalExceptionsStatusTextBlock.Text =
                $"{result.Rows.Count} physical exception ContentIDs: {samePath} with duplicate rekordbox paths, " +
                $"{absent} absent from foobar BRIDGE PATHS. Playlist/Cue/Beatgrid/MyTag row counts are shown " +
                "where directly queryable; unavailable data is NOT counted as zero. " +
                "No music files, tag assignments, aliases or DB rows changed.";
            AppendDiagnostic(PhysicalExceptionsStatusTextBlock.Text);
            SetOperationStatus("Targeted read-only exception investigation complete; no files changed.");
            ExportPhysicalExceptionsButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            RevokeExceptionReport();
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
