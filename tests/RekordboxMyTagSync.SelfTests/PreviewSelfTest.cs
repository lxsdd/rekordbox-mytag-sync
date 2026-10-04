using RekordboxMyTagSync.Core;

public static class PreviewSelfTest
{
    public static void Run(string temp)
    {
        var canonicalRoot = Path.Combine(temp, "PreviewCanonicalMusic");
        var aliasRoot = Path.Combine(temp, "PreviewAliasMusic");
        var aliases = new[] { new PathAlias(aliasRoot, canonicalRoot) };

        var bridgeOne = Track(
            Path.Combine(aliasRoot, "Artist", "One.mp3"),
            0,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["genre"] = new[] { "House" }
            },
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["MOOD"] = new[] { "Euphoric" }
            });
        var bridgeTwo = Track(
            Path.Combine(canonicalRoot, "Artist", "Two.mp3"),
            0,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["genre"] = new[] { "Techno" }
            },
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase));

        var mappings = new[]
        {
            new MappingRule("GENRE", "Genre"),
            new MappingRule("MOOD", "Mood")
        };
        var targetOne = new RekordboxTrackSnapshot(
            "C1",
            Path.Combine(canonicalRoot, "Artist", "One.mp3"),
            new[]
            {
                new MyTagAssignment("Genre", "House"),
                new MyTagAssignment("Mood", "Old"),
                new MyTagAssignment("Mood", "Manual")
            });
        var managed = new[]
        {
            new ManagedAssignment("C1", new MyTagAssignment("Mood", "Old"))
        };

        var request = new PreviewRequest(
            "dbid:fixture-a",
            new[] { bridgeOne, bridgeTwo },
            mappings,
            new[] { targetOne },
            managed,
            aliases);
        var preview = PreviewEngine.Create(request);

        if (!preview.IsValid) throw new InvalidOperationException("baseline preview should be valid");
        AssertCounts(preview, additions: 1, removals: 1, already: 1, conflicts: 0, unmatched: 1);
        AssertDetail(preview, PreviewDetailKind.Add, "C1", "Mood", "Euphoric");
        AssertDetail(preview, PreviewDetailKind.Remove, "C1", "Mood", "Old");
        AssertDetail(preview, PreviewDetailKind.AlreadyCorrect, "C1", "Genre", "House");
        if (preview.Details.Any(x => x.Kind == PreviewDetailKind.Remove &&
                                     string.Equals(x.Tag?.Value, "Manual", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("manual/unmanaged MyTag assignment was scheduled for removal");

        var reordered = PreviewEngine.Create(request with
        {
            BridgeTracks = new[] { bridgeTwo, bridgeOne },
            Mappings = mappings.Reverse().ToArray(),
            RekordboxTracks = new[]
            {
                targetOne with { Assignments = targetOne.Assignments.Reverse().ToArray() }
            },
            ManagedAssignments = managed.Reverse().ToArray()
        });
        if (!string.Equals(preview.FingerprintSha256, reordered.FingerprintSha256, StringComparison.Ordinal))
            throw new InvalidOperationException("preview fingerprint changed under semantically irrelevant input ordering");

        var duplicateBridge = Track(
            Path.Combine(canonicalRoot, "Artist", "One.mp3"),
            1,
            bridgeOne.Core,
            bridgeOne.Extra);
        var duplicateBridgePreview = PreviewEngine.Create(request with
        {
            BridgeTracks = new[] { bridgeOne, duplicateBridge }
        });
        AssertInvalidConflict(duplicateBridgePreview, "bridge path/subsong collapse did not fail closed");

        var duplicateTargetPreview = PreviewEngine.Create(request with
        {
            BridgeTracks = new[] { bridgeOne },
            RekordboxTracks = new[]
            {
                targetOne,
                new RekordboxTrackSnapshot("C2", Path.Combine(aliasRoot, "Artist", "One.mp3"), Array.Empty<MyTagAssignment>())
            }
        });
        AssertInvalidConflict(duplicateTargetPreview, "duplicate canonical target path did not fail closed");

        var provenanceDrift = PreviewEngine.Create(request with
        {
            BridgeTracks = new[] { bridgeOne },
            RekordboxTracks = new[]
            {
                targetOne with
                {
                    Assignments = new[]
                    {
                        new MyTagAssignment("Genre", "House"),
                        new MyTagAssignment("Mood", "Manual")
                    }
                }
            }
        });
        AssertInvalidConflict(provenanceDrift, "managed provenance drift did not fail closed");

        var missingContentProvenance = PreviewEngine.Create(request with
        {
            BridgeTracks = new[] { bridgeOne },
            ManagedAssignments = new[]
            {
                new ManagedAssignment("C404", new MyTagAssignment("Mood", "Old"))
            }
        });
        AssertInvalidConflict(missingContentProvenance, "provenance for missing ContentId did not fail closed");

        var invalidRegexMapping = PreviewEngine.Create(request with
        {
            BridgeTracks = new[] { bridgeOne },
            Mappings = new[]
            {
                new MappingRule("MOOD", "Mood", TransformKind.RegexReplace, Pattern: null)
            },
            ManagedAssignments = Array.Empty<ManagedAssignment>()
        });
        AssertInvalidConflict(invalidRegexMapping, "invalid RegexReplace mapping did not become a preview conflict");

        var conflictingAliasPreview = PreviewEngine.Create(request with
        {
            BridgeTracks = new[] { bridgeOne },
            ManagedAssignments = Array.Empty<ManagedAssignment>(),
            PathAliases = new[]
            {
                new PathAlias(aliasRoot, canonicalRoot),
                new PathAlias(aliasRoot + Path.DirectorySeparatorChar, Path.Combine(temp, "PreviewOtherMusic"))
            }
        });
        AssertInvalidConflict(conflictingAliasPreview, "conflicting path aliases did not become a preview conflict");
    }

    private static BridgeTrack Track(
        string path,
        uint subsong,
        IReadOnlyDictionary<string, IReadOnlyList<string>> core,
        IReadOnlyDictionary<string, IReadOnlyList<string>> extra) =>
        new(path, subsong, core, extra);

    private static void AssertCounts(
        PreviewResult result,
        int additions,
        int removals,
        int already,
        int conflicts,
        int unmatched)
    {
        var c = result.Counts;
        if (c.Additions != additions || c.Removals != removals || c.AlreadyCorrect != already ||
            c.Conflicts != conflicts || c.Unmatched != unmatched)
            throw new InvalidOperationException(
                $"preview counts mismatch: expected {additions}/{removals}/{already}/{conflicts}/{unmatched}, got " +
                $"{c.Additions}/{c.Removals}/{c.AlreadyCorrect}/{c.Conflicts}/{c.Unmatched}");
    }

    private static void AssertDetail(
        PreviewResult result,
        PreviewDetailKind kind,
        string contentId,
        string group,
        string value)
    {
        if (!result.Details.Any(x =>
                x.Kind == kind &&
                string.Equals(x.ContentId, contentId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.Tag?.Group, group, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.Tag?.Value, value, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"missing preview detail {kind}: {contentId} {group}/{value}");
    }

    private static void AssertInvalidConflict(PreviewResult result, string message)
    {
        if (result.IsValid || result.Counts.Conflicts == 0) throw new InvalidOperationException(message);
    }
}
