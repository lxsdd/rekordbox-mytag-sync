using Microsoft.Data.Sqlite;
using RekordboxMyTagSync.Core;

public static class RekordboxMutationVerificationSelfTest
{
    public static void Run(string temp)
    {
        var databasePath = Path.Combine(temp, "VerificationLibrary", "master.db");
        var beforeIdentity = new RekordboxDatabaseIdentity(
            "db-verify",
            "6.0",
            Path.GetFullPath(databasePath),
            100,
            new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc),
            "fixture");
        var afterIdentity = beforeIdentity with
        {
            FileLength = 120,
            LastWriteUtc = beforeIdentity.LastWriteUtc.AddSeconds(1)
        };
        var definitions = new RekordboxMyTagDefinition[]
        {
            new("G1", "Mood", null, 0, 0),
            new("V1", "Old", "G1", 0, 0),
            new("V2", "Euphoric", "G1", 1, 0),
            new("V3", "Manual", "G1", 2, 0)
        };
        var beforeTrack = new RekordboxTrackSnapshot(
            "C1",
            Path.Combine(temp, "Music", "Track.mp3"),
            new[]
            {
                new MyTagAssignment("Mood", "Old"),
                new MyTagAssignment("Mood", "Manual")
            });
        var afterTrack = beforeTrack with
        {
            Assignments = new[]
            {
                new MyTagAssignment("Mood", "Euphoric"),
                new MyTagAssignment("Mood", "Manual")
            }
        };
        var before = new RekordboxDatabaseSnapshot(beforeIdentity, new[] { beforeTrack }, definitions);
        var after = new RekordboxDatabaseSnapshot(afterIdentity, new[] { afterTrack }, definitions);
        var provenance = new ProvenanceDocument(
            ProvenanceStore.CurrentSchemaVersion,
            beforeIdentity.DbId,
            beforeIdentity.CanonicalPath,
            new[] { new OwnedAssignment("C1", "V1", "Mood", "Old") });
        var mutations = new RekordboxAssignmentMutation[]
        {
            new(PreviewDetailKind.Add, "C1", "V2", "Mood", "Euphoric"),
            new(PreviewDetailKind.Remove, "C1", "V1", "Mood", "Old")
        };

        var next = RekordboxMutationVerification.VerifyPostimageAndBuildProvenance(
            before,
            after,
            provenance,
            mutations);
        if (next.Assignments.Count != 1 ||
            !string.Equals(next.Assignments[0].ContentId, "C1", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(next.Assignments[0].MyTagId, "V2", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("postimage verification did not rotate provenance from removed to added assignment");

        // A newly inserted root-sentinel group appears with ParentId=null in
        // the normalized snapshot; postimage verification must compare these
        // as the SAME definition, without waiving any other drift checks.
        var rootSentinelCreated = new RekordboxCreatedMyTagDefinition[]
        {
            new("G2", "Energy", "root", 2, 10, 101),
            new("V4", "Peak", "G2", 1, 20, 102)
        };
        var rootSentinelAfter = before with
        {
            Identity = afterIdentity,
            MyTagDefinitions = definitions.Concat(new[]
            {
                new RekordboxMyTagDefinition("G2", "Energy", null, 2, 10),
                new RekordboxMyTagDefinition("V4", "Peak", "G2", 1, 20)
            }).ToArray()
        };
        _ = RekordboxMutationVerification.VerifyPostimageAndBuildProvenance(
            before, rootSentinelAfter, provenance,
            Array.Empty<RekordboxAssignmentMutation>(), rootSentinelCreated);
        var invalidParentRejected = false;
        try
        {
            _ = RekordboxMutationVerification.VerifyPostimageAndBuildProvenance(
                before, rootSentinelAfter, provenance,
                Array.Empty<RekordboxAssignmentMutation>(),
                rootSentinelCreated.Select(x => x.Id == "G2"
                    ? x with { ParentId = "ghost" }
                    : x).ToArray());
        }
        catch (InvalidDataException ex) when (
            ex.Message.Contains("postimage", StringComparison.OrdinalIgnoreCase))
        {
            invalidParentRejected = true;
        }
        if (!invalidParentRejected)
            throw new InvalidOperationException(
                "postimage validation wrongly accepted an unrelated missing MyTag parent.");

        var manualLossBlocked = false;
        try
        {
            var badAfter = after with
            {
                Tracks = new[]
                {
                    afterTrack with
                    {
                        Assignments = new[] { new MyTagAssignment("Mood", "Euphoric") }
                    }
                }
            };
            _ = RekordboxMutationVerification.VerifyPostimageAndBuildProvenance(
                before,
                badAfter,
                provenance,
                mutations);
        }
        catch (InvalidDataException ex) when (ex.Message.Contains("Unexpected MyTag assignment delta", StringComparison.Ordinal))
        {
            manualLossBlocked = true;
        }
        if (!manualLossBlocked)
            throw new InvalidOperationException("loss of manual/unmanaged MyTag was not rejected by postimage validation");

        var definitionDriftBlocked = false;
        try
        {
            var badAfter = after with
            {
                MyTagDefinitions = definitions
                    .Select(x => string.Equals(x.Id, "V2", StringComparison.OrdinalIgnoreCase)
                        ? x with { Name = "Changed" }
                        : x)
                    .ToArray()
            };
            _ = RekordboxMutationVerification.VerifyPostimageAndBuildProvenance(
                before,
                badAfter,
                provenance,
                mutations);
        }
        catch (InvalidDataException ex) when (ex.Message.Contains("definitions changed", StringComparison.OrdinalIgnoreCase))
        {
            definitionDriftBlocked = true;
        }
        if (!definitionDriftBlocked)
            throw new InvalidOperationException("MyTag definition drift was not rejected during assignment-only mutation");

        var unownedRemovalBlocked = false;
        try
        {
            var unowned = new ProvenanceDocument(
                ProvenanceStore.CurrentSchemaVersion,
                beforeIdentity.DbId,
                beforeIdentity.CanonicalPath,
                Array.Empty<OwnedAssignment>());
            _ = RekordboxMutationVerification.VerifyPostimageAndBuildProvenance(
                before,
                after,
                unowned,
                mutations);
        }
        catch (InvalidDataException ex) when (ex.Message.Contains("not tool-owned", StringComparison.OrdinalIgnoreCase))
        {
            unownedRemovalBlocked = true;
        }
        if (!unownedRemovalBlocked)
            throw new InvalidOperationException("unowned removal did not fail provenance coordination");

        RekordboxSqlCipherDatabase.EnsureSqliteInitialized();
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TABLE fixture (id INTEGER PRIMARY KEY, value TEXT NOT NULL); INSERT INTO fixture(value) VALUES ('ok');";
            command.ExecuteNonQuery();
        }
        RekordboxMutationVerification.VerifySqliteIntegrity(connection);
    }
}
