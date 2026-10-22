namespace RekordboxMyTagSync.Core;

public static class PathMatchAdvisorSelfTest
{
    public static void Run(string temp)
    {
        var sourceRoot = Path.Combine(temp, "SourceMusic");
        var targetRoot = Path.Combine(temp, "RekordboxMusic");
        (string Folder, string File)[] files = { (@"Singles\A", "One.mp3"), (@"Singles\B", "Two.mp3"), (@"Singles\C", "Three.mp3"), (@"Singles\D", "Four.mp3") };
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
        if (multiSubsong.BridgeItems != 5 ||
            multiSubsong.AmbiguousBridgePaths != 0 ||
            multiSubsong.NonFileSubsongs != 1 ||
            multiSubsong.ConfiguredAliasMatches != 4 ||
            multiSubsong.UnmatchedBridgeItems != 1 ||
            multiSubsong.Proposals.Count != 0)
            throw new InvalidOperationException(
                "Virtual CUE siblings were incorrectly counted as ambiguous physical paths.");

        // A second virtual track on the same source path is diagnostic only.
        // The physical subsong 0 remains a unique match; virtual rows still
        // appear in the total/unmatched counts, never as extra ContentIDs.
        var severalVirtual = PathMatchAdvisor.Analyze(
            bridges.Concat(new[] { Bridge(files[0], 1), Bridge(files[0], 2) }).ToArray(),
            targets, aliases);
        if (severalVirtual.BridgeItems != 6 ||
            severalVirtual.NonFileSubsongs != 2 ||
            severalVirtual.AmbiguousBridgePaths != 0 ||
            severalVirtual.ConfiguredAliasMatches != 4 ||
            severalVirtual.UnmatchedBridgeItems != 2)
            throw new InvalidOperationException(
                "Two virtual CUE siblings stole the unique physical file match.");

        // Only virtual rows for a path MUST NOT produce a physical candidate,
        // even where their file path is an exact string match.
        var virtualOnly = PathMatchAdvisor.Analyze(
            new[] { Bridge(files[0], 1), Bridge(files[0], 2) },
            new[] { Target(files[0]) }, aliases);
        if (virtualOnly.BridgeItems != 2 ||
            virtualOnly.NonFileSubsongs != 2 ||
            virtualOnly.ExactPathMatches != 0 ||
            virtualOnly.ConfiguredAliasMatches != 0 ||
            virtualOnly.AmbiguousBridgePaths != 0 ||
            virtualOnly.UnmatchedBridgeItems != 2 ||
            virtualOnly.Proposals.Count != 0)
            throw new InvalidOperationException(
                "Virtual-only CUE tracks were treated as a rekordbox file.");

        // Invalid/non-file paths from virtual subsongs must not prevent an
        // otherwise valid physical source from being counted and matched.
        // The invalid row remains visible as an excluded virtual entry.
        var malformedVirtual = PathMatchAdvisor.Analyze(
            bridges.Concat(new[] {
                new BridgeTrack("https://example.invalid/cue/1", 3, empty, empty)
            }).ToArray(), targets, aliases);
        if (malformedVirtual.BridgeItems != 5 ||
            malformedVirtual.NonFileSubsongs != 1 ||
            malformedVirtual.AmbiguousBridgePaths != 0 ||
            malformedVirtual.ConfiguredAliasMatches != 4 ||
            malformedVirtual.UnmatchedBridgeItems != 1 ||
            malformedVirtual.Proposals.Count != 0)
            throw new InvalidOperationException(
                "Non-file virtual subsong path blocked valid physical-only diagnostics.");

        // Even when the entire Bridge snapshot contains only a virtual
        // non-file path, no identity or root proposal must be inferred.
        var malformedVirtualOnly = PathMatchAdvisor.Analyze(
            new[] { new BridgeTrack("https://example.invalid/cue/2", 8, empty, empty) },
            targets, aliases);
        if (malformedVirtualOnly.BridgeItems != 1 ||
            malformedVirtualOnly.NonFileSubsongs != 1 ||
            malformedVirtualOnly.UnmatchedBridgeItems != 1 ||
            malformedVirtualOnly.ExactPathMatches != 0 ||
            malformedVirtualOnly.ConfiguredAliasMatches != 0 ||
            malformedVirtualOnly.Proposals.Count != 0)
            throw new InvalidOperationException(
                "Virtual-only unsupported path incorrectly became file-level evidence.");

        // Two real subsong-0 sources sharing a normalized path remain unsafe.
        var ambiguousPhysical = PathMatchAdvisor.Analyze(
            bridges.Concat(new[] { Bridge(files[0]) }).ToArray(), targets, aliases);
        if (ambiguousPhysical.AmbiguousBridgePaths != 1 ||
            ambiguousPhysical.ConfiguredAliasMatches != 3 ||
            ambiguousPhysical.UnmatchedBridgeItems != 2)
            throw new InvalidOperationException(
                "Duplicate physical subsong-0 source paths lost ambiguity guard.");

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
            new BridgeTrack(Path.Combine(temp, "OtherMusic", "Singles", "A", "One.mp3"), 0, empty, empty)
        }).ToArray();
        var collision = PathMatchAdvisor.Analyze(duplicateSuffixBridge, targets);
        if (collision.Proposals.Count != 1 || collision.Proposals[0].UniqueSuffixPairs != 3)
            throw new InvalidOperationException("Nonunique folder/file suffix entered path proposal evidence.");

        // Real R:/Z: style fixture: foobar exports a file URI while
        // rekordbox holds an ordinary drive path. This is read-only
        // evidence, not authority to adopt the suggested drive alias.
        var uriBridge = new[]
        {
            new BridgeTrack(@"file://Z:/Music/Singles/Artist/One.mp3", 0, empty, empty),
            new BridgeTrack(@"file:///Z:/Music/Singles/Artist/Two.mp3", 0, empty, empty),
            new BridgeTrack(@"file://Z:\Music\Singles\Artist\Three.mp3", 0, empty, empty),
            new BridgeTrack(@"file://Z:/Music/Singles/Artist/Four.mp3", 1, empty, empty)
        };
        var uriTarget = new[]
        {
            new RekordboxTrackSnapshot("U1", @"R:\Singles\Artist\One.mp3", Array.Empty<MyTagAssignment>()),
            new RekordboxTrackSnapshot("U2", @"R:\Singles\Artist\Two.mp3", Array.Empty<MyTagAssignment>()),
            new RekordboxTrackSnapshot("U3", @"R:\Singles\Artist\Three.mp3", Array.Empty<MyTagAssignment>()),
            new RekordboxTrackSnapshot("U4", @"R:\Singles\Artist\Four.mp3", Array.Empty<MyTagAssignment>())
        };
        var uriAnalysis = PathMatchAdvisor.Analyze(uriBridge, uriTarget);
        if (uriAnalysis.ExactPathMatches != 0 ||
            uriAnalysis.ConfiguredAliasMatches != 0 ||
            uriAnalysis.Proposals.Count != 1 ||
            !string.Equals(uriAnalysis.Proposals[0].SourceRoot, @"Z:\Music", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uriAnalysis.Proposals[0].TargetRoot, @"R:\", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("File URI root proposal did not recover the proper Z:/Music -> R:/ pair.");
        var uriExplicit = PathMatchAdvisor.Analyze(
            uriBridge, uriTarget, new[] { new PathAlias(@"Z:\Music", @"R:\") });
        if (uriExplicit.ConfiguredAliasMatches != 3 ||
            uriExplicit.NonFileSubsongs != 1 ||
            uriExplicit.UnmatchedBridgeItems != 1)
            throw new InvalidOperationException("File URI configured alias mishandled a nonzero subsong.");

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
