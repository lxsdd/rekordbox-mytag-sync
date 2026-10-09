namespace RekordboxMyTagSync.Core;

public static class PathMatchAdvisorSelfTest
{
    public static void Run(string temp)
    {
        var sourceRoot = Path.Combine(temp, "SourceMusic", "Singles");
        var targetRoot = Path.Combine(temp, "RekordboxMusic", "Collection");
        var files = new[] { ("A", "One.mp3"), ("B", "Two.mp3"), ("C", "Three.mp3"), ("D", "Four.mp3") };
        var empty = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        BridgeTrack Bridge((string Folder, string File) x, uint subsong = 0) =>
            new(Path.Combine(sourceRoot, x.Folder, x.File), subsong, empty, empty);
        RekordboxTrackSnapshot Target((string Folder, string File) x) =>
            new(x.File, Path.Combine(targetRoot, x.Folder, x.File), Array.Empty<MyTagAssignment>());

        var bridges = files.Select(x => Bridge(x)).ToArray();
        var targets = files.Select(x => Target(x)).ToArray();
        var observed = PathMatchAdvisor.Analyze(bridges, targets);

        if (observed.ExactPathMatches != 0 || observed.ConfiguredAliasMatches != 0 ||
            observed.UnmatchedBridgeItems != 4 || observed.Proposals.Count != 1 ||
            observed.Proposals[0].UniqueSuffixPairs != 4 ||
            !string.Equals(observed.Proposals[0].SourceRoot, sourceRoot, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(observed.Proposals[0].TargetRoot, targetRoot, StringComparison.OrdinalIgnoreCase) ||
            !observed.Proposals[0].Evidence.Contains("UNVERIFIED", StringComparison.Ordinal))
            throw new InvalidOperationException("Read-only path suffix proposal was incorrect or represented as verified.");

        var aliases = new[] { new PathAlias(sourceRoot, targetRoot) };
        var matched = PathMatchAdvisor.Analyze(bridges, targets, aliases);
        if (matched.ConfiguredAliasMatches != 4 || matched.ExactPathMatches != 0 ||
            matched.UnmatchedBridgeItems != 0 || matched.Proposals.Count != 0)
            throw new InvalidOperationException("Configured path aliases did not produce deterministic unique matches.");

        var exact = PathMatchAdvisor.Analyze(
            bridges,
            files.Select(x => new RekordboxTrackSnapshot(x.File,
                Path.Combine(sourceRoot, x.Folder, x.File), Array.Empty<MyTagAssignment>())).ToArray());
        if (exact.ExactPathMatches != 4 || exact.ConfiguredAliasMatches != 0 ||
            exact.Proposals.Count != 0)
            throw new InvalidOperationException("Exact path identities were not distinguished from alias matches.");

        var multiSubsong = PathMatchAdvisor.Analyze(
            bridges.Concat(new[] { Bridge(files[0], 1) }).ToArray(), targets, aliases);
        if (multiSubsong.AmbiguousBridgePaths != 1 ||
            multiSubsong.NonFileSubsongs != 1 ||
            multiSubsong.ConfiguredAliasMatches != 3 ||
            multiSubsong.UnmatchedBridgeItems != 2)
            throw new InvalidOperationException("Multiple bridge subsongs were incorrectly collapsed.");

        var ambiguousTarget = PathMatchAdvisor.Analyze(bridges,
            targets.Concat(new[] {
                new RekordboxTrackSnapshot("Copy", targets[0].Path, Array.Empty<MyTagAssignment>())
            }).ToArray(), aliases);
        if (ambiguousTarget.AmbiguousTargetPaths != 1 ||
            ambiguousTarget.ConfiguredAliasMatches != 3)
            throw new InvalidOperationException("Ambiguous target path was incorrectly matched.");

        // Different roots with the same suffix cannot justify a unique alias suggestion.
        var duplicateSuffixBridge = bridges.Concat(new[]
        {
            new BridgeTrack(Path.Combine(temp, "OtherMusic", "A", "One.mp3"), 0, empty, empty)
        }).ToArray();
        var collision = PathMatchAdvisor.Analyze(duplicateSuffixBridge, targets);
        if (collision.Proposals.Count != 1 || collision.Proposals[0].UniqueSuffixPairs != 3)
            throw new InvalidOperationException("Nonunique folder/file suffix entered path proposal evidence.");

        var conflicting = new[] {
            new PathAlias(sourceRoot, targetRoot),
            new PathAlias(sourceRoot + Path.DirectorySeparatorChar, Path.Combine(temp, "Different"))
        };
        var rejected = false;
        try { _ = PathMatchAdvisor.Analyze(bridges, targets, conflicting); }
        catch (ArgumentException) { rejected = true; }
        if (!rejected)
            throw new InvalidOperationException("Conflicting aliases were not rejected during diagnostics.");
    }
}
