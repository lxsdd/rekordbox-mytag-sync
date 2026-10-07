using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using RekordboxMyTagSync.Core;

public static class RekordboxMutationExecutorSelfTest
{
    public static void Run(string temp)
    {
        QualifySuccessfulMutation(temp);
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
            ProvenanceStore.ToManagedAssignments(provenance, before)));

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
            Array.Empty<ManagedAssignment>()));
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
