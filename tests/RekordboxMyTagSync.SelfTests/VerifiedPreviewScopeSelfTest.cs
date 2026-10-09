using System.IO;

namespace RekordboxMyTagSync.Core;

public static class VerifiedPreviewScopeSelfTest
{
    public static void Run(string temp)
    {
        var one = Path.Combine(temp, "Verified", "Source", "Singles", "A", "One.mp3");
        var two = Path.Combine(temp, "Verified", "Source", "Singles", "B", "Two.mp3");
        var first = Path.Combine(temp, "Verified", "Target", "Singles", "A", "One.mp3");
        var second = Path.Combine(temp, "Verified", "Target", "Singles", "B", "Two.mp3");
        var other = Path.Combine(temp, "Verified", "Target", "Unknown.mp3");
        var empty = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var b = new[]
        {
            new BridgeTrack(one, 0, empty, empty),
            new BridgeTrack(two, 0, empty, empty),
            new BridgeTrack(two, 1, empty, empty)
        };
        var t = new[]
        {
            new RekordboxTrackSnapshot("C1", first, Array.Empty<MyTagAssignment>()),
            new RekordboxTrackSnapshot("C2", second, Array.Empty<MyTagAssignment>()),
            new RekordboxTrackSnapshot("C3", other, Array.Empty<MyTagAssignment>())
        };
        var alias = new PathAlias(Path.Combine(temp, "Verified", "Source"),
            Path.Combine(temp, "Verified", "Target"));
        var scope = VerifiedPreviewScope.Build(b, t, alias,
            new[] { new VerifiedPhysicalPair(one, first) });
        if (scope.Sources.Count != 1 || scope.Targets.Count != 1 ||
            scope.Targets[0].ContentId != "C1" ||
            scope.ExcludedTargets.Count != 2 ||
            !scope.ExcludedTargets.Any(x => x.ContentId == "C2") ||
            !scope.ExcludedTargets.Any(x => x.ContentId == "C3") ||
            !scope.ExcludedTargets.Any(x => x.ContentId == "C2" &&
                x.Message.StartsWith("FILE_IDENTITY_UNCONFIRMED:", StringComparison.Ordinal)) ||
            !scope.ExcludedTargets.Any(x => x.ContentId == "C3" &&
                x.Message.StartsWith("NOT_IN_FOOBAR_SOURCE:", StringComparison.Ordinal)))
            throw new InvalidOperationException("Verified scope did not preserve unmatched/virtual entries as excluded.");

        var rejected = false;
        try
        {
            _ = VerifiedPreviewScope.Build(b, t, alias,
                new[] { new VerifiedPhysicalPair(one, first), new VerifiedPhysicalPair(one, first) });
        }
        catch (InvalidDataException) { rejected = true; }
        if (!rejected)
            throw new InvalidOperationException("Duplicate verified file pair was treated as two unique tracks.");

        rejected = false;
        try
        {
            _ = VerifiedPreviewScope.Build(b, t, alias,
                new[] { new VerifiedPhysicalPair(two, second) });
        }
        catch (InvalidDataException) { rejected = true; }
        // A unique subsong-0 file may be paired despite virtual siblings,
        // but only after explicit physical identity proof supplied by verifier.
        if (rejected)
            throw new InvalidOperationException("File-level scope must not depend on virtual subsong path identity.");
        var mixedScope = VerifiedPreviewScope.Build(
            b, t, alias, new[] { new VerifiedPhysicalPair(two, second) });
        if (mixedScope.Targets.Count != 1 ||
            mixedScope.Targets[0].ContentId != "C2" ||
            mixedScope.Sources.Single().Subsong != 0)
            throw new InvalidOperationException(
                "Physical subsong-0 identity was lost in mixed cue/physical source group.");

        // An exclusive virtual cue path is still not a physical file identity.
        var virtualOnlyPath = Path.Combine(temp, "Verified", "Source", "Singles", "C", "Cue.mp3");
        var virtualOnlyTarget = Path.Combine(temp, "Verified", "Target", "Singles", "C", "Cue.mp3");
        var virtualOnly = VerifiedPreviewScope.Build(
            b.Concat(new[] { new BridgeTrack(virtualOnlyPath, 1, empty, empty) }).ToArray(),
            t.Concat(new[] {
                new RekordboxTrackSnapshot("C4", virtualOnlyTarget, Array.Empty<MyTagAssignment>())
            }).ToArray(),
            alias, new[] { new VerifiedPhysicalPair(one, first) });
        if (!virtualOnly.ExcludedTargets.Any(x => x.ContentId == "C4" &&
            x.Message.StartsWith("VIRTUAL_SUBSONG_ONLY:", StringComparison.Ordinal)))
            throw new InvalidOperationException("Virtual-only cue path was not safely excluded.");

        var duplicateSource = VerifiedPreviewScope.Build(
            b.Concat(new[] { b[1] }).ToArray(), t, alias,
            new[] { new VerifiedPhysicalPair(one, first) });
        if (!duplicateSource.ExcludedTargets.Any(x => x.ContentId == "C2" &&
            x.Message.StartsWith("MULTIPLE_PHYSICAL_SOURCE:", StringComparison.Ordinal)))
            throw new InvalidOperationException(
                "Duplicate physical source paths were not distinguished from virtual subsongs.");

        var duplicatedTarget = VerifiedPreviewScope.Build(
            b, t.Concat(new[]
            {
                new RekordboxTrackSnapshot("C5", first, Array.Empty<MyTagAssignment>())
            }).ToArray(), alias, Array.Empty<VerifiedPhysicalPair>());
        if (!duplicatedTarget.ExcludedTargets.Any(x =>
                x.ContentId == "C1" && x.Message.StartsWith("DUPLICATE_TARGET_PATH:", StringComparison.Ordinal) &&
                x.Message.Contains("C5", StringComparison.Ordinal)) ||
            !duplicatedTarget.ExcludedTargets.Any(x => x.ContentId == "C5" &&
                x.Message.Contains("C1", StringComparison.Ordinal)))
            throw new InvalidOperationException(
                "Same-path rekordbox ContentIds were not cross-referenced for review.");

        var mismatched = false;
        try { _ = VerifiedPreviewScope.Build(b, t, alias, new[] { new VerifiedPhysicalPair(one, second) }); }
        catch (InvalidDataException) { mismatched = true; }
        if (!mismatched)
            throw new InvalidOperationException("Physical evidence with wrong target path was not rejected.");
    }
}
