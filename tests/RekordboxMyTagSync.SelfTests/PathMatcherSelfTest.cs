namespace RekordboxMyTagSync.Core;

public static class PathMatcherSelfTest
{
    public static void Run(string temp)
    {
        var canonicalMusic = Path.Combine(temp, "CanonicalMusic");
        var aliasMusic = Path.Combine(temp, "AliasDrive");
        var aliases = new[] { new PathAlias(aliasMusic, canonicalMusic) };
        var aliasedTrack = Path.Combine(aliasMusic, "Artist", "Track.mp3");
        var canonicalTrack = Path.Combine(canonicalMusic, "Artist", "Track.mp3");
        if (!WindowsPathMatcher.Equivalent(aliasedTrack, canonicalTrack, aliases))
            throw new InvalidOperationException("path alias normalization failed");

        var sibling = Path.Combine(temp, "AliasDriveOther", "Artist", "Track.mp3");
        if (WindowsPathMatcher.Equivalent(sibling, canonicalTrack, aliases))
            throw new InvalidOperationException("path alias escaped its root boundary");

        var duplicateEquivalentAliases = new[]
        {
            new PathAlias(aliasMusic, canonicalMusic),
            new PathAlias(aliasMusic + Path.DirectorySeparatorChar, canonicalMusic + Path.DirectorySeparatorChar)
        };
        if (!WindowsPathMatcher.Equivalent(aliasedTrack, canonicalTrack, duplicateEquivalentAliases))
            throw new InvalidOperationException("equivalent duplicate aliases should remain deterministic");

        var conflictingAliases = new[]
        {
            new PathAlias(aliasMusic, canonicalMusic),
            new PathAlias(aliasMusic + Path.DirectorySeparatorChar, Path.Combine(temp, "OtherMusic"))
        };
        var rejectedConflict = false;
        try { _ = WindowsPathMatcher.Normalize(aliasedTrack, conflictingAliases); }
        catch (ArgumentException) { rejectedConflict = true; }
        if (!rejectedConflict)
            throw new InvalidOperationException("conflicting aliases did not fail closed");
    }
}
