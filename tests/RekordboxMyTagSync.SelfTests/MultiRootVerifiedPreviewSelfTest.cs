using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace RekordboxMyTagSync.Core;

public static class MultiRootVerifiedPreviewSelfTest
{
    public static void Run(string temp)
    {
        if (!OperatingSystem.IsWindows()) return;

        var root = Path.Combine(temp, "TwoRoots");
        var sourceOneRoot = Path.Combine(root, "Source", "Singles");
        var sourceTwoRoot = Path.Combine(root, "Source", "Albums");
        var targetOneRoot = Path.Combine(root, "Target", "Singles");
        var targetTwoRoot = Path.Combine(root, "Target", "Albums");
        foreach (var directory in new[] { sourceOneRoot, sourceTwoRoot,
                     targetOneRoot, targetTwoRoot })
            Directory.CreateDirectory(directory);

        var sourceOne = Path.Combine(sourceOneRoot, "One.mp3");
        var sourceTwo = Path.Combine(sourceTwoRoot, "Two.mp3");
        var targetOne = Path.Combine(targetOneRoot, "One.mp3");
        var targetTwo = Path.Combine(targetTwoRoot, "Two.mp3");
        File.WriteAllText(sourceOne, "synthetic-one");
        File.WriteAllText(sourceTwo, "synthetic-two");
        Link(targetOne, sourceOne);
        Link(targetTwo, sourceTwo);

        var databasePath = Path.Combine(root, "master.db");
        EncryptedMutationFixture.Create(databasePath, targetOne, targetTwo);
        var databaseHash = SHA256.HashData(File.ReadAllBytes(databasePath));
        var snapshot = RekordboxSqlCipherDatabase.ReadSnapshot(databasePath,
            EncryptedMutationFixture.Key, new RekordboxDatabaseReadPolicy(
                new HashSet<string>(StringComparer.Ordinal) {
                    EncryptedMutationFixture.DbVersion }, RequireRekordboxClosed: false));
        var coreOne = new Dictionary<string, IReadOnlyList<string>>(
            StringComparer.OrdinalIgnoreCase) { ["GENRE"] = new[] { "House" } };
        var coreTwo = new Dictionary<string, IReadOnlyList<string>>(
            StringComparer.OrdinalIgnoreCase) { ["GENRE"] = new[] { "Techno" } };
        var empty = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var bridge = new[]
        {
            new BridgeTrack(sourceOne, 0, coreOne, empty),
            new BridgeTrack(sourceOne, 1, coreTwo, empty),
            new BridgeTrack(sourceTwo, 0, coreTwo, empty)
        };
        var aliases = new[] {
            new PathAlias(sourceOneRoot, targetOneRoot),
            new PathAlias(sourceTwoRoot, targetTwoRoot)
        };

        var report = PhysicalFileIdentityVerifier.Verify(
            bridge, snapshot.Tracks, aliases);
        if (report.EligiblePairs != 2 || report.SamePhysicalFiles != 2 ||
            report.ExcludedSubsongs != 1 || report.DistinctPhysicalFiles != 0 ||
            report.AmbiguousSourcePaths != 0 || report.AmbiguousTargetPaths != 0 ||
            report.VerifiedPairs?.Count != 2)
            throw new InvalidOperationException(
                "Disjoint multi-root FileIndex/VolumeSerial verification failed.");
        var scope = VerifiedPreviewScope.Build(
            bridge, snapshot.Tracks, aliases, report.VerifiedPairs);
        if (scope.Sources.Count != 2 || scope.Targets.Count != 2 ||
            scope.ExcludedTargets.Count != 0 ||
            scope.Sources.Any(x => x.Subsong != 0) ||
            scope.ConfiguredAliases?.Count != 2)
            throw new InvalidOperationException(
                "Verified physical preview admitted a virtual cue or lost a root.");

        var preview = PreviewEngine.Create(new PreviewRequest(
            snapshot.Identity.PreviewIdentity, scope.Sources,
            new[] { new MappingRule("GENRE", "Genre") }, scope.Targets,
            Array.Empty<ManagedAssignment>(), aliases, snapshot.MyTagDefinitions));
        if (!preview.IsValid || preview.Counts.AlreadyCorrect != 2 ||
            preview.Counts.Additions != 0 || preview.Counts.Unmatched != 0 ||
            !databaseHash.AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(databasePath))))
            throw new InvalidOperationException(
                "Multi-root encrypted SQLCipher preview changed DB or evaluated virtual metadata.");

        File.Delete(targetTwo);
        File.Copy(sourceTwo, targetTwo);
        var copied = PhysicalFileIdentityVerifier.Verify(bridge, snapshot.Tracks, aliases);
        if (copied.SamePhysicalFiles != 1 || copied.DistinctPhysicalFiles != 1 ||
            copied.VerifiedPairs?.Count != 1)
            throw new InvalidOperationException(
                "Copy was accepted as matching native physical file.");
        var onlyOne = VerifiedPreviewScope.Build(
            bridge, snapshot.Tracks, aliases, copied.VerifiedPairs);
        if (onlyOne.Targets.Count != 1 ||
            onlyOne.Targets[0].ContentId != "C1" ||
            onlyOne.ExcludedTargets.Single().ContentId != "C2")
            throw new InvalidOperationException(
                "Unverified second-root ContentID was not explicitly excluded.");

        // Source-root collisions cannot resolve by picking a preferred alias.
        var shadowSource = Path.Combine(sourceTwoRoot, "One.mp3");
        File.WriteAllText(shadowSource, "synthetic-shadow");
        var competing = bridge.Concat(new[] {
            new BridgeTrack(shadowSource, 0, coreOne, empty)
        }).ToArray();
        var competingAliases = new[] {
            new PathAlias(sourceOneRoot, targetOneRoot),
            new PathAlias(sourceTwoRoot, targetOneRoot)
        };
        var ambiguous = PhysicalFileIdentityVerifier.Verify(
            competing, snapshot.Tracks, competingAliases);
        if (ambiguous.AmbiguousSourcePaths < 1 ||
            ambiguous.VerifiedPairs?.Any(x =>
                string.Equals(x.TargetPath, targetOne, StringComparison.OrdinalIgnoreCase)) == true)
            throw new InvalidOperationException(
                "Cross-root alias collision silently selected a ContentID.");

        var contradictory = false;
        try
        {
            _ = PhysicalFileIdentityVerifier.Verify(bridge, snapshot.Tracks,
                new[] { new PathAlias(sourceOneRoot, targetOneRoot),
                        new PathAlias(sourceOneRoot, targetTwoRoot) });
        }
        catch (ArgumentException) { contradictory = true; }
        if (!contradictory)
            throw new InvalidOperationException(
                "Contradictory aliases for the same source root were not rejected.");
        if (!databaseHash.AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(databasePath))))
            throw new InvalidOperationException(
                "Read-only multi-root verification mutated encrypted master.db fixture.");
    }

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
