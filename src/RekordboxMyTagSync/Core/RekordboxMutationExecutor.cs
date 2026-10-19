using System.IO;

namespace RekordboxMyTagSync.Core;

internal sealed record RekordboxMutationExecutionResult(
    PreviewResult Preview,
    RekordboxDatabaseIdentity DatabaseIdentity,
    int ChangeCount,
    long? FirstLocalUsn,
    long? FinalLocalUpdateCount,
    string? BackupPackagePath);

internal static class RekordboxMutationExecutor
{
    internal static RekordboxMutationExecutionResult Apply(
        string databasePath,
        string key,
        RekordboxDatabaseReadPolicy policy,
        PreviewResult approvedPreview,
        IReadOnlyList<BridgeTrack> bridgeTracks,
        IReadOnlyList<MappingRule> mappings,
        string provenancePath,
        string backupRoot,
        string mappingHashSha256,
        string toolVersion,
        IReadOnlyList<PathAlias>? pathAliases = null,
        VerifiedMutationAuthority? physicalAuthority = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(approvedPreview);
        ArgumentNullException.ThrowIfNull(bridgeTracks);
        ArgumentNullException.ThrowIfNull(mappings);
        ArgumentException.ThrowIfNullOrWhiteSpace(provenancePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(mappingHashSha256);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolVersion);

        var (initialSnapshot, approvedDatabaseHash) = ReadVerifiedSnapshot(databasePath, key, policy);
        var initialProvenance = ProvenanceStore.Load(provenancePath, initialSnapshot.Identity);
        var initialPreflight = RekordboxMutationPreflight.Recheck(
            approvedPreview,
            initialSnapshot,
            initialProvenance,
            bridgeTracks,
            mappings,
            pathAliases,
            physicalAuthority);

        if (!initialPreflight.HasWork)
        {
            return new RekordboxMutationExecutionResult(
                initialPreflight.CurrentPreview,
                initialSnapshot.Identity,
                0,
                null,
                null,
                null);
        }

        var backup = RekordboxRollingBackup.CreateOrReplace(
            backupRoot,
            initialSnapshot.Identity,
            mappingHashSha256,
            toolVersion,
            expectedDatabaseSha256: approvedDatabaseHash);

        // The backup identity check proves the database bytes/metadata still match the approved snapshot.
        // Re-evaluate source, mapping, provenance and database state once more after backup creation and
        // immediately before opening the read-write mutation session.
        var (before, _) = ReadVerifiedSnapshot(databasePath, key, policy, approvedDatabaseHash);
        var provenance = ProvenanceStore.Load(provenancePath, before.Identity);
        var preflight = RekordboxMutationPreflight.Recheck(
            approvedPreview,
            before,
            provenance,
            bridgeTracks,
            mappings,
            pathAliases,
            physicalAuthority);
        if (!preflight.HasWork)
            throw new InvalidOperationException(
                "Approved mutation set changed after pre-write backup creation; mutation is blocked.");
        if (!SameDatabase(initialSnapshot.Identity, before.Identity))
            throw new InvalidOperationException(
                "rekordbox database identity changed after pre-write backup creation; mutation is blocked.");

        long currentCounter;
        long firstLocalUsn;
        long finalCounter;
        var changeCount = 0;
        IReadOnlyList<RekordboxCreatedMyTagDefinition> createdDefinitions =
            Array.Empty<RekordboxCreatedMyTagDefinition>();
        IReadOnlyList<RekordboxAssignmentMutation> appliedMutations =
            Array.Empty<RekordboxAssignmentMutation>();
        var committed = false;

        try
        {
            // Final native file-ID recheck before opening the mutation
            // session. A source/target replacement after backup must fail.
            // The read-only preview alone never grants write permission.
            if (physicalAuthority is not null)
                _ = physicalAuthority.Reverify(before, bridgeTracks, pathAliases);
            using var session = RekordboxMutationSession.Open(databasePath, key);
            var connection = session.Connection;
            var requireTombstone = preflight.CurrentPreview.Details.Any(
                x => x.Kind == PreviewDetailKind.Remove);
            var assignmentProfile = RekordboxSongMyTagWriteSemantics.Qualify(
                connection,
                transaction: null,
                requireTombstone: requireTombstone);
            var definitionProfile = preflight.RequiresDefinitionCreation
                ? RekordboxMyTagDefinitionWriter.Qualify(connection, transaction: null)
                : null;

            currentCounter = RekordboxUpdateCounter.Read(connection);
            try
            {
                firstLocalUsn = checked(currentCounter + 1);
            }
            catch (OverflowException ex)
            {
                throw new InvalidDataException("agentRegistry localUpdateCount cannot allocate a new rb_local_usn.", ex);
            }

            var transactionResult = RekordboxCombinedMutationWriter.Apply(
                connection,
                before,
                preflight,
                definitionProfile,
                assignmentProfile,
                currentCounter,
                firstLocalUsn);

            committed = true;
            changeCount = transactionResult.TotalChangeCount;
            createdDefinitions = transactionResult.CreatedDefinitions;
            appliedMutations = transactionResult.AssignmentMutations;
            finalCounter = transactionResult.FinalLocalUpdateCount;
            RekordboxMutationVerification.VerifySqliteIntegrity(connection);
        }
        catch (Exception mutationError)
        {
            if (!committed)
                throw;

            try
            {
                RestoreVerifiedPostCommitImage(backup.PackagePath, databasePath, key, policy, before.Identity);
            }
            catch (Exception restoreError)
            {
                throw new AggregateException(
                    "Database mutation failed after commit and automatic backup restore also failed.",
                    mutationError,
                    restoreError);
            }

            throw;
        }

        try
        {
            var after = RekordboxSqlCipherDatabase.ReadSnapshot(databasePath, key, policy);
            var nextProvenance = RekordboxMutationVerification.VerifyPostimageAndBuildProvenance(
                before,
                after,
                provenance,
                appliedMutations,
                createdDefinitions);
            ProvenanceStore.SaveAtomic(provenancePath, nextProvenance, after.Identity);

            return new RekordboxMutationExecutionResult(
                preflight.CurrentPreview,
                after.Identity,
                changeCount,
                firstLocalUsn,
                finalCounter,
                backup.PackagePath);
        }
        catch (Exception verificationError)
        {
            try
            {
                RestoreVerifiedPostCommitImage(backup.PackagePath, databasePath, key, policy, before.Identity);
            }
            catch (Exception restoreError)
            {
                throw new AggregateException(
                    "Postimage/provenance validation failed and automatic backup restore also failed.",
                    verificationError,
                    restoreError);
            }

            throw;
        }
    }

    // A successful mutation changes the target's length/timestamp. The pre-write
    // identity must never be reused as the identity of the post-commit target.
    // Reopen the current SQLCipher image, verify its library identity and bind
    // the exact bytes to the rollback before replacing them.
    private static void RestoreVerifiedPostCommitImage(
        string packagePath,
        string databasePath,
        string key,
        RekordboxDatabaseReadPolicy policy,
        RekordboxDatabaseIdentity approvedIdentity)
    {
        _ = RekordboxRollingBackup.RestoreVerified(
            packagePath, databasePath, key, policy, approvedIdentity);
    }

    private static (RekordboxDatabaseSnapshot Snapshot, string DatabaseSha256) ReadVerifiedSnapshot(
        string databasePath,
        string key,
        RekordboxDatabaseReadPolicy policy,
        string? expectedDatabaseSha256 = null)
    {
        var beforeHash = RekordboxRollingBackup.ComputeFileSha256(databasePath);
        if (expectedDatabaseSha256 is not null &&
            !string.Equals(beforeHash, expectedDatabaseSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "rekordbox database bytes changed since approved snapshot; mutation is blocked.");

        var snapshot = RekordboxSqlCipherDatabase.ReadSnapshot(databasePath, key, policy);
        var afterHash = RekordboxRollingBackup.ComputeFileSha256(databasePath);
        if (!string.Equals(beforeHash, afterHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "rekordbox database bytes changed while reading the mutation snapshot; mutation is blocked.");

        var file = new FileInfo(databasePath);
        if (file.Length != snapshot.Identity.FileLength ||
            file.LastWriteTimeUtc != snapshot.Identity.LastWriteUtc)
            throw new InvalidOperationException(
                "rekordbox database metadata changed while reading the mutation snapshot; mutation is blocked.");

        return (snapshot, afterHash);
    }

    private static bool SameDatabase(
        RekordboxDatabaseIdentity left,
        RekordboxDatabaseIdentity right) =>
        string.Equals(left.DbId, right.DbId, StringComparison.Ordinal) &&
        string.Equals(left.DbVersion, right.DbVersion, StringComparison.Ordinal) &&
        string.Equals(
            Canonical(left.CanonicalPath),
            Canonical(right.CanonicalPath),
            StringComparison.OrdinalIgnoreCase);

    private static string Canonical(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
}
