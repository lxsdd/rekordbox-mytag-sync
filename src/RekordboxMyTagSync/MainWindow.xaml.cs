using System.Collections.ObjectModel;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using RekordboxMyTagSync.Core;

namespace RekordboxMyTagSync;

public partial class MainWindow : Window
{
    private readonly string _settingsPath = AppSettingsStore.DefaultPath;
    private readonly ObservableCollection<MappingRow> _mappings = new();
    private readonly ObservableCollection<AliasRow> _aliases = new();
    private AppSettings _settings = new();
    private readonly List<string> _diagnosticEvents = new();
    private bool _operationBusy;
    private bool _sourceSafe;
    private bool _targetSafe;
    private bool _databaseAccessQualified;
    private bool _previewExists;
    private bool _previewValid;
    private bool _previewFresh;
    private bool _backupAvailable;
    private bool _backupMatchesTarget;
    private RekordboxDatabaseSnapshot? _databaseSnapshot;
    private RekordboxResolvedDatabaseAccess? _databaseAccess;
    private BridgeSourceSnapshot? _bridgeSnapshot;
    private PreviewResult? _approvedPreview;

    public MainWindow()
    {
        InitializeComponent();

        MappingGrid.ItemsSource = _mappings;
        PathAliasGrid.ItemsSource = _aliases;
        Loaded += (_, _) => LoadSettings();
        SourceInitialized += (_, _) => WindowPlacementStore.Restore(this);
        Closing += (_, _) =>
        {
            if (!Equals(Tag, "ui-smoke"))
                WindowPlacementStore.Save(this);
        };

        SaveSettingsButton.Click += (_, _) => SaveSettings();
        DiscoverSourceButton.Click += (_, _) => DiscoverSources();
        InspectSourceButton.Click += async (_, _) => await InspectSelectedSourceAsync();
        DiscoverTargetButton.Click += (_, _) => DiscoverTargets();
        BrowseTargetButton.Click += (_, _) => BrowseTarget();
        ValidateDatabaseAccessButton.Click += async (_, _) => await ValidateDatabaseAccessAsync();
        AddMappingButton.Click += (_, _) => AddMapping();
        RemoveMappingButton.Click += (_, _) => RemoveSelectedMapping();
        AddAliasButton.Click += (_, _) => AddAlias();
        RemoveAliasButton.Click += (_, _) => RemoveSelectedAlias();
        RefreshDiagnosticsButton.Click += (_, _) => RefreshDiagnostics();
        BuildPreviewButton.Click += (_, _) => BuildPreview();
        ApplyButton.Click += (_, _) => ApplyApprovedPreview();
        RestoreButton.Click += (_, _) => RestoreRollingBackup();

        SourceCandidatesGrid.SelectionChanged += (_, _) =>
        {
            if (SourceCandidatesGrid.SelectedItem is BridgeSourceCandidate source)
            {
                BridgeDirectoryTextBox.Text = source.DirectoryPath;
                _sourceSafe = source.Safe;
                SourceInspectionStatusTextBlock.Text =
                    "Selected source changed. Click Inspect to verify its stable snapshot.";
                UpdateWorkflowGate();
            }
        };
        TargetCandidatesGrid.SelectionChanged += (_, _) =>
        {
            if (TargetCandidatesGrid.SelectedItem is RekordboxLibraryCandidate target)
            {
                TargetDatabaseTextBox.Text = target.DatabasePath;
                _targetSafe = target.Safe;
                InvalidateDatabaseAccess("Target library changed.");
                UpdateWorkflowGate();
            }
        };
        BridgeDirectoryTextBox.TextChanged += (_, _) =>
        {
            _sourceSafe = false;
            _bridgeSnapshot = null;
            SourceInspectionStatusTextBlock.Text =
                "Source path changed. Click Inspect to verify its stable snapshot.";
            InvalidatePreview("Bridge source changed and must be requalified.");
            UpdateWorkflowGate();
        };
        TargetDatabaseTextBox.TextChanged += (_, _) =>
        {
            _targetSafe = false;
            InvalidateDatabaseAccess("Target library path changed and must be requalified.");
            UpdateWorkflowGate();
        };
        MappingGrid.CellEditEnding += (_, _) => InvalidatePreview("Mapping changed.");
        PathAliasGrid.CellEditEnding += (_, _) => InvalidatePreview("Path alias changed.");

        UpdateWorkflowGate();
    }

    private void LoadSettings()
    {
        SettingsPathTextBlock.Text = $"Settings: {_settingsPath}";
        try
        {
            _settings = AppSettingsStore.Load(_settingsPath);
            BridgeDirectoryTextBox.Text = _settings.EffectiveBridgeDirectory ?? string.Empty;
            TargetDatabaseTextBox.Text = _settings.RekordboxDatabasePath ?? string.Empty;

            _mappings.Clear();
            foreach (var rule in _settings.EffectiveMappings)
                _mappings.Add(MappingRow.FromRule(rule));

            _aliases.Clear();
            foreach (var alias in _settings.EffectivePathAliases)
                _aliases.Add(AliasRow.FromAlias(alias));

            AppendDiagnostic("Settings loaded.");
            DiscoverSources();
            DiscoverTargets();
            UpdateWorkflowGate();
        }
        catch (Exception ex)
        {
            _settings = new AppSettings();
            AppendDiagnostic($"Settings load blocked: {ex.Message}");
        }
    }

    private void SaveSettings()
    {
        try
        {
            var settings = BuildSettingsFromUi();
            AppSettingsStore.SaveAtomic(_settingsPath, settings);
            _settings = AppSettingsStore.Load(_settingsPath);
            BridgeDirectoryTextBox.Text = _settings.EffectiveBridgeDirectory ?? string.Empty;
            TargetDatabaseTextBox.Text = _settings.RekordboxDatabasePath ?? string.Empty;
            AppendDiagnostic("Settings validated and saved atomically.");
            UpdateWorkflowGate();
        }
        catch (Exception ex)
        {
            AppendDiagnostic($"Settings save blocked: {ex.Message}");
        }
    }

    private AppSettings BuildSettingsFromUi()
    {
        var bridgeDirectory = OptionalPath(BridgeDirectoryTextBox.Text);
        var databasePath = OptionalPath(TargetDatabaseTextBox.Text);
        var snapshotPath = bridgeDirectory is null
            ? null
            : Path.Combine(bridgeDirectory, BridgeSourceDiscovery.SnapshotFileName);

        var known = _settings.EffectiveKnownBridgeDirectories
            .Concat(bridgeDirectory is null ? Array.Empty<string>() : new[] { bridgeDirectory })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var mappings = _mappings
            .Select(x => x.ToRule())
            .ToArray();
        var aliases = _aliases
            .Select(x => x.ToAlias())
            .ToArray();

        return AppSettingsStore.ValidateAndNormalize(new AppSettings(
            snapshotPath,
            databasePath,
            aliases,
            mappings,
            bridgeDirectory,
            known));
    }

    private void DiscoverSources()
    {
        try
        {
            var bridgeDirectory = OptionalPath(BridgeDirectoryTextBox.Text);
            var known = _settings.EffectiveKnownBridgeDirectories
                .Concat(bridgeDirectory is null ? Array.Empty<string>() : new[] { bridgeDirectory })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            var discoverySettings = _settings with
            {
                BridgeDirectory = bridgeDirectory,
                KnownBridgeDirectories = known
            };
            var candidates = BridgeSourceDiscovery.Discover(discoverySettings);
            SourceCandidatesGrid.ItemsSource = candidates;

            if (bridgeDirectory is null)
            {
                var preferred = candidates.FirstOrDefault(x => x.Selected && x.Safe)
                                ?? candidates.FirstOrDefault(x => x.Safe);
                if (preferred is not null)
                {
                    BridgeDirectoryTextBox.Text = preferred.DirectoryPath;
                    bridgeDirectory = preferred.DirectoryPath;
                }
            }

            _sourceSafe = bridgeDirectory is not null &&
                candidates.Any(x =>
                    x.Safe &&
                    PathsEqual(x.DirectoryPath, bridgeDirectory));

            var safeCount = candidates.Count(x => x.Safe);
            SourceInspectionStatusTextBlock.Text =
                $"Discovered {safeCount} safe source(s). Click Inspect to validate the selected stable snapshot.";
            AppendDiagnostic($"Bridge discovery: {candidates.Count} candidate(s), {safeCount} safe.");
            UpdateWorkflowGate();
        }
        catch (Exception ex)
        {
            _sourceSafe = false;
            SourceCandidatesGrid.ItemsSource = null;
            SourceInspectionStatusTextBlock.Text = $"Source discovery failed: {ex.Message}";
            AppendDiagnostic($"Bridge discovery blocked: {ex.Message}");
            UpdateWorkflowGate();
        }
    }

    private async Task InspectSelectedSourceAsync()
    {
        if (_operationBusy) return;
        _sourceSafe = false;
        InvalidatePreview("Bridge source is being re-inspected.");
        InspectSourceButton.IsEnabled = false;
        SourceInspectionStatusTextBlock.Text = "Inspect is running — reading and validating the full bridge snapshot…";
        SetOperationStatus("Inspecting foobar2000 source — please wait…", busy: true);
        AppendDiagnostic("Source inspection started.");
        UpdateWorkflowGate();
        try
        {
            var directory = OptionalPath(BridgeDirectoryTextBox.Text)
                ?? throw new InvalidDataException("No bridge source directory is selected.");

            // The bridge may contain tens of thousands of rows. Never block
            // the WPF dispatcher while validating its compressed snapshot.
            var inspection = await Task.Run(() =>
            {
                var candidate = BridgeSourceDiscovery.Inspect(directory, selected: true);
                var snapshot = candidate.Safe
                    ? BridgeSourceDiscovery.ReadStable(directory)
                    : null;
                return (candidate, snapshot);
            });

            if (!PathsEqual(directory, OptionalPath(BridgeDirectoryTextBox.Text) ?? string.Empty))
                throw new InvalidOperationException("Selected source changed during inspection.");
            SourceCandidatesGrid.ItemsSource = new[] { inspection.candidate };
            if (!inspection.candidate.Safe || inspection.snapshot is null)
                throw new InvalidDataException(
                    inspection.candidate.Error ?? "Bridge source is not safe.");

            _bridgeSnapshot = inspection.snapshot;
            _sourceSafe = true;
            SourceInspectionStatusTextBlock.Text =
                $"Source verified: {inspection.snapshot.Tracks.Count:N0} tracks, " +
                $"schema {inspection.snapshot.State.SchemaVersion}, " +
                $"generation {inspection.snapshot.State.Generation}. No database changes made.";
            SetOperationStatus(
                $"Source verified — {inspection.snapshot.Tracks.Count:N0} tracks; no changes made.");
            AppendDiagnostic(SourceInspectionStatusTextBlock.Text);
        }
        catch (Exception ex)
        {
            _sourceSafe = false;
            _bridgeSnapshot = null;
            SourceInspectionStatusTextBlock.Text = $"Source verification failed: {ex.Message}";
            SetOperationStatus($"Inspect failed — {ex.Message}", error: true);
            AppendDiagnostic($"Bridge source inspection blocked: {ex.Message}");
        }
        finally
        {
            _operationBusy = false;
            OperationProgressBar.Visibility = Visibility.Collapsed;
            InspectSourceButton.IsEnabled = true;
            UpdateWorkflowGate();
        }
    }

    private void DiscoverTargets()
    {
        try
        {
            var result = RekordboxDiscovery.Discover();
            TargetCandidatesGrid.ItemsSource = result.Libraries;

            var safe = result.Libraries.Where(x => x.Safe).ToArray();
            if (string.IsNullOrWhiteSpace(TargetDatabaseTextBox.Text) && safe.Length == 1)
                TargetDatabaseTextBox.Text = safe[0].DatabasePath;

            var selectedPath = OptionalPath(TargetDatabaseTextBox.Text);
            _targetSafe = selectedPath is not null &&
                safe.Any(x => PathsEqual(x.DatabasePath, selectedPath));

            AppendDiagnostic(
                $"rekordbox discovery: {result.Installations.Count} installation(s), {result.Libraries.Count} library candidate(s), {safe.Length} safe.");
            foreach (var diagnostic in result.Diagnostics)
                AppendDiagnostic($"rekordbox: {diagnostic}");

            if (safe.Length > 1 && string.IsNullOrWhiteSpace(TargetDatabaseTextBox.Text))
                AppendDiagnostic("Target selection remains fail-closed because multiple safe libraries were discovered.");
            UpdateWorkflowGate();
        }
        catch (Exception ex)
        {
            _targetSafe = false;
            TargetCandidatesGrid.ItemsSource = null;
            AppendDiagnostic($"rekordbox discovery blocked: {ex.Message}");
            UpdateWorkflowGate();
        }
    }

    private void BrowseTarget()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select rekordbox master.db",
            Filter = "rekordbox master.db|master.db|SQLite database (*.db)|*.db|All files (*.*)|*.*",
            FileName = "master.db",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true)
            return;

        try
        {
            var candidate = RekordboxDiscovery.InspectManual(dialog.FileName);
            TargetDatabaseTextBox.Text = candidate.DatabasePath;
            TargetCandidatesGrid.ItemsSource = new[] { candidate };
            _targetSafe = candidate.Safe;
            InvalidateDatabaseAccess("Manual target selection requires fresh database access qualification.");

            if (!candidate.Safe)
                AppendDiagnostic(
                    $"Manual target rejected: {candidate.Error ?? "unknown qualification error"}");
            else
                AppendDiagnostic(
                    $"Manual target selected: {candidate.DatabasePath}. Runtime access qualification is still required.");
        }
        catch (Exception ex)
        {
            _targetSafe = false;
            InvalidateDatabaseAccess("Manual target selection failed.");
            AppendDiagnostic($"Manual target selection blocked: {ex.Message}");
        }

        UpdateWorkflowGate();
    }

    private async Task ValidateDatabaseAccessAsync()
    {
        if (_operationBusy) return;
        ValidateDatabaseAccessButton.IsEnabled = false;
        DatabaseAccessStatusTextBlock.Text =
            "Validation running — checking rekordbox installation, SQLCipher and database schema…";
        SetOperationStatus("Validating rekordbox database access — please wait…", busy: true);
        AppendDiagnostic("Database access validation started.");
        await Dispatcher.Yield(DispatcherPriority.Background);
        try
        {
            var databasePath = OptionalPath(TargetDatabaseTextBox.Text)
                ?? throw new InvalidDataException("No target master.db is selected.");

            var discovery = RekordboxDiscovery.Discover();
            var matches = discovery.Libraries
                .Where(x =>
                    x.Safe &&
                    PathsEqual(x.DatabasePath, databasePath))
                .ToArray();
            if (matches.Length > 1)
                throw new InvalidDataException(
                    "Selected target is ambiguous across discovered rekordbox libraries.");

            var target = matches.Length == 1
                ? matches[0]
                : RekordboxDiscovery.InspectManual(databasePath);
            if (!target.Safe)
                throw new InvalidDataException(
                    target.Error ?? "Selected target is not qualified as a safe rekordbox 6/7 library.");

            var access = await RekordboxDatabaseAccessResolver.ResolveAsync(target);
            if (!PathsEqual(databasePath, OptionalPath(TargetDatabaseTextBox.Text) ?? string.Empty))
                throw new InvalidOperationException("Target path changed during database validation.");
            _databaseAccess = access;
            _databaseSnapshot = access.Snapshot;
            _targetSafe = true;
            _databaseAccessQualified = true;
            QualifyRestorePoint(access.Snapshot.Identity);
            InvalidatePreview("Database access was requalified.");

            DatabaseAccessStatusTextBlock.Text =
                $"Qualified automatically: DBVersion {access.Snapshot.Identity.DbVersion}, " +
                $"DBID {access.Snapshot.Identity.DbId}, {access.Snapshot.Tracks.Count} tracks, " +
                $"{access.Snapshot.MyTagDefinitions.Count} MyTag definitions.";
            AppendDiagnostic(
                $"Database access automatically qualified from {access.KeySource}; " +
                $"SQLite3MC {access.Snapshot.Identity.SqliteCipherVersion}. " +
                "Key material was neither logged nor stored in settings.");
            SetOperationStatus("Database access qualified — read-only inspection succeeded.");
        }
        catch (Exception ex)
        {
            InvalidateDatabaseAccess("Automatic database access validation failed.");
            DatabaseAccessStatusTextBlock.Text = $"Blocked: {ex.Message}";
            SetOperationStatus($"Database access blocked — {ex.Message}", error: true);
            AppendDiagnostic($"Automatic database access validation blocked: {ex.Message}");
        }
        finally
        {
            _operationBusy = false;
            OperationProgressBar.Visibility = Visibility.Collapsed;
            ValidateDatabaseAccessButton.IsEnabled = true;
            UpdateWorkflowGate();
        }
    }

    private void InvalidateDatabaseAccess(string reason)
    {
        if (_databaseAccessQualified || _databaseSnapshot is not null)
            AppendDiagnostic(reason);

        _databaseAccessQualified = false;
        _databaseSnapshot = null;
        _databaseAccess = null;
        _previewExists = false;
        _previewValid = false;
        _previewFresh = false;
        _backupAvailable = false;
        _backupMatchesTarget = false;
        _approvedPreview = null;
        BackupPackageTextBox.Text = string.Empty;
        DatabaseAccessStatusTextBlock.Text =
            "Database access is not qualified for the current target/key/version state.";
    }

    private void AddMapping()
    {
        _mappings.Add(new MappingRow
        {
            SourceField = "GENRE",
            TargetMyTag = "Genre",
            Transform = TransformKind.Direct.ToString(),
            PerValue = true,
            IgnoreEmpty = true
        });
        MappingGrid.SelectedItem = _mappings[^1];
        MappingGrid.ScrollIntoView(_mappings[^1]);
        InvalidatePreview("Mapping added.");
        UpdateWorkflowGate();
    }

    private void RemoveSelectedMapping()
    {
        if (MappingGrid.SelectedItem is MappingRow row)
        {
            _mappings.Remove(row);
            InvalidatePreview("Mapping removed.");
            UpdateWorkflowGate();
        }
    }

    private void AddAlias()
    {
        _aliases.Add(new AliasRow());
        PathAliasGrid.SelectedItem = _aliases[^1];
        PathAliasGrid.ScrollIntoView(_aliases[^1]);
        InvalidatePreview("Path alias added.");
        UpdateWorkflowGate();
    }

    private void RemoveSelectedAlias()
    {
        if (PathAliasGrid.SelectedItem is AliasRow row)
        {
            _aliases.Remove(row);
            InvalidatePreview("Path alias removed.");
            UpdateWorkflowGate();
        }
    }

    private void BuildPreview()
    {
        try
        {
            if (!_sourceSafe)
                throw new InvalidDataException("Selected bridge source is not qualified as safe.");
            if (!_targetSafe || !_databaseAccessQualified || _databaseSnapshot is null)
                throw new InvalidDataException("Target database access is not qualified.");

            var settings = BuildSettingsFromUi();
            var bridgeDirectory = settings.EffectiveBridgeDirectory
                ?? throw new InvalidDataException("No bridge source directory is selected.");
            var bridge = BridgeSourceDiscovery.ReadStable(bridgeDirectory);
            var provenanceRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RekordboxMyTagSync",
                "provenance");
            var provenancePath = ProvenanceStore.GetStatePath(
                provenanceRoot,
                _databaseSnapshot.Identity);
            var provenance = ProvenanceStore.Load(
                provenancePath,
                _databaseSnapshot.Identity);
            var managed = ProvenanceStore.ToManagedAssignments(
                provenance,
                _databaseSnapshot);

            var preview = PreviewEngine.Create(new PreviewRequest(
                _databaseSnapshot.Identity.PreviewIdentity,
                bridge.Tracks,
                settings.EffectiveMappings,
                _databaseSnapshot.Tracks,
                managed,
                settings.EffectivePathAliases,
                _databaseSnapshot.MyTagDefinitions));

            _bridgeSnapshot = bridge;
            _approvedPreview = preview;
            _previewExists = true;
            _previewValid = preview.IsValid && preview.Counts.Conflicts == 0;
            _previewFresh = true;

            PreviewGrid.ItemsSource = preview.Details;
            PreviewCountsTextBlock.Text =
                $"Add {preview.Counts.Additions} · Remove {preview.Counts.Removals} · " +
                $"Correct {preview.Counts.AlreadyCorrect} · Conflicts {preview.Counts.Conflicts} · " +
                $"Unmatched {preview.Counts.Unmatched}";
            AppendDiagnostic(
                $"Fresh preview built: valid={preview.IsValid}, fingerprint={preview.FingerprintSha256}, " +
                $"missing definitions={preview.MissingDefinitions?.Count ?? 0}.");
        }
        catch (Exception ex)
        {
            _approvedPreview = null;
            _previewExists = false;
            _previewValid = false;
            _previewFresh = false;
            PreviewGrid.ItemsSource = null;
            PreviewCountsTextBlock.Text = "Add 0 · Remove 0 · Correct 0 · Conflicts 0 · Unmatched 0";
            AppendDiagnostic($"Preview blocked: {ex.Message}");
        }

        UpdateWorkflowGate();
    }

    private void ApplyApprovedPreview()
    {
        try
        {
            if (_approvedPreview is null ||
                !_previewExists ||
                !_previewValid ||
                !_previewFresh)
                throw new InvalidDataException("A fresh conflict-free preview is required before Apply.");
            if (_databaseSnapshot is null ||
                _databaseAccess is null ||
                !_databaseAccessQualified)
                throw new InvalidDataException("Target database access is not qualified.");

            var settings = BuildSettingsFromUi();
            var databasePath = settings.RekordboxDatabasePath
                ?? throw new InvalidDataException("No target master.db is selected.");
            var bridgeDirectory = settings.EffectiveBridgeDirectory
                ?? throw new InvalidDataException("No bridge source directory is selected.");
            var key = _databaseAccess.Key;
            var policy = _databaseAccess.Policy;
            var bridge = BridgeSourceDiscovery.ReadStable(bridgeDirectory);
            var stateRoot = AppStateRoot();
            var provenancePath = ProvenanceStore.GetStatePath(
                Path.Combine(stateRoot, "provenance"),
                _databaseSnapshot.Identity);
            var backupRoot = Path.Combine(stateRoot, "backups");
            var preApplyIdentity = _databaseSnapshot.Identity;
            var preApplyProvenance = ProvenanceStore.Load(
                provenancePath,
                preApplyIdentity);
            var mappingHash = ComputeMappingHashSha256(settings.EffectiveMappings);
            var toolVersion = ProductVersion();

            var result = RekordboxMutationExecutor.Apply(
                databasePath,
                key,
                policy,
                _approvedPreview,
                bridge.Tracks,
                settings.EffectiveMappings,
                provenancePath,
                backupRoot,
                mappingHash,
                toolVersion,
                settings.EffectivePathAliases);

            _databaseSnapshot = RekordboxSqlCipherDatabase.ReadSnapshot(
                databasePath,
                key,
                policy);
            _databaseAccess = _databaseAccess with
            {
                Snapshot = _databaseSnapshot
            };
            _bridgeSnapshot = bridge;

            if (!string.IsNullOrWhiteSpace(result.BackupPackagePath))
            {
                var inspection = RekordboxRollingBackup.Inspect(
                    result.BackupPackagePath,
                    _databaseSnapshot.Identity);
                try
                {
                    ProvenanceStore.SaveAtomic(
                        RestoreProvenancePath(inspection),
                        preApplyProvenance,
                        preApplyIdentity);
                    _backupAvailable = true;
                    _backupMatchesTarget = true;
                    BackupPackageTextBox.Text = inspection.PackagePath;
                }
                catch (Exception restoreStateError)
                {
                    _backupAvailable = false;
                    _backupMatchesTarget = false;
                    BackupPackageTextBox.Text = inspection.PackagePath;
                    AppendDiagnostic(
                        $"Apply succeeded, but coordinated Restore was disabled because the pre-Apply provenance snapshot could not be persisted: {restoreStateError.Message}");
                }
            }
            else
            {
                _backupAvailable = false;
                _backupMatchesTarget = false;
                BackupPackageTextBox.Text = string.Empty;
            }

            ApplyStatusTextBox.Text =
                result.ChangeCount == 0
                    ? "No database changes were required."
                    : $"Applied {result.ChangeCount} database change(s). " +
                      $"localUpdateCount={result.FinalLocalUpdateCount}.";
            AppendDiagnostic(
                $"Apply completed: changes={result.ChangeCount}, " +
                $"first rb_local_usn={result.FirstLocalUsn?.ToString() ?? "n/a"}, " +
                $"backup={(result.BackupPackagePath is null ? "none" : "validated")}.");

            InvalidatePreview("Applied preview consumed; a new preview is required before another Apply.");
        }
        catch (Exception ex)
        {
            InvalidatePreview("Apply failed; preview approval was invalidated.");
            ApplyStatusTextBox.Text = $"Apply blocked/failed: {ex.Message}";
            AppendDiagnostic($"Apply blocked/failed: {ex.Message}");
        }

        UpdateWorkflowGate();
    }

    private void RestoreRollingBackup()
    {
        try
        {
            if (_databaseSnapshot is null ||
                _databaseAccess is null ||
                !_databaseAccessQualified)
                throw new InvalidDataException("Target database access is not qualified.");
            if (!_backupAvailable || !_backupMatchesTarget)
                throw new InvalidDataException("No coordinated rolling restore point matches the selected target.");

            var settings = BuildSettingsFromUi();
            var databasePath = settings.RekordboxDatabasePath
                ?? throw new InvalidDataException("No target master.db is selected.");
            var key = _databaseAccess.Key;

            var packagePath = BackupPackageTextBox.Text.Trim();
            if (packagePath.Length == 0)
                throw new InvalidDataException("Rolling backup package path is empty.");

            var currentIdentity = _databaseSnapshot.Identity;
            var inspection = RekordboxRollingBackup.Inspect(
                packagePath,
                currentIdentity);
            var restoreProvenancePath = RestoreProvenancePath(inspection);
            if (!File.Exists(restoreProvenancePath))
                throw new InvalidDataException(
                    "Matching pre-Apply provenance snapshot is missing; Restore remains fail-closed.");

            var preApplyProvenance = ProvenanceStore.Load(
                restoreProvenancePath,
                currentIdentity);

            RekordboxRollingBackup.Restore(
                inspection.PackagePath,
                currentIdentity);

            var policy = _databaseAccess.Policy;
            var restoredSnapshot = RekordboxSqlCipherDatabase.ReadSnapshot(
                databasePath,
                key,
                policy);
            var provenancePath = ProvenanceStore.GetStatePath(
                Path.Combine(AppStateRoot(), "provenance"),
                restoredSnapshot.Identity);
            ProvenanceStore.SaveAtomic(
                provenancePath,
                preApplyProvenance,
                restoredSnapshot.Identity);

            var restoredProvenance = ProvenanceStore.Load(
                provenancePath,
                restoredSnapshot.Identity);
            _ = ProvenanceStore.ToManagedAssignments(
                restoredProvenance,
                restoredSnapshot);

            _databaseSnapshot = restoredSnapshot;
            _databaseAccess = _databaseAccess with
            {
                Snapshot = restoredSnapshot
            };
            _databaseAccessQualified = true;
            InvalidatePreview("Rolling backup restored; a new preview is required.");
            QualifyRestorePoint(restoredSnapshot.Identity);

            ApplyStatusTextBox.Text =
                "Rolling backup and matching pre-Apply provenance were restored and revalidated.";
            AppendDiagnostic(
                $"Restore completed and revalidated for DBID '{restoredSnapshot.Identity.DbId}'.");
        }
        catch (Exception ex)
        {
            InvalidateDatabaseAccess("Restore failed; database access must be requalified.");
            ApplyStatusTextBox.Text = $"Restore blocked/failed: {ex.Message}";
            AppendDiagnostic($"Restore blocked/failed: {ex.Message}");
        }

        UpdateWorkflowGate();
    }

    private void QualifyRestorePoint(RekordboxDatabaseIdentity identity)
    {
        _backupAvailable = false;
        _backupMatchesTarget = false;
        BackupPackageTextBox.Text = string.Empty;

        try
        {
            var packagePath = RekordboxRollingBackup.GetPackagePath(
                Path.Combine(AppStateRoot(), "backups"),
                identity);
            if (!File.Exists(packagePath))
                return;

            var inspection = RekordboxRollingBackup.Inspect(
                packagePath,
                identity);
            var provenancePath = RestoreProvenancePath(inspection);
            if (!File.Exists(provenancePath))
            {
                BackupPackageTextBox.Text = inspection.PackagePath;
                return;
            }

            _ = ProvenanceStore.Load(provenancePath, identity);
            _backupAvailable = true;
            _backupMatchesTarget = true;
            BackupPackageTextBox.Text = inspection.PackagePath;
        }
        catch (Exception ex)
        {
            _backupAvailable = false;
            _backupMatchesTarget = false;
            BackupPackageTextBox.Text = string.Empty;
            AppendDiagnostic($"Existing rolling restore point rejected: {ex.Message}");
        }
    }

    private static string RestoreProvenancePath(
        RekordboxBackupInspection inspection)
    {
        ArgumentNullException.ThrowIfNull(inspection);
        var hash = inspection.Metadata.DatabaseSha256;
        if (string.IsNullOrWhiteSpace(hash))
            throw new InvalidDataException("Backup database SHA-256 is missing.");
        return inspection.PackagePath + "." + hash.ToLowerInvariant() + ".provenance.json";
    }

    private static string ProductVersion()
    {
        var assembly = typeof(MainWindow).Assembly;
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
            return informational.Trim();

        return assembly.GetName().Version?.ToString()
               ?? throw new InvalidOperationException("Product version metadata is unavailable.");
    }

    private static string AppStateRoot()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException("Local application data directory is unavailable.");
        return Path.Combine(root, "RekordboxMyTagSync");
    }

    private static string ComputeMappingHashSha256(IReadOnlyList<MappingRule> mappings)
    {
        ArgumentNullException.ThrowIfNull(mappings);
        var payload = JsonSerializer.Serialize(mappings);
        return Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(payload)))
            .ToLowerInvariant();
    }

    private void InvalidatePreview(string reason)
    {
        if (_previewExists || _approvedPreview is not null)
            AppendDiagnostic(reason);

        _approvedPreview = null;
        _previewExists = false;
        _previewValid = false;
        _previewFresh = false;
        PreviewGrid.ItemsSource = null;
        PreviewCountsTextBlock.Text = "Add 0 · Remove 0 · Correct 0 · Conflicts 0 · Unmatched 0";
    }

    private void UpdateWorkflowGate()
    {
        var settingsValid = true;
        try
        {
            _ = BuildSettingsFromUi();
        }
        catch
        {
            settingsValid = false;
        }

        var state = UiWorkflowGate.Evaluate(new UiWorkflowInputs(
            SettingsValid: settingsValid,
            BridgeSourceSafe: _sourceSafe,
            TargetLibrarySafe: _targetSafe,
            RekordboxClosed: !RekordboxProcessGuard.IsRunning(),
            DatabaseAccessQualified: _databaseAccessQualified,
            PreviewExists: _previewExists,
            PreviewValid: _previewValid,
            PreviewFresh: _previewFresh,
            BackupAvailable: _backupAvailable,
            BackupMatchesTarget: _backupMatchesTarget));

        BuildPreviewButton.IsEnabled = state.CanBuildPreview;
        ApplyButton.IsEnabled = state.CanApply;
        RestoreButton.IsEnabled = state.CanRestore;

        PreviewStatusTextBlock.Text = state.CanApply
            ? "Fresh conflict-free preview approved."
            : state.CanBuildPreview
                ? "Ready to build a fresh preview."
                : "Preview blocked by safety prerequisites.";

        ApplyStatusTextBox.Text = state.Blockers.Count == 0
            ? "All workflow safety gates are satisfied."
            : string.Join(Environment.NewLine, state.Blockers.Select(x => "• " + x));
    }

    private void RefreshDiagnostics()
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Settings: {_settingsPath}");
        builder.AppendLine($"Bridge: {BridgeDirectoryTextBox.Text.Trim()}");
        builder.AppendLine($"Target: {TargetDatabaseTextBox.Text.Trim()}");
        builder.AppendLine($"Mappings: {_mappings.Count}");
        builder.AppendLine($"Path aliases: {_aliases.Count}");
        builder.AppendLine($"rekordbox running: {RekordboxProcessGuard.IsRunning()}");
        var discovered = RekordboxDiscovery.Discover();
        builder.AppendLine($"rekordbox 6/7 installations found: {discovered.Installations.Count}");
        foreach (var installed in discovered.Installations)
            builder.AppendLine($"Installed rekordbox {installed.Version}: {installed.DirectoryPath}");
        foreach (var message in discovered.Diagnostics)
            builder.AppendLine($"Discovery: {message}");
        builder.AppendLine($"Database access qualified: {_databaseAccessQualified}");
        builder.AppendLine($"Database access source: {_databaseAccess?.KeySource ?? "none"}");
        builder.AppendLine($"Database version: {_databaseSnapshot?.Identity.DbVersion ?? "unknown"}");
        builder.AppendLine($"Preview: exists={_previewExists}, valid={_previewValid}, fresh={_previewFresh}");
        builder.AppendLine($"Backup: available={_backupAvailable}, matches target={_backupMatchesTarget}");
        builder.AppendLine();
        builder.AppendLine("Recent events:");
        foreach (var message in _diagnosticEvents.TakeLast(200))
            builder.AppendLine(message);
        DiagnosticsTextBox.Text = builder.ToString();
        UpdateWorkflowGate();
    }

    private void SetOperationStatus(string message, bool busy = false, bool error = false)
    {
        _operationBusy = busy;
        OperationStatusTextBlock.Text = message;
        OperationStatusTextBlock.Foreground = error
            ? Brushes.Firebrick
            : SystemColors.ControlTextBrush;
        OperationProgressBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AppendDiagnostic(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        _diagnosticEvents.Add(line);
        if (_diagnosticEvents.Count > 500) _diagnosticEvents.RemoveAt(0);
        DiagnosticsTextBox.AppendText(
            (DiagnosticsTextBox.Text.Length == 0 ? string.Empty : Environment.NewLine) + line);
        DiagnosticsTextBox.ScrollToEnd();
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static string? OptionalPath(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : Path.GetFullPath(value.Trim());

    private sealed class AliasRow
    {
        public string SourceRoot { get; set; } = string.Empty;
        public string TargetRoot { get; set; } = string.Empty;

        internal PathAlias ToAlias()
        {
            if (string.IsNullOrWhiteSpace(SourceRoot) ||
                string.IsNullOrWhiteSpace(TargetRoot))
                throw new InvalidDataException("Path alias source and target roots must not be empty.");

            return new PathAlias(SourceRoot.Trim(), TargetRoot.Trim());
        }

        internal static AliasRow FromAlias(PathAlias alias) =>
            new()
            {
                SourceRoot = alias.SourceRoot,
                TargetRoot = alias.TargetRoot
            };
    }

    private sealed class MappingRow
    {
        public string SourceField { get; set; } = string.Empty;
        public string TargetMyTag { get; set; } = string.Empty;
        public string Transform { get; set; } = TransformKind.Direct.ToString();
        public bool PerValue { get; set; } = true;
        public bool IgnoreEmpty { get; set; } = true;
        public string Prefix { get; set; } = string.Empty;
        public string? Pattern { get; set; }
        public string Replacement { get; set; } = string.Empty;

        internal MappingRule ToRule()
        {
            if (!Enum.TryParse<TransformKind>(Transform, ignoreCase: true, out var transform) ||
                !Enum.IsDefined(transform))
                throw new InvalidDataException($"Unknown mapping transform '{Transform}'.");

            return new MappingRule(
                SourceField,
                TargetMyTag,
                transform,
                PerValue,
                IgnoreEmpty,
                Prefix ?? string.Empty,
                Pattern,
                Replacement ?? string.Empty);
        }

        internal static MappingRow FromRule(MappingRule rule) =>
            new()
            {
                SourceField = rule.SourceField,
                TargetMyTag = rule.TargetMyTag,
                Transform = rule.Transform.ToString(),
                PerValue = rule.PerValue,
                IgnoreEmpty = rule.IgnoreEmpty,
                Prefix = rule.Prefix,
                Pattern = rule.Pattern,
                Replacement = rule.Replacement
            };
    }
}
