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
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        var header = (reader.ReadLine() ?? throw new InvalidDataException("Missing bridge header")).Split('\t');
        if (header.Length is not (24 or 25) || !header.Take(24).SequenceEqual(V1Header, StringComparer.Ordinal))
            throw new InvalidDataException("Unsupported bridge schema.");
        if (header.Length == 25 && header[24] != "extra_metadata_json")
            throw new InvalidDataException("Unsupported bridge schema extension.");

        var tracks = new List<BridgeTrack>();
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (string? line; (line = reader.ReadLine()) is not null;)
        {
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
            if (header.Length == 25)
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

            tracks.Add(new BridgeTrack(cols[0], subsong, core, extra));
        }
        return tracks;
    }
}
