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
        // These are EXISTING definition rows on a purely synthetic encrypted
        // fixture, using the same proven djmdMyTag columns and usn values as
        // EncryptedMutationFixture.Create. No guessed schema alteration or
        // automatic definition creation is needed for the V3 mapping test.
        using (var connection = EncryptedMutationFixture.Open(dbPath))
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO djmdMyTag(
                    ID, UUID, Seq, Name, Attribute, ParentID,
                    rb_data_status, rb_local_data_status, rb_local_deleted,
                    rb_local_synced, usn, rb_local_usn, created_at, updated_at)
                VALUES
                    ('G3','uuid-G3',3,'Year',0,NULL,0,0,0,0,1,1,
                        '2026-10-19 00:00:00.000 +00:00','2026-10-19 00:00:00.000 +00:00'),
                    ('T4','uuid-T4',1,'2001',0,'G3',0,0,0,0,1,1,
                        '2026-10-19 00:00:00.000 +00:00','2026-10-19 00:00:00.000 +00:00'),
                    ('T5','uuid-T5',2,'2002',0,'G3',0,0,0,0,1,1,
                        '2026-10-19 00:00:00.000 +00:00','2026-10-19 00:00:00.000 +00:00');
                """;
            if (cmd.ExecuteNonQuery() != 3)
                throw new InvalidOperationException(
                    "Synthetic Year MyTag definitions were not initialized.");
        }
        WritePublication(bridgeRoot, stateSchema: 3, payloadSchema: 3, songOne, songTwo);
        // The encrypted SQLCipher Preview/Apply E2E below must use the
        // producer's YEAR-only fallback, not a DATE vector.
        RewriteFirstRowVectors(Path.Combine(
                bridgeRoot, BridgeSourceDiscovery.SnapshotFileName),
            "[{\"name\":\"GENRE\",\"values\":[\"House\",\"Techno\"]},{\"name\":\"YEAR\",\"values\":[\"2001-01-01\",\"2002-02-02\"]}]");
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
        // database with Year definitions explicitly present in the fixture.
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
            preview.Counts.Unmatched != 0 || preview.Counts.Additions != 3 ||
            (preview.MissingDefinitions?.Count ?? 0) != 0)
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

        // Producer synonyms YEAR and PUBLISHER must retain full Core
        // vectors instead of falling back to their scalar DATE/LABEL column.
        var aliases = Path.Combine(root, "positive-core-aliases");
        WritePublication(aliases, 3, 3, songOne, songTwo);
        var aliasFile = Path.Combine(aliases, BridgeSourceDiscovery.SnapshotFileName);
        RewriteFirstRowVectors(aliasFile,
            "[{\"name\":\"YEAR\",\"values\":[\"2001-01-01\",\"2002-02-02\"]}," +
            "{\"name\":\"PUBLISHER\",\"values\":[\"Label\",\"Alternative Label\"]}]");
        var parsedAliases = BridgeSourceDiscovery.ReadStable(aliases);
        if (!parsedAliases.Tracks[0].GetFieldValues("DATE").SequenceEqual(
                new[] { "2001-01-01", "2002-02-02" }) ||
            !parsedAliases.Tracks[0].GetFieldValues("LABEL").SequenceEqual(
                new[] { "Label", "Alternative Label" }) ||
            !MappingEngine.Apply(
                new MappingRule("DATE", "Year", TransformKind.YearFromDate),
                parsedAliases.Tracks[0].GetFieldValues("DATE")).SequenceEqual(
                    new[] { "2001", "2002" }))
            throw new InvalidOperationException(
                "Bridge V3 YEAR/PUBLISHER synonyms lost Core multivalues.");

        // Two foobar synonyms are legitimate when the producer exposes
        // both: its explicit priority resolves them without JSON last-wins.
        var priorityDir0 = Path.Combine(root, "producer-priority-0");
        WritePublication(priorityDir0, 3, 3, songOne, songTwo);
        RewriteFirstRowVectors(Path.Combine(
                priorityDir0, BridgeSourceDiscovery.SnapshotFileName),
            "[{\"name\":\"YEAR\",\"values\":[\"1999\"]},{\"name\":\"DATE\",\"values\":[\"2001\",\"2002\"]}]");
        if (!BridgeSourceDiscovery.ReadStable(priorityDir0).Tracks[0]
            .GetFieldValues("DATE")
            .SequenceEqual(new[] { "2001", "2002" }))
            throw new InvalidOperationException(
                "Bridge V3 producer alias precedence case 0 did not hold.");
        var priorityDir1 = Path.Combine(root, "producer-priority-1");
        WritePublication(priorityDir1, 3, 3, songOne, songTwo);
        RewriteFirstRowVectors(Path.Combine(
                priorityDir1, BridgeSourceDiscovery.SnapshotFileName),
            "[{\"name\":\"TRACK\",\"values\":[\"2\"]},{\"name\":\"TRACKNUMBER\",\"values\":[\"1\"]}]");
        if (!BridgeSourceDiscovery.ReadStable(priorityDir1).Tracks[0]
            .GetFieldValues("track_number")
            .SequenceEqual(new[] { "1" }))
            throw new InvalidOperationException(
                "Bridge V3 producer alias precedence case 1 did not hold.");
        var priorityDir2 = Path.Combine(root, "producer-priority-2");
        WritePublication(priorityDir2, 3, 3, songOne, songTwo);
        RewriteFirstRowVectors(Path.Combine(
                priorityDir2, BridgeSourceDiscovery.SnapshotFileName),
            "[{\"name\":\"PUBLISHER\",\"values\":[\"B\"]},{\"name\":\"LABEL\",\"values\":[\"A\",\"C\"]}]");
        if (!BridgeSourceDiscovery.ReadStable(priorityDir2).Tracks[0]
            .GetFieldValues("LABEL")
            .SequenceEqual(new[] { "A", "C" }))
            throw new InvalidOperationException(
                "Bridge V3 producer alias precedence case 2 did not hold.");
        var priorityDir3 = Path.Combine(root, "producer-priority-3");
        WritePublication(priorityDir3, 3, 3, songOne, songTwo);
        RewriteFirstRowVectors(Path.Combine(
                priorityDir3, BridgeSourceDiscovery.SnapshotFileName),
            "[{\"name\":\"CATALOG\",\"values\":[\"FALLBACK\"]},{\"name\":\"CATALOGNUMBER\",\"values\":[\"PRIMARY\"]}]");
        if (!BridgeSourceDiscovery.ReadStable(priorityDir3).Tracks[0]
            .GetFieldValues("catalog_number")
            .SequenceEqual(new[] { "PRIMARY" }))
            throw new InvalidOperationException(
                "Bridge V3 producer alias precedence case 3 did not hold.");
        var priorityDir4 = Path.Combine(root, "producer-priority-4");
        WritePublication(priorityDir4, 3, 3, songOne, songTwo);
        RewriteFirstRowVectors(Path.Combine(
                priorityDir4, BridgeSourceDiscovery.SnapshotFileName),
            "[{\"name\":\"DATE\",\"values\":[]},{\"name\":\"YEAR\",\"values\":[\"2001\",\"2002\"]}]");
        if (!BridgeSourceDiscovery.ReadStable(priorityDir4).Tracks[0]
            .GetFieldValues("DATE")
            .SequenceEqual(new[] { "2001", "2002" }))
            throw new InvalidOperationException(
                "Bridge V3 producer alias precedence case 4 did not hold.");

        // Ambiguous names differing only by case still fail closed, as do
        // non-string vector elements, malformed JSON and atomicity failures.
        var duplicateNamesDir = Path.Combine(root, "duplicate-case-fields");
        WritePublication(duplicateNamesDir, 3, 3, songOne, songTwo);
        RewriteFirstRowVectors(Path.Combine(
                duplicateNamesDir, BridgeSourceDiscovery.SnapshotFileName),
            "[{\"name\":\"DATE\",\"values\":[\"2001\"]},{\"name\":\"date\",\"values\":[\"2002\"]}]");
        Reject(() => BridgeSourceDiscovery.ReadStable(duplicateNamesDir),
            "Case-insensitive duplicate vector names were accepted.");

        // foobar can legitimately contain user-defined fields named
        // CODEC, BITRATE and DURATION_SECONDS. They may occur in vectors
        // but must NOT override technical info/get_length columns.
        var spoof = Path.Combine(root, "technical-field-spoof");
        WritePublication(spoof, 3, 3, songOne, songTwo);
        RewriteFirstRowVectors(Path.Combine(
                spoof, BridgeSourceDiscovery.SnapshotFileName),
            "[{\"name\":\"CODEC\",\"values\":[\"FAKE\"]},{\"name\":\"BITRATE\",\"values\":[\"1\"]},{\"name\":\"DURATION_SECONDS\",\"values\":[\"999\"]},{\"name\":\"GENRE\",\"values\":[\"House\",\"Techno\"]}]");
        var technicalSnapshot = BridgeSourceDiscovery.ReadStable(spoof);
        var technicalTrack = technicalSnapshot.Tracks[0];
        if (!technicalTrack.GetFieldValues("codec").SequenceEqual(new[] { "MP3" }) ||
            !technicalTrack.GetFieldValues("bitrate").SequenceEqual(new[] { "320" }) ||
            !technicalTrack.GetFieldValues("duration_seconds").SequenceEqual(new[] { "300" }) ||
            !technicalTrack.GetFieldValues("genre").SequenceEqual(
                new[] { "House", "Techno" }))
            throw new InvalidOperationException(
                "User-supplied Bridge V3 metadata spoofed technical file properties.");

        var zeroRows = Path.Combine(root, "inflated-row-count");
        WritePublication(zeroRows, 3, 3, songOne, songTwo, count: 0);
        Reject(() => BridgeSourceDiscovery.ReadStable(zeroRows),
            "Compressed snapshot was allowed to exceed advertised row count.");

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

    // Produce fully valid V3 gzip records while replacing only one original
    // foobar metadata_vectors_json field. No production media is involved.
    private static void RewriteFirstRowVectors(string gzipPath, string vectors)
    {
        string[] records;
        using (var file = File.OpenRead(gzipPath))
        using (var zip = new GZipStream(file, CompressionMode.Decompress))
        using (var reader = new StreamReader(zip))
            records = reader.ReadToEnd().Split('\n')
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x.TrimEnd('\r')).ToArray();
        if (records.Length != 3)
            throw new InvalidOperationException("V3 synthetic rewrite expected two rows.");
        var row = records[1].Split('\t');
        if (row.Length != 26) throw new InvalidOperationException("V3 synthetic row width.");
        row[25] = vectors;
        records[1] = string.Join('\t', row);
        using var output = File.Create(gzipPath);
        using var zipped = new GZipStream(output, CompressionLevel.SmallestSize);
        using var writer = new StreamWriter(zipped, new UTF8Encoding(false));
        foreach (var record in records) writer.WriteLine(record);
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
