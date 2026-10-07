using System.IO;
using System.Text;
using System.Text.Json;

namespace RekordboxMyTagSync.Core;

public sealed record AppSettings(
    string? BridgeSnapshotPath = null,
    string? RekordboxDatabasePath = null,
    IReadOnlyList<PathAlias>? PathAliases = null,
    IReadOnlyList<MappingRule>? Mappings = null)
{
    public IReadOnlyList<PathAlias> EffectivePathAliases =>
        PathAliases ?? Array.Empty<PathAlias>();

    public IReadOnlyList<MappingRule> EffectiveMappings =>
        Mappings ?? Array.Empty<MappingRule>();
}

public static class AppSettingsStore
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static string DefaultPath
    {
        get
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(root))
                throw new InvalidOperationException("Local application data directory is unavailable.");
            return Path.Combine(root, "RekordboxMyTagSync", "settings.json");
        }
    }

    public static AppSettings Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var canonicalPath = Path.GetFullPath(path);
        if (!File.Exists(canonicalPath))
            return new AppSettings();

        try
        {
            using var stream = File.OpenRead(canonicalPath);
            var document = JsonSerializer.Deserialize<SettingsDocument>(stream, JsonOptions)
                ?? throw new InvalidDataException("Settings document is empty.");
            if (document.SchemaVersion != CurrentSchemaVersion)
                throw new InvalidDataException(
                    $"Unsupported settings schema version '{document.SchemaVersion}'.");
            if (document.Settings is null)
                throw new InvalidDataException("Settings document has no settings payload.");

            return ValidateAndNormalize(document.Settings);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Settings JSON is invalid.", ex);
        }
    }

    public static void SaveAtomic(string path, AppSettings settings)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(settings);

        var canonicalPath = Path.GetFullPath(path);
        var normalized = ValidateAndNormalize(settings);
        var directory = Path.GetDirectoryName(canonicalPath)
            ?? throw new InvalidDataException("Settings path has no parent directory.");
        Directory.CreateDirectory(directory);

        var tempPath = canonicalPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var payload = JsonSerializer.Serialize(
                new SettingsDocument(CurrentSchemaVersion, normalized),
                JsonOptions);
            using (var stream = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       4096,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(payload);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(canonicalPath))
                File.Replace(tempPath, canonicalPath, null);
            else
                File.Move(tempPath, canonicalPath);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
                // Preserve the primary save result/error; stale temp files are never read as settings.
            }
        }
    }

    public static AppSettings ValidateAndNormalize(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var bridgePath = NormalizeOptionalPath(settings.BridgeSnapshotPath);
        var databasePath = NormalizeOptionalPath(settings.RekordboxDatabasePath);

        var aliases = (settings.PathAliases ?? Array.Empty<PathAlias>())
            .Select(x => x ?? throw new InvalidDataException("Settings contain a null path alias."))
            .Select(x => new PathAlias(
                RequirePath(x.SourceRoot, "Path alias source"),
                RequirePath(x.TargetRoot, "Path alias target")))
            .ToArray();

        if (aliases.Length != 0)
            _ = WindowsPathMatcher.Normalize(aliases[0].SourceRoot, aliases);

        var mappings = (settings.Mappings ?? Array.Empty<MappingRule>())
            .Select(ValidateMapping)
            .ToArray();

        return new AppSettings(bridgePath, databasePath, aliases, mappings);
    }

    private static MappingRule ValidateMapping(MappingRule rule)
    {
        if (rule is null)
            throw new InvalidDataException("Settings contain a null mapping rule.");
        if (string.IsNullOrWhiteSpace(rule.SourceField) ||
            string.IsNullOrWhiteSpace(rule.TargetMyTag))
            throw new InvalidDataException("Mapping source field and target MyTag must not be empty.");
        if (!Enum.IsDefined(rule.Transform))
            throw new InvalidDataException($"Unknown mapping transform '{rule.Transform}'.");

        var normalized = rule with
        {
            SourceField = rule.SourceField.Trim(),
            TargetMyTag = rule.TargetMyTag.Trim(),
            Prefix = rule.Prefix ?? string.Empty,
            Replacement = rule.Replacement ?? string.Empty
        };

        try
        {
            _ = MappingEngine.Apply(normalized, new[] { "validation" });
        }
        catch (Exception ex) when (ex is ArgumentException or System.Text.RegularExpressions.RegexMatchTimeoutException)
        {
            throw new InvalidDataException(
                $"Mapping '{normalized.SourceField}' → '{normalized.TargetMyTag}' is invalid.", ex);
        }

        return normalized;
    }

    private static string? NormalizeOptionalPath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path.Trim());

    private static string RequirePath(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidDataException($"{label} is empty.");
        return Path.GetFullPath(path.Trim());
    }

    private sealed record SettingsDocument(
        int SchemaVersion,
        AppSettings? Settings);
}
