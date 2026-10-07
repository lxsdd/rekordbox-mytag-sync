using System.IO;
using System.Text.Json;
using System.Windows;

namespace RekordboxMyTagSync;

public partial class App : Application { }

public sealed record PersistedMappingRule(
    string SourceTag,
    string TargetGroup,
    string Mode = "Passthrough",
    string? Pattern = null,
    string? Replacement = null,
    string? Prefix = null,
    bool IgnoreEmpty = true);

public sealed record AppSettingsDocument(
    int SchemaVersion,
    string? SourcePath,
    string? TargetDatabasePath,
    string? DiagnosticsPath,
    IReadOnlyList<PersistedMappingRule> Mappings)
{
    public const int CurrentSchemaVersion = 1;

    public static AppSettingsDocument Empty() =>
        new(
            CurrentSchemaVersion,
            SourcePath: null,
            TargetDatabasePath: null,
            DiagnosticsPath: null,
            Mappings: Array.Empty<PersistedMappingRule>());
}

public static class AppSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static AppSettingsDocument Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var canonical = Path.GetFullPath(path);
        if (!File.Exists(canonical))
            return AppSettingsDocument.Empty();

        AppSettingsDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<AppSettingsDocument>(
                File.ReadAllText(canonical),
                JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Settings JSON is malformed.", ex);
        }

        if (document is null)
            throw new InvalidDataException("Settings document is empty.");

        Validate(document);
        return document;
    }

    public static void SaveAtomic(string path, AppSettingsDocument document)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(document);
        Validate(document);

        var canonical = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(canonical)
            ?? throw new InvalidDataException("Settings path has no parent directory.");
        Directory.CreateDirectory(directory);

        var tempPath = canonical + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(document, JsonOptions));
        try
        {
            File.Move(tempPath, canonical, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
                File.Delete(tempPath);
        }
    }

    public static string DefaultPath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException("LOCALAPPDATA is unavailable.");

        return Path.Combine(root, "RekordboxMyTagSync", "settings.json");
    }

    private static void Validate(AppSettingsDocument document)
    {
        if (document.SchemaVersion != AppSettingsDocument.CurrentSchemaVersion)
            throw new InvalidDataException(
                $"Unsupported settings schema version '{document.SchemaVersion}'.");

        ArgumentNullException.ThrowIfNull(document.Mappings);
        foreach (var rule in document.Mappings)
        {
            if (rule is null ||
                string.IsNullOrWhiteSpace(rule.SourceTag) ||
                string.IsNullOrWhiteSpace(rule.TargetGroup) ||
                string.IsNullOrWhiteSpace(rule.Mode))
                throw new InvalidDataException("Settings contain an incomplete mapping rule.");
        }
    }
}
