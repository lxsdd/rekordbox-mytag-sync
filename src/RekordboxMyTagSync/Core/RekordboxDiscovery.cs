using System.Text.Json;
using System.Xml.Linq;

namespace RekordboxMyTagSync.Core;

public sealed record RekordboxInstallation(int MajorVersion, string Version, string DirectoryPath);

public sealed record RekordboxLibraryCandidate(
    string DatabasePath,
    IReadOnlyList<RekordboxInstallation> UsedBy,
    IReadOnlyList<string> Evidence,
    bool Safe,
    string? Error);

public sealed record RekordboxDiscoveryResult(
    IReadOnlyList<RekordboxInstallation> Installations,
    IReadOnlyList<RekordboxLibraryCandidate> Libraries,
    IReadOnlyList<string> Diagnostics);

public sealed record RekordboxDiscoveryOptions(string ProgramFilesRoot, string PioneerAppDataRoot)
{
    public static RekordboxDiscoveryOptions ForCurrentUser()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return new RekordboxDiscoveryOptions(programFiles, Path.Combine(appData, "Pioneer"));
    }
}

public static class RekordboxDiscovery
{
    public static RekordboxDiscoveryResult Discover(RekordboxDiscoveryOptions? options = null)
    {
        options ??= RekordboxDiscoveryOptions.ForCurrentUser();
        var diagnostics = new List<string>();
        var installations = DiscoverInstallations(options.ProgramFilesRoot, diagnostics);

        var settingsPath = Path.Combine(options.PioneerAppDataRoot, "rekordbox6", "rekordbox3.settings");
        var optionsPath = Path.Combine(options.PioneerAppDataRoot, "rekordboxAgent", "storage", "options.json");
        string? settingsDb = null;
        string? agentDb = null;

        try { settingsDb = ReadSettingsDatabasePath(settingsPath); }
        catch (Exception ex) { diagnostics.Add($"settings: {ex.Message}"); }
        try { agentDb = ReadAgentDatabasePath(optionsPath); }
        catch (Exception ex) { diagnostics.Add($"agent-options: {ex.Message}"); }

        var evidenceByPath = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        AddEvidence(evidenceByPath, settingsDb, "rekordbox3.settings");
        AddEvidence(evidenceByPath, agentDb, "rekordboxAgent options.json");

        var conflict = settingsDb is not null && agentDb is not null &&
                       !PathsEqual(settingsDb, agentDb);
        if (conflict)
            diagnostics.Add($"database-path conflict: settings='{settingsDb}', agent='{agentDb}'");

        var libraries = new List<RekordboxLibraryCandidate>();
        foreach (var pair in evidenceByPath.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            var fullPath = pair.Key;
            var exists = File.Exists(fullPath);
            var error = conflict
                ? "Rekordbox settings and rekordboxAgent disagree about the active master.db path."
                : !exists ? "Referenced master.db does not exist." : null;
            libraries.Add(new RekordboxLibraryCandidate(
                fullPath,
                installations,
                pair.Value,
                error is null,
                error));
        }

        if (libraries.Count == 0)
            diagnostics.Add("No rekordbox master.db path was found in rekordbox3.settings or rekordboxAgent options.json.");

        return new RekordboxDiscoveryResult(installations, libraries, diagnostics);
    }

    private static IReadOnlyList<RekordboxInstallation> DiscoverInstallations(string programFilesRoot, List<string> diagnostics)
    {
        var found = new List<RekordboxInstallation>();
        ScanInstallRoot(Path.Combine(programFilesRoot, "Pioneer"), 6, found, diagnostics);
        ScanInstallRoot(Path.Combine(programFilesRoot, "rekordbox"), 7, found, diagnostics);
        return found
            .GroupBy(x => CanonicalPath(x.DirectoryPath), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(x => x.MajorVersion).First())
            .OrderBy(x => x.MajorVersion)
            .ThenBy(x => x.Version, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void ScanInstallRoot(string root, int expectedMajor, List<RekordboxInstallation> found, List<string> diagnostics)
    {
        if (!Directory.Exists(root)) return;
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(root, "rekordbox*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(directory);
                if (!TryExtractVersion(name, out var version, out var major) || major != expectedMajor) continue;
                found.Add(new RekordboxInstallation(major, version, CanonicalPath(directory)));
            }
        }
        catch (Exception ex)
        {
            diagnostics.Add($"install-scan '{root}': {ex.Message}");
        }
    }

    private static bool TryExtractVersion(string directoryName, out string version, out int major)
    {
        version = string.Empty;
        major = 0;
        const string prefix = "rekordbox";
        if (!directoryName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        var remainder = directoryName[prefix.Length..].Trim();
        if (remainder.Length == 0) return false;
        var first = remainder.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        if (!Version.TryParse(first, out var parsed)) return false;
        version = parsed.ToString();
        major = parsed.Major;
        return major is 6 or 7;
    }

    internal static string? ReadSettingsDatabasePath(string settingsPath)
    {
        if (!File.Exists(settingsPath)) return null;
        var document = XDocument.Load(settingsPath, LoadOptions.None);
        var value = document.Descendants("VALUE")
            .FirstOrDefault(x => string.Equals((string?)x.Attribute("name"), "masterDbDirectory", StringComparison.Ordinal));
        var raw = (string?)value?.Attribute("val");
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return CanonicalPath(Path.Combine(raw, "master.db"));
    }

    internal static string? ReadAgentDatabasePath(string optionsPath)
    {
        if (!File.Exists(optionsPath)) return null;
        using var stream = File.OpenRead(optionsPath);
        using var json = JsonDocument.Parse(stream);
        if (!json.RootElement.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("options.json has no options array.");

        foreach (var entry in options.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Array || entry.GetArrayLength() < 2) continue;
            var key = entry[0].ValueKind == JsonValueKind.String ? entry[0].GetString() : null;
            if (!string.Equals(key, "db-path", StringComparison.Ordinal)) continue;
            var raw = entry[1].ValueKind == JsonValueKind.String ? entry[1].GetString() : null;
            if (string.IsNullOrWhiteSpace(raw)) throw new InvalidDataException("db-path is empty.");
            return CanonicalPath(raw);
        }
        return null;
    }

    private static void AddEvidence(Dictionary<string, List<string>> map, string? path, string evidence)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var canonical = CanonicalPath(path);
        if (!map.TryGetValue(canonical, out var list)) map[canonical] = list = new List<string>();
        if (!list.Contains(evidence, StringComparer.Ordinal)) list.Add(evidence);
    }

    internal static bool PathsEqual(string a, string b) =>
        string.Equals(CanonicalPath(a), CanonicalPath(b), StringComparison.OrdinalIgnoreCase);

    internal static string CanonicalPath(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
