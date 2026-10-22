using System.Globalization;
using System.IO;
using System.Text;
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
        path ??= DefaultPath;
        if (Linked(path))
            throw new InvalidDataException("Linked diagnostic history is not writable.");
        var parent = Path.GetDirectoryName(Path.GetFullPath(path))
            ?? throw new InvalidDataException("Diagnostic history has no parent.");
        if (Directory.Exists(parent) && Linked(parent))
            throw new InvalidDataException("Linked diagnostic directory is not writable.");
        var events = Load(path).ToList();
        events.Add(new Entry(DateTimeOffset.UtcNow, Classify(message)));
        if (events.Count > Limit) events.RemoveRange(0, events.Count - Limit);
        Directory.CreateDirectory(parent);
        var json = JsonSerializer.Serialize(new Document(1, events));
        if (Encoding.UTF8.GetByteCount(json) > ByteLimit)
            throw new InvalidDataException("Diagnostic history exceeded size limit.");
        var temp = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            if (File.Exists(path))
            {
                if (Linked(path))
                    throw new InvalidDataException("Diagnostic history became a link.");
                File.Replace(temp, path, null);
            }
            else File.Move(temp, path);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public static string FormatForUi(Entry entry) =>
        $"[previous {entry.AtUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} UTC] {entry.Code} (details not retained)";

    private static bool Linked(string path) =>
        (File.Exists(path) || Directory.Exists(path)) &&
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

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
