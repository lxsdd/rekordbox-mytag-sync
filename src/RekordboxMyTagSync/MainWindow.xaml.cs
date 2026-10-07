using System.Collections.ObjectModel;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using RekordboxMyTagSync.Core;

namespace RekordboxMyTagSync;

public partial class MainWindow : Window
{
    private readonly string _settingsPath = AppSettingsStore.DefaultPath;
    private readonly ObservableCollection<MappingRow> _mappings = new();
    private readonly ObservableCollection<AliasRow> _aliases = new();
    private AppSettings _settings = new();
    private bool _sourceSafe;
    private bool _targetSafe;
    private bool _databaseAccessQualified;
    private bool _previewExists;
    private bool _previewValid;
    private bool _previewFresh;
    private bool _backupAvailable;
    private bool _backupMatchesTarget;
    private RekordboxDatabaseSnapshot? _databaseSnapshot;
    private BridgeSourceSnapshot? _bridgeSnapshot;
    private PreviewResult? _approvedPreview;

    public MainWindow()
    {
        InitializeComponent();

        MappingGrid.ItemsSource = _mappings;
        PathAliasGrid.ItemsSource = _aliases;
        Loaded += (_, _) => LoadSettings();

        SaveSettingsButton.Click += (_, _) => SaveSettings();
        DiscoverSourceButton.Click += (_, _) => DiscoverSources();
        InspectSourceButton.Click += (_, _) => InspectSelectedSource();
        DiscoverTargetButton.Click += (_, _) => DiscoverTargets();
        ValidateDatabaseAccessButton.Click += (_, _) => ValidateDatabaseAccess();
        AddMappingButton.Click += (_, _) => AddMapping();
        RemoveMappingButton.Click += (_, _) => RemoveSelectedMapping();
        AddAliasButton.Click += (_, _) => AddAlias();
        RemoveAliasButton.Click += (_, _) => RemoveSelectedAlias();
        RefreshDiagnosticsButton.Click += (_, _) => RefreshDiagnostics();
        BuildPreviewButton.Click += (_, _) => BuildPreview();
        ApplyButton.Click += (_, _) => ApplyApprovedPreview();

        SourceCandidatesGrid.SelectionChanged += (_, _) =>
        {
            if (SourceCandidatesGrid.SelectedItem is BridgeSourceCandidate source)
            {
                BridgeDirectoryTextBox.Text = source.DirectoryPath;
                _sourceSafe = source.Safe;
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
        DatabaseKeyPasswordBox.PasswordChanged += (_, _) =>
        {
            InvalidateDatabaseAccess("Database key changed.");
            UpdateWorkflowGate();
        };
        SupportedDbVersionsTextBox.TextChanged += (_, _) =>
        {
            InvalidateDatabaseAccess("Supported DBVersion list changed.");
            UpdateWorkflowGate();
        };
        BridgeDirectoryTextBox.TextChanged += (_, _) =>
        {
            InvalidatePreview("Bridge source changed.");
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
            SupportedDbVersionsTextBox.Text = string.Join(", ", _settings.EffectiveSupportedDbVersions);

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
            known,
            ParseSupportedDbVersions(SupportedDbVersionsTextBox.Text)));
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
            AppendDiagnostic($"Bridge discovery: {candidates.Count} candidate(s), {safeCount} safe.");
            UpdateWorkflowGate();
        }
        catch (Exception ex)
        {
            _sourceSafe = false;
            SourceCandidatesGrid.ItemsSource = null;
            AppendDiagnostic($"Bridge discovery blocked: {ex.Message}");
            UpdateWorkflowGate();
        }
    }

    private void InspectSelectedSource()
    {
        try
        {
            var directory = OptionalPath(BridgeDirectoryTextBox.Text)
                ?? throw new InvalidDataException("No bridge source directory is selected.");
            var candidate = BridgeSourceDiscovery.Inspect(directory, selected: true);
            SourceCandidatesGrid.ItemsSource = new[] { candidate };

            if (!candidate.Safe)
                throw new InvalidDataException(candidate.Error ?? "Bridge source is not safe.");

            var snapshot = BridgeSourceDiscovery.ReadStable(directory);
            _bridgeSnapshot = snapshot;
            _sourceSafe = true;
            InvalidatePreview("Bridge source was re-read.");
            AppendDiagnostic(
                $"Bridge source verified: schema {snapshot.State.SchemaVersion}, generation {snapshot.State.Generation}, {snapshot.Tracks.Count} track(s).");
            UpdateWorkflowGate();
        }
        catch (Exception ex)
        {
            _sourceSafe = false;
            AppendDiagnostic($"Bridge source inspection blocked: {ex.Message}");
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

    private void ValidateDatabaseAccess()
    {
        try
        {
            if (!_targetSafe)
                throw new InvalidDataException("Selected target library is not qualified as safe.");

            var databasePath = OptionalPath(TargetDatabaseTextBox.Text)
                ?? throw new InvalidDataException("No target master.db is selected.");
            var key = DatabaseKeyPasswordBox.Password;
            if (string.IsNullOrWhiteSpace(key))
                throw new InvalidDataException("SQLCipher key is empty.");

            var versions = ParseSupportedDbVersions(SupportedDbVersionsTextBox.Text);
            if (versions.Count == 0)
                throw new InvalidDataException("At least one exact supported DBVersion is required.");

            var snapshot = RekordboxSqlCipherDatabase.ReadSnapshot(
                databasePath,
                key,
                new RekordboxDatabaseReadPolicy(
                    versions.ToHashSet(StringComparer.Ordinal),
                    RequireRekordboxClosed: true));

            _databaseSnapshot = snapshot;
            _databaseAccessQualified = true;
            _previewExists = false;
            _previewValid = false;
            _previewFresh = false;

            DatabaseAccessStatusTextBlock.Text =
                $"Qualified: DBVersion {snapshot.Identity.DbVersion}, DBID {snapshot.Identity.DbId}, " +
                $"{snapshot.Tracks.Count} tracks, {snapshot.MyTagDefinitions.Count} MyTag definitions.";
            AppendDiagnostic(
                $"Database access qualified for exact DBVersion '{snapshot.Identity.DbVersion}' " +
                $"using SQLite3MC {snapshot.Identity.SqliteCipherVersion}. Key material was not persisted or logged.");
        }
        catch (Exception ex)
        {
            InvalidateDatabaseAccess("Database access validation failed.");
            DatabaseAccessStatusTextBlock.Text = $"Blocked: {ex.Message}";
            AppendDiagnostic($"Database access validation blocked: {ex.Message}");
        }

        UpdateWorkflowGate();
    }

    private void InvalidateDatabaseAccess(string reason)
    {
        if (_databaseAccessQualified || _databaseSnapshot is not null)
            AppendDiagnostic(reason);

        _databaseAccessQualified = false;
        _databaseSnapshot = null;
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
            if (_databaseSnapshot is null || !_databaseAccessQualified)
                throw new InvalidDataException("Target database access is not qualified.");

            var settings = BuildSettingsFromUi();
            var databasePath = settings.RekordboxDatabasePath
                ?? throw new InvalidDataException("No target master.db is selected.");
            var bridgeDirectory = settings.EffectiveBridgeDirectory
                ?? throw new InvalidDataException("No bridge source directory is selected.");
            var key = DatabaseKeyPasswordBox.Password;
            if (string.IsNullOrWhiteSpace(key))
                throw new InvalidDataException("SQLCipher key is empty.");

            var versions = settings.EffectiveSupportedDbVersions;
            if (versions.Count == 0)
                throw new InvalidDataException("At least one exact supported DBVersion is required.");

            var policy = new RekordboxDatabaseReadPolicy(
                versions.ToHashSet(StringComparer.Ordinal),
                RequireRekordboxClosed: true);
            var bridge = BridgeSourceDiscovery.ReadStable(bridgeDirectory);
            var stateRoot = AppStateRoot();
            var provenancePath = ProvenanceStore.GetStatePath(
                Path.Combine(stateRoot, "provenance"),
                _databaseSnapshot.Identity);
            var backupRoot = Path.Combine(stateRoot, "backups");
            var mappingHash = ComputeMappingHashSha256(settings.EffectiveMappings);
            var toolVersion =
                typeof(MainWindow).Assembly.GetName().Version?.ToString()
                ?? "0.0.0";

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
            _bridgeSnapshot = bridge;

            if (!string.IsNullOrWhiteSpace(result.BackupPackagePath))
            {
                var inspection = RekordboxRollingBackup.Inspect(
                    result.BackupPackagePath,
                    _databaseSnapshot.Identity);
                _backupAvailable = true;
                _backupMatchesTarget = true;
                BackupPackageTextBox.Text = inspection.PackagePath;
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
        builder.AppendLine($"Database access qualified: {_databaseAccessQualified}");
        builder.AppendLine($"Preview: exists={_previewExists}, valid={_previewValid}, fresh={_previewFresh}");
        builder.AppendLine($"Backup: available={_backupAvailable}, matches target={_backupMatchesTarget}");
        DiagnosticsTextBox.Text = builder.ToString();
        UpdateWorkflowGate();
    }

    private void AppendDiagnostic(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        DiagnosticsTextBox.AppendText(
            (DiagnosticsTextBox.Text.Length == 0 ? string.Empty : Environment.NewLine) + line);
        DiagnosticsTextBox.ScrollToEnd();
    }

    private static IReadOnlyList<string> ParseSupportedDbVersions(string? text) =>
        (text ?? string.Empty)
            .Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => x.Length != 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

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
