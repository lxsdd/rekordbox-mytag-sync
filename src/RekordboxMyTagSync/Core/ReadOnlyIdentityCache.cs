using System.IO;

namespace RekordboxMyTagSync.Core;

/// <summary>
/// In-memory speedup for READ-ONLY previews only. This evidence is an earlier
/// observation, never a write authorization or a persisted file identity.
/// All published source/database file signatures must still match.
/// </summary>
public sealed record ReadOnlyIdentityCache(
    BridgeSourceState BridgeState,
    string BridgeDirectory,
    long BridgeFileLength,
    DateTime BridgeFileWriteUtc,
    RekordboxDatabaseIdentity Database,
    string SourceRoot,
    string TargetRoot,
    PhysicalIdentityReport Report,
    string AliasesSignature = "")
{
    public static ReadOnlyIdentityCache Capture(
        BridgeSourceSnapshot bridge,
        RekordboxDatabaseSnapshot database,
        PathAlias root,
        PhysicalIdentityReport report) =>
        Capture(bridge, database, new[] { root }, report);

    public static ReadOnlyIdentityCache Capture(
        BridgeSourceSnapshot bridge,
        RekordboxDatabaseSnapshot database,
        IReadOnlyList<PathAlias> roots,
        PhysicalIdentityReport report)
    {
        ArgumentNullException.ThrowIfNull(roots);
        if (roots.Count == 0) throw new InvalidDataException("No identity-cache aliases.");
        if (report.VerifiedPairs is null ||
            report.VerifiedPairs.Count != report.SamePhysicalFiles ||
            report.EligiblePairs < report.SamePhysicalFiles ||
            report.SamePhysicalFiles == 0)
            throw new InvalidDataException("Cannot cache incomplete file identity evidence.");
        var blob = new FileInfo(Path.Combine(
            bridge.DirectoryPath, BridgeSourceDiscovery.SnapshotFileName));
        if (!blob.Exists) throw new FileNotFoundException("Bridge snapshot missing.");
        return new ReadOnlyIdentityCache(
            bridge.State, bridge.DirectoryPath, blob.Length,
            blob.LastWriteTimeUtc, database.Identity,
            WindowsPathMatcher.Normalize(roots[0].SourceRoot),
            WindowsPathMatcher.Normalize(roots[0].TargetRoot),
            report, BuildAliasSignature(roots));
    }

    public bool IsValidFor(
        BridgeSourceSnapshot bridge,
        RekordboxDatabaseSnapshot database,
        PathAlias root) => IsValidFor(bridge, database, new[] { root });

    public bool IsValidFor(
        BridgeSourceSnapshot bridge,
        RekordboxDatabaseSnapshot database,
        IReadOnlyList<PathAlias> roots)
    {
        if (roots is null || roots.Count == 0) return false;
        if (!Equals(BridgeState, bridge.State) ||
            !Equals(Database, database.Identity) ||
            !string.Equals(BridgeDirectory, bridge.DirectoryPath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(SourceRoot, WindowsPathMatcher.Normalize(roots[0].SourceRoot), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(TargetRoot, WindowsPathMatcher.Normalize(roots[0].TargetRoot), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(AliasesSignature, BuildAliasSignature(roots), StringComparison.Ordinal))
            return false;

        var blob = new FileInfo(Path.Combine(
            bridge.DirectoryPath, BridgeSourceDiscovery.SnapshotFileName));
        var db = new FileInfo(database.Identity.CanonicalPath);
        return blob.Exists &&
            blob.Length == BridgeFileLength &&
            blob.LastWriteTimeUtc == BridgeFileWriteUtc &&
            db.Exists &&
            db.Length == database.Identity.FileLength &&
            db.LastWriteTimeUtc == database.Identity.LastWriteUtc;
    }

    private static string BuildAliasSignature(IReadOnlyList<PathAlias> roots) =>
        string.Join("\n", roots.Select(x =>
                WindowsPathMatcher.Normalize(x.SourceRoot).ToUpperInvariant() + "\u0000" +
                WindowsPathMatcher.Normalize(x.TargetRoot).ToUpperInvariant())
            .OrderBy(x => x, StringComparer.Ordinal));
}
