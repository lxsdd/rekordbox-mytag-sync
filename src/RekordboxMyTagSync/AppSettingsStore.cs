using System.Text.Json;

namespace RekordboxMyTagSync;

public sealed record MyTagMappingSetting(string SourceTag, string TargetGroup, string Transform = "Direct", string? Argument = null, bool IgnoreEmpty = true);
public sealed record AppSettings(string? BridgeSourcePath, string? RekordboxDatabasePath, IReadOnlyList<MyTagMappingSetting> Mappings)
{
    public static AppSettings Empty { get; } = new(null, null, Array.Empty<MyTagMappingSetting>());
}

public sealed class AppSettingsStore
{
    private readonly string _path;
    public AppSettingsStore(string? path = null) => _path = path ?? System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RekordboxMyTagSync", "settings.json");
    public string Path => _path;

    public AppSettings Load()
    {
        if (!File.Exists(_path)) return AppSettings.Empty;
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_path));
            return settings ?? AppSettings.Empty;
        }
        catch (JsonException) { return AppSettings.Empty; }
        catch (IOException) { return AppSettings.Empty; }
        catch (UnauthorizedAccessException) { return AppSettings.Empty; }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var directory = System.IO.Path.GetDirectoryName(_path) ?? throw new InvalidOperationException("Settings path has no parent directory.");
        Directory.CreateDirectory(directory);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temp, _path, true);
    }
}
