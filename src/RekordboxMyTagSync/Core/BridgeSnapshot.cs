using System.IO.Compression;
using System.Text.Json;

namespace RekordboxMyTagSync.Core;

public sealed record BridgeTrack(string Path, uint Subsong, IReadOnlyDictionary<string, IReadOnlyList<string>> Core, IReadOnlyDictionary<string, IReadOnlyList<string>> Extra);

public static class BridgeSnapshot
{
    private static readonly string[] V1Header = "path\\tsubsong\\tartist\\tartists\\ttitle\\toriginal_title\\tremixed_by\\talbum\\talbum_artist\\ttrack_number\\ttotal_tracks\\tdisc_number\\ttotal_discs\\tdate\\tgenre\\tstyle\\tbpm\\tlabel\\tcatalog_number\\tduration_seconds\\tisrc\\tcodec\\tbitrate\\ttag_fingerprint".Split('\\t');
    public static IReadOnlyList<BridgeTrack> Read(string gzipPath)
    {
        using var file = File.OpenRead(gzipPath); using var gzip = new GZipStream(file, CompressionMode.Decompress); using var reader = new StreamReader(gzip);
        var header = (reader.ReadLine() ?? throw new InvalidDataException("Missing bridge header")).Split('\\t');
        if (header.Length is not (24 or 25) || !header.Take(24).SequenceEqual(V1Header, StringComparer.Ordinal)) throw new InvalidDataException("Unsupported bridge schema.");
        if (header.Length == 25 && header[24] != "extra_metadata_json") throw new InvalidDataException("Unsupported bridge schema extension.");
        var tracks = new List<BridgeTrack>();
        for (string? line; (line = reader.ReadLine()) is not null;)
        {
            var cols = line.Split('\\t'); if (cols.Length != header.Length) throw new InvalidDataException("Bridge row column count mismatch.");
            if (!uint.TryParse(cols[1], out var subsong)) throw new InvalidDataException("Invalid subsong.");
            var core = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            for (var i = 2; i < 23; i++) core[header[i]] = string.IsNullOrEmpty(cols[i]) ? Array.Empty<string>() : new[] { cols[i] };
            IReadOnlyDictionary<string, IReadOnlyList<string>> extra = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            if (header.Length == 25 && !string.IsNullOrWhiteSpace(cols[24]))
            {
                var parsed = JsonSerializer.Deserialize<Dictionary<string, string[]>>(cols[24]) ?? throw new InvalidDataException("Invalid extra metadata JSON.");
                extra = parsed.ToDictionary(k => k.Key, v => (IReadOnlyList<string>)v.Value, StringComparer.OrdinalIgnoreCase);
            }
            tracks.Add(new BridgeTrack(cols[0], subsong, core, extra));
        }
        return tracks;
    }
}
