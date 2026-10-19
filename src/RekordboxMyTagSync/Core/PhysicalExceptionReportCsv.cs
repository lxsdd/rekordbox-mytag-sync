using System.IO;
using System.Text;

namespace RekordboxMyTagSync.Core;

/// <summary>Local CSV evidence only; no database, media, tag or backup writes.</summary>
public static class PhysicalExceptionReportCsv
{
    private static readonly string[] Headers =
    [
        "Reason", "Content ID", "rekordbox path", "Related Content IDs",
        "Expected foobar path", "Disk evidence", "Bridge evidence",
        "Playlist refs", "Cue refs", "Beatgrid refs", "MyTag refs",
        "Other DJ refs", "Conclusion"
    ];

    public static string Render(IReadOnlyList<PhysicalExceptionRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count > 100)
            throw new InvalidDataException("Exception report exceeds 100 inspected ContentIDs.");
        var output = new StringBuilder();
        Append(output, Headers);
        foreach (var row in rows)
        {
            if (row is null) throw new InvalidDataException("Null exception report row.");
            Append(output,
            [
                row.Reason, row.ContentId, row.TargetPath, row.RelatedContentIds,
                row.ExpectedSourcePath, row.DiskEvidence, row.BridgeEvidence,
                row.PlaylistReferences, row.CueReferences, row.BeatgridReferences,
                row.MyTagReferences, row.OtherDjReferences, row.Conclusion
            ]);
        }
        return output.ToString();
    }

    // CSV evidence depends on the actual foobar (path, subsong) identities,
    // not only bridge-state.tsv. A replaced compressed publication with the
    // same state metadata must not authorize a stale diagnostic export.
    public static void AssertBridgePathsUnchanged(
        IReadOnlyList<BridgeTrack> inspected,
        IReadOnlyList<BridgeTrack> current)
    {
        ArgumentNullException.ThrowIfNull(inspected);
        ArgumentNullException.ThrowIfNull(current);
        if (inspected.Count != current.Count)
            throw new InvalidDataException("Bridge path count changed since exception inspection.");
        for (var i = 0; i < inspected.Count; i++)
        {
            if (!string.Equals(inspected[i].Path, current[i].Path, StringComparison.Ordinal) ||
                inspected[i].Subsong != current[i].Subsong)
                throw new InvalidDataException(
                    "Bridge physical/virtual path identity changed since exception inspection.");
        }
    }

    public static void WriteAtomic(string destination, IReadOnlyList<PhysicalExceptionRow> rows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        var path = Path.GetFullPath(destination);
        if (!string.Equals(Path.GetExtension(path), ".csv", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Report destination must end in .csv.");
        var dir = Path.GetDirectoryName(path) ??
            throw new InvalidDataException("Missing CSV directory.");
        if (!Directory.Exists(dir)) throw new DirectoryNotFoundException(dir);
        var content = Render(rows);
        var tmp = Path.Combine(dir, "." + Path.GetFileName(path) + "." +
            Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(tmp, content, new UTF8Encoding(true));
            File.Move(tmp, path, overwrite: true);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }

    private static void Append(StringBuilder output, IEnumerable<string> fields)
    {
        output.AppendJoin(",", fields.Select(Quote));
        output.Append("\r\n");
    }

    private static string Quote(string? value)
    {
        var s = value ?? string.Empty;
        var significant = s.TrimStart(' ', '\t', '\r', '\n');
        if ((significant.Length > 0 && "=+-@".Contains(significant[0])) ||
            (s.Length > 0 && (s[0] == '\t' || s[0] == '\r' || s[0] == '\n')))
            s = "'" + s;
        return "\"" + s.Replace("\"", "\"\"") + "\"";
    }
}
