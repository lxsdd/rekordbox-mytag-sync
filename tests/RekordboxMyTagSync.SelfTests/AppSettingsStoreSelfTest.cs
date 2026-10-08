using RekordboxMyTagSync.Core;

public static class AppSettingsStoreSelfTest
{
    public static void Run(string temp)
    {
        QualifyUiWorkflowGate();

        var root = Path.Combine(temp, "AppSettings");
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "settings.json");

        var empty = AppSettingsStore.Load(path);
        if (empty.BridgeSnapshotPath is not null ||
            empty.RekordboxDatabasePath is not null ||
            empty.EffectivePathAliases.Count != 0 ||
            empty.EffectiveMappings.Count != 0)
            throw new InvalidOperationException("missing settings file did not yield empty defaults");

        var source = Path.Combine(root, "bridge", "digital-items.tsv.gz");
        var target = Path.Combine(root, "rekordbox", "master.db");
        var aliasSource = Path.Combine(root, "alias-source");
        var aliasTarget = Path.Combine(root, "alias-target");
        var settings = new AppSettings(
            source,
            target,
            new[] { new PathAlias(aliasSource, aliasTarget) },
            new[]
            {
                new MappingRule("DATE", "Year", TransformKind.YearFromDate),
                new MappingRule(
                    "STYLE",
                    "Style",
                    TransformKind.RegexReplace,
                    PerValue: true,
                    IgnoreEmpty: true,
                    Prefix: "Style: ",
                    Pattern: @"\s+",
                    Replacement: " ")
            },
            SupportedDbVersions: new[] { "6.0.0", "7.0.0", "6.0.0" });

        AppSettingsStore.SaveAtomic(path, settings);
        var loaded = AppSettingsStore.Load(path);
        if (!string.Equals(loaded.BridgeSnapshotPath, Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(loaded.RekordboxDatabasePath, Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase) ||
            loaded.EffectivePathAliases.Count != 1 ||
            loaded.EffectiveMappings.Count != 2 ||
            loaded.EffectiveMappings[0].Transform != TransformKind.YearFromDate ||
            loaded.EffectiveMappings[1].Transform != TransformKind.RegexReplace ||
            loaded.EffectiveMappings[1].Prefix != "Style: " ||
            loaded.EffectiveSupportedDbVersions.Count != 0 ||
            loaded.SupportedDbVersions is not null)
            throw new InvalidOperationException("settings roundtrip mismatch");

        var updatedTarget = Path.Combine(root, "rekordbox-2", "master.db");
        AppSettingsStore.SaveAtomic(path, loaded with { RekordboxDatabasePath = updatedTarget });
        var updated = AppSettingsStore.Load(path);
        if (!string.Equals(
                updated.RekordboxDatabasePath,
                Path.GetFullPath(updatedTarget),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("atomic settings replacement did not persist updated target");
        if (Directory.EnumerateFiles(root, "settings.json.tmp-*", SearchOption.TopDirectoryOnly).Any())
            throw new InvalidOperationException("settings save left a temporary file behind");

        File.WriteAllText(path, """{"schemaVersion":999,"settings":{}}""");
        AssertBlocked(() => AppSettingsStore.Load(path), "schema");

        File.WriteAllText(path, "{not-json");
        AssertBlocked(() => AppSettingsStore.Load(path), "JSON");

        var invalidMapping = new AppSettings(
            Mappings: new[]
            {
                new MappingRule(
                    "STYLE",
                    "Style",
                    TransformKind.RegexReplace,
                    Pattern: "(")
            });
        AssertBlocked(() => AppSettingsStore.SaveAtomic(path, invalidMapping), "invalid");
    }

    private static void QualifyUiWorkflowGate()
    {
        var readyForPreview = UiWorkflowGate.Evaluate(new UiWorkflowInputs(
            SettingsValid: true,
            BridgeSourceSafe: true,
            TargetLibrarySafe: true,
            RekordboxClosed: true,
            DatabaseAccessQualified: true,
            PreviewExists: false,
            PreviewValid: false,
            PreviewFresh: false,
            BackupAvailable: false,
            BackupMatchesTarget: false));
        if (!readyForPreview.CanBuildPreview ||
            readyForPreview.CanApply ||
            readyForPreview.CanRestore)
            throw new InvalidOperationException("UI workflow gate did not expose preview-only readiness");

        var fullyReady = UiWorkflowGate.Evaluate(new UiWorkflowInputs(
            SettingsValid: true,
            BridgeSourceSafe: true,
            TargetLibrarySafe: true,
            RekordboxClosed: true,
            DatabaseAccessQualified: true,
            PreviewExists: true,
            PreviewValid: true,
            PreviewFresh: true,
            BackupAvailable: true,
            BackupMatchesTarget: true));
        if (!fullyReady.CanBuildPreview ||
            !fullyReady.CanApply ||
            !fullyReady.CanRestore ||
            fullyReady.Blockers.Count != 0)
            throw new InvalidOperationException("UI workflow gate rejected fully qualified state");

        var stalePreview = UiWorkflowGate.Evaluate(new UiWorkflowInputs(
            SettingsValid: true,
            BridgeSourceSafe: true,
            TargetLibrarySafe: true,
            RekordboxClosed: true,
            DatabaseAccessQualified: true,
            PreviewExists: true,
            PreviewValid: true,
            PreviewFresh: false,
            BackupAvailable: true,
            BackupMatchesTarget: true));
        if (!stalePreview.CanBuildPreview ||
            stalePreview.CanApply ||
            !stalePreview.CanRestore ||
            !stalePreview.Blockers.Any(x => x.Contains("stale", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("UI workflow gate did not block stale preview apply");

        var running = UiWorkflowGate.Evaluate(new UiWorkflowInputs(
            SettingsValid: true,
            BridgeSourceSafe: true,
            TargetLibrarySafe: true,
            RekordboxClosed: false,
            DatabaseAccessQualified: true,
            PreviewExists: true,
            PreviewValid: true,
            PreviewFresh: true,
            BackupAvailable: true,
            BackupMatchesTarget: true));
        if (running.CanBuildPreview ||
            running.CanApply ||
            running.CanRestore ||
            !running.Blockers.Any(x => x.Contains("running", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("UI workflow gate did not fail closed while rekordbox is running");

        var incompatible = UiWorkflowGate.Evaluate(new UiWorkflowInputs(
            SettingsValid: true,
            BridgeSourceSafe: true,
            TargetLibrarySafe: true,
            RekordboxClosed: true,
            DatabaseAccessQualified: false,
            PreviewExists: true,
            PreviewValid: true,
            PreviewFresh: true,
            BackupAvailable: true,
            BackupMatchesTarget: false));
        if (incompatible.CanBuildPreview ||
            incompatible.CanApply ||
            incompatible.CanRestore ||
            !incompatible.Blockers.Any(x => x.Contains("compatibility", StringComparison.OrdinalIgnoreCase)) ||
            !incompatible.Blockers.Any(x => x.Contains("does not match", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("UI workflow gate did not block incompatible database/backup state");
    }

    private static void AssertBlocked(Action action, string expected)
    {
        try
        {
            action();
        }
        catch (InvalidDataException ex) when (
            ex.Message.Contains(expected, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new InvalidOperationException(
            $"settings store did not fail closed for '{expected}'");
    }
}
