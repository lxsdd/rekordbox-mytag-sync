using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using RekordboxMyTagSync.Core;

namespace RekordboxMyTagSync;

public partial class MainWindow : Window
{
    private readonly string _settingsPath = AppSettingsStore.DefaultPath;
    private readonly ObservableCollection<MappingRow> _mappings = new();
    private AppSettings _settings = new();

    public MainWindow()
    {
        InitializeComponent();

        MappingGrid.ItemsSource = _mappings;
        Loaded += (_, _) => LoadSettings();

        SaveSettingsButton.Click += (_, _) => SaveSettings();
        DiscoverSourceButton.Click += (_, _) => DiscoverSources();
        InspectSourceButton.Click += (_, _) => InspectSelectedSource();
        DiscoverTargetButton.Click += (_, _) => DiscoverTargets();
        AddMappingButton.Click += (_, _) => AddMapping();
        RemoveMappingButton.Click += (_, _) => RemoveSelectedMapping();
        RefreshDiagnosticsButton.Click += (_, _) => RefreshDiagnostics();

        SourceCandidatesGrid.SelectionChanged += (_, _) =>
        {
            if (SourceCandidatesGrid.SelectedItem is BridgeSourceCandidate source)
                BridgeDirectoryTextBox.Text = source.DirectoryPath;
        };
        TargetCandidatesGrid.SelectionChanged += (_, _) =>
        {
            if (TargetCandidatesGrid.SelectedItem is RekordboxLibraryCandidate target)
                TargetDatabaseTextBox.Text = target.DatabasePath;
        };

        BuildPreviewButton.IsEnabled = false;
        ApplyButton.IsEnabled = false;
        RestoreButton.IsEnabled = false;
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

            AppendDiagnostic("Settings loaded.");
            DiscoverSources();
            DiscoverTargets();
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

        return AppSettingsStore.ValidateAndNormalize(new AppSettings(
            snapshotPath,
            databasePath,
            _settings.EffectivePathAliases,
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
                    BridgeDirectoryTextBox.Text = preferred.DirectoryPath;
            }

            var safeCount = candidates.Count(x => x.Safe);
            AppendDiagnostic($"Bridge discovery: {candidates.Count} candidate(s), {safeCount} safe.");
        }
        catch (Exception ex)
        {
            SourceCandidatesGrid.ItemsSource = null;
            AppendDiagnostic($"Bridge discovery blocked: {ex.Message}");
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
            AppendDiagnostic(
                $"Bridge source verified: schema {snapshot.State.SchemaVersion}, generation {snapshot.State.Generation}, {snapshot.Tracks.Count} track(s).");
        }
        catch (Exception ex)
        {
            AppendDiagnostic($"Bridge source inspection blocked: {ex.Message}");
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

            AppendDiagnostic(
                $"rekordbox discovery: {result.Installations.Count} installation(s), {result.Libraries.Count} library candidate(s), {safe.Length} safe.");
            foreach (var diagnostic in result.Diagnostics)
                AppendDiagnostic($"rekordbox: {diagnostic}");

            if (safe.Length > 1 && string.IsNullOrWhiteSpace(TargetDatabaseTextBox.Text))
                AppendDiagnostic("Target selection remains fail-closed because multiple safe libraries were discovered.");
        }
        catch (Exception ex)
        {
            TargetCandidatesGrid.ItemsSource = null;
            AppendDiagnostic($"rekordbox discovery blocked: {ex.Message}");
        }
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
    }

    private void RemoveSelectedMapping()
    {
        if (MappingGrid.SelectedItem is MappingRow row)
            _mappings.Remove(row);
    }

    private void RefreshDiagnostics()
    {
        var builder = new StringBuilder();
        builder.AppendLine($"Settings: {_settingsPath}");
        builder.AppendLine($"Bridge: {BridgeDirectoryTextBox.Text.Trim()}");
        builder.AppendLine($"Target: {TargetDatabaseTextBox.Text.Trim()}");
        builder.AppendLine($"Mappings: {_mappings.Count}");
        builder.AppendLine($"rekordbox running: {RekordboxProcessGuard.IsRunning()}");
        builder.AppendLine("Preview/apply controls remain locked until the production preview controller is bound.");
        DiagnosticsTextBox.Text = builder.ToString();
    }

    private void AppendDiagnostic(string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        DiagnosticsTextBox.AppendText(
            (DiagnosticsTextBox.Text.Length == 0 ? string.Empty : Environment.NewLine) + line);
        DiagnosticsTextBox.ScrollToEnd();
    }

    private static string? OptionalPath(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : Path.GetFullPath(value.Trim());

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
