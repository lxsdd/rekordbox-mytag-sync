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
    IReadOnlyList<VerifiedPhysicalPair>? VerifiedPairs = null,
    int RecoveredMixedSubsongPaths = 0)
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
        // File-ID lookups are latency-bound on mapped/network drives.
        // Four workers substantially reduce total wall time without
        // unbounded handles, random audio reads or persisted cache files.
        // Indexed results preserve deterministic diagnostics and pair lists.
        var outcomes = new (IdentityStatus Status, string? Example)[mapping.Pairs.Count];
        var completed = 0;
        var progressGate = new object();
        progress?.Report(new PhysicalIdentityProgress(0, mapping.Pairs.Count));
        var options = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Min(4, Math.Max(1, Environment.ProcessorCount))
        };
        Parallel.For(0, mapping.Pairs.Count, options, i =>
        {
            options.CancellationToken.ThrowIfCancellationRequested();
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
                    outcomes[i] = (IdentityStatus.Unsupported, "Native file identity unavailable for this pair.");
                else if (a.VolumeSerialNumber == b.VolumeSerialNumber &&
                         a.FileIndexHigh == b.FileIndexHigh &&
                         a.FileIndexLow == b.FileIndexLow)
                    outcomes[i] = (IdentityStatus.SameFile, null);
                else
                    outcomes[i] = (IdentityStatus.DistinctFile,
                        $"Different underlying file IDs: {source} <> {destination}");
            }
            catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
            {
                outcomes[i] = (IdentityStatus.Missing, $"Missing file: {source} <> {destination}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                outcomes[i] = (IdentityStatus.Unreadable,
                    $"Unable to inspect file IDs: {source} ({e.GetType().Name})");
            }

            var done = Interlocked.Increment(ref completed);
            if (done % 100 == 0)
            {
                lock (progressGate)
                    progress?.Report(new PhysicalIdentityProgress(done, mapping.Pairs.Count));
            }
        });
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new PhysicalIdentityProgress(mapping.Pairs.Count, mapping.Pairs.Count));

        var verified = new List<VerifiedPhysicalPair>();
        var examples = new List<string>();
        var identical = 0;
        var distinct = 0;
        var missing = 0;
        var unreadable = 0;
        var unsupported = 0;
        for (var i = 0; i < outcomes.Length; i++)
        {
            var (status, example) = outcomes[i];
            switch (status)
            {
                case IdentityStatus.SameFile:
                    identical++;
                    verified.Add(new VerifiedPhysicalPair(
                        mapping.Pairs[i].Source, mapping.Pairs[i].Target));
                    break;
                case IdentityStatus.DistinctFile: distinct++; break;
                case IdentityStatus.Missing: missing++; break;
                case IdentityStatus.Unreadable: unreadable++; break;
                case IdentityStatus.Unsupported: unsupported++; break;
                default: throw new InvalidDataException("File identity check produced an unknown status.");
            }
            if (example is not null && examples.Count < 6)
                examples.Add(example);
        }
        return new PhysicalIdentityReport(
            mapping.Pairs.Count, identical, distinct, missing, unreadable, unsupported,
            mapping.AmbiguousSourcePaths, mapping.AmbiguousTargetPaths,
            mapping.ExcludedSubsongs, examples, verified,
            mapping.RecoveredMixedSubsongPaths);
    }

    private enum IdentityStatus
    {
        SameFile,
        DistinctFile,
        Missing,
        Unreadable,
        Unsupported
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
