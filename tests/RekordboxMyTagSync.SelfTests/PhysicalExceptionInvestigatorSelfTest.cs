using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using RekordboxMyTagSync.Core;

public static class PhysicalExceptionInvestigatorSelfTest
{
    public static void Run(string temp)
    {
        QualifyLargeLibraryWithVirtualCueOnlyFiles();
        var empty = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var bridge = new[]
        {
            new BridgeTrack(@"Z:\Music\Singles\Duplicate.mp3", 0, empty, empty),
            new BridgeTrack(@"Z:\Music\Singles\Cue.mp3", 1, empty, empty)
        };
        var snapshots = new[]
        {
            new RekordboxTrackSnapshot("C1", @"R:\Singles\Duplicate.mp3", []),
            new RekordboxTrackSnapshot("C2", @"R:\Singles\Duplicate.mp3", []),
            new RekordboxTrackSnapshot("C3", @"R:\Singles\NotInBridge.mp3", []),
            new RekordboxTrackSnapshot("C4", @"R:\Singles\Cue.mp3", [])
        };
        var alias = new PathAlias(@"Z:\Music", @"R:\");
        var rows = PhysicalExceptionInvestigator.Identify(bridge, snapshots, alias);
        if (rows.Count != 3 ||
            rows.Count(x => x.Reason == "SAME_REKORDBOX_PATH") != 2 ||
            rows.Count(x => x.Reason == "MISSING_FROM_BRIDGE_EXPORT") != 1 ||
            rows.Any(x => x.ContentId == "C4") ||
            !rows.Where(x => x.Reason == "SAME_REKORDBOX_PATH")
                .All(x => x.RelatedContentIds.Contains("C1", StringComparison.Ordinal) &&
                          x.RelatedContentIds.Contains("C2", StringComparison.Ordinal)) ||
            !rows.Single(x => x.ContentId == "C3").ExpectedSourcePath.Equals(
                @"Z:\Music\Singles\NotInBridge.mp3", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Physical exception scope mixed cue segments with duplicate or absent files.");

        // The real encrypted fixture lets us verify how direct rekordbox
        // playlist and cue references differ across same-path ContentIDs.
        // The snapshot is not modified during the read-only inspection.
        var db = Path.Combine(temp, "targeted-exceptions-fixture.db");
        EncryptedMutationFixture.Create(
            db, @"R:\Singles\Duplicate.mp3", @"R:\Singles\Duplicate.mp3");
        using (var writable = EncryptedMutationFixture.Open(db, SqliteOpenMode.ReadWrite))
        {
            using var cmd = writable.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE djmdSongPlaylist(
                    ID TEXT PRIMARY KEY, ContentID TEXT, rb_local_deleted INTEGER DEFAULT 0);
                CREATE TABLE djmdCue(
                    ID TEXT PRIMARY KEY, ContentID TEXT, rb_local_deleted INTEGER DEFAULT 0);
                INSERT INTO djmdSongPlaylist VALUES ('P1','C1',0);
                INSERT INTO djmdSongPlaylist VALUES ('P2','C1',0);
                INSERT INTO djmdSongPlaylist VALUES ('P3','C2',1);
                INSERT INTO djmdCue VALUES ('Q1','C1',0);
                INSERT INTO djmdCue VALUES ('Q2','C2',0);
                INSERT INTO djmdCue VALUES ('Q3','C2',0);
                """;
            cmd.ExecuteNonQuery();
        }
        var before = SHA256.HashData(File.ReadAllBytes(db));
        var inspected = PhysicalExceptionInvestigator.InspectReadOnly(
            db, EncryptedMutationFixture.Key,
            rows.Where(x => x.ContentId is "C1" or "C2").ToArray());
        var a = inspected.Single(x => x.ContentId == "C1");
        var b = inspected.Single(x => x.ContentId == "C2");
        if (a.PlaylistReferences != "2" || a.CueReferences != "1" ||
            b.PlaylistReferences != "0" || b.CueReferences != "2" ||
            a.MyTagReferences != "2" || b.MyTagReferences != "1" ||
            !a.OtherDjReferences.StartsWith("Not verifiable", StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Read-only exception audit omitted, invented or miscounted DJ references.");
        if (!before.AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(db))))
            throw new InvalidOperationException("Exception investigation changed encrypted master.db fixture.");

        var wrongId = false;
        try
        {
            _ = PhysicalExceptionInvestigator.InspectReadOnly(db,
                EncryptedMutationFixture.Key,
                rows.Where(x => x.ContentId == "C3").ToArray());
        }
        catch (InvalidDataException) { wrongId = true; }
        if (!wrongId)
            throw new InvalidOperationException("Stale/nonexistent rekordbox ContentID was accepted.");
    }

    private static void QualifyLargeLibraryWithVirtualCueOnlyFiles()
    {
        const int normalTracks = 52776;
        const int cueOnlyMainFiles = 670;
        var empty = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var bridge = new List<BridgeTrack>(normalTracks + 3);
        var target = new List<RekordboxTrackSnapshot>(normalTracks + 8);
        for (var i = 0; i < normalTracks; i++)
        {
            var filename = $"Track{i:D5}.mp3";
            bridge.Add(new BridgeTrack(@"Z:\Music\Library\" + filename,
                i < cueOnlyMainFiles ? 1 : 0, empty, empty));
            target.Add(new RekordboxTrackSnapshot("N" + i,
                @"R:\Library\" + filename, []));
        }

        for (var group = 1; group <= 3; group++)
        {
            var name = $"Duplicate{group}.mp3";
            bridge.Add(new BridgeTrack(@"Z:\Music\Library\" + name, 0, empty, empty));
            target.Add(new RekordboxTrackSnapshot($"D{group}a", @"R:\Library\" + name, []));
            target.Add(new RekordboxTrackSnapshot($"D{group}b", @"R:\Library\" + name, []));
        }
        target.Add(new RekordboxTrackSnapshot("M1", @"R:\Library\Absent1.mp3", []));
        target.Add(new RekordboxTrackSnapshot("M2", @"R:\Library\Absent2.mp3", []));

        if (target.Count != 52784)
            throw new InvalidOperationException("Large library test has an incorrect rekordbox track count");

        var cases = PhysicalExceptionInvestigator.Identify(
            bridge, target, new PathAlias(@"Z:\Music", @"R:\"));
        if (cases.Count != 8 ||
            cases.Count(x => x.Reason == "SAME_REKORDBOX_PATH") != 6 ||
            cases.Count(x => x.Reason == "MISSING_FROM_BRIDGE_EXPORT") != 2 ||
            cases.Any(x => x.ContentId.StartsWith("N", StringComparison.Ordinal)) ||
            cases.Where(x => x.Reason == "SAME_REKORDBOX_PATH")
                .GroupBy(x => x.TargetPath, StringComparer.OrdinalIgnoreCase)
                .Any(g => g.Count() != 2))
            throw new InvalidOperationException(
                "Large library exception investigation misclassified duplicates, absent bridge paths, or 670 cue-only main files");

        // The 670 cue-only foobar records reference real file-level rekordbox
        // rows, not imported virtual cue ContentIDs or absent audio files.
        var cueOnlyTarget = target.Take(cueOnlyMainFiles).ToArray();
        var cueOnlySource = bridge.Take(cueOnlyMainFiles).ToArray();
        if (PhysicalExceptionInvestigator.Identify(
                cueOnlySource, cueOnlyTarget, new PathAlias(@"Z:\Music", @"R:\")).Count != 0)
            throw new InvalidOperationException("Virtual cue-only bridge paths were treated as missing rekordbox audio files");
    }
}
