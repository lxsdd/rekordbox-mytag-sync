using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace RekordboxMyTagSync.Core;

public static class VerifiedMutationAuthoritySelfTest
{
    public static void Run(string temp)
    {
        if (!OperatingSystem.IsWindows()) return;
        QualifySameMetadataEncryptedDatabaseDrift(temp);

        var root = Path.Combine(temp, "VerifiedApplyE2E");
        var sourceRoot = Path.Combine(root, "foobar");
        var targetRoot = Path.Combine(root, "rekordbox-files");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(targetRoot);
        var source1 = Path.Combine(sourceRoot, "One.mp3");
        var source2 = Path.Combine(sourceRoot, "Two.mp3");
        var dest1 = Path.Combine(targetRoot, "One.mp3");
        var dest2 = Path.Combine(targetRoot, "Two.mp3");
        File.WriteAllText(source1, "one-test-bytes");
        File.WriteAllText(source2, "two-test-bytes");
        Link(dest1, source1);
        File.Copy(source2, dest2); // same bytes, NOT same FileIndex

        var db = Path.Combine(root, "master.db");
        EncryptedMutationFixture.Create(db, dest1, dest2);
        var policy = new RekordboxDatabaseReadPolicy(
            new HashSet<string>(StringComparer.Ordinal) { EncryptedMutationFixture.DbVersion },
            RequireRekordboxClosed: false);
        var snapshot = RekordboxSqlCipherDatabase.ReadSnapshot(
            db, EncryptedMutationFixture.Key, policy);
        var alias = new[] { new PathAlias(sourceRoot, targetRoot) };
        var firstFields = new Dictionary<string, IReadOnlyList<string>>(
            StringComparer.OrdinalIgnoreCase) { ["GENRE"] = new[] { "Techno" } };
        var secondFields = new Dictionary<string, IReadOnlyList<string>>(
            StringComparer.OrdinalIgnoreCase) { ["GENRE"] = new[] { "House" } };
        var empty = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var bridge = new[]
        {
            new BridgeTrack(source1, 0, firstFields, empty),
            new BridgeTrack(source1, 1, secondFields, empty),
            new BridgeTrack(source2, 0, secondFields, empty)
        };
        var mapping = new[] { new MappingRule("GENRE", "Genre") };
        var evidence = PhysicalFileIdentityVerifier.Verify(
            bridge, snapshot.Tracks, alias);
        if (evidence.SamePhysicalFiles != 1 || evidence.DistinctPhysicalFiles != 1 ||
            evidence.ExcludedSubsongs != 1)
            throw new InvalidOperationException(
                "Synthetic main-file source was not separated from copied/virtual tracks.");

        var scope = VerifiedPreviewScope.Build(
            bridge, snapshot.Tracks, alias, evidence.VerifiedPairs!);
        var authority = VerifiedMutationAuthority.CaptureReadOnly(
            snapshot, bridge, alias, evidence);
        if (authority.AuthorizedContentIds != 1 || scope.Targets.Single().ContentId != "C1" ||
            scope.Sources.Single().Subsong != 0)
            throw new InvalidOperationException(
                "Physical authority included an unverified or virtual ContentID.");
        var approved = PreviewEngine.Create(new PreviewRequest(
            snapshot.Identity.PreviewIdentity, scope.Sources, mapping,
            scope.Targets, Array.Empty<ManagedAssignment>(), alias,
            snapshot.MyTagDefinitions));
        if (!approved.IsValid || approved.Counts.Additions != 1 ||
            approved.Details.Single(x => x.Kind == PreviewDetailKind.Add).ContentId != "C1")
            throw new InvalidOperationException("Scoped preview did not isolate the eligible ContentID.");

        var provenance = Path.Combine(root, "state", "owned.json");
        var backup = Path.Combine(root, "backups");

        // An attacker/bug handing the entire unverified library to this
        // authority cannot cause mutations of copied/virtual records.
        var unsafeFull = PreviewEngine.Create(new PreviewRequest(
            snapshot.Identity.PreviewIdentity, bridge, mapping,
            snapshot.Tracks, Array.Empty<ManagedAssignment>(), alias,
            snapshot.MyTagDefinitions));
        var dbBefore = RekordboxRollingBackup.ComputeFileSha256(db);
        var rejectedFull = false;
        try
        {
            _ = RekordboxMutationExecutor.Apply(
                db, EncryptedMutationFixture.Key, policy,
                unsafeFull, bridge, mapping, provenance, backup,
                Hash("unverified-scope"), "0.1.0-dev", alias, authority);
        }
        catch (InvalidOperationException) { rejectedFull = true; }
        if (!rejectedFull || Directory.Exists(backup) ||
            dbBefore != RekordboxRollingBackup.ComputeFileSha256(db))
            throw new InvalidOperationException(
                "Unscoped preview bypassed physical authority before first backup.");

        // Native file ID change while the preview is cached must reject,
        // even if bytes, track path and MyTag metadata remain identical.
        File.Delete(dest1);
        File.Copy(source1, dest1);
        var changedFileRejected = false;
        try
        {
            _ = RekordboxMutationExecutor.Apply(
                db, EncryptedMutationFixture.Key, policy,
                approved, bridge, mapping, provenance, backup,
                Hash("replaced-physical-file"), "0.1.0-dev", alias, authority);
        }
        catch (InvalidOperationException) { changedFileRejected = true; }
        if (!changedFileRejected || Directory.Exists(backup) ||
            dbBefore != RekordboxRollingBackup.ComputeFileSha256(db))
            throw new InvalidOperationException(
                "Same-byte copied target bypassed native FileIndex recheck.");

        File.Delete(dest1);
        Link(dest1, source1);
        var result = RekordboxMutationExecutor.Apply(
            db, EncryptedMutationFixture.Key, policy,
            approved, bridge, mapping, provenance, backup,
            Hash("authorized-scope"), "0.1.0-dev", alias, authority);
        if (result.ChangeCount != 1 || result.BackupPackagePath is null ||
            !File.Exists(result.BackupPackagePath))
            throw new InvalidOperationException(
                "Verified ContentID Apply did not commit exactly one authorized link.");

        var after = RekordboxSqlCipherDatabase.ReadSnapshot(
            db, EncryptedMutationFixture.Key, policy);
        var c1 = after.Tracks.Single(x => x.ContentId == "C1");
        var c2 = after.Tracks.Single(x => x.ContentId == "C2");
        if (!c1.Assignments.Any(x => x.Group == "Genre" && x.Value == "Techno") ||
            !c1.Assignments.Any(x => x.Group == "Genre" && x.Value == "House") ||
            !c1.Assignments.Any(x => x.Group == "Mood" && x.Value == "Euphoric") ||
            c2.Assignments.Any(x => x.Group == "Genre" && x.Value == "House"))
            throw new InvalidOperationException(
                "Verified Apply changed an excluded/manual ContentID or lost manual MyTags.");
        var owned = ProvenanceStore.Load(provenance, after.Identity);
        if (owned.Assignments.Count != 1 ||
            owned.Assignments.Single().ContentId != "C1" ||
            owned.Assignments.Single().Value != "Techno")
            throw new InvalidOperationException(
                "Physical Apply did not retain minimal tool-owned provenance.");
        var saved = RekordboxRollingBackup.Inspect(
            result.BackupPackagePath, snapshot.Identity);
        if (!string.Equals(saved.Metadata.DatabaseSha256, dbBefore,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Verified Apply backup did not capture original encrypted preimage.");

        // No-op and previous approval both need a freshly qualified DB
        // snapshot. A fresh authority can approve an idempotent reapply.
        var proof = PhysicalFileIdentityVerifier.Verify(
            bridge, after.Tracks, alias);
        var repeatScope = VerifiedPreviewScope.Build(
            bridge, after.Tracks, alias, proof.VerifiedPairs!);
        var repeatAuthority = VerifiedMutationAuthority.CaptureReadOnly(
            after, bridge, alias, proof);
        var repeatPreview = PreviewEngine.Create(new PreviewRequest(
            after.Identity.PreviewIdentity,
            repeatScope.Sources, mapping, repeatScope.Targets,
            ProvenanceStore.ToManagedAssignments(owned, after), alias,
            after.MyTagDefinitions));
        var finalDbHash = RekordboxRollingBackup.ComputeFileSha256(db);
        var finalBackupHash = RekordboxRollingBackup.ComputeFileSha256(
            result.BackupPackagePath);
        var again = RekordboxMutationExecutor.Apply(
            db, EncryptedMutationFixture.Key, policy,
            repeatPreview, bridge, mapping, provenance, backup,
            Hash("authorized-scope"), "0.1.0-dev", alias, repeatAuthority);
        if (again.ChangeCount != 0 || again.BackupPackagePath is not null ||
            finalDbHash != RekordboxRollingBackup.ComputeFileSha256(db) ||
            finalBackupHash != RekordboxRollingBackup.ComputeFileSha256(result.BackupPackagePath))
            throw new InvalidOperationException(
                "Verified scope idempotent repeat wrote SQLCipher database or rolling backup.");

        var mappingChanged = new[] { mapping[0] with { Prefix = "Changed:" } };
        var invalidated = false;
        try
        {
            _ = RekordboxMutationExecutor.Apply(
                db, EncryptedMutationFixture.Key, policy,
                repeatPreview, bridge, mappingChanged, provenance, backup,
                Hash("changed-mapping"), "0.1.0-dev", alias, repeatAuthority);
        }
        catch (InvalidOperationException) { invalidated = true; }
        if (!invalidated || finalDbHash != RekordboxRollingBackup.ComputeFileSha256(db))
            throw new InvalidOperationException(
                "Changed mapping did not invalidate physically scoped approval.");
    }


    // Simulate a concurrent SQLCipher editor that changes the database while
    // retaining DBID, DBVersion, file length and exact LastWriteTimeUtc.
    // Snapshot-only checks would not detect this; the preview SHA must.
    private static void QualifySameMetadataEncryptedDatabaseDrift(string temp)
    {
        var root = Path.Combine(temp, "EncryptedSameMetadataDrift");
        var sourceRoot = Path.Combine(root, "foobar");
        var targetRoot = Path.Combine(root, "rekordbox");
        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(targetRoot);
        var sourceFile = Path.Combine(sourceRoot, "One.mp3");
        var targetFile = Path.Combine(targetRoot, "One.mp3");
        var anotherTarget = Path.Combine(targetRoot, "Two.mp3");
        File.WriteAllText(sourceFile, "physical-matching-synthetic-audio");
        Link(targetFile, sourceFile);
        var databasePath = Path.Combine(root, "master.db");
        EncryptedMutationFixture.Create(databasePath, targetFile, anotherTarget);
        var policy = new RekordboxDatabaseReadPolicy(
            new HashSet<string>(StringComparer.Ordinal) { EncryptedMutationFixture.DbVersion },
            RequireRekordboxClosed: false);
        var before = RekordboxSqlCipherDatabase.ReadSnapshot(
            databasePath, EncryptedMutationFixture.Key, policy);
        var bridge = new[] { new BridgeTrack(
            sourceFile, 0,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["GENRE"] = new[] { "Techno" }
            },
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)) };
        var aliases = new[] { new PathAlias(sourceRoot, targetRoot) };
        var proof = PhysicalFileIdentityVerifier.Verify(
            bridge, before.Tracks, aliases);
        var authority = VerifiedMutationAuthority.CaptureReadOnly(
            before, bridge, aliases, proof);
        var scope = VerifiedPreviewScope.Build(
            bridge, before.Tracks, aliases, proof.VerifiedPairs!);
        var mapping = new[] { new MappingRule("GENRE", "Genre") };
        var preview = PreviewEngine.Create(new PreviewRequest(
            before.Identity.PreviewIdentity, scope.Sources, mapping,
            scope.Targets, Array.Empty<ManagedAssignment>(), aliases,
            before.MyTagDefinitions));
        if (!preview.IsValid || preview.Counts.Additions != 1)
            throw new InvalidOperationException(
                "Encrypted same-metadata drift fixture did not create an actionable preview.");
        var file = new FileInfo(databasePath);
        var originalLength = file.Length;
        var originalTimestamp = file.LastWriteTimeUtc;
        using (var connection = EncryptedMutationFixture.Open(databasePath))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE agentRegistry
                SET int_1=110 WHERE registry_id='localUpdateCount';
                """;
            if (command.ExecuteNonQuery() != 1)
                throw new InvalidOperationException(
                    "Synthetic SQLCipher concurrent editor did not change one update counter.");
        }
        File.SetLastWriteTimeUtc(databasePath, originalTimestamp);
        file.Refresh();
        if (file.Length != originalLength || file.LastWriteTimeUtc != originalTimestamp)
            throw new InvalidOperationException(
                "Same-length/same-timestamp adversarial fixture setup failed.");
        var after = RekordboxSqlCipherDatabase.ReadSnapshot(
            databasePath, EncryptedMutationFixture.Key, policy);
        if (before.Identity.PreviewIdentity != after.Identity.PreviewIdentity)
            throw new InvalidOperationException(
                "Concurrent fixture changed visible database metadata unexpectedly.");
        var changedSha = RekordboxRollingBackup.ComputeFileSha256(databasePath);
        if (string.Equals(changedSha, authority.ApprovedDatabaseSha256,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Concurrent encrypted fixture did not actually change bytes.");

        var provenancePath = Path.Combine(root, "state", "owned.json");
        var backupRoot = Path.Combine(root, "backup");
        var blocked = false;
        try
        {
            _ = RekordboxMutationExecutor.Apply(
                databasePath, EncryptedMutationFixture.Key, policy,
                preview, bridge, mapping, provenancePath, backupRoot,
                Hash("unchanged-mapping"), "0.1.0-dev", aliases, authority);
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("SHA-256", StringComparison.OrdinalIgnoreCase))
        {
            blocked = true;
        }
        if (!blocked || Directory.Exists(backupRoot) ||
            File.Exists(provenancePath) ||
            changedSha != RekordboxRollingBackup.ComputeFileSha256(databasePath))
            throw new InvalidOperationException(
                "Same-metadata SQLCipher drift was not blocked BEFORE provenance, backup or mutation.");
    }

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static void Link(string destination, string source)
    {
        if (!CreateHardLink(destination, source, IntPtr.Zero))
            throw new IOException("Synthetic hardlink failed: " + Marshal.GetLastWin32Error());
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW",
        SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(
        string newFileName, string existingFileName, IntPtr securityAttributes);
}
