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
        IReadOnlyList<PathAlias>? pathAliases = null)
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

        var initialSnapshot = RekordboxSqlCipherDatabase.ReadSnapshot(databasePath, key, policy);
        var initialProvenance = ProvenanceStore.Load(provenancePath, initialSnapshot.Identity);
        var initialPreflight = RekordboxMutationPreflight.Recheck(
            approvedPreview,
            initialSnapshot,
            initialProvenance,
            bridgeTracks,
            mappings,
            pathAliases);

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
            toolVersion);

        // The backup identity check proves the database bytes/metadata still match the approved snapshot.
        // Re-evaluate source, mapping, provenance and database state once more after backup creation and
        // immediately before opening the read-write mutation session.
        var before = RekordboxSqlCipherDatabase.ReadSnapshot(databasePath, key, policy);
        var provenance = ProvenanceStore.Load(provenancePath, before.Identity);
        var preflight = RekordboxMutationPreflight.Recheck(
            approvedPreview,
            before,
            provenance,
            bridgeTracks,
            mappings,
            pathAliases);
        if (!preflight.HasWork)
            throw new InvalidOperationException(
                "Approved mutation set changed after pre-write backup creation; mutation is blocked.");
        if (!SameDatabase(initialSnapshot.Identity, before.Identity))
            throw new InvalidOperationException(
                "rekordbox database identity changed after pre-write backup creation; mutation is blocked.");

        long currentCounter;
        long firstLocalUsn;
        long finalCounter;
        var committed = false;

        try
        {
            using var session = RekordboxMutationSession.Open(databasePath, key);
            var connection = session.Connection;
            var requireTombstone = preflight.Mutations.Any(x => x.Kind == PreviewDetailKind.Remove);
            var profile = RekordboxSongMyTagWriteSemantics.Qualify(
                connection,
                transaction: null,
                requireTombstone: requireTombstone);

            currentCounter = RekordboxUpdateCounter.Read(connection);
            try
            {
                firstLocalUsn = checked(currentCounter + 1);
            }
            catch (OverflowException ex)
            {
                throw new InvalidDataException("agentRegistry localUpdateCount cannot allocate a new rb_local_usn.", ex);
            }

            var transactionResult = RekordboxMutationTransaction.Execute(
                connection,
                transaction =>
                {
                    var changed = RekordboxSongMyTagWriter.Apply(
                        connection,
                        transaction,
                        preflight.Mutations,
                        profile,
                        firstLocalUsn);
                    if (changed != preflight.Mutations.Count)
                        throw new InvalidDataException(
                            "SongMyTag writer changed-count does not match the approved mutation plan.");

                    var next = RekordboxUpdateCounter.Advance(
                        connection,
                        currentCounter,
                        changed,
                        transaction);
                    return (Changed: changed, FinalCounter: next);
                },
                (_, result) =>
                {
                    if (result.Changed != preflight.Mutations.Count)
                        throw new InvalidDataException(
                            "Pre-commit changed-count does not match the approved mutation plan.");
                });

            committed = true;
            finalCounter = transactionResult.FinalCounter;
            RekordboxMutationVerification.VerifySqliteIntegrity(connection);
        }
        catch (Exception mutationError)
        {
            if (!committed)
                throw;

            try
            {
                RekordboxRollingBackup.Restore(backup.PackagePath, before.Identity);
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
                preflight.Mutations);
            ProvenanceStore.SaveAtomic(provenancePath, nextProvenance, after.Identity);

            return new RekordboxMutationExecutionResult(
                preflight.CurrentPreview,
                after.Identity,
                preflight.Mutations.Count,
                firstLocalUsn,
                finalCounter,
                backup.PackagePath);
        }
        catch (Exception verificationError)
        {
            try
            {
                RekordboxRollingBackup.Restore(backup.PackagePath, before.Identity);
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
