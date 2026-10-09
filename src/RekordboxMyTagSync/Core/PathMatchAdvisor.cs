using System.IO;

namespace RekordboxMyTagSync.Core;

/// <summary>
/// Read-only structural path evidence. Filename/directory similarity is NOT
/// proof of file identity and must never authorize MyTag writes or aliases.
/// </summary>
public sealed record PathAliasProposal(
    string SourceRoot,
    string TargetRoot,
    int UniqueSuffixPairs,
    string ExampleSource,
    string ExampleTarget)
{
    public string Evidence => "UNVERIFIED — folder/file suffix only";
}

public sealed record PathMatchAnalysis(
    int BridgeItems,
    int TargetTracks,
    int ExactPathMatches,
    int ConfiguredAliasMatches,
    int AmbiguousBridgePaths,
    int AmbiguousTargetPaths,
    int NonFileSubsongs,
    int UnmatchedBridgeItems,
    IReadOnlyList<PathAliasProposal> Proposals);

public static class PathMatchAdvisor
{
    /// <summary>
    /// Does not access audio bytes, modify settings, or create aliases.
    /// A path-suffix proposal is diagnostic evidence only; manual verification
    /// and a fresh production preview are required before any mutation.
    /// </summary>
    public static PathMatchAnalysis Analyze(
        IReadOnlyList<BridgeTrack> bridge,
        IReadOnlyList<RekordboxTrackSnapshot> target,
        IReadOnlyList<PathAlias>? configuredAliases = null)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(target);
        var aliases = configuredAliases ?? Array.Empty<PathAlias>();

        // Validate every alias even when source data is empty.
        foreach (var alias in aliases)
            _ = WindowsPathMatcher.Normalize(alias.SourceRoot, aliases);

        var source = bridge.Select(x => new Source(
                x, WindowsPathMatcher.Normalize(x.Path),
                WindowsPathMatcher.Normalize(x.Path, aliases)))
            .ToArray();
        var destination = target.Select(x => new Destination(
                x, WindowsPathMatcher.Normalize(x.Path),
                WindowsPathMatcher.Normalize(x.Path, aliases)))
            .ToArray();

        var sourceGroups = source.GroupBy(x => x.MappedPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.OrdinalIgnoreCase);
        var targetGroups = destination.GroupBy(x => x.MappedPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.OrdinalIgnoreCase);
        var matched = new HashSet<BridgeTrack>();
        var exact = 0;
        var aliased = 0;
        foreach (var (path, entries) in sourceGroups)
        {
            if (entries.Length != 1 || entries[0].Track.Subsong != 0 ||
                !targetGroups.TryGetValue(path, out var matches) || matches.Length != 1)
                continue;
            matched.Add(entries[0].Track);
            if (string.Equals(entries[0].OriginalPath, matches[0].OriginalPath,
                    StringComparison.OrdinalIgnoreCase))
                exact++;
            else
                aliased++;
        }

        // Infer consistent candidate roots only from at least THREE
        // independent path components (category/artist/filename), not
        // individual artist/filename pairs. A suffix is useful only when it
        // is unique on both sides. This suggests a broad Z:\Music -> R:\
        // mapping when justified, instead of dozens of artist-level aliases.
        var possibleSource = source
            .Where(x => !matched.Contains(x.Track) && x.Track.Subsong == 0)
            .Where(x => sourceGroups[x.MappedPath].Length == 1)
            .SelectMany(x => EnumerateSuffixes(x.OriginalPath)
                .Select(s => (Item: x, s.Key, s.Root)))
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Where(x => x.Count() == 1)
            .ToDictionary(x => x.Key, x => x.Single(), StringComparer.OrdinalIgnoreCase);

        var possibleTarget = destination
            .Where(x => targetGroups[x.MappedPath].Length == 1)
            .SelectMany(x => EnumerateSuffixes(x.OriginalPath)
                .Select(s => (Item: x, s.Key, s.Root)))
            .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Where(x => x.Count() == 1)
            .ToDictionary(x => x.Key, x => x.Single(), StringComparer.OrdinalIgnoreCase);

        var proposals = new List<(string Source, string Target, string ExampleSource, string ExampleTarget)>();
        foreach (var (suffix, item) in possibleSource)
        {
            if (!possibleTarget.TryGetValue(suffix, out var match) ||
                string.Equals(item.Item.MappedPath, match.Item.MappedPath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(item.Root, match.Root, StringComparison.OrdinalIgnoreCase))
                continue;
            proposals.Add((item.Root, match.Root, item.Item.Track.Path, match.Item.Track.Path));
        }

        var raw = proposals
            .GroupBy(x => (x.Source, x.Target), RootPairComparer.Instance)
            .Select(g =>
            {
                var evidence = g.GroupBy(x => x.ExampleSource, StringComparer.OrdinalIgnoreCase)
                    .Select(x => x.First())
                    .OrderBy(x => x.ExampleSource, StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                return new PathAliasProposal(
                    g.Key.Source, g.Key.Target, evidence.Length,
                    evidence[0].ExampleSource, evidence[0].ExampleTarget);
            })
            .Where(x => x.UniqueSuffixPairs >= 3)
            .ToArray();

        // Never resolve competing root mappings by picking a "winner".
        // A parent mapping already supported by at least as many distinct
        // paths supersedes its nested subfolder mapping for presentation.
        var unique = raw
            .Where(x => raw.Count(y => string.Equals(x.SourceRoot, y.SourceRoot, StringComparison.OrdinalIgnoreCase)) == 1)
            .Where(x => raw.Count(y => string.Equals(x.TargetRoot, y.TargetRoot, StringComparison.OrdinalIgnoreCase)) == 1)
            .Where(x => !raw.Any(y =>
                !ReferenceEquals(x, y) &&
                IsNested(x.SourceRoot, y.SourceRoot) &&
                IsNested(x.TargetRoot, y.TargetRoot) &&
                y.UniqueSuffixPairs >= x.UniqueSuffixPairs))
            .OrderByDescending(x => x.UniqueSuffixPairs)
            .ThenBy(x => x.SourceRoot, StringComparer.OrdinalIgnoreCase)
            .Take(30)
            .ToArray();

        return new PathMatchAnalysis(
            bridge.Count, target.Count, exact, aliased,
            sourceGroups.Values.Count(x => x.Length > 1),
            targetGroups.Values.Count(x => x.Length > 1),
            source.Count(x => x.Track.Subsong != 0),
            source.Count(x => !matched.Contains(x.Track)),
            unique);
    }

    private static IEnumerable<(string Key, string Root)> EnumerateSuffixes(string path)
    {
        var volume = Path.GetPathRoot(path);
        if (string.IsNullOrWhiteSpace(volume)) yield break;

        var segments = path[volume.Length..]
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        // A name alone, or even artist/filename, is never enough to
        // justify proposing a library-level root. Capped depth bounds work.
        for (var depth = 3; depth <= Math.Min(6, segments.Length); depth++)
        {
            var suffix = string.Join("\\", segments.Skip(segments.Length - depth));
            var prefix = segments.Take(segments.Length - depth).ToArray();
            var baseRoot = prefix.Length == 0
                ? volume
                : Path.Combine(new[] { volume }.Concat(prefix).ToArray());
            yield return (suffix, WindowsPathMatcher.Normalize(baseRoot));
        }
    }

    private static bool IsNested(string child, string parent) =>
        child.Length > parent.Length &&
        child.StartsWith(parent, StringComparison.OrdinalIgnoreCase) &&
        (parent.EndsWith('\\') || child[parent.Length] == '\\');

    private sealed record Source(BridgeTrack Track, string OriginalPath, string MappedPath);
    private sealed record Destination(RekordboxTrackSnapshot Track, string OriginalPath, string MappedPath);

    private sealed class RootPairComparer : IEqualityComparer<(string Source, string Target)>
    {
        public static readonly RootPairComparer Instance = new();

        public bool Equals((string Source, string Target) a, (string Source, string Target) b) =>
            string.Equals(a.Source, b.Source, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(a.Target, b.Target, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Source, string Target) value) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Source),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.Target));
    }
}
