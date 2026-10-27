using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Text.Json;

namespace RekordboxMyTagSync.Core;

/// <summary>
/// Persists only fixed event categories. Detailed WPF diagnostics can contain
/// personal paths and identifiers, so the raw message never leaves memory.
/// </summary>
public static class DiagnosticHistoryStore
{
    private const int Limit = 200;
    private const long ByteLimit = 32 * 1024;
    private static readonly HashSet<string> Codes = new(StringComparer.Ordinal)
    {
        "SOURCE_DISCOVERY", "SOURCE_INSPECT", "SOURCE_VERIFIED",
        "TARGET_DISCOVERY", "TARGET_VALIDATION", "TARGET_VERIFIED",
        "PATH_ANALYSIS", "PHYSICAL_VERIFICATION", "PREVIEW",
        "SETTINGS", "READ_ONLY_WRITE_BLOCK", "OPERATION_BLOCKED",
        "OTHER_OPERATION"
    };
    public sealed record Entry(DateTimeOffset AtUtc, string Code);
    private sealed record Document(int SchemaVersion, List<Entry> Events);

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RekordboxMyTagSync", "diagnostic-history.json");

    public static IReadOnlyList<Entry> Load(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path) || Linked(path) || new FileInfo(path).Length > ByteLimit)
                return Array.Empty<Entry>();
            var document = JsonSerializer.Deserialize<Document>(File.ReadAllText(path));
            if (document is null || document.SchemaVersion != 1 ||
                document.Events is null || document.Events.Count > Limit ||
                document.Events.Any(e => e is null || !Codes.Contains(e.Code) ||
                    e.AtUtc == default || e.AtUtc.Offset != TimeSpan.Zero))
                return Array.Empty<Entry>();
            return document.Events;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Array.Empty<Entry>();
        }
    }

    public static void Append(string message, string? path = null)
    {
        // Two application instances can otherwise overwrite one another's
        // read-modify-replace history. The mutex contains only a path hash.
        path = Path.GetFullPath(path ?? DefaultPath);
        var key = OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
        var mutexName = (OperatingSystem.IsWindows() ? @"Local\" : string.Empty)
            + "RekordboxMyTagSync.DiagnosticHistory." + hash;
        using var mutex = new Mutex(false, mutexName);
        var acquired = false;
        try
        {
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(10)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired)
                throw new IOException("Timed out waiting for diagnostic history lock.");

            if (Linked(path))
                throw new InvalidDataException("Linked diagnostic history path is not writable.");
            var parent = Path.GetDirectoryName(path)
                ?? throw new InvalidDataException("Diagnostic history has no parent.");
            var events = Load(path).ToList();
            events.Add(new Entry(DateTimeOffset.UtcNow, Classify(message)));
            if (events.Count > Limit) events.RemoveRange(0, events.Count - Limit);
            Directory.CreateDirectory(parent);
            if (Linked(path))
                throw new InvalidDataException("Diagnostic history directory became a link.");
            var json = JsonSerializer.Serialize(new Document(1, events));
            if (Encoding.UTF8.GetByteCount(json) > ByteLimit)
                throw new InvalidDataException("Diagnostic history exceeded size limit.");
            var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                File.WriteAllText(temp, json, new UTF8Encoding(false));
                if (Linked(path))
                    throw new InvalidDataException("Diagnostic history path became a link.");
                if (File.Exists(path))
                    File.Replace(temp, path, null);
                else
                    File.Move(temp, path);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }
        finally
        {
            if (acquired) mutex.ReleaseMutex();
        }
    }

    public static string FormatForUi(Entry entry) =>
        $"[previous {entry.AtUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} UTC] {entry.Code} (details not retained)";

    private static bool Linked(string path)
    {
        // Checking only the file and immediate parent misses junctions or
        // symbolic links in higher ancestors, including dangling file links.
        var file = new FileInfo(Path.GetFullPath(path));
        if (file.LinkTarget is not null ||
            (file.Exists && (file.Attributes & FileAttributes.ReparsePoint) != 0))
            return true;
        for (var directory = file.Directory; directory is not null; directory = directory.Parent)
        {
            if (directory.LinkTarget is not null ||
                (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0))
                return true;
        }
        return false;
    }

    private static string Classify(string? raw)
    {
        var s = raw ?? string.Empty;
        if (Has(s, "Apply refused") || Has(s, "Restore refused"))
            return "READ_ONLY_WRITE_BLOCK";
        if (Has(s, "blocked") || Has(s, "failed") || Has(s, "rejected"))
            return "OPERATION_BLOCKED";
        if (Has(s, "Source verified")) return "SOURCE_VERIFIED";
        if (Has(s, "Source inspection")) return "SOURCE_INSPECT";
        if (Has(s, "Bridge discovery")) return "SOURCE_DISCOVERY";
        if (Has(s, "Database access automatically qualified")) return "TARGET_VERIFIED";
        if (Has(s, "Database access validation")) return "TARGET_VALIDATION";
        if (Has(s, "rekordbox discovery")) return "TARGET_DISCOVERY";
        if (Has(s, "path analysis")) return "PATH_ANALYSIS";
        if (Has(s, "physical") || Has(s, "file sample")) return "PHYSICAL_VERIFICATION";
        if (Has(s, "preview")) return "PREVIEW";
        if (Has(s, "Settings")) return "SETTINGS";
        return "OTHER_OPERATION";
    }

    private static bool Has(string text, string value) =>
        text.Contains(value, StringComparison.OrdinalIgnoreCase);
}
