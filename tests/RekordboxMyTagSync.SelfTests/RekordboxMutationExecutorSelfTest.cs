using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using RekordboxMyTagSync.Core;

public static class RekordboxMutationExecutorSelfTest
{
    public static void Run(string temp)
    {
        QualifySuccessfulMutation(temp);
        QualifyDefinitionCreationEndToEnd(temp);
        QualifyRestoreAfterPostCommitFailure(temp);
    }

    private static void QualifySuccessfulMutation(string temp)
    {
        var root = Path.Combine(temp, "ExecutorSuccess");
        var databasePath = Path.Combine(root, "master.db");
        var trackOne = Path.Combine(temp, "ExecutorMusic", "One.mp3");
        var trackTwo = Path.Combine(temp, "ExecutorMusic", "Two.mp3");
        EncryptedMutationFixture.Create(databasePath, trackOne, trackTwo);

        var provenancePath = Path.Combine(root, "state", "provenance.json");
        var backupRoot = Path.Combine(root, "backup");
        var policy = Policy();
        var before = RekordboxSqlCipherDatabase.ReadSnapshot(
            databasePath,
            EncryptedMutationFixture.Key,
            policy);

        var provenance = ProvenanceStore.ReplaceAssignments(
            ProvenanceStore.Empty(before.Identity),
            before.Identity,
            new[] { new OwnedAssignment("C1", "T3", "Mood", "Euphoric") });
        ProvenanceStore.SaveAtomic(provenancePath, provenance, before.Identity);

        var bridgeTracks = new[]
        {
            Bridge(trackOne, "House"),
            Bridge(trackTwo, "House")
        };
        var mappings = new[] { new MappingRule("GENRE", "Genre") };
        var approved = PreviewEngine.Create(new PreviewRequest(
            before.Identity.PreviewIdentity,
            bridgeTracks,
            mappings,
            before.Tracks,
            ProvenanceStore.ToManagedAssignments(provenance, before),
            MyTagDefinitions: before.MyTagDefinitions));

        AssertPreview(
            approved,
            additions: 1,
            removals: 1,
            alreadyCorrect: 1,
            conflicts: 0,
            unmatched: 0);

        var mappingHash = Sha256("executor-mapping-fixture");
        var result = RekordboxMutationExecutor.Apply(
            databasePath,
            EncryptedMutationFixture.Key,
            policy,
            approved,
            bridgeTracks,
            mappings,
            provenancePath,
            backupRoot,
            mappingHash,
            "0.1.0-dev");

        if (result.ChangeCount != 2 ||
            result.FirstLocalUsn != 101 ||
            result.FinalLocalUpdateCount != 102 ||
            string.IsNullOrWhiteSpace(result.BackupPackagePath) ||
            !File.Exists(result.BackupPackagePath))
            throw new InvalidOperationException("executor success result did not report expected mutation/USN/backup state");

        var after = RekordboxSqlCipherDatabase.ReadSnapshot(
            databasePath,
            EncryptedMutationFixture.Key,
            policy);
        var c1 = after.Tracks.Single(x => x.ContentId == "C1");
        var c2 = after.Tracks.Single(x => x.ContentId == "C2");

        AssertHas(c1, "Genre", "House");
        AssertMissing(c1, "Mood", "Euphoric");
        AssertHas(c2, "Genre", "Techno");
        AssertHas(c2, "Genre", "House");

        var nextProvenance = ProvenanceStore.Load(provenancePath, after.Identity);
        if (nextProvenance.Assignments.Count != 1 ||
            nextProvenance.Assignments[0].ContentId != "C2" ||
            nextProvenance.Assignments[0].MyTagId != "T1" ||
            nextProvenance.Assignments[0].Group != "Genre" ||
            nextProvenance.Assignments[0].Value != "House")
            throw new InvalidOperationException("executor provenance did not retain exactly the newly tool-owned assignment");

        var backup = RekordboxRollingBackup.Inspect(result.BackupPackagePath!, before.Identity);
        if (!string.Equals(
                backup.Metadata.MappingHashSha256,
                mappingHash,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("executor backup mapping hash mismatch");

        using var connection = EncryptedMutationFixture.Open(databasePath);
        if (ScalarLong(connection,
                "SELECT int_1 FROM agentRegistry WHERE registry_id='localUpdateCount';") != 102)
            throw new InvalidOperationException("executor did not advance agentRegistry localUpdateCount by mutation count");
        AssertSongLink(connection, "C2", "T1", deleted: 0, localUsn: 101);
        AssertSongLink(connection, "C1", "T3", deleted: 1, localUsn: 102);
    }

    private static void QualifyDefinitionCreationEndToEnd(string temp)
    {
        var root = Path.Combine(temp, "ExecutorDefinitionCreation");
        var databasePath = Path.Combine(root, "master.db");
        var trackPath = Path.Combine(temp, "DefinitionMusic", "One.mp3");
        CreateDefinitionCapableEncryptedFixture(databasePath, trackPath);

        var provenancePath = Path.Combine(root, "state", "provenance.json");
        var backupRoot = Path.Combine(root, "backup");
        var policy = Policy();
        var before = RekordboxSqlCipherDatabase.ReadSnapshot(
            databasePath,
            EncryptedMutationFixture.Key,
            policy);
        var bridgeTracks = new[] { Bridge(trackPath, "Peak") };
        var mappings = new[] { new MappingRule("GENRE", "Energy") };
        var approved = PreviewEngine.Create(new PreviewRequest(
            before.Identity.PreviewIdentity,
            bridgeTracks,
            mappings,
            before.Tracks,
            Array.Empty<ManagedAssignment>(),
            MyTagDefinitions: before.MyTagDefinitions));

        AssertPreview(
            approved,
            additions: 1,
            removals: 0,
            alreadyCorrect: 0,
            conflicts: 0,
            unmatched: 0);
        if (approved.MissingDefinitions?.Count != 2)
            throw new InvalidOperationException("definition-creation preview did not identify group and value");

        var result = RekordboxMutationExecutor.Apply(
            databasePath,
            EncryptedMutationFixture.Key,
            policy,
            approved,
            bridgeTracks,
            mappings,
            provenancePath,
            backupRoot,
            Sha256("executor-definition-creation-mapping"),
            "0.1.0-dev");

        if (result.ChangeCount != 3 ||
            result.FirstLocalUsn != 101 ||
            result.FinalLocalUpdateCount != 103 ||
            string.IsNullOrWhiteSpace(result.BackupPackagePath) ||
            !File.Exists(result.BackupPackagePath))
            throw new InvalidOperationException("definition-creation executor result mismatch");

        var after = RekordboxSqlCipherDatabase.ReadSnapshot(
            databasePath,
            EncryptedMutationFixture.Key,
            policy);
        var track = after.Tracks.Single(x => x.ContentId == "C1");
        AssertHas(track, "Genre", "House");
        AssertHas(track, "Energy", "Peak");

        var energy = after.MyTagDefinitions.Single(x =>
            x.ParentId is null &&
            string.Equals(x.Name, "Energy", StringComparison.OrdinalIgnoreCase));
        var peak = after.MyTagDefinitions.Single(x =>
            string.Equals(x.ParentId, energy.Id, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(x.Name, "Peak", StringComparison.OrdinalIgnoreCase));

        var provenance = ProvenanceStore.Load(provenancePath, after.Identity);
        if (provenance.Assignments.Count != 1 ||
            provenance.Assignments[0].ContentId != "C1" ||
            provenance.Assignments[0].MyTagId != peak.Id ||
            provenance.Assignments[0].Group != "Energy" ||
            provenance.Assignments[0].Value != "Peak")
            throw new InvalidOperationException("definition-created assignment provenance mismatch");

        using (var connection = EncryptedMutationFixture.Open(databasePath))
        {
            if (ScalarLong(connection,
                    "SELECT int_1 FROM agentRegistry WHERE registry_id='localUpdateCount';") != 103)
                throw new InvalidOperationException("definition creation did not advance localUpdateCount by all DB mutations");
            if (ScalarLong(connection,
                    "SELECT rb_local_usn FROM djmdMyTag WHERE ID='" + energy.Id + "';") != 101 ||
                ScalarLong(connection,
                    "SELECT rb_local_usn FROM djmdMyTag WHERE ID='" + peak.Id + "';") != 102)
                throw new InvalidOperationException("definition creation did not allocate sequential rb_local_usn values");
            AssertSongLink(connection, "C1", peak.Id, deleted: 0, localUsn: 103);
        }

        var repeatPreview = PreviewEngine.Create(new PreviewRequest(
            after.Identity.PreviewIdentity,
            bridgeTracks,
            mappings,
            after.Tracks,
            ProvenanceStore.ToManagedAssignments(provenance, after),
            MyTagDefinitions: after.MyTagDefinitions));
        AssertPreview(
            repeatPreview,
            additions: 0,
            removals: 0,
            alreadyCorrect: 1,
            conflicts: 0,
            unmatched: 0);

        var repeat = RekordboxMutationExecutor.Apply(
            databasePath,
            EncryptedMutationFixture.Key,
            policy,
            repeatPreview,
            bridgeTracks,
            mappings,
            provenancePath,
            backupRoot,
            Sha256("executor-definition-creation-mapping"),
            "0.1.0-dev");
        if (repeat.ChangeCount != 0 ||
            repeat.FirstLocalUsn is not null ||
            repeat.FinalLocalUpdateCount is not null ||
            repeat.BackupPackagePath is not null)
            throw new InvalidOperationException("idempotent definition/assignment reapply was not a no-op");
    }

    private static void QualifyRestoreAfterPostCommitFailure(string temp)
    {
        var root = Path.Combine(temp, "ExecutorRestore");
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "master.db");
        var trackOne = Path.Combine(temp, "RestoreMusic", "One.mp3");
        var trackTwo = Path.Combine(temp, "RestoreMusic", "Two.mp3");
        EncryptedMutationFixture.Create(databasePath, trackOne, trackTwo);

        var policy = Policy();
        var before = RekordboxSqlCipherDatabase.ReadSnapshot(
            databasePath,
            EncryptedMutationFixture.Key,
            policy);

        var bridgeTracks = new[] { Bridge(trackTwo, "House") };
        var mappings = new[] { new MappingRule("GENRE", "Genre") };
        var approved = PreviewEngine.Create(new PreviewRequest(
            before.Identity.PreviewIdentity,
            bridgeTracks,
            mappings,
            before.Tracks,
            Array.Empty<ManagedAssignment>(),
            MyTagDefinitions: before.MyTagDefinitions));
        AssertPreview(
            approved,
            additions: 1,
            removals: 0,
            alreadyCorrect: 0,
            conflicts: 0,
            unmatched: 0);

        var blockedParent = Path.Combine(root, "provenance-parent-is-a-file");
        File.WriteAllText(blockedParent, "fixture", new UTF8Encoding(false));
        var impossibleProvenancePath = Path.Combine(blockedParent, "provenance.json");

        var failed = false;
        try
        {
            _ = RekordboxMutationExecutor.Apply(
                databasePath,
                EncryptedMutationFixture.Key,
                policy,
                approved,
                bridgeTracks,
                mappings,
                impossibleProvenancePath,
                Path.Combine(root, "backup"),
                Sha256("executor-restore-mapping-fixture"),
                "0.1.0-dev");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AggregateException)
        {
            failed = true;
        }

        if (!failed)
            throw new InvalidOperationException("post-commit provenance failure did not fail the executor");

        var restored = RekordboxSqlCipherDatabase.ReadSnapshot(
            databasePath,
            EncryptedMutationFixture.Key,
            policy);
        var restoredC2 = restored.Tracks.Single(x => x.ContentId == "C2");
        AssertHas(restoredC2, "Genre", "Techno");
        AssertMissing(restoredC2, "Genre", "House");

        using var connection = EncryptedMutationFixture.Open(databasePath);
        if (ScalarLong(connection,
                "SELECT int_1 FROM agentRegistry WHERE registry_id='localUpdateCount';") != 100)
            throw new InvalidOperationException("automatic restore did not recover pre-write localUpdateCount");
        if (ScalarLong(connection,
                "SELECT COUNT(*) FROM djmdSongMyTag WHERE ContentID='C2' AND MyTagID='T1' AND COALESCE(rb_local_deleted,0)=0;") != 0)
            throw new InvalidOperationException("automatic restore left committed SongMyTag mutation behind");
    }

    private static void CreateDefinitionCapableEncryptedFixture(
        string databasePath,
        string trackPath)
    {
        if (File.Exists(databasePath))
            File.Delete(databasePath);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);

        using var connection = EncryptedMutationFixture.Open(databasePath, SqliteOpenMode.ReadWriteCreate);
        ExecuteSql(connection, """
            CREATE TABLE djmdProperty(
                DBID TEXT PRIMARY KEY, DBVersion TEXT, created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP);
            CREATE TABLE djmdContent(
                ID TEXT PRIMARY KEY, FolderPath TEXT, rb_local_deleted INTEGER DEFAULT 0);
            CREATE TABLE agentRegistry(
                registry_id TEXT PRIMARY KEY, int_1 INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE djmdMyTag(
                ID TEXT PRIMARY KEY, UUID TEXT NOT NULL, Seq INTEGER NOT NULL, Name TEXT NOT NULL,
                Attribute INTEGER NOT NULL, ParentID TEXT,
                rb_data_status INTEGER NOT NULL DEFAULT 0,
                rb_local_data_status INTEGER NOT NULL DEFAULT 0,
                rb_local_deleted INTEGER NOT NULL DEFAULT 0,
                rb_local_synced INTEGER NOT NULL DEFAULT 0,
                usn INTEGER NOT NULL DEFAULT 0, rb_local_usn INTEGER NOT NULL,
                created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP);
            CREATE TABLE djmdSongMyTag(
                ID TEXT PRIMARY KEY, UUID TEXT NOT NULL, MyTagID TEXT NOT NULL, ContentID TEXT NOT NULL,
                TrackNo INTEGER NOT NULL,
                rb_data_status INTEGER NOT NULL DEFAULT 256,
                rb_local_data_status INTEGER NOT NULL DEFAULT 0,
                rb_local_deleted INTEGER NOT NULL DEFAULT 0,
                rb_local_synced INTEGER NOT NULL DEFAULT 0,
                usn INTEGER NOT NULL DEFAULT 0, rb_local_usn INTEGER NOT NULL,
                created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
                updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP);
            """);
        ExecuteSql(connection,
            "INSERT INTO djmdProperty(DBID, DBVersion) VALUES('db-definition-executor-fixture',$version);",
            ("$version", EncryptedMutationFixture.DbVersion));
        ExecuteSql(connection,
            "INSERT INTO djmdContent(ID, FolderPath, rb_local_deleted) VALUES('C1',$path,0);",
            ("$path", trackPath));
        ExecuteSql(connection,
            "INSERT INTO agentRegistry(registry_id, int_1) VALUES('localUpdateCount',100);");

        InsertDefinition(connection, "1", "Genre", null, 1, 10);
        InsertDefinition(connection, "2", "Mood", null, 2, 10);
        InsertDefinition(connection, "3", "House", "1", 1, 20);
        InsertDefinition(connection, "4", "Techno", "1", 2, 20);
        InsertDefinition(connection, "5", "Euphoric", "2", 1, 20);

        ExecuteSql(connection,
            """
            INSERT INTO djmdSongMyTag(
                ID, UUID, MyTagID, ContentID, TrackNo,
                rb_data_status, rb_local_data_status, rb_local_deleted, rb_local_synced,
                usn, rb_local_usn)
            VALUES('1',$uuid,'3','C1',1,256,0,0,0,0,90);
            """,
            ("$uuid", Guid.NewGuid().ToString()));
    }

    private static void InsertDefinition(
        SqliteConnection connection,
        string id,
        string name,
        string? parentId,
        int sequence,
        int attribute) =>
        ExecuteSql(connection,
            """
            INSERT INTO djmdMyTag(
                ID, UUID, Seq, Name, Attribute, ParentID,
                rb_data_status, rb_local_data_status, rb_local_deleted, rb_local_synced,
                usn, rb_local_usn)
            VALUES($id,$uuid,$seq,$name,$attribute,$parent,0,0,0,0,0,1);
            """,
            ("$id", id),
            ("$uuid", Guid.NewGuid().ToString()),
            ("$seq", sequence),
            ("$name", name),
            ("$attribute", attribute),
            ("$parent", parentId));

    private static void ExecuteSql(
        SqliteConnection connection,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static RekordboxDatabaseReadPolicy Policy() =>
        new(
            new HashSet<string>(StringComparer.Ordinal) { EncryptedMutationFixture.DbVersion },
            RequireRekordboxClosed: false);

    private static BridgeTrack Bridge(string path, string genre) =>
        new(
            path,
            0,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["GENRE"] = new[] { genre }
            },
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase));

    private static string Sha256(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static void AssertPreview(
        PreviewResult preview,
        int additions,
        int removals,
        int alreadyCorrect,
        int conflicts,
        int unmatched)
    {
        if (!preview.IsValid ||
            preview.Counts.Additions != additions ||
            preview.Counts.Removals != removals ||
            preview.Counts.AlreadyCorrect != alreadyCorrect ||
            preview.Counts.Conflicts != conflicts ||
            preview.Counts.Unmatched != unmatched)
            throw new InvalidOperationException(
                $"executor preview mismatch: add={preview.Counts.Additions}, remove={preview.Counts.Removals}, " +
                $"correct={preview.Counts.AlreadyCorrect}, conflicts={preview.Counts.Conflicts}, unmatched={preview.Counts.Unmatched}");
    }

    private static void AssertHas(RekordboxTrackSnapshot track, string group, string value)
    {
        if (!track.Assignments.Any(x =>
                string.Equals(x.Group, group, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.Value, value, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"missing expected assignment {track.ContentId} -> {group}/{value}");
    }

    private static void AssertMissing(RekordboxTrackSnapshot track, string group, string value)
    {
        if (track.Assignments.Any(x =>
                string.Equals(x.Group, group, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.Value, value, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"unexpected assignment {track.ContentId} -> {group}/{value}");
    }

    private static void AssertSongLink(
        SqliteConnection connection,
        string contentId,
        string myTagId,
        long deleted,
        long localUsn)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT rb_local_deleted, rb_local_usn
            FROM djmdSongMyTag
            WHERE ContentID=$content AND MyTagID=$tag;
            """;
        command.Parameters.AddWithValue("$content", contentId);
        command.Parameters.AddWithValue("$tag", myTagId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            throw new InvalidOperationException($"SongMyTag link {contentId}/{myTagId} missing");
        if (reader.GetInt64(0) != deleted || reader.GetInt64(1) != localUsn)
            throw new InvalidOperationException($"SongMyTag link {contentId}/{myTagId} state mismatch");
        if (reader.Read())
            throw new InvalidOperationException($"SongMyTag link {contentId}/{myTagId} is duplicated");
    }

    private static long ScalarLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
