using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RekordboxMyTagSync.Core;

/// <summary>
/// A Windows file ID identifies the same open filesystem object. File identity
/// is distinct from having equal bytes and is never an authorization to
/// mutate rekordbox or adopt an alias.
/// </summary>
public sealed record PhysicalIdentityProgress(int Completed, int Total);

public sealed record VerifiedPhysicalPair(string SourcePath, string TargetPath);

public sealed record PhysicalIdentityReport(
    int EligiblePairs,
    int SamePhysicalFiles,
    int DistinctPhysicalFiles,
    int MissingPairs,
    int UnreadablePairs,
    int UnsupportedPairs,
    int AmbiguousSourcePaths,
    int AmbiguousTargetPaths,
    int ExcludedSubsongs,
    IReadOnlyList<string> Examples,
    IReadOnlyList<VerifiedPhysicalPair>? VerifiedPairs = null)
{
    // Pure evidence, NOT an approval of any database write or a persistent
    // assertion that files cannot change after the handles are closed.
    public bool AllEligibleWereSamePhysicalFile =>
        EligiblePairs > 0 && SamePhysicalFiles == EligiblePairs;
}

public static class PhysicalFileIdentityVerifier
{
    /// <summary>
    /// Open all 1:1 physical file path pairs with read-only handles.
    /// Compare native Windows volume serial + file index without reading
    /// audio data, creating files, saving settings or touching master.db.
    /// </summary>
    public static PhysicalIdentityReport Verify(
        IReadOnlyList<BridgeTrack> bridge,
        IReadOnlyList<RekordboxTrackSnapshot> target,
        PathAlias proposedRoot,
        CancellationToken cancellationToken = default,
        IProgress<PhysicalIdentityProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(proposedRoot);

        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Native file IDs can only be checked on Windows.");

        var mapping = PathPairVerifier.CollectPairs(bridge, target, proposedRoot);
        var identical = 0;
        var distinct = 0;
        var missing = 0;
        var unreadable = 0;
        var unsupported = 0;
        var examples = new List<string>();
        var verified = new List<VerifiedPhysicalPair>();
        static bool Missing(Exception ex) => ex is FileNotFoundException or DirectoryNotFoundException;
        void Note(string message)
        {
            // Use counts for diagnostics; do not dump private library paths to CI.
            if (examples.Count < 6) examples.Add(message);
        }

        progress?.Report(new PhysicalIdentityProgress(0, mapping.Pairs.Count));
        for (var i = 0; i < mapping.Pairs.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (source, destination) = mapping.Pairs[i];
            try
            {
                using var first = new FileStream(
                    source, FileMode.Open, FileAccess.Read, FileShare.Read,
                    bufferSize: 1, options: FileOptions.None);
                using var second = new FileStream(
                    destination, FileMode.Open, FileAccess.Read, FileShare.Read,
                    bufferSize: 1, options: FileOptions.None);
                if (!TryGetFileInformation(first.SafeFileHandle, out var a) ||
                    !TryGetFileInformation(second.SafeFileHandle, out var b))
                {
                    unsupported++;
                    Note("Native file identity unavailable for an open pair.");
                }
                else if (a.VolumeSerialNumber == b.VolumeSerialNumber &&
                         a.FileIndexHigh == b.FileIndexHigh &&
                         a.FileIndexLow == b.FileIndexLow)
                {
                    identical++;
                    verified.Add(new VerifiedPhysicalPair(source, destination));
                }
                else
                {
                    distinct++;
                    Note($"Different underlying file IDs: {source} <> {destination}");
                }
            }
            catch (Exception e) when (Missing(e))
            {
                missing++;
                Note($"Missing file: {source} <> {destination}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                unreadable++;
                Note($"Unable to inspect file IDs: {source} ({e.GetType().Name})");
            }

            if ((i + 1) % 100 == 0 || i + 1 == mapping.Pairs.Count)
                progress?.Report(new PhysicalIdentityProgress(i + 1, mapping.Pairs.Count));
        }

        return new PhysicalIdentityReport(
            mapping.Pairs.Count, identical, distinct, missing, unreadable, unsupported,
            mapping.AmbiguousSourcePaths, mapping.AmbiguousTargetPaths,
            mapping.ExcludedSubsongs, examples, verified);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileInformation
    {
        public uint FileAttributes;
        // Win32 FILETIME is two DWORDs (4-byte alignment), not a
        // C# Int64 whose default 8-byte alignment would shift the file ID.
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle",
        SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TryGetFileInformation(
        SafeFileHandle file, out NativeFileInformation information);
}
