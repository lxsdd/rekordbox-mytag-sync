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
            !scope.ExcludedTargets.Any(x => x.ContentId == "C3"))
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
        // A virtual subsong must be excluded by the native verifier. It
        // cannot independently authorize the same physical file.
        if (rejected)
            throw new InvalidOperationException("File-level scope must not depend on virtual subsong path identity.");

        var mismatched = false;
        try { _ = VerifiedPreviewScope.Build(b, t, alias, new[] { new VerifiedPhysicalPair(one, second) }); }
        catch (InvalidDataException) { mismatched = true; }
        if (!mismatched)
            throw new InvalidOperationException("Physical evidence with wrong target path was not rejected.");
    }
}
