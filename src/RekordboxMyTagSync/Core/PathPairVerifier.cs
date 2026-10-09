using System.IO;
using System.Security.Cryptography;

namespace RekordboxMyTagSync.Core;

/// <summary>
/// Evidence about distinct *physical files*, not a proof that every pair
/// shares its bytes. This never persists aliases or modifies source/target.
/// </summary>
public sealed record PathPairVerification(
    int UniqueFilePairs,
    int AmbiguousSourcePaths,
    int AmbiguousTargetPaths,
    int ExcludedSubsongs,
    int EqualLengthPairs,
    int DifferentLengthPairs,
    int MissingFilePairs,
    int UnreadableFilePairs,
    int SampledPairs,
    int EqualHashSamples,
    int DifferentHashSamples,
    int SkippedLargeSamples,
    int InconclusiveSamples,
    IReadOnlyList<string> Examples)
{
    public bool EntireMappingProven => false; // A sample never authorizes a write.
}

public static class PathPairVerifier
{
    /// <summary>
    /// Enumerates unambiguous file-level path pairs under ONE proposed root.
    /// Checks file lengths for all candidate pairs and hashes up to sampleCount
    /// deterministic spread-out pairs, subject to a per-file read cap.
    /// No settings/metadata/database/audio writes or file copies are performed.
    /// </summary>
    public static PathPairVerification Verify(
        IReadOnlyList<BridgeTrack> bridge,
        IReadOnlyList<RekordboxTrackSnapshot> target,
        PathAlias proposedRoot,
        int sampleCount = 16,
        long maximumSampleBytes = 64L * 1024 * 1024)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(proposedRoot);
        if (sampleCount is < 1 or > 32)
            throw new ArgumentOutOfRangeException(nameof(sampleCount));
        if (maximumSampleBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(maximumSampleBytes));

        var sourceRoot = WindowsPathMatcher.Normalize(proposedRoot.SourceRoot);
        var targetRoot = WindowsPathMatcher.Normalize(proposedRoot.TargetRoot);
        if (string.Equals(sourceRoot, targetRoot, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Proposed alias roots must differ.");

        var aliases = new[] { new PathAlias(sourceRoot, targetRoot) };
        var sources = bridge.Select(t => new Source(
            t, WindowsPathMatcher.Normalize(t.Path),
            WindowsPathMatcher.Normalize(t.Path, aliases))).ToArray();
        var destinations = target.Select(t => new Target(
            t, WindowsPathMatcher.Normalize(t.Path))).ToArray();

        var sourceGroups = sources.GroupBy(x => x.MappedPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.OrdinalIgnoreCase);
        var destGroups = destinations.GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.OrdinalIgnoreCase);

        var pairs = new List<(string Source, string Target)>();
        foreach (var group in sourceGroups)
        {
            if (group.Value.Length != 1) continue;
            var item = group.Value[0];
            // Never apply file-level tags from a virtual cue/subsong identity.
            if (item.Track.Subsong != 0) continue;
            if (!Within(item.OriginalPath, sourceRoot)) continue;
            if (!destGroups.TryGetValue(group.Key, out var matches) || matches.Length != 1)
                continue;
            var destination = matches[0];
            if (!Within(destination.Path, targetRoot)) continue;
            if (string.Equals(item.OriginalPath, destination.Path, StringComparison.OrdinalIgnoreCase))
                continue;
            pairs.Add((item.OriginalPath, destination.Path));
        }

        var eligible = pairs.OrderBy(x => x.Source, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Target, StringComparer.OrdinalIgnoreCase).ToArray();
        var equal = new List<(string Source, string Target)>();
        var different = 0;
        var missing = 0;
        var unreadable = 0;
        var examples = new List<string>();
        void Record(string message)
        {
            if (examples.Count < 6) examples.Add(message);
        }

        foreach (var pair in eligible)
        {
            try
            {
                var a = new FileInfo(pair.Source);
                var b = new FileInfo(pair.Target);
                if (!a.Exists || !b.Exists)
                {
                    missing++;
                    Record($"Missing file: {(!a.Exists ? pair.Source : pair.Target)}");
                    continue;
                }
                if (a.Length != b.Length)
                {
                    different++;
                    Record($"Different byte lengths: {pair.Source} <> {pair.Target}");
                    continue;
                }
                equal.Add(pair);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
            {
                unreadable++;
                Record($"Unreadable path: {pair.Source} ({e.GetType().Name})");
            }
        }

        var equalHashes = 0;
        var differentHashes = 0;
        var skipped = 0;
        var inconclusive = 0;
        var samples = 0;
        if (equal.Count != 0)
        {
            var selectedIndices = Enumerable.Range(0, Math.Min(sampleCount, equal.Count))
                .Select(i => (int)((long)i * equal.Count / Math.Min(sampleCount, equal.Count)))
                .Distinct().ToArray();
            foreach (var index in selectedIndices)
            {
                var pair = equal[index];
                samples++;
                try
                {
                    var first = HashReadOnly(pair.Source, maximumSampleBytes);
                    var second = HashReadOnly(pair.Target, maximumSampleBytes);
                    if (first is null || second is null)
                    {
                        skipped++;
                        continue;
                    }
                    if (first.AsSpan().SequenceEqual(second))
                        equalHashes++;
                    else
                    {
                        differentHashes++;
                        Record($"Different SHA-256 bytes: {pair.Source} <> {pair.Target}");
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException)
                {
                    inconclusive++;
                    Record($"File changed or could not be hashed: {pair.Source} ({e.GetType().Name})");
                }
            }
        }

        return new PathPairVerification(
            eligible.Length,
            sourceGroups.Values.Count(x => x.Length > 1),
            destGroups.Values.Count(x => x.Length > 1),
            sources.Count(x => x.Track.Subsong != 0),
            equal.Count,
            different,
            missing,
            unreadable,
            samples,
            equalHashes,
            differentHashes,
            skipped,
            inconclusive,
            examples);
    }

    private static byte[]? HashReadOnly(string path, long maxBytes)
    {
        var before = new FileInfo(path);
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 128 * 1024, options: FileOptions.SequentialScan);
        if (stream.Length > maxBytes) return null;
        if (stream.Length != before.Length)
            throw new InvalidDataException("File length changed during verification.");
        var bytes = SHA256.HashData(stream);
        before.Refresh();
        if (!before.Exists || before.Length != stream.Length ||
            before.LastWriteTimeUtc != File.GetLastWriteTimeUtc(path))
            throw new InvalidDataException("File changed during verification.");
        return bytes;
    }

    private static bool Within(string path, string root) =>
        string.Equals(path, root, StringComparison.OrdinalIgnoreCase) ||
        (path.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
         (root.EndsWith('\\') || path.Length > root.Length && path[root.Length] == '\\'));

    private sealed record Source(BridgeTrack Track, string OriginalPath, string MappedPath);
    private sealed record Target(RekordboxTrackSnapshot Track, string Path);
}
