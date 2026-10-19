using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;

namespace RekordboxMyTagSync.Core;

public sealed record BridgeTrack(string Path, uint Subsong, IReadOnlyDictionary<string, IReadOnlyList<string>> Core, IReadOnlyDictionary<string, IReadOnlyList<string>> Extra)
{
    public IReadOnlyList<string> GetFieldValues(string field)
    {
        if (Core.TryGetValue(field, out var core)) return core;
        if (Extra.TryGetValue(field, out var extra)) return extra;
        return Array.Empty<string>();
    }
}

public static class BridgeSnapshot
{
    private static readonly string[] V1Header = "path\tsubsong\tartist\tartists\ttitle\toriginal_title\tremixed_by\talbum\talbum_artist\ttrack_number\ttotal_tracks\tdisc_number\ttotal_discs\tdate\tgenre\tstyle\tbpm\tlabel\tcatalog_number\tduration_seconds\tisrc\tcodec\tbitrate\ttag_fingerprint".Split('\t');

    private static readonly HashSet<string> ReservedMetadataNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "ARTIST", "ARTISTS", "TITLE", "ORIGINAL TITLE", "REMIXED BY",
        "ALBUM", "ALBUM ARTIST", "TRACKNUMBER", "TRACK", "TOTALTRACKS", "TRACKTOTAL",
        "DISCNUMBER", "DISC", "TOTALDISCS", "DISCTOTAL", "DATE", "YEAR",
        "GENRE", "STYLE", "BPM", "LABEL", "PUBLISHER",
        "CATALOGNUMBER", "CATALOG NUMBER", "CATALOG", "ISRC"
    };

    public static IReadOnlyList<BridgeTrack> Read(string gzipPath)
    {
        using var file = File.OpenRead(gzipPath);
        return ReadCompressed(file, expectedSchemaVersion: null);
    }

    // Source discovery needs only the gzip header, not a 52k-row inflation.
    internal static int ProbeSchemaVersion(string gzipPath)
    {
        using var file = File.OpenRead(gzipPath);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        return ValidateHeader(reader.ReadLine());
    }

    // Pin one compressed snapshot image before parsing: no path reopen.
    internal static IReadOnlyList<BridgeTrack> ReadCompressed(
        Stream compressed, int? expectedSchemaVersion, int? expectedItemCount = null)
    {
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress, leaveOpen: true);
        using var reader = new StreamReader(gzip);
        var headerLine = reader.ReadLine();
        var actual = ValidateHeader(headerLine);
        if (expectedSchemaVersion.HasValue && actual != expectedSchemaVersion.Value)
            throw new InvalidDataException(
                "Bridge state schema_version=" + expectedSchemaVersion.Value +
                " but digital-items.tsv.gz is schema v" + actual +
                "; rejecting mixed snapshot generations.");
        var header = headerLine!.Split('\t');

        var tracks = new List<BridgeTrack>();
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (string? line; (line = reader.ReadLine()) is not null;)
        {
            // Never parse an unbounded compressed stream while the state
            // advertises fewer rows. This limits both memory use and the
            // amplification of malformed producer payloads.
            if (expectedItemCount.HasValue && tracks.Count >= expectedItemCount.Value)
                throw new InvalidDataException(
                    "Bridge snapshot contains more tracks than its advertised item_count.");
            var cols = line.Split('\t');
            if (cols.Length != header.Length) throw new InvalidDataException("Bridge row column count mismatch.");
            if (string.IsNullOrWhiteSpace(cols[0])) throw new InvalidDataException("Bridge row path is empty.");
            if (!uint.TryParse(cols[1], out var subsong)) throw new InvalidDataException("Invalid subsong.");
            var identity = cols[0] + "\n" + subsong;
            if (!identities.Add(identity)) throw new InvalidDataException("Duplicate bridge path/subsong identity.");

            var core = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            for (var i = 2; i < 23; i++)
                core[header[i]] = string.IsNullOrEmpty(cols[i]) ? Array.Empty<string>() : new[] { cols[i] };

            IReadOnlyDictionary<string, IReadOnlyList<string>> extra =
                new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            if (header.Length >= 25)
            {
                if (string.IsNullOrWhiteSpace(cols[24]))
                    throw new InvalidDataException("Schema-v2 bridge row is missing canonical extra metadata JSON.");

                Dictionary<string, string[]> parsed;
                try
                {
                    parsed = JsonSerializer.Deserialize<Dictionary<string, string[]>>(cols[24])
                        ?? throw new InvalidDataException("Invalid extra metadata JSON.");
                }
                catch (JsonException ex)
                {
                    throw new InvalidDataException("Invalid extra metadata JSON.", ex);
                }

                var normalized = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var pair in parsed)
                {
                    if (string.IsNullOrWhiteSpace(pair.Key)) throw new InvalidDataException("Extra metadata contains an empty field name.");
                    if (ReservedMetadataNames.Contains(pair.Key))
                        throw new InvalidDataException($"Extra metadata duplicates core field '{pair.Key}'.");
                    if (pair.Value is null) throw new InvalidDataException($"Extra metadata field '{pair.Key}' has a null value array.");
                    if (pair.Value.Any(string.IsNullOrEmpty)) throw new InvalidDataException($"Extra metadata field '{pair.Key}' contains an empty value.");
                    if (!normalized.TryAdd(pair.Key, pair.Value))
                        throw new InvalidDataException($"Extra metadata contains duplicate field '{pair.Key}' ignoring case.");
                }
                extra = normalized;
            }

            if (header.Length == 26)
                ProjectCoreMetadataVectors(cols[25], core);

            tracks.Add(new BridgeTrack(cols[0], subsong, core, extra));
        }
        return tracks;
    }

    private static int ValidateHeader(string? line)
    {
        if (line is null) throw new InvalidDataException("Missing bridge header.");
        var header = line.Split('\t');
        if (header.Length is not (24 or 25 or 26) ||
            !header.Take(24).SequenceEqual(V1Header, StringComparer.Ordinal))
            throw new InvalidDataException("Unsupported bridge schema.");
        if (header.Length >= 25 && header[24] != "extra_metadata_json")
            throw new InvalidDataException("Unsupported bridge schema extension.");
        if (header.Length == 26 && header[25] != "metadata_vectors_json")
            throw new InvalidDataException("Unsupported bridge schema-v3 extension.");
        return header.Length - 23;
    }

    // The bridge producer applies meta_first_of in this order; its canonical
    // JSON array is alphabetized and cannot supply synonym precedence.
    private static readonly (string Column, string[] Tags)[] CoreVectorPriority =
    {
        ("artist", new[] { "ARTIST" }),
        ("artists", new[] { "ARTISTS" }),
        ("title", new[] { "TITLE" }),
        ("original_title", new[] { "ORIGINAL TITLE" }),
        ("remixed_by", new[] { "REMIXED BY" }),
        ("album", new[] { "ALBUM" }),
        ("album_artist", new[] { "ALBUM ARTIST" }),
        ("track_number", new[] { "TRACKNUMBER", "TRACK" }),
        ("total_tracks", new[] { "TOTALTRACKS", "TRACKTOTAL" }),
        ("disc_number", new[] { "DISCNUMBER", "DISC" }),
        ("total_discs", new[] { "TOTALDISCS", "DISCTOTAL" }),
        ("date", new[] { "DATE", "YEAR" }),
        ("genre", new[] { "GENRE" }),
        ("style", new[] { "STYLE" }),
        ("bpm", new[] { "BPM" }),
        ("label", new[] { "LABEL", "PUBLISHER" }),
        ("catalog_number", new[] { "CATALOGNUMBER", "CATALOG NUMBER", "CATALOG" }),
        ("isrc", new[] { "ISRC" })
    };

    // Bridge schema V3 contains the original foobar field vectors. Core
    // columns remain scalar backwards-compatibility projections; take their
    // real multiple values from metadata_vectors_json instead. Non-Core
    // metadata ALWAYS comes from canonical extra_metadata_json and must not
    // be duplicated in another model or used to override core fields.
    private static void ProjectCoreMetadataVectors(
        string json, Dictionary<string, IReadOnlyList<string>> core)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidDataException("Bridge schema-v3 metadata vectors are empty.");

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException(
                    "Bridge schema-v3 metadata vectors must be an array.");

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var vectors = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in document.RootElement.EnumerateArray())
            {
                if (field.ValueKind != JsonValueKind.Object ||
                    !field.TryGetProperty("name", out var nameNode) ||
                    nameNode.ValueKind != JsonValueKind.String ||
                    !field.TryGetProperty("values", out var valuesNode) ||
                    valuesNode.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException(
                        "Bridge schema-v3 metadata vector entry is invalid.");
                var name = nameNode.GetString();
                if (string.IsNullOrWhiteSpace(name) || !names.Add(name))
                    throw new InvalidDataException(
                        "Bridge schema-v3 has an empty or duplicate metadata vector field name.");

                var values = new List<string>();
                foreach (var element in valuesNode.EnumerateArray())
                {
                    if (element.ValueKind != JsonValueKind.String)
                        throw new InvalidDataException(
                            "Bridge schema-v3 vector has a non-string value.");
                    var value = element.GetString()!;
                    if (!string.IsNullOrEmpty(value)) values.Add(value);
                }

                // Technical properties remain bound to info_get/get_length,
                // never an identically named user foobar tag.
                if (ReservedMetadataNames.Contains(name))
                    vectors.Add(name, values.ToArray());
            }

            // DATE beats YEAR, LABEL beats PUBLISHER, etc. even when JSON
            // happens to list fallback tags first. Empty primary vectors
            // fall back to the first nonempty synonym. V1/V2 scalar fields
            // and absent V3 vectors retain their existing behavior.
            foreach (var (column, tags) in CoreVectorPriority)
            {
                var seen = false;
                IReadOnlyList<string> selected = Array.Empty<string>();
                foreach (var tag in tags)
                {
                    if (!vectors.TryGetValue(tag, out var values)) continue;
                    seen = true;
                    if (values.Count == 0) continue;
                    selected = values;
                    break;
                }
                if (seen)
                    core[column] = selected;
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                "Bridge schema-v3 metadata vectors contain invalid JSON.", ex);
        }
    }
}
