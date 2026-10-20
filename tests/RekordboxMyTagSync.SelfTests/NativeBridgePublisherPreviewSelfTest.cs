using System.Security.Cryptography;
using RekordboxMyTagSync.Core;

// Runs only with synthetic, freshly generated output of the ORIGINAL
// foo_dj_library_bridge native C++ Publisher on the same Windows runner.
// The Bridge repository is checked out at an immutable, previously
// qualified public commit. No DJ profile, media or real master.db is opened.
public static class NativeBridgePublisherPreviewSelfTest
{
    public static void Run(string publisherRoot)
    {
        if (string.IsNullOrWhiteSpace(publisherRoot))
            throw new ArgumentException("Missing native Publisher fixture root.");
        var stages = Path.Combine(Path.GetFullPath(publisherRoot), "stages");
        var g2 = Stage(stages, "g2-add", 2, 2);
        var g3 = Stage(stages, "g3-edit", 3, 2);
        var g4 = Stage(stages, "g4-remove", 4, 1);
        var g6 = Stage(stages, "g6-restart", 6, 3);
        var g7 = Stage(stages, "g7-delete", 7, 0);
        var g8 = Stage(stages, "g8-many", 8, 1024);

        var beforeEdit = g2.Tracks.Single(x => x.Path.EndsWith(
            "Track1.flac", StringComparison.OrdinalIgnoreCase));
        var edited = g3.Tracks.Single(x => x.Path == beforeEdit.Path);
        if (beforeEdit.GetFieldValues("TITLE").Single() != "Alpha" ||
            edited.GetFieldValues("TITLE").Single() != "Alpha (updated)" ||
            g4.Tracks.Count != 1 || !g4.Tracks.Any(x => x.Path == edited.Path) ||
            g7.Tracks.Count != 0 ||
            g8.Tracks.Select(x => x.Path + "\n" + x.Subsong)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1024)
            throw new InvalidOperationException(
                "Original native Publisher add/edit/remove/restart/large record semantics changed.");

        var physical = g6.Tracks.Single(x => x.Path.EndsWith(
            "Track3.flac", StringComparison.OrdinalIgnoreCase));
        if (physical.Subsong != 0 ||
            g6.Tracks.Count(x => x.Subsong != 0) != 2 ||
            !physical.GetFieldValues("GENRE").SequenceEqual(new[] { "Trance" }) ||
            !physical.GetFieldValues("MOOD").SequenceEqual(new[] { "Synthetic", "CI" }))
            throw new InvalidOperationException(
                "Actual native v3 Publisher lost physical/subsong or multivalue metadata.");

        var root = Path.Combine(Path.GetTempPath(),
            "mytagsync-native-publisher-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var db = Path.Combine(root, "master.db");
            // This encrypted master.db is CREATED exclusively in an isolated
            // disposable runner directory; these invented paths are not media.
            EncryptedMutationFixture.Create(db, physical.Path,
                @"C:\SyntheticCI\Unmatched.mp3");
            using (var connection = EncryptedMutationFixture.Open(db))
            using (var command = connection.CreateCommand())
            {
                // Existing definitions with fixture-qualified SQLCipher columns:
                // do not invent production djmdMyTag schema or grant write authority.
                command.CommandText = """
                    INSERT INTO djmdMyTag(
                        ID, UUID, Seq, Name, Attribute, ParentID,
                        rb_data_status, rb_local_data_status, rb_local_deleted,
                        rb_local_synced, usn, rb_local_usn, created_at, updated_at)
                    VALUES
                    ('T-NATIVE-1','native-v3-trance',3,'Trance',0,'G1',0,0,0,0,1,1,
                     '2026-10-20 00:00:00.000 +00:00','2026-10-20 00:00:00.000 +00:00'),
                    ('T-NATIVE-2','native-v3-synthetic',2,'Synthetic',0,'G2',0,0,0,0,1,1,
                     '2026-10-20 00:00:00.000 +00:00','2026-10-20 00:00:00.000 +00:00'),
                    ('T-NATIVE-3','native-v3-ci',3,'CI',0,'G2',0,0,0,0,1,1,
                     '2026-10-20 00:00:00.000 +00:00','2026-10-20 00:00:00.000 +00:00');
                    """;
                if (command.ExecuteNonQuery() != 3)
                    throw new InvalidOperationException("Encrypted fixture MyTag setup failed.");
            }

            var unchangedHash = Hash(db);
            var policy = new RekordboxDatabaseReadPolicy(
                new HashSet<string>(StringComparer.Ordinal) {
                    EncryptedMutationFixture.DbVersion
                }, RequireRekordboxClosed: false);
            var target = RekordboxSqlCipherDatabase.ReadSnapshot(
                db, EncryptedMutationFixture.Key, policy);
            var mapping = new[]
            {
                new MappingRule("GENRE", "Genre"),
                new MappingRule("MOOD", "Mood")
            };
            var preview = PreviewEngine.Create(new PreviewRequest(
                target.Identity.PreviewIdentity, g6.Tracks, mapping, target.Tracks,
                Array.Empty<ManagedAssignment>(),
                MyTagDefinitions: target.MyTagDefinitions));

            // Only subsong 0 may resolve to ContentID C1. A nonzero virtual
            // subsong must remain unmatched even with a valid physical path.
            var additions = preview.Details.Where(x =>
                x.Kind == PreviewDetailKind.Add && x.ContentId == "C1"
                && x.Tag is not null).Select(x =>
                    x.Tag!.Group + ":" + x.Tag.Value).ToHashSet(
                        StringComparer.OrdinalIgnoreCase);
            if (!preview.IsValid || preview.Counts.Additions != 3 ||
                preview.Counts.Conflicts != 0 || preview.Counts.Unmatched != 2 ||
                (preview.MissingDefinitions?.Count ?? 0) != 0 ||
                !additions.SetEquals(new[] {
                    "Genre:Trance", "Mood:Synthetic", "Mood:CI"
                }))
                throw new InvalidOperationException(
                    "Native Bridge v3 -> real encrypted SQLCipher Preview lost MyTags, " +
                    "accepted virtual CUE subsongs or changed ContentID scope.");

            var virtualOnly = PreviewEngine.Create(new PreviewRequest(
                target.Identity.PreviewIdentity, g4.Tracks, mapping, target.Tracks,
                Array.Empty<ManagedAssignment>(),
                MyTagDefinitions: target.MyTagDefinitions));
            if (!virtualOnly.IsValid || virtualOnly.Counts.Additions != 0 ||
                virtualOnly.Counts.Unmatched != 1)
                throw new InvalidOperationException(
                    "Original native Publisher virtual-only snapshot authorized writes.");

            // Negative: join two individually valid but DIFFERENT Publisher
            // generations. The production stable snapshot reader must reject
            // the count mismatch before any preview, backup or assignment.
            var corrupt = Path.Combine(root, "mixed-native-publication");
            Directory.CreateDirectory(corrupt);
            File.Copy(Path.Combine(g6.DirectoryPath, BridgeSourceDiscovery.StateFileName),
                Path.Combine(corrupt, BridgeSourceDiscovery.StateFileName));
            File.Copy(Path.Combine(g8.DirectoryPath, BridgeSourceDiscovery.SnapshotFileName),
                Path.Combine(corrupt, BridgeSourceDiscovery.SnapshotFileName));
            var rejected = false;
            try { _ = BridgeSourceDiscovery.ReadStable(corrupt); }
            catch (InvalidDataException) { rejected = true; }
            if (!rejected || Hash(db) != unchangedHash)
                throw new InvalidOperationException(
                    "Mixed native Publisher snapshot was accepted or SQLCipher file changed.");

            if (Hash(db) != unchangedHash)
                throw new InvalidOperationException(
                    "Read-only native-to-SQLCipher Preview changed encrypted master.db.");
            Console.WriteLine(
                "NATIVE_BRIDGE_PUBLISHER_TO_SQLCIPHER_PREVIEW=PASS " +
                "source=original-C++-publisher; v3/add-edit-remove-restart/1024; " +
                "physical=1; virtual-excluded=2; add=3; mixed-publication=REJECT; " +
                "encrypted-masterdb=BYTE_UNCHANGED");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static BridgeSourceSnapshot Stage(
        string stages, string name, long generation, int count)
    {
        var dir = Path.Combine(stages, name);
        var status = BridgeSourceDiscovery.Inspect(dir);
        if (!status.Safe || status.State?.Generation != generation ||
            status.State.ItemCount != count ||
            status.State.SchemaVersion != 3 ||
            status.State.SourceId != "ci-synthetic-source")
            throw new InvalidOperationException(
                "Original native Publisher " + name + " failed production source Inspect.");
        var snapshot = BridgeSourceDiscovery.ReadStable(dir);
        if (snapshot.Tracks.Count != count ||
            snapshot.State.Generation != generation)
            throw new InvalidOperationException(
                "Original native Publisher " + name + " failed production atomic ReadStable.");
        return snapshot;
    }

    private static string Hash(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
