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
        QualifyDefinitionCreationRestoreAfterPostCommitFailure(temp);
        QualifyRestoreAfterPostCommitFailure(temp);
        QualifyVerifiedRestoreRoundTrip(temp);
        QualifyExternalWholeSetEditorProvenanceDrift(temp);
        QualifyFilteredMultivalueEncryptedApply(temp);
    }

    // Model a second SQLCipher client that replaces a track's whole MyTag set:
    // it tombstones one tool-owned membership and reactivates an unrelated,
    // previously tombstoned manual membership. Our stale preview must never
    // adopt that new link, delete the remaining manual tag or write the DB.
    private static void QualifyExternalWholeSetEditorProvenanceDrift(string temp)
    {
        var root = Path.Combine(temp, "ExternalWholeSetEditor");
        var databasePath = Path.Combine(root, "master.db");
        var trackOne = Path.Combine(temp, "ExternalEditorMusic", "One.mp3");
        var trackTwo = Path.Combine(temp, "ExternalEditorMusic", "Two.mp3");
        EncryptedMutationFixture.Create(databasePath, trackOne, trackTwo);

        var policy = Policy();
        var before = RekordboxSqlCipherDatabase.ReadSnapshot(
            databasePath, EncryptedMutationFixture.Key, policy);
        var provenancePath = Path.Combine(root, "state", "provenance.json");
        var owned = ProvenanceStore.ReplaceAssignments(
            ProvenanceStore.Empty(before.Identity), before.Identity,
            new[] { new OwnedAssignment("C1", "T3", "Mood", "Euphoric") });
        ProvenanceStore.SaveAtomic(provenancePath, owned, before.Identity);
        var bridgeTracks = new[] { Bridge(trackOne, "House") };
        var mappings = new[] { new MappingRule("GENRE", "Genre") };
        var approved = PreviewEngine.Create(new PreviewRequest(
            before.Identity.PreviewIdentity, bridgeTracks, mappings,
            before.Tracks, ProvenanceStore.ToManagedAssignments(owned, before),
            MyTagDefinitions: before.MyTagDefinitions));
        AssertPreview(approved, additions: 0, removals: 1,
            alreadyCorrect: 1, conflicts: 0, unmatched: 0);

        // Simulate rbxport-style external set replacement on a SYNTHETIC
        // encrypted database only; no production DB or music file is opened.
        using (var connection = EncryptedMutationFixture.Open(databasePath))
        {
            ExecuteSql(connection, """
                UPDATE djmdSongMyTag SET rb_local_deleted=1, rb_local_usn=101
                WHERE ContentID='C1' AND MyTagID='T3';
                UPDATE djmdSongMyTag SET rb_local_deleted=0, rb_local_usn=102
                WHERE ContentID='C1' AND MyTagID='T2';
                UPDATE agentRegistry SET int_1=102
                WHERE registry_id='localUpdateCount';
                """);
        }

        var external = RekordboxSqlCipherDatabase.ReadSnapshot(
            databasePath, EncryptedMutationFixture.Key, policy);
        var c1 = external.Tracks.Single(x => x.ContentId == "C1");
        AssertHas(c1, "Genre", "House");
        AssertHas(c1, "Genre", "Techno");
        AssertMissing(c1, "Mood", "Euphoric");

        var rejectedOwnershipDrift = false;
        try
        {
            _ = ProvenanceStore.ToManagedAssignments(owned, external);
        }
        catch (InvalidDataException)
        {
            rejectedOwnershipDrift = true;
        }
        if (!rejectedOwnershipDrift)
            throw new InvalidOperationException(
                "external whole-set replacement silently adopted stale ownership");

        var externalHash = RekordboxRollingBackup.ComputeFileSha256(databasePath);
        var provenanceHash = RekordboxRollingBackup.ComputeFileSha256(provenancePath);
        var backupRoot = Path.Combine(root, "backup");
        var blocked = false;
        try
        {
            _ = RekordboxMutationExecutor.Apply(
                databasePath, EncryptedMutationFixture.Key, policy,
                approved, bridgeTracks, mappings, provenancePath, backupRoot,
                Sha256("external-whole-set-mapping"), "0.1.0-dev");
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
        {
            blocked = true;
        }
        if (!blocked)
            throw new InvalidOperationException(
                "stale preview applied after external whole-set MyTag replacement");
        if (!string.Equals(externalHash,
                RekordboxRollingBackup.ComputeFileSha256(databasePath),
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(provenanceHash,
                RekordboxRollingBackup.ComputeFileSha256(provenancePath),
                StringComparison.OrdinalIgnoreCase) ||
            Directory.Exists(backupRoot))
            throw new InvalidOperationException(
                "blocked external edit unexpectedly wrote database, provenance or backup");

        var after = RekordboxSqlCipherDatabase.ReadSnapshot(
            databasePath, EncryptedMutationFixture.Key, policy);
        var retained = after.Tracks.Single(x => x.ContentId == "C1");
        AssertHas(retained, "Genre", "House");
        AssertHas(retained, "Genre", "Techno");
        AssertMissing(retained, "Mood", "Euphoric");
    }


    // Complete synthetic SQLCipher Preview -> Apply -> rolling backup ->
    // filter change -> stale-preview rejection -> Apply -> idempotent re-Apply.
    private static void QualifyFilteredMultivalueEncryptedApply(string temp)
    {
        var root = Path.Combine(temp, "FilteredMultivalueExecutor");
        var databasePath = Path.Combine(root, "master.db");
        var trackOne = Path.Combine(temp, "FilteredMusic", "One.mp3");
        var trackTwo = Path.Combine(temp, "FilteredMusic", "Two.mp3");
        EncryptedMutationFixture.Create(databasePath, trackOne, trackTwo);
        var policy = Policy();
        var provenancePath = Path.Combine(root, "state", "provenance.json");
        var backupRoot = Path.Combine(root, "backup");
        var sources = new[]
        {
            BridgeWithGenres(trackOne, "House", "Techno"),
            BridgeWithGenres(trackTwo, "Techno", "House")
        };
        var include = new[] { new MappingRule(
            "GENRE", "Genre", FilterPattern: "^House$") };
        var exclude = new[] { include[0] with { FilterExclude = true } };
        var initial = RekordboxSqlCipherDatabase.ReadSnapshot(
            databasePath, EncryptedMutationFixture.Key, policy);
        var approved = PreviewEngine.Create(new PreviewRequest(
            initial.Identity.PreviewIdentity, sources, include, initial.Tracks,
            Array.Empty<ManagedAssignment>(),
            MyTagDefinitions: initial.MyTagDefinitions));
        AssertPreview(approved, additions: 1, removals: 0,
            alreadyCorrect: 1, conflicts: 0, unmatched: 0);

        var initialHash = RekordboxRollingBackup.ComputeFileSha256(databasePath);
        var first = RekordboxMutationExecutor.Apply(
            databasePath, EncryptedMutationFixture.Key, policy, approved,
            sources, include, provenancePath, backupRoot,
            Sha256("filter-include-House"), "0.1.0-dev");
        if (first.ChangeCount != 1 || first.BackupPackagePath is null ||
            !File.Exists(first.BackupPackagePath))
            throw new InvalidOperationException(
                "Filtered initial Apply must add one assignment and create a rolling backup.");
        var afterFirst = RekordboxSqlCipherDatabase.ReadSnapshot(
            databasePath, EncryptedMutationFixture.Key, policy);
        var a1 = afterFirst.Tracks.Single(x => x.ContentId == "C1");
        var b1 = afterFirst.Tracks.Single(x => x.ContentId == "C2");
        AssertHas(a1, "Genre", "House");
        AssertHas(a1, "Mood", "Euphoric");
        AssertMissing(a1, "Genre", "Techno");
        AssertHas(b1, "Genre", "Techno");
        AssertHas(b1, "Genre", "House");
        var ownedFirst = ProvenanceStore.Load(provenancePath, afterFirst.Identity);
        if (ownedFirst.Assignments.Count != 1 ||
            ownedFirst.Assignments[0].ContentId != "C2" ||
            ownedFirst.Assignments[0].MyTagId != "T1")
            throw new InvalidOperationException(
                "Filter Apply adopted a pre-existing manual MyTag.");
        var firstBackup = RekordboxRollingBackup.Inspect(
            first.BackupPackagePath, initial.Identity);
        if (!string.Equals(firstBackup.Metadata.DatabaseSha256, initialHash,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "First backup is not bound to the original encrypted database image.");

        // Changing only filter inclusion must refuse old approval BEFORE
        // touching master.db, provenance or the existing rolling backup.
        var oldApproval = PreviewEngine.Create(new PreviewRequest(
            afterFirst.Identity.PreviewIdentity, sources, include,
            afterFirst.Tracks, ProvenanceStore.ToManagedAssignments(ownedFirst, afterFirst),
            MyTagDefinitions: afterFirst.MyTagDefinitions));
        AssertPreview(oldApproval, additions: 0, removals: 0,
            alreadyCorrect: 2, conflicts: 0, unmatched: 0);
        var dbHash = RekordboxRollingBackup.ComputeFileSha256(databasePath);
        var provenanceHash = RekordboxRollingBackup.ComputeFileSha256(provenancePath);
        var backupHash = RekordboxRollingBackup.ComputeFileSha256(first.BackupPackagePath);
        var rejected = false;
        try
        {
            _ = RekordboxMutationExecutor.Apply(
                databasePath, EncryptedMutationFixture.Key, policy,
                oldApproval, sources, exclude, provenancePath, backupRoot,
                Sha256("filter-exclude-House"), "0.1.0-dev");
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException)
        {
            rejected = true;
        }
        if (!rejected ||
            !string.Equals(dbHash, RekordboxRollingBackup.ComputeFileSha256(databasePath),
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(provenanceHash,
                RekordboxRollingBackup.ComputeFileSha256(provenancePath),
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(backupHash,
                RekordboxRollingBackup.ComputeFileSha256(first.BackupPackagePath),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Stale filter approval mutated DB, provenance or rolling backup.");

        var updatedApproval = PreviewEngine.Create(new PreviewRequest(
            afterFirst.Identity.PreviewIdentity, sources, exclude,
            afterFirst.Tracks, ProvenanceStore.ToManagedAssignments(ownedFirst, afterFirst),
            MyTagDefinitions: afterFirst.MyTagDefinitions));
        if (updatedApproval.FingerprintSha256 == oldApproval.FingerprintSha256)
            throw new InvalidOperationException(
                "Filter toggle did not invalidate the Preview fingerprint.");
        AssertPreview(updatedApproval, additions: 1, removals: 1,
            alreadyCorrect: 1, conflicts: 0, unmatched: 0);
        var second = RekordboxMutationExecutor.Apply(
            databasePath, EncryptedMutationFixture.Key, policy,
            updatedApproval, sources, exclude, provenancePath, backupRoot,
            Sha256("filter-exclude-House"), "0.1.0-dev");
        if (second.ChangeCount != 2 ||
            second.BackupPackagePath != first.BackupPackagePath)
            throw new InvalidOperationException(
                "Filter change failed to make two tool-owned changes with one rolling backup.");
        var secondBackup = RekordboxRollingBackup.Inspect(
            second.BackupPackagePath!, afterFirst.Identity);
        if (!string.Equals(secondBackup.Metadata.DatabaseSha256, dbHash,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Second rolling backup does not match the previous encrypted image.");
        var afterSecond = RekordboxSqlCipherDatabase.ReadSnapshot(
            databasePath, EncryptedMutationFixture.Key, policy);
        var a2 = afterSecond.Tracks.Single(x => x.ContentId == "C1");
        var b2 = afterSecond.Tracks.Single(x => x.ContentId == "C2");
        AssertHas(a2, "Genre", "House"); // original manual tag
        AssertHas(a2, "Mood", "Euphoric"); // unrelated original manual tag
        AssertHas(a2, "Genre", "Techno"); // newly tool-owned
        AssertHas(b2, "Genre", "Techno"); // original manual tag
        AssertMissing(b2, "Genre", "House"); // only old tool-owned link
        var ownedSecond = ProvenanceStore.Load(provenancePath, afterSecond.Identity);
        if (ownedSecond.Assignments.Count != 1 ||
            ownedSecond.Assignments[0].ContentId != "C1" ||
            ownedSecond.Assignments[0].MyTagId != "T2" ||
            ownedSecond.Assignments[0].Value != "Techno")
            throw new InvalidOperationException(
                "Changed filter did not retain minimal tool-owned provenance.");

        var repeatPreview = PreviewEngine.Create(new PreviewRequest(
            afterSecond.Identity.PreviewIdentity, sources, exclude,
            afterSecond.Tracks,
            ProvenanceStore.ToManagedAssignments(ownedSecond, afterSecond),
            MyTagDefinitions: afterSecond.MyTagDefinitions));
        AssertPreview(repeatPreview, additions: 0, removals: 0,
            alreadyCorrect: 2, conflicts: 0, unmatched: 0);
        var dbFinal = RekordboxRollingBackup.ComputeFileSha256(databasePath);
        var provenanceFinal = RekordboxRollingBackup.ComputeFileSha256(provenancePath);
        var backupFinal = RekordboxRollingBackup.ComputeFileSha256(second.BackupPackagePath!);
        var repeat = RekordboxMutationExecutor.Apply(
            databasePath, EncryptedMutationFixture.Key, policy,
            repeatPreview, sources, exclude, provenancePath, backupRoot,
            Sha256("filter-exclude-House"), "0.1.0-dev");
        if (repeat.ChangeCount != 0 || repeat.BackupPackagePath is not null ||
            !string.Equals(dbFinal, RekordboxRollingBackup.ComputeFileSha256(databasePath),
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(provenanceFinal,
                RekordboxRollingBackup.ComputeFileSha256(provenancePath),
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(backupFinal,
                RekordboxRollingBackup.ComputeFileSha256(second.BackupPackagePath!),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Idempotent Apply altered encrypted DB, provenance or rolling backup.");

        var malformed = PreviewEngine.Create(new PreviewRequest(
            afterSecond.Identity.PreviewIdentity, sources,
            new[] { exclude[0] with { FilterPattern = "(" } },
            afterSecond.Tracks,
            ProvenanceStore.ToManagedAssignments(ownedSecond, afterSecond),
            MyTagDefinitions: afterSecond.MyTagDefinitions));
        if (malformed.IsValid || malformed.Counts.Conflicts != 2)
            throw new InvalidOperationException(
                "Malformed regex failed to produce conflicts for both physical ContentIDs.");
    }

    private static void QualifyVerifiedRestoreRoundTrip(string temp)
    {
        var root = Path.Combine(temp, "VerifiedRestoreRoundTrip");
        var databasePath = Path.Combine(root, "master.db");
        var trackOne = Path.Combine(temp, "VerifiedRestoreMusic", "One.mp3");
        var trackTwo = Path.Combine(temp, "VerifiedRestoreMusic", "Two.mp3");
        EncryptedMutationFixture.Create(databasePath, trackOne, trackTwo);
        var policy = Policy();
        var approved = RekordboxSqlCipherDatabase.ReadSnapshot(
            databasePath, EncryptedMutationFixture.Key, policy);
        var originalHash = RekordboxRollingBackup.ComputeFileSha256(databasePath);
        var package = RekordboxRollingBackup.CreateOrReplace(
            Path.Combine(root, "backup"), approved.Identity,
            Sha256("verified-restore-round-trip"), "0.1.0-dev",
            expectedDatabaseSha256: originalHash);

        using (var connection = EncryptedMutationFixture.Open(databasePath))
        {
            ExecuteSql(connection, "UPDATE agentRegistry SET int_1=123 WHERE registry_id='localUpdateCount';");
        }

        var modifiedHash = RekordboxRollingBackup.ComputeFileSha256(databasePath);
        if (string.Equals(originalHash, modifiedHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("verified restore fixture did not change database bytes");

        var rejectedWrongKey = false;
        try
        {
            _ = RekordboxRollingBackup.RestoreVerified(
                package.PackagePath, databasePath, "invalid-test-key",
                policy, approved.Identity);
        }
        catch (Exception)
        {
            rejectedWrongKey = true;
        }
        if (!rejectedWrongKey)
            throw new InvalidOperationException("verified restore accepted an invalid SQLCipher key");
        if (!string.Equals(modifiedHash, RekordboxRollingBackup.ComputeFileSha256(databasePath),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("rejected verified restore changed database bytes");

        // Even a DIFFERENT otherwise valid rolling package must fail when
        // its preimage does not equal the locally authorized SQLCipher backup.
        var staleApprovalRejected = false;
        try
        {
            _ = RekordboxRollingBackup.RestoreVerified(
                package.PackagePath, databasePath, EncryptedMutationFixture.Key,
                policy, approved.Identity, Sha256("foreign-rotated-backup-image"));
        }
        catch (InvalidOperationException)
        {
            staleApprovalRejected = true;
        }
        if (!staleApprovalRejected ||
            !string.Equals(modifiedHash, RekordboxRollingBackup.ComputeFileSha256(databasePath),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Restore allowed a rolling backup other than the human-approved image.");

        _ = RekordboxRollingBackup.RestoreVerified(
            package.PackagePath, databasePath, EncryptedMutationFixture.Key,
            policy, approved.Identity, originalHash);

        var restoredHash = RekordboxRollingBackup.ComputeFileSha256(databasePath);
        if (!string.Equals(originalHash, restoredHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("verified restore did not recover original encrypted bytes");
        using var restored = EncryptedMutationFixture.Open(databasePath);
        if (ScalarLong(restored,
                "SELECT int_1 FROM agentRegistry WHERE registry_id='localUpdateCount';") != 100)
            throw new InvalidOperationException("verified restore did not recover original SQLCipher state");
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
        if (approved.MissingDefinitions?.Count != 1 ||
            approved.MissingDefinitions[0].Group != "Energy" ||
            approved.MissingDefinitions[0].Value != "Peak")
            throw new InvalidOperationException("definition-creation preview did not identify the missing group/value pair");

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

    private static void QualifyDefinitionCreationRestoreAfterPostCommitFailure(string temp)
    {
        var root = Path.Combine(temp, "ExecutorDefinitionRestore");
        var databasePath = Path.Combine(root, "master.db");
        var trackPath = Path.Combine(temp, "DefinitionRestoreMusic", "One.mp3");
        CreateDefinitionCapableEncryptedFixture(databasePath, trackPath);

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
                Sha256("executor-definition-restore-mapping"),
                "0.1.0-dev");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AggregateException)
        {
            failed = true;
        }

        if (!failed)
            throw new InvalidOperationException("definition post-commit provenance failure did not fail the executor");

        var restored = RekordboxSqlCipherDatabase.ReadSnapshot(
            databasePath,
            EncryptedMutationFixture.Key,
            policy);
        if (restored.MyTagDefinitions.Any(x =>
                string.Equals(x.Name, "Energy", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(x.Name, "Peak", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("automatic restore left created MyTag definitions behind");
        var restoredTrack = restored.Tracks.Single(x => x.ContentId == "C1");
        AssertHas(restoredTrack, "Genre", "House");
        AssertMissing(restoredTrack, "Energy", "Peak");

        using var connection = EncryptedMutationFixture.Open(databasePath);
        if (ScalarLong(connection,
                "SELECT int_1 FROM agentRegistry WHERE registry_id='localUpdateCount';") != 100)
            throw new InvalidOperationException("definition restore did not recover pre-write localUpdateCount");
        if (ScalarLong(connection, "SELECT COUNT(*) FROM djmdMyTag;") != 5 ||
            ScalarLong(connection, "SELECT COUNT(*) FROM djmdSongMyTag;") != 1)
            throw new InvalidOperationException("definition restore did not recover exact pre-write row counts");
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

    private static BridgeTrack BridgeWithGenres(string path, params string[] genres) =>
        new(
            path,
            0,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["GENRE"] = genres
            },
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase));

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
