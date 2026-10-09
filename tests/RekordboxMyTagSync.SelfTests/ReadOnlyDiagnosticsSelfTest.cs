using System.IO;
using RekordboxMyTagSync.Core;

public static class ReadOnlyDiagnosticsSelfTest
{
    public static void Run(string temp)
    {
        var duplicates = MyTagLinkAudit.FindDuplicates(new[]
        {
            new MyTagLinkAuditRow("row-1", "C1", "M1", "Genre", "House"),
            new MyTagLinkAuditRow("row-2", "C1", "M1", "genre", "HOUSE"),
            new MyTagLinkAuditRow("row-3", "C2", "M1", "Genre", "House"),
            new MyTagLinkAuditRow("row-4", "C2", "M2", "Genre", "HOUSE"),
            new MyTagLinkAuditRow("row-5", "C3", "M1", "Genre", "House")
        });
        if (duplicates.Count != 2 ||
            duplicates[0].ContentId != "C1" ||
            !duplicates[0].Reason.StartsWith("DUPLICATE_SAME_MYTAG_ID", StringComparison.Ordinal) ||
            !duplicates[0].AssignmentRowIds.SequenceEqual(new[] { "row-1", "row-2" }) ||
            duplicates[1].ContentId != "C2" ||
            !duplicates[1].Reason.StartsWith("DUPLICATE_DISPLAY_VALUE", StringComparison.Ordinal))
            throw new InvalidOperationException("Raw link duplicates were confused with duplicate music files.");

        var root = Path.Combine(temp, "DiagnosticCache");
        Directory.CreateDirectory(root);
        var bridgePath = Path.Combine(root, BridgeSourceDiscovery.SnapshotFileName);
        var dbPath = Path.Combine(root, "master.db");
        File.WriteAllText(bridgePath, "stable snapshot fixture");
        File.WriteAllText(dbPath, "stable encrypted DB fixture fingerprint");
        var state = new BridgeSourceState(3, 9, 1, DateTime.UtcNow,
            null, null, null, null, null);
        var source = new BridgeSourceSnapshot(root, state, Array.Empty<BridgeTrack>());
        var dbFile = new FileInfo(dbPath);
        var db = new RekordboxDatabaseSnapshot(
            new RekordboxDatabaseIdentity("db-1", "6000", dbPath,
                dbFile.Length, dbFile.LastWriteTimeUtc, "fixture"),
            Array.Empty<RekordboxTrackSnapshot>(),
            Array.Empty<RekordboxMyTagDefinition>());
        var alias = new PathAlias(Path.Combine(root, "Source"), Path.Combine(root, "Target"));
        var report = new PhysicalIdentityReport(1, 1, 0, 0, 0, 0,
            0, 0, 0, Array.Empty<string>(),
            new[] { new VerifiedPhysicalPair(
                Path.Combine(root, "Source", "One.mp3"), Path.Combine(root, "Target", "One.mp3")) });
        var cache = ReadOnlyIdentityCache.Capture(source, db, alias, report);
        if (!cache.IsValidFor(source, db, alias) ||
            cache.IsValidFor(source with { State = state with { Generation = 10 } }, db, alias) ||
            cache.IsValidFor(source, db, new PathAlias(alias.SourceRoot, Path.Combine(root, "Wrong"))))
            throw new InvalidOperationException("Read-only identity cache accepted wrong source generation or root.");
        File.AppendAllText(bridgePath, "changed");
        if (cache.IsValidFor(source, db, alias))
            throw new InvalidOperationException("Read-only cache accepted changed bridge bytes.");
        File.WriteAllText(bridgePath, "stable snapshot fixture");
        File.AppendAllText(dbPath, "changed");
        if (cache.IsValidFor(source, db, alias))
            throw new InvalidOperationException("Read-only cache accepted changed rekordbox database.");
    }
}
