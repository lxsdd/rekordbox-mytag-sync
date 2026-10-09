using System.IO;
using System.Text;

namespace RekordboxMyTagSync.Core;

public static class PathPairVerifierSelfTest
{
    public static void Run(string temp)
    {
        var left = Path.Combine(temp, "PairVerifier", "Source");
        var right = Path.Combine(temp, "PairVerifier", "Target");
        var names = new[] { "Equal.mp3", "Different.mp3", "Missing.mp3", "Large.mp3", "Virtual.mp3" };
        var bridge = new List<BridgeTrack>();
        var target = new List<RekordboxTrackSnapshot>();
        var empty = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            var source = Path.Combine(left, "Singles", "Artist", name);
            var destination = Path.Combine(right, "Singles", "Artist", name);
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllBytes(source, Encoding.UTF8.GetBytes(
                name switch { "Different.mp3" => "12345", "Large.mp3" => "Oversize", _ => "matching" }));
            if (name != "Missing.mp3")
                File.WriteAllBytes(destination, Encoding.UTF8.GetBytes(
                    name switch { "Different.mp3" => "54321", "Large.mp3" => "Oversize", _ => "matching" }));
            bridge.Add(new BridgeTrack(source, name == "Virtual.mp3" ? 1U : 0U, empty, empty));
            target.Add(new RekordboxTrackSnapshot(name, destination, Array.Empty<MyTagAssignment>()));
        }

        var equalPath = Path.Combine(left, "Singles", "Artist", "Equal.mp3");
        var equalTarget = Path.Combine(right, "Singles", "Artist", "Equal.mp3");
        var timestampA = File.GetLastWriteTimeUtc(equalPath);
        var timestampB = File.GetLastWriteTimeUtc(equalTarget);

        var result = PathPairVerifier.Verify(bridge, target, new PathAlias(left, right),
            sampleCount: 16, maximumSampleBytes: 7);
        if (result.UniqueFilePairs != 4 ||
            result.ExcludedSubsongs != 1 ||
            result.AmbiguousSourcePaths != 0 ||
            result.AmbiguousTargetPaths != 0 ||
            result.EqualLengthPairs != 3 ||
            result.DifferentLengthPairs != 0 ||
            result.MissingFilePairs != 1 ||
            result.UnreadableFilePairs != 0 ||
            result.SampledPairs != 3 ||
            result.EqualHashSamples != 0 ||
            result.DifferentHashSamples != 1 ||
            result.SkippedLargeSamples != 2 ||
            result.InconclusiveSamples != 0 ||
            result.EntireMappingProven)
            throw new InvalidOperationException(
                $"Read-only path verification counts or write-authorization boundary failed: {result}");

        var fullyHashed = PathPairVerifier.Verify(bridge, target, new PathAlias(left, right),
            sampleCount: 16, maximumSampleBytes: 1024);
        if (fullyHashed.EqualHashSamples != 2 ||
            fullyHashed.DifferentHashSamples != 1 ||
            fullyHashed.SkippedLargeSamples != 0 ||
            fullyHashed.EntireMappingProven)
            throw new InvalidOperationException("Full-byte hash mismatch was not detected or sample was misclassified as authority.");

        if (timestampA != File.GetLastWriteTimeUtc(equalPath) ||
            timestampB != File.GetLastWriteTimeUtc(equalTarget))
            throw new InvalidOperationException("Read-only verification changed file write time.");

        var duplicates = target.Concat(new[]
        {
            new RekordboxTrackSnapshot("Duplicate", equalTarget, Array.Empty<MyTagAssignment>())
        }).ToArray();
        var collision = PathPairVerifier.Verify(bridge, duplicates, new PathAlias(left, right));
        if (collision.AmbiguousTargetPaths != 1 ||
            collision.UniqueFilePairs != 3 ||
            collision.EntireMappingProven)
            throw new InvalidOperationException("Duplicate target path was implicitly accepted.");

        var physicalAndVirtual = bridge.Concat(new[]
        {
            new BridgeTrack(equalPath, 3, empty, empty)
        }).ToArray();
        var excluded = PathPairVerifier.Verify(physicalAndVirtual, target, new PathAlias(left, right));
        if (excluded.AmbiguousSourcePaths != 1 ||
            excluded.ExcludedSubsongs != 2 ||
            excluded.UniqueFilePairs != 4)
            throw new InvalidOperationException(
                "Unique file-level subsong-0 should be recognized beside virtual children, without adding virtual ContentIDs.");

        var rejected = false;
        try { _ = PathPairVerifier.Verify(bridge, target, new PathAlias(left, left)); }
        catch (InvalidDataException) { rejected = true; }
        if (!rejected)
            throw new InvalidOperationException("Self-mapping alias was not rejected.");
    }
}
