using System.IO;
using System.Runtime.InteropServices;

namespace RekordboxMyTagSync.Core;

public static class PhysicalFileIdentityVerifierSelfTest
{
    public static void Run(string temp)
    {
        if (!OperatingSystem.IsWindows()) return;

        var original = Path.Combine(temp, "PhysicalIdentity", "Music");
        var targetRoot = Path.Combine(temp, "PhysicalIdentity", "Mapped");
        Directory.CreateDirectory(Path.Combine(original, "Singles", "Band"));
        Directory.CreateDirectory(Path.Combine(targetRoot, "Singles", "Band"));
        var empty = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var tracks = new List<BridgeTrack>();
        var targets = new List<RekordboxTrackSnapshot>();
        var names = new[] { "Same.mp3", "Copy.mp3", "Absent.mp3", "Virtual.mp3" };
        foreach (var name in names)
        {
            var source = Path.Combine(original, "Singles", "Band", name);
            var dest = Path.Combine(targetRoot, "Singles", "Band", name);
            File.WriteAllText(source, "same exact content on both paths");
            if (name is "Same.mp3" or "Virtual.mp3")
            {
                if (!CreateHardLink(dest, source, IntPtr.Zero))
                    throw new IOException("Unable to create native Windows test hardlink: " + Marshal.GetLastWin32Error());
            }
            else if (name == "Copy.mp3") File.Copy(source, dest);
            tracks.Add(new BridgeTrack(source, name == "Virtual.mp3" ? 1U : 0U, empty, empty));
            targets.Add(new RekordboxTrackSnapshot(name, dest, Array.Empty<MyTagAssignment>()));
        }

        var progress = new List<PhysicalIdentityProgress>();
        var originalTimestamp = File.GetLastWriteTimeUtc(tracks[0].Path);
        var report = PhysicalFileIdentityVerifier.Verify(
            tracks, targets, new PathAlias(original, targetRoot),
            progress: new DirectProgress(x => progress.Add(x)));
        if (report.EligiblePairs != 3 || report.SamePhysicalFiles != 1 ||
            report.DistinctPhysicalFiles != 1 || report.MissingPairs != 1 ||
            report.UnreadablePairs != 0 || report.UnsupportedPairs != 0 ||
            report.ExcludedSubsongs != 1 ||
            report.AmbiguousSourcePaths != 0 || report.AmbiguousTargetPaths != 0 ||
            report.AllEligibleWereSamePhysicalFile ||
            progress.Count < 2 || progress[0].Completed != 0 ||
            progress[^1].Completed != report.EligiblePairs)
            throw new InvalidOperationException("Native read-only physical file ID evidence was misclassified.");

        if (File.GetLastWriteTimeUtc(tracks[0].Path) != originalTimestamp)
            throw new InvalidOperationException("File identity read changed source modification time.");

        var onlyLinked = PhysicalFileIdentityVerifier.Verify(
            new[] { tracks[0] }, new[] { targets[0] },
            new PathAlias(original, targetRoot));
        if (!onlyLinked.AllEligibleWereSamePhysicalFile ||
            onlyLinked.SamePhysicalFiles != 1)
            throw new InvalidOperationException("Two links to the same file were not recognized.");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var rejectedCancel = false;
        try
        {
            _ = PhysicalFileIdentityVerifier.Verify(
                tracks, targets, new PathAlias(original, targetRoot),
                cancellationToken: cancelled.Token);
        }
        catch (OperationCanceledException) { rejectedCancel = true; }
        if (!rejectedCancel)
            throw new InvalidOperationException("Native file identity analysis ignored cancellation.");

        var ambiguous = PhysicalFileIdentityVerifier.Verify(tracks,
            targets.Concat(new[] { targets[0] with { ContentId = "duplicate" } }).ToArray(),
            new PathAlias(original, targetRoot));
        if (ambiguous.EligiblePairs != 2 || ambiguous.AmbiguousTargetPaths != 1 ||
            ambiguous.SamePhysicalFiles != 0)
            throw new InvalidOperationException("Duplicate rekordbox path entered physical file verification.");

        // Same file byte content deliberately has a different physical file ID
        // if copied. This is not a hash mismatch and cannot be reported as one.
        var copied = PhysicalFileIdentityVerifier.Verify(
            new[] { tracks[1] }, new[] { targets[1] }, new PathAlias(original, targetRoot));
        if (copied.DistinctPhysicalFiles != 1 || copied.SamePhysicalFiles != 0)
            throw new InvalidOperationException("A copied file was misrepresented as the same filesystem object.");
    }

    private sealed class DirectProgress(Action<PhysicalIdentityProgress> write)
        : IProgress<PhysicalIdentityProgress>
    {
        public void Report(PhysicalIdentityProgress value) => write(value);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW",
        SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLink(
        string newFileName, string existingFileName, IntPtr securityAttributes);
}
