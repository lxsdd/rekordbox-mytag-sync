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

        // Regression from live foobar bridge: file://Z:/Music was being
        // resolved relative to the executable directory rather than to Z:.
        var uriVariants = new[]
        {
            @"file://Z:/Music/Singles/Artist/Track.mp3",
            @"file:///Z:/Music/Singles/Artist/Track.mp3",
            @"file://Z:\Music\Singles\Artist\Track.mp3",
            @"file:///Z:/Music/Singles/Artist/Track%20Mix.mp3"
        };
        foreach (var uri in uriVariants.Take(3))
        {
            if (!WindowsPathMatcher.Equivalent(
                    uri, @"Z:\Music\Singles\Artist\Track.mp3"))
                throw new InvalidOperationException("foobar drive file URI was not normalized as an absolute path.");
            if (WindowsPathMatcher.Normalize(uri).Contains("Program Files", StringComparison.OrdinalIgnoreCase) ||
                WindowsPathMatcher.Normalize(uri).Contains("file:", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("file URI was incorrectly reinterpreted as an EXE-relative path.");
        }
        if (!WindowsPathMatcher.Equivalent(
                uriVariants[3], @"Z:\Music\Singles\Artist\Track Mix.mp3"))
            throw new InvalidOperationException("percent-encoded spaces in file URIs were not decoded.");

        var driveAlias = new[] { new PathAlias(@"Z:\Music", @"R:\") };
        if (!WindowsPathMatcher.Equivalent(
                @"file://Z:/Music/Singles/Artist/Track.mp3",
                @"R:\Singles\Artist\Track.mp3", driveAlias))
            throw new InvalidOperationException("file URI to configured root alias mismatch.");
        if (WindowsPathMatcher.Equivalent(
                @"file://Z:/Music/Singles/Artist/Track.mp3",
                @"R:\Other\Artist\Track.mp3", driveAlias))
            throw new InvalidOperationException("file URI alias matched an unrelated folder.");
        if (WindowsPathMatcher.Normalize(@"R:\") != @"R:\")
            throw new InvalidOperationException("Drive root trailing separator must be preserved.");

        var unsafePaths = new[]
        {
            @"file://Z:/Music/Artist/Bad%2FTrack.mp3",
            @"file://Z:/Music/Artist/Bad%5CTrack.mp3",
            @"https://example.org/music/track.mp3",
            @"file:///",
            @"file:relative/track.mp3",
            @"relative\track.mp3"
        };
        foreach (var unsafePath in unsafePaths)
        {
            var blocked = false;
            try { _ = WindowsPathMatcher.Normalize(unsafePath); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { blocked = true; }
            if (!blocked)
                throw new InvalidOperationException("Unsafe path unexpectedly normalized: " + unsafePath);
        }

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
