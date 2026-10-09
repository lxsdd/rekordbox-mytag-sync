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
    PhysicalIdentityReport Report)
{
    public static ReadOnlyIdentityCache Capture(
        BridgeSourceSnapshot bridge,
        RekordboxDatabaseSnapshot database,
        PathAlias root,
        PhysicalIdentityReport report)
    {
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
            WindowsPathMatcher.Normalize(root.SourceRoot),
            WindowsPathMatcher.Normalize(root.TargetRoot),
            report);
    }

    public bool IsValidFor(
        BridgeSourceSnapshot bridge,
        RekordboxDatabaseSnapshot database,
        PathAlias root)
    {
        if (!Equals(BridgeState, bridge.State) ||
            !Equals(Database, database.Identity) ||
            !string.Equals(BridgeDirectory, bridge.DirectoryPath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(SourceRoot, WindowsPathMatcher.Normalize(root.SourceRoot), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(TargetRoot, WindowsPathMatcher.Normalize(root.TargetRoot), StringComparison.OrdinalIgnoreCase))
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
}
