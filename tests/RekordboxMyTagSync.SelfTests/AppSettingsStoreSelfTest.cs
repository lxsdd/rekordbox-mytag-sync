using RekordboxMyTagSync.Core;

public static class AppSettingsStoreSelfTest
{
    public static void Run(string temp)
    {
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
            });

        AppSettingsStore.SaveAtomic(path, settings);
        var loaded = AppSettingsStore.Load(path);
        if (!string.Equals(loaded.BridgeSnapshotPath, Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(loaded.RekordboxDatabasePath, Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase) ||
            loaded.EffectivePathAliases.Count != 1 ||
            loaded.EffectiveMappings.Count != 2 ||
            loaded.EffectiveMappings[0].Transform != TransformKind.YearFromDate ||
            loaded.EffectiveMappings[1].Transform != TransformKind.RegexReplace ||
            loaded.EffectiveMappings[1].Prefix != "Style: ")
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
