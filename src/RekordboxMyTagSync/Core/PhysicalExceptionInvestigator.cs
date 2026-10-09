using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace RekordboxMyTagSync.Core;

/// <summary>
/// A small read-only exception report. Only duplicate target paths and
/// target paths entirely absent from the current bridge export are probed.
/// A virtual-only foobar path is NEVER interpreted as an imported cue track.
/// </summary>
public sealed record PhysicalExceptionRow(
    string Reason,
    string ContentId,
    string TargetPath,
    string RelatedContentIds,
    string ExpectedSourcePath,
    string DiskEvidence,
    string BridgeEvidence,
    string PlaylistReferences,
    string CueReferences,
    string MyTagReferences,
    string OtherDjReferences,
    string Conclusion);

public static class PhysicalExceptionInvestigator
{
    private const string NotAvailable = "Not verifiable (table/column unavailable)";
    private static readonly (string Table, string Heading)[] DjRelations =
    [
        ("djmdSongPlaylist", "Playlists"),
        ("djmdCue", "Cue points"),
        ("djmdSongMyTag", "MyTags"),
        ("djmdSongHotCueBanklist", "Hot-cue banks")
    ];

    // No DB needed to classify the concrete eight exceptional ContentIDs.
    // This does NOT enumerate all virtual cues as separate rekordbox tracks.
    public static IReadOnlyList<PhysicalExceptionRow> Identify(
        IReadOnlyList<BridgeTrack> bridge,
        IReadOnlyList<RekordboxTrackSnapshot> target,
        PathAlias root)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(root);
        var byTarget = target.GroupBy(t => WindowsPathMatcher.Normalize(t.Path),
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderBy(t => t.ContentId, StringComparer.OrdinalIgnoreCase).ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var byBridge = bridge.GroupBy(t =>
                WindowsPathMatcher.Normalize(t.Path, new[] { root }),
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToArray(), StringComparer.OrdinalIgnoreCase);
        var byFilename = bridge
            .GroupBy(t => Path.GetFileName(WindowsPathMatcher.Normalize(t.Path)),
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Select(t => WindowsPathMatcher.Normalize(t.Path))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var result = new List<PhysicalExceptionRow>();
        foreach (var (path, entries) in byTarget)
        {
            var duplicate = entries.Length > 1;
            var missing = !byBridge.ContainsKey(path);
            if (!duplicate && !missing) continue;

            var otherIds = duplicate
                ? string.Join(", ", entries.Select(t => t.ContentId))
                : "—";
            string expected = "Outside the proposed path mapping";
            if (IsWithin(path, root.TargetRoot))
            {
                var relative = Path.GetRelativePath(
                    WindowsPathMatcher.Normalize(root.TargetRoot), path);
                var sourceCandidate = WindowsPathMatcher.Normalize(
                    Path.Combine(WindowsPathMatcher.Normalize(root.SourceRoot), relative));
                if (WindowsPathMatcher.Equivalent(sourceCandidate, path, new[] { root }))
                    expected = sourceCandidate;
            }
            var fileEvidence = DescribeDisk(path, expected);
            var leaf = Path.GetFileName(path);
            var candidates = byFilename.GetValueOrDefault(leaf) ?? [];
            var sourceEvidence = byBridge.TryGetValue(path, out var exact)
                ? $"{exact.Length} foobar entry/entries at the mapped path " +
                  $"({exact.Count(t => t.Subsong == 0)} physical; " +
                  $"{exact.Count(t => t.Subsong != 0)} virtual)"
                : candidates.Length == 0
                    ? "No corresponding path or same-filename candidate in current bridge export"
                    : $"No matching mapped path; {candidates.Length} other foobar path(s) with the same filename. Names alone do not prove identity.";
            foreach (var t in entries)
                result.Add(new PhysicalExceptionRow(
                    duplicate ? "SAME_REKORDBOX_PATH" : "MISSING_FROM_BRIDGE_EXPORT",
                    t.ContentId, path, otherIds, expected, fileEvidence, sourceEvidence,
                    NotAvailable, NotAvailable, NotAvailable, NotAvailable,
                    duplicate
                        ? "Two rekordbox records point to one path. Compare DJ references; do not remove either automatically."
                        : "No matching path in this foobar export. Check file location / export freshness; not proof that the audio is missing."));
        }
        return result.OrderBy(x => x.Reason, StringComparer.Ordinal)
            .ThenBy(x => x.TargetPath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.ContentId, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static IReadOnlyList<PhysicalExceptionRow> InspectReadOnly(
        string databasePath,
        string encryptionKey,
        IReadOnlyList<PhysicalExceptionRow> rows)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(encryptionKey);
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count > 100)
            throw new InvalidDataException("Targeted investigation may inspect at most 100 exceptional ContentIDs.");
        if (RekordboxProcessGuard.IsRunning())
            throw new InvalidOperationException("Close rekordbox before inspecting its database.");
        var canonical = Path.GetFullPath(databasePath);
        var before = new FileInfo(canonical);
        if (!before.Exists) throw new FileNotFoundException("Database disappeared.", canonical);
        var length = before.Length;
        var writeUtc = before.LastWriteTimeUtc;

        RekordboxSqlCipherDatabase.EnsureSqliteInitialized();
        using var connection = new SqliteConnection(
            RekordboxSqlCipherDatabase.BuildConnectionString(canonical, encryptionKey, SqliteOpenMode.ReadOnly));
        connection.Open();

        var discovered = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type='table';";
            using var reader = command.ExecuteReader();
            while (reader.Read()) discovered[reader.GetString(0)] = reader.GetString(0);
        }

        var available = new Dictionary<string, (string Table, string IdColumn, bool HasDeleteFlag)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var (tableName, _) in DjRelations)
        {
            if (!discovered.TryGetValue(tableName, out var table)) continue;
            // Table identifiers are obtained from sqlite_master, never user input.
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var columnsQuery = connection.CreateCommand();
            columnsQuery.CommandText = "PRAGMA table_info(\"" + table.Replace("\"", "\"\"") + "\");";
            using (var reader = columnsQuery.ExecuteReader())
                while (reader.Read()) columns.Add(reader.GetString(1));
            if (!columns.Contains("ContentID")) continue;
            available[tableName] = (table, "ContentID", columns.Contains("rb_local_deleted"));
        }

        var updated = new List<PhysicalExceptionRow>(rows.Count);
        foreach (var entry in rows)
        {
            using (var verify = connection.CreateCommand())
            {
                verify.CommandText = """
                    SELECT FolderPath FROM djmdContent
                    WHERE ID = $cid AND COALESCE(rb_local_deleted, 0) = 0;
                    """;
                verify.Parameters.AddWithValue("$cid", entry.ContentId);
                var actualPath = verify.ExecuteScalar() as string;
                if (string.IsNullOrWhiteSpace(actualPath) ||
                    !WindowsPathMatcher.Equivalent(actualPath, entry.TargetPath))
                    throw new InvalidDataException(
                        "Target ContentID/path changed since validation; do not trust this investigation.");
            }
            var counts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (table, _) in DjRelations)
            {
                if (!available.TryGetValue(table, out var schema))
                {
                    counts[table] = NotAvailable;
                    continue;
                }
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM \"" +
                    schema.Table.Replace("\"", "\"\"") +
                    "\" WHERE \"ContentID\" = $cid" +
                    (schema.HasDeleteFlag ? " AND COALESCE(\"rb_local_deleted\",0) = 0" : "") + ";";
                command.Parameters.AddWithValue("$cid", entry.ContentId);
                counts[table] = Convert.ToInt64(command.ExecuteScalar(),
                    CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);
            }
            updated.Add(entry with
            {
                PlaylistReferences = counts["djmdSongPlaylist"],
                CueReferences = counts["djmdCue"],
                MyTagReferences = counts["djmdSongMyTag"],
                OtherDjReferences = counts["djmdSongHotCueBanklist"]
            });
        }
        connection.Close();
        before.Refresh();
        if (!before.Exists || before.Length != length || before.LastWriteTimeUtc != writeUtc)
            throw new InvalidDataException("rekordbox database changed during read-only exception inspection.");
        return updated;
    }

    private static bool IsWithin(string path, string root)
    {
        var canonical = WindowsPathMatcher.Normalize(root);
        return path.StartsWith(canonical, StringComparison.OrdinalIgnoreCase) &&
            (canonical.EndsWith('\\') || path.Length == canonical.Length ||
             path.Length > canonical.Length && path[canonical.Length] == '\\');
    }

    private static string DescribeDisk(string target, string source)
    {
        try
        {
            var targetInfo = new FileInfo(target);
            if (!targetInfo.Exists)
                return "Rekordbox path NOT present on disk (at time checked)";
            var sourceInfo = source == "Outside the proposed path mapping" ? null : new FileInfo(source);
            var second = sourceInfo is null
                ? "source path outside proposed root mapping"
                : sourceInfo.Exists
                    ? "expected source path exists; length " +
                      sourceInfo.Length.ToString("N0", CultureInfo.InvariantCulture) + " bytes"
                    : "expected source path NOT present on disk";
            return "Target exists; length " +
                targetInfo.Length.ToString("N0", CultureInfo.InvariantCulture) +
                " bytes; " + second +
                ". Equal names or lengths do not prove physical identity.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return "Disk state could not be checked: " + ex.GetType().Name;
        }
    }
}
