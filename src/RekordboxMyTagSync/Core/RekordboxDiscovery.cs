using System.IO;
using System.Diagnostics;
using System.Security;
using System.Text.RegularExpressions;
using Microsoft.Win32;
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
        var discoverRegistered = options is null;
        options ??= RekordboxDiscoveryOptions.ForCurrentUser();
        var diagnostics = new List<string>();
        var installations = DiscoverInstallations(options.ProgramFilesRoot, diagnostics, discoverRegistered);

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

    public static RekordboxLibraryCandidate InspectManual(
        string databasePath,
        RekordboxDiscoveryOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        var discoverRegistered = options is null;
        options ??= RekordboxDiscoveryOptions.ForCurrentUser();

        var diagnostics = new List<string>();
        var installations = DiscoverInstallations(
            options.ProgramFilesRoot,
            diagnostics,
            discoverRegistered);
        var canonical = CanonicalPath(databasePath);
        var exists = File.Exists(canonical);
        var supportedInstallations = installations
            .Where(x => x.MajorVersion is 6 or 7)
            .ToArray();

        string? error = null;
        if (!exists)
            error = "Selected master.db does not exist.";
        else if (supportedInstallations.Length == 0)
            error = "No supported rekordbox 6/7 installation was discovered for manual target qualification.";

        return new RekordboxLibraryCandidate(
            canonical,
            supportedInstallations,
            new[] { "manual browse selection" },
            error is null,
            error);
    }

    private static IReadOnlyList<RekordboxInstallation> DiscoverInstallations(
        string programFilesRoot,
        List<string> diagnostics,
        bool discoverRegistered)
    {
        var found = new List<RekordboxInstallation>();
        ScanInstallRoot(Path.Combine(programFilesRoot, "Pioneer"), 6, found, diagnostics);
        ScanInstallRoot(Path.Combine(programFilesRoot, "rekordbox"), 7, found, diagnostics);

        // Real installations may be under Program Files (x86), a customized
        // installation root, or a versionless "rekordbox" directory. The
        // Windows uninstall registry and App Paths identify those installations.
        if (discoverRegistered && OperatingSystem.IsWindows())
        {
            var x86Root = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrWhiteSpace(x86Root) &&
                !PathsEqual(x86Root, programFilesRoot))
            {
                ScanInstallRoot(Path.Combine(x86Root, "Pioneer"), 6, found, diagnostics);
                ScanInstallRoot(Path.Combine(x86Root, "rekordbox"), 7, found, diagnostics);
            }
            DiscoverRegisteredInstallations(found, diagnostics);
        }

        return found
            .GroupBy(x => CanonicalPath(x.DirectoryPath), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(x => x.MajorVersion).First())
            .OrderBy(x => x.MajorVersion)
            .ThenBy(x => x.Version, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void DiscoverRegisteredInstallations(
        List<RekordboxInstallation> found,
        List<string> diagnostics)
    {
        var originalCount = found.Count;
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = root.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is not null)
                {
                    foreach (var name in uninstall.GetSubKeyNames())
                    {
                        try
                        {
                            using var item = uninstall.OpenSubKey(name);
                            if (item is null) continue;
                            var installation = InspectRegisteredInstallation(
                                item.GetValue("DisplayName") as string,
                                item.GetValue("DisplayVersion") as string,
                                item.GetValue("InstallLocation") as string,
                                item.GetValue("DisplayIcon") as string);
                            if (installation is not null)
                                found.Add(installation);
                        }
                        catch (Exception ex) when (
                            ex is IOException or UnauthorizedAccessException or
                            SecurityException or ArgumentException)
                        {
                            // An inaccessible/invalid unrelated uninstall entry
                            // cannot authorize an installation.
                        }
                    }
                }

                using var appPaths = root.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\rekordbox.exe");
                if (appPaths is not null)
                {
                    var executable = appPaths.GetValue(null) as string;
                    var installation = InspectRegisteredInstallation(
                        "rekordbox", null, null, executable);
                    if (installation is not null)
                        found.Add(installation);
                }
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or
                SecurityException or ArgumentException)
            {
                diagnostics.Add("Windows installation registry lookup was unavailable.");
            }
        }

        diagnostics.Add(
            $"Windows registry installation discovery: {found.Count - originalCount} candidate(s).");
    }

    // Unit-testable registration evidence. An uninstall/app-path entry alone is
    // insufficient: the exact rekordbox executable must also exist on disk.
    internal static RekordboxInstallation? InspectRegisteredInstallation(
        string? displayName,
        string? displayVersion,
        string? installLocation,
        string? displayIcon)
    {
        var name = displayName?.Trim();
        if (string.IsNullOrWhiteSpace(name) ||
            (!name.Equals("rekordbox", StringComparison.OrdinalIgnoreCase) &&
             !name.StartsWith("rekordbox ", StringComparison.OrdinalIgnoreCase)))
            return null;

        var paths = new List<string>();
        if (!string.IsNullOrWhiteSpace(installLocation))
        {
            var dir = installLocation.Trim().Trim('"');
            paths.Add(Path.Combine(dir, "rekordbox.exe"));
            paths.Add(Path.Combine(dir, "bin", "rekordbox.exe"));
        }
        if (!string.IsNullOrWhiteSpace(displayIcon))
        {
            var icon = displayIcon.Trim().Trim('"');
            icon = Regex.Replace(icon, @",\s*-?\d+$", string.Empty);
            icon = icon.Trim('"');
            paths.Add(icon);
        }

        foreach (var path in paths)
        {
            try
            {
                var executable = Path.GetFullPath(path);
                if (!string.Equals(Path.GetFileName(executable), "rekordbox.exe",
                        StringComparison.OrdinalIgnoreCase) || !File.Exists(executable))
                    continue;

                var info = FileVersionInfo.GetVersionInfo(executable);
                var version = GetRegisteredVersion(displayVersion)
                    ?? GetRegisteredVersion(name)
                    ?? GetRegisteredVersion(info.ProductVersion)
                    ?? GetRegisteredVersion(info.FileVersion);
                if (version is null)
                    continue;
                var major = version[0] - '0';
                // Reject a registry registration that conflicts with the
                // version actually embedded in the executable.
                if (info.ProductMajorPart is 6 or 7 && info.ProductMajorPart != major)
                    continue;
                if (info.FileMajorPart is 6 or 7 && info.FileMajorPart != major)
                    continue;
                return new RekordboxInstallation(
                    major, version, CanonicalPath(Path.GetDirectoryName(executable)!));
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or
                SecurityException or ArgumentException or
                System.ComponentModel.Win32Exception)
            {
                // Invalid registration cannot authorize database access.
            }
        }
        return null;
    }

    private static string? GetRegisteredVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var match = Regex.Match(value, @"(?<!\d)(?<version>[67](?:\.\d+){0,3})(?!\d)");
        return match.Success ? match.Groups["version"].Value : null;
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
