using System.IO;

namespace RekordboxMyTagSync.Core;

/// <summary>
/// Binds a READ-ONLY approved preview to the exact physically verified
/// source-file/ContentID set. A cached report is not trusted for mutation:
/// the executor reruns native VolumeSerial/FileIndex verification before
/// preflight and again immediately before its read-write session.
/// </summary>
internal sealed class VerifiedMutationAuthority
{
    private readonly PathAlias[] _aliases;
    private readonly string[] _approvedPairs;
    private readonly string _identity;
    private readonly string _approvedDatabaseSha256;

    private VerifiedMutationAuthority(
        string identity, PathAlias[] aliases, string[] approvedPairs,
        string approvedDatabaseSha256)
    {
        _identity = identity;
        _aliases = aliases;
        _approvedPairs = approvedPairs;
        _approvedDatabaseSha256 = approvedDatabaseSha256;
    }

    internal IReadOnlyList<PathAlias> Aliases => Array.AsReadOnly(_aliases);
    internal int AuthorizedContentIds => _approvedPairs.Length;
    internal string ApprovedDatabaseSha256 => _approvedDatabaseSha256;

    internal void RequireApprovedDatabaseHash(string freshHash)
    {
        if (!string.Equals(_approvedDatabaseSha256, freshHash,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Encrypted master.db SHA-256 changed since the approved physical preview, " +
                "even though its DBID, schema, size or timestamp may appear unchanged.");
    }

    internal static VerifiedMutationAuthority CaptureReadOnly(
        RekordboxDatabaseSnapshot database,
        IReadOnlyList<BridgeTrack> bridge,
        IReadOnlyList<PathAlias> aliases,
        PhysicalIdentityReport evidence)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(aliases);
        ArgumentNullException.ThrowIfNull(evidence);
        if (aliases.Count == 0 || evidence.SamePhysicalFiles == 0 ||
            evidence.VerifiedPairs is null ||
            evidence.VerifiedPairs.Count != evidence.SamePhysicalFiles)
            throw new InvalidDataException(
                "Physical preview has no complete verified ContentID evidence.");
        var roots = aliases.Select(x => new PathAlias(
            WindowsPathMatcher.Normalize(x.SourceRoot),
            WindowsPathMatcher.Normalize(x.TargetRoot))).ToArray();
        var scope = VerifiedPreviewScope.Build(
            bridge, database.Tracks, roots, evidence.VerifiedPairs);
        if (scope.Targets.Count != evidence.SamePhysicalFiles)
            throw new InvalidDataException(
                "Native verified pair count does not match the preview scope.");
        // Cryptographically bind this approval to the exact encrypted
        // database image, not just its DBID, length and LastWriteTimeUtc.
        // The Apply executor already hashes the file before backup; compare
        // against that existing pass instead of adding repeated 52k-track
        // database reads during every native FileIndex recheck.
        var approvedFile = new FileInfo(database.Identity.CanonicalPath);
        if (!approvedFile.Exists ||
            approvedFile.Length != database.Identity.FileLength ||
            approvedFile.LastWriteTimeUtc != database.Identity.LastWriteUtc)
            throw new InvalidOperationException(
                "Encrypted database changed before physical preview approval.");
        var approvedHash = RekordboxRollingBackup.ComputeFileSha256(
            database.Identity.CanonicalPath);
        approvedFile.Refresh();
        if (!approvedFile.Exists ||
            approvedFile.Length != database.Identity.FileLength ||
            approvedFile.LastWriteTimeUtc != database.Identity.LastWriteUtc)
            throw new InvalidOperationException(
                "Encrypted database changed during physical preview SHA-256 capture.");
        return new VerifiedMutationAuthority(
            database.Identity.PreviewIdentity, roots, PairKeys(scope), approvedHash);
    }

    internal VerifiedPreviewScope Reverify(
        RekordboxDatabaseSnapshot freshDatabase,
        IReadOnlyList<BridgeTrack> freshBridge,
        IReadOnlyList<PathAlias>? requestedAliases)
    {
        ArgumentNullException.ThrowIfNull(freshDatabase);
        ArgumentNullException.ThrowIfNull(freshBridge);
        if (!string.Equals(_identity, freshDatabase.Identity.PreviewIdentity,
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Database identity/metadata changed since the physically approved preview.");
        if (requestedAliases is null ||
            !AliasKeys(_aliases).SequenceEqual(AliasKeys(requestedAliases),
                StringComparer.Ordinal))
            throw new InvalidOperationException(
                "Path aliases changed since the physically approved preview.");

        // Read-only native handles: never rely on cached names, paths, file
        // hashes or previously sampled evidence to authorize an Apply.
        var proof = PhysicalFileIdentityVerifier.Verify(
            freshBridge, freshDatabase.Tracks, _aliases);
        if (proof.VerifiedPairs is null ||
            proof.VerifiedPairs.Count != proof.SamePhysicalFiles ||
            proof.SamePhysicalFiles != _approvedPairs.Length)
            throw new InvalidOperationException(
                "Physical identity proof is incomplete or changed since approval.");
        var scope = VerifiedPreviewScope.Build(
            freshBridge, freshDatabase.Tracks, _aliases, proof.VerifiedPairs);
        if (!PairKeys(scope).SequenceEqual(_approvedPairs, StringComparer.Ordinal))
            throw new InvalidOperationException(
                "File-level source/rekordbox ContentID pairing changed since approval.");
        return scope;
    }

    private static string[] PairKeys(VerifiedPreviewScope scope)
    {
        if (scope.Sources.Count != scope.Targets.Count || scope.Sources.Count == 0)
            throw new InvalidDataException("Invalid verified physical scope.");
        return scope.Sources.Zip(scope.Targets,
                (source, target) =>
                    WindowsPathMatcher.Normalize(source.Path).ToUpperInvariant() + "\u0000" +
                    target.ContentId.ToUpperInvariant() + "\u0000" +
                    WindowsPathMatcher.Normalize(target.Path).ToUpperInvariant())
            .OrderBy(x => x, StringComparer.Ordinal).ToArray();
    }

    private static string[] AliasKeys(IReadOnlyList<PathAlias> aliases) =>
        aliases.Select(x =>
                WindowsPathMatcher.Normalize(x.SourceRoot).ToUpperInvariant() + "\u0000" +
                WindowsPathMatcher.Normalize(x.TargetRoot).ToUpperInvariant())
            .OrderBy(x => x, StringComparer.Ordinal).ToArray();
}
