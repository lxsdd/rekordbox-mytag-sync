using System.Globalization;
using System.IO;
using System.Text;

namespace RekordboxMyTagSync.Core;

public sealed record BridgeSourceState(
    int SchemaVersion,
    long Generation,
    int ItemCount,
    DateTime LastChangeUtc,
    string? SourceId,
    string? SourceName,
    string? ProfilePath,
    string? ProducerVersion,
    int? ProducerPid);

public sealed record BridgeSourceCandidate(
    string DirectoryPath,
    bool Safe,
    BridgeSourceState? State,
    string? Error,
    bool Selected);

public sealed record BridgeSourceSnapshot(
    string DirectoryPath,
    BridgeSourceState State,
    IReadOnlyList<BridgeTrack> Tracks);

public static class BridgeSourceDiscovery
{
    public const string StateFileName = "bridge-state.tsv";
    public const string SnapshotFileName = "digital-items.tsv.gz";

    public static string GetStandardProfileDirectory(string? roamingAppDataRoot = null)
    {
        var root = string.IsNullOrWhiteSpace(roamingAppDataRoot)
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : roamingAppDataRoot;
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException("Roaming application data directory is unavailable.");
        return Path.GetFullPath(
            Path.Combine(root, "foobar2000-v2", "foo_dj_library_bridge"));
    }

    public static IReadOnlyList<BridgeSourceCandidate> Discover(
        AppSettings settings,
        string? roamingAppDataRoot = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = AppSettingsStore.ValidateAndNormalize(settings);
        var selected = normalized.EffectiveBridgeDirectory;

        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            GetStandardProfileDirectory(roamingAppDataRoot)
        };
        foreach (var directory in normalized.EffectiveKnownBridgeDirectories)
            directories.Add(Path.GetFullPath(directory));
        if (!string.IsNullOrWhiteSpace(selected))
            directories.Add(Path.GetFullPath(selected));

        return directories
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .Select(x => Inspect(
                x,
                selected is not null &&
                string.Equals(
                    Path.GetFullPath(selected),
                    Path.GetFullPath(x),
                    StringComparison.OrdinalIgnoreCase)))
            .ToArray();
    }

    public static BridgeSourceCandidate Inspect(
        string directory,
        bool selected = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var canonical = Path.GetFullPath(directory);
        try
        {
            var statePath = Path.Combine(canonical, StateFileName);
            var snapshotPath = Path.Combine(canonical, SnapshotFileName);
            if (!File.Exists(statePath))
                return new BridgeSourceCandidate(
                    canonical,
                    false,
                    null,
                    "bridge-state.tsv is missing.",
                    selected);

            var stateBytes = ReadSharedBytes(statePath);
            var state = ParseState(stateBytes);
            if (!File.Exists(snapshotPath))
                return new BridgeSourceCandidate(
                    canonical,
                    false,
                    state,
                    "digital-items.tsv.gz is missing.",
                    selected);

            return new BridgeSourceCandidate(
                canonical,
                true,
                state,
                null,
                selected);
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
        {
            return new BridgeSourceCandidate(
                canonical,
                false,
                null,
                ex.Message,
                selected);
        }
    }

    public static BridgeSourceSnapshot ReadStable(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        var canonical = Path.GetFullPath(directory);
        var statePath = Path.Combine(canonical, StateFileName);
        var snapshotPath = Path.Combine(canonical, SnapshotFileName);

        if (!File.Exists(statePath))
            throw new FileNotFoundException("bridge-state.tsv not found.", statePath);
        if (!File.Exists(snapshotPath))
            throw new FileNotFoundException("digital-items.tsv.gz not found.", snapshotPath);

        var beforeBytes = ReadSharedBytes(statePath);
        var state = ParseState(beforeBytes);
        var tracks = BridgeSnapshot.Read(snapshotPath);
        if (tracks.Count != state.ItemCount)
            throw new InvalidDataException(
                $"Bridge item_count={state.ItemCount}, snapshot={tracks.Count}.");

        var afterBytes = ReadSharedBytes(statePath);
        if (!beforeBytes.AsSpan().SequenceEqual(afterBytes))
            throw new InvalidDataException(
                "Bridge state changed while reading the snapshot; the snapshot is rejected.");

        return new BridgeSourceSnapshot(canonical, state, tracks);
    }

    internal static BridgeSourceState ParseState(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        var lines = Encoding.UTF8.GetString(bytes)
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (string.IsNullOrWhiteSpace(line))
                continue;
            var tab = line.IndexOf('\t');
            if (tab <= 0 || tab != line.LastIndexOf('\t'))
                throw new InvalidDataException(
                    $"bridge-state.tsv line {index + 1} is invalid.");
            var key = line[..tab];
            var value = line[(tab + 1)..];
            if (!values.TryAdd(key, value))
                throw new InvalidDataException(
                    $"bridge-state.tsv contains duplicate key '{key}'.");
        }

        var schema = RequiredInt(values, "schema_version");
        if (schema is < 1 or > 3)
            throw new InvalidDataException(
                $"Unsupported bridge schema version '{schema}'.");

        var generation = RequiredLong(values, "generation");
        if (generation <= 0)
            throw new InvalidDataException("Bridge generation must be positive.");

        var complete = Required(values, "complete");
        if (complete != "1")
        {
            if (complete == "0")
                throw new InvalidDataException(
                    "Bridge snapshot publication is incomplete.");
            throw new InvalidDataException("Bridge complete must be 0 or 1.");
        }

        var itemCount = RequiredInt(values, "item_count");
        if (itemCount < 0)
            throw new InvalidDataException("Bridge item_count is negative.");

        if (!DateTime.TryParse(
                Required(values, "last_change_utc"),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var lastChangeUtc))
            throw new InvalidDataException(
                "Bridge last_change_utc is invalid.");

        int? producerPid = null;
        if (values.TryGetValue("producer_pid", out var rawPid) &&
            !string.IsNullOrWhiteSpace(rawPid))
        {
            if (!int.TryParse(
                    rawPid,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var parsedPid) ||
                parsedPid <= 0)
                throw new InvalidDataException(
                    "Bridge producer_pid is invalid.");
            producerPid = parsedPid;
        }

        return new BridgeSourceState(
            schema,
            generation,
            itemCount,
            lastChangeUtc,
            Optional(values, "source_id"),
            Optional(values, "source_name"),
            Optional(values, "profile_path"),
            Optional(values, "producer_version"),
            producerPid);
    }

    private static byte[] ReadSharedBytes(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static string Required(
        IReadOnlyDictionary<string, string> values,
        string key)
    {
        if (!values.TryGetValue(key, out var value) ||
            string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException(
                $"bridge-state.tsv required field '{key}' is missing or empty.");
        return value;
    }

    private static int RequiredInt(
        IReadOnlyDictionary<string, string> values,
        string key)
    {
        var raw = Required(values, key);
        if (!int.TryParse(
                raw,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var value))
            throw new InvalidDataException(
                $"bridge-state.tsv field '{key}' is not an integer.");
        return value;
    }

    private static long RequiredLong(
        IReadOnlyDictionary<string, string> values,
        string key)
    {
        var raw = Required(values, key);
        if (!long.TryParse(
                raw,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var value))
            throw new InvalidDataException(
                $"bridge-state.tsv field '{key}' is not an integer.");
        return value;
    }

    private static string? Optional(
        IReadOnlyDictionary<string, string> values,
        string key) =>
        values.TryGetValue(key, out var value) &&
        !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
}
