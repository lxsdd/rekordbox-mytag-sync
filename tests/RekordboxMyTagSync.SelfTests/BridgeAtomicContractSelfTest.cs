using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using RekordboxMyTagSync.Core;

public static class BridgeAtomicContractSelfTest
{
    private const string CoreHeader =
        "path\tsubsong\tartist\tartists\ttitle\toriginal_title\tremixed_by\talbum\t" +
        "album_artist\ttrack_number\ttotal_tracks\tdisc_number\ttotal_discs\tdate\t" +
        "genre\tstyle\tbpm\tlabel\tcatalog_number\tduration_seconds\tisrc\tcodec\t" +
        "bitrate\ttag_fingerprint";

    public static void Run(string temp)
    {
        var root = Path.Combine(temp, "BridgeAtomicV3E2E");
        var bridgeRoot = Path.Combine(root, "foobar-bridge");
        var dbPath = Path.Combine(root, "rekordbox", "master.db");
        var songOne = Path.Combine(root, "music", "One.mp3");
        var songTwo = Path.Combine(root, "music", "Two.mp3");

        EncryptedMutationFixture.Create(dbPath, songOne, songTwo);
        WritePublication(bridgeRoot, stateSchema: 3, payloadSchema: 3, songOne, songTwo);
        var candidate = BridgeSourceDiscovery.Inspect(bridgeRoot, selected: true);
        if (!candidate.Safe || !candidate.Selected ||
            candidate.State?.SchemaVersion != 3)
            throw new InvalidOperationException(
                "Complete V3 multivalue snapshot was not safely discoverable.");

        var snapshot = BridgeSourceDiscovery.ReadStable(bridgeRoot);
        if (snapshot.State.ItemCount != 2 || snapshot.Tracks.Count != 2 ||
            !snapshot.Tracks[0].GetFieldValues("GENRE").SequenceEqual(
                new[] { "House", "Techno" }) ||
            !snapshot.Tracks[0].GetFieldValues("DATE").SequenceEqual(
                new[] { "2001-01-01", "2002-02-02" }) ||
            !snapshot.Tracks[0].GetFieldValues("MOOD").SequenceEqual(
                new[] { "Driving" }) ||
            !snapshot.Tracks[1].GetFieldValues("GENRE").SequenceEqual(
                new[] { "Techno" }))
            throw new InvalidOperationException(
                "Atomic V3 bridge read lost foobar Core multivalues.");

        // Full real Preview -> encrypted SQLCipher Apply on a synthetic
        // database, including missing Year MyTag definition creation.
        var policy = new RekordboxDatabaseReadPolicy(
            new HashSet<string>(StringComparer.Ordinal) {
                EncryptedMutationFixture.DbVersion }, RequireRekordboxClosed: false);
        var before = RekordboxSqlCipherDatabase.ReadSnapshot(
            dbPath, EncryptedMutationFixture.Key, policy);
        var preimage = RekordboxRollingBackup.ComputeFileSha256(dbPath);
        var mappings = new[]
        {
            new MappingRule("GENRE", "Genre"),
            new MappingRule("DATE", "Year", TransformKind.YearFromDate)
        };
        var preview = PreviewEngine.Create(new PreviewRequest(
            before.Identity.PreviewIdentity, snapshot.Tracks, mappings,
            before.Tracks, Array.Empty<ManagedAssignment>(),
            MyTagDefinitions: before.MyTagDefinitions));
        if (!preview.IsValid || preview.Counts.Conflicts != 0 ||
            preview.Counts.Unmatched != 0 || preview.Counts.Additions < 3 ||
            preview.MissingDefinitions is null ||
            !preview.MissingDefinitions.Any(x => x.Group == "Year" &&
                x.Value == "2001") ||
            !preview.MissingDefinitions.Any(x => x.Group == "Year" &&
                x.Value == "2002"))
            throw new InvalidOperationException(
                "V3 Core multivalues did not produce safe GENRE/DATE Preview additions.");
        if (preimage != RekordboxRollingBackup.ComputeFileSha256(dbPath))
            throw new InvalidOperationException(
                "Read-only V3 Preview changed the encrypted database.");

        var provenancePath = Path.Combine(root, "owned", "provenance.json");
        var backupRoot = Path.Combine(root, "backups");
        var mappingHash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes("BridgeAtomicV3 mapping"))).ToLowerInvariant();
        var applied = RekordboxMutationExecutor.Apply(
            dbPath, EncryptedMutationFixture.Key, policy, preview,
            snapshot.Tracks, mappings, provenancePath, backupRoot,
            mappingHash, "0.1.0-dev");
        if (applied.ChangeCount < 3 || applied.BackupPackagePath is null ||
            !File.Exists(applied.BackupPackagePath))
            throw new InvalidOperationException(
                "V3 transactional SQLCipher E2E failed to produce a backup and changes.");

        var after = RekordboxSqlCipherDatabase.ReadSnapshot(
            dbPath, EncryptedMutationFixture.Key, policy);
        var a = after.Tracks.Single(x => x.ContentId == "C1");
        var b = after.Tracks.Single(x => x.ContentId == "C2");
        if (!Has(a, "Genre", "House") || !Has(a, "Genre", "Techno") ||
            !Has(a, "Year", "2001") || !Has(a, "Year", "2002") ||
            !Has(a, "Mood", "Euphoric") || !Has(b, "Genre", "Techno"))
            throw new InvalidOperationException(
                "V3 encrypted Apply lost manual MyTags or true multivalue assignments.");
        var owned = ProvenanceStore.Load(provenancePath, after.Identity);
        if (!owned.Assignments.Any(x => x.ContentId == "C1" &&
                x.Value == "Techno") ||
            owned.Assignments.Any(x => x.ContentId == "C2" || x.Group == "Mood"))
            throw new InvalidOperationException(
                "V3 Apply adopted a pre-existing manual tag into provenance.");
        var backup = RekordboxRollingBackup.Inspect(
            applied.BackupPackagePath, before.Identity);
        if (backup.Metadata.DatabaseSha256 != preimage)
            throw new InvalidOperationException(
                "V3 pre-write backup hash differs from the encrypted preimage.");

        var freshPreview = PreviewEngine.Create(new PreviewRequest(
            after.Identity.PreviewIdentity, snapshot.Tracks, mappings,
            after.Tracks, ProvenanceStore.ToManagedAssignments(owned, after),
            MyTagDefinitions: after.MyTagDefinitions));
        if (!freshPreview.IsValid || freshPreview.Counts.Additions != 0 ||
            freshPreview.Counts.Removals != 0 || freshPreview.Counts.Conflicts != 0)
            throw new InvalidOperationException(
                "V3 encrypted update is not idempotent.");

        var currentDbHash = RekordboxRollingBackup.ComputeFileSha256(dbPath);
        var currentBackupHash = RekordboxRollingBackup.ComputeFileSha256(
            applied.BackupPackagePath);
        var repeated = RekordboxMutationExecutor.Apply(
            dbPath, EncryptedMutationFixture.Key, policy, freshPreview,
            snapshot.Tracks, mappings, provenancePath, backupRoot,
            mappingHash, "0.1.0-dev");
        if (repeated.ChangeCount != 0 || repeated.BackupPackagePath is not null ||
            currentDbHash != RekordboxRollingBackup.ComputeFileSha256(dbPath) ||
            currentBackupHash != RekordboxRollingBackup.ComputeFileSha256(
                applied.BackupPackagePath))
            throw new InvalidOperationException(
                "Idempotent V3 repeat modified encrypted DB or rolling backup.");

        // Corrupt publication state/header pairs, incomplete generation,
        // invalid item count, and preserve V1/V2 compatibility.
        foreach (var version in new[] { (State: 3, Payload: 2),
                 (State: 2, Payload: 3), (State: 2, Payload: 1),
                 (State: 1, Payload: 2) })
        {
            var directory = Path.Combine(root, "mismatch-" +
                version.State + "-" + version.Payload);
            WritePublication(directory, version.State, version.Payload,
                songOne, songTwo);
            if (BridgeSourceDiscovery.Inspect(directory).Safe)
                throw new InvalidOperationException(
                    "Source discovery accepted mixed state and payload versions.");
            Reject(() => BridgeSourceDiscovery.ReadStable(directory),
                "V3/legacy mixed publication was accepted.");
        }
        var incomplete = Path.Combine(root, "incomplete");
        WritePublication(incomplete, 3, 3, songOne, songTwo, complete: false);
        Reject(() => BridgeSourceDiscovery.ReadStable(incomplete),
            "Incomplete V3 generation was accepted.");
        var wrongCount = Path.Combine(root, "wrong-count");
        WritePublication(wrongCount, 3, 3, songOne, songTwo, count: 1);
        Reject(() => BridgeSourceDiscovery.ReadStable(wrongCount),
            "Incorrect bridge state item count was accepted.");

        foreach (var schema in new[] { 1, 2 })
        {
            var legacy = Path.Combine(root, "valid-v" + schema);
            WritePublication(legacy, schema, schema, songOne, songTwo);
            var original = BridgeSourceDiscovery.ReadStable(legacy);
            if (original.Tracks.Count != 2 ||
                original.Tracks[0].GetFieldValues("GENRE").Count != 1)
                throw new InvalidOperationException(
                    "V1/V2 scalar compatibility changed.");
        }
    }

    private static bool Has(RekordboxTrackSnapshot track, string group,
        string value) =>
        track.Assignments.Any(x =>
            x.Group.Equals(group, StringComparison.OrdinalIgnoreCase) &&
            x.Value.Equals(value, StringComparison.OrdinalIgnoreCase));

    private static void Reject(Action operation, string explanation)
    {
        var rejected = false;
        try { operation(); }
        catch (InvalidDataException) { rejected = true; }
        if (!rejected) throw new InvalidOperationException(explanation);
    }

    private static void WritePublication(string directory, int stateSchema,
        int payloadSchema, string trackOne, string trackTwo,
        bool complete = true, int count = 2)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, BridgeSourceDiscovery.StateFileName),
            "schema_version\t" + stateSchema +
            "\ngeneration\t1\ncomplete\t" + (complete ? 1 : 0) +
            "\nitem_count\t" + count +
            "\nlast_change_utc\t2026-10-19T00:00:00Z\n",
            new UTF8Encoding(false));

        using var file = File.Create(Path.Combine(
            directory, BridgeSourceDiscovery.SnapshotFileName));
        using var gzip = new GZipStream(file, CompressionLevel.SmallestSize);
        using var writer = new StreamWriter(gzip, new UTF8Encoding(false));
        writer.WriteLine(CoreHeader +
            (payloadSchema >= 2 ? "\textra_metadata_json" : "") +
            (payloadSchema == 3 ? "\tmetadata_vectors_json" : ""));

        for (var index = 0; index < 2; index++)
        {
            var fields = new[]
            {
                index == 0 ? trackOne : trackTwo, "0", "Artist",
                "", "Title", "", "", "Album", "Artist",
                (index + 1).ToString(), "2", "1", "1",
                index == 0 ? "2001-01-01" : "",
                index == 0 ? "House" : "Techno",
                "", "128", "Label", "CAT", "300",
                "ISRC", "MP3", "320", "fixturehash"
            };
            writer.Write(string.Join('\t', fields));
            if (payloadSchema >= 2)
                writer.Write("\t" +
                    (index == 0 ? "{\"MOOD\":[\"Driving\"]}" : "{}"));
            if (payloadSchema == 3)
                writer.Write("\t" + (index == 0
                    ? "[{\"name\":\"GENRE\",\"values\":[\"House\",\"Techno\"]}," +
                      "{\"name\":\"DATE\",\"values\":[\"2001-01-01\",\"2002-02-02\"]}]"
                    : "[{\"name\":\"GENRE\",\"values\":[\"Techno\"]}," +
                      "{\"name\":\"DATE\",\"values\":[]}]"));
            writer.WriteLine();
        }
    }
}
