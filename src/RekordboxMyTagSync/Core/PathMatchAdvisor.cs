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

        var possibleSource = source
            .Where(x => !matched.Contains(x.Track) && x.Track.Subsong == 0)
            .GroupBy(x => x.OriginalPath, StringComparer.OrdinalIgnoreCase)
            .Where(x => x.Count() == 1)
            .Select(x => x.Single())
            .Select(x => (Item: x, Suffix: TrySuffix(x.OriginalPath)))
            .Where(x => x.Suffix is not null)
            .GroupBy(x => x.Suffix!.Value.Key, StringComparer.OrdinalIgnoreCase)
            .Where(x => x.Count() == 1)
            .ToDictionary(x => x.Key, x => x.Single().Item, StringComparer.OrdinalIgnoreCase);

        var possibleTarget = destination
            .GroupBy(x => x.OriginalPath, StringComparer.OrdinalIgnoreCase)
            .Where(x => x.Count() == 1)
            .Select(x => x.Single())
            .Select(x => (Item: x, Suffix: TrySuffix(x.OriginalPath)))
            .Where(x => x.Suffix is not null)
            .GroupBy(x => x.Suffix!.Value.Key, StringComparer.OrdinalIgnoreCase)
            .Where(x => x.Count() == 1)
            .ToDictionary(x => x.Key, x => x.Single().Item, StringComparer.OrdinalIgnoreCase);

        var proposals = new List<(string Source, string Target, string ExampleSource, string ExampleTarget)>();
        foreach (var (suffix, item) in possibleSource)
        {
            if (!possibleTarget.TryGetValue(suffix, out var match) ||
                string.Equals(item.MappedPath, match.MappedPath, StringComparison.OrdinalIgnoreCase))
                continue;
            var sourceSuffix = TrySuffix(item.OriginalPath)!.Value;
            var targetSuffix = TrySuffix(match.OriginalPath)!.Value;
            if (string.Equals(sourceSuffix.Root, targetSuffix.Root, StringComparison.OrdinalIgnoreCase))
                continue;
            proposals.Add((sourceSuffix.Root, targetSuffix.Root, item.Track.Path, match.Track.Path));
        }

        var raw = proposals
            .GroupBy(x => (x.Source, x.Target), RootPairComparer.Instance)
            .Select(g => new PathAliasProposal(
                g.Key.Source, g.Key.Target, g.Count(),
                g.OrderBy(x => x.ExampleSource, StringComparer.OrdinalIgnoreCase).First().ExampleSource,
                g.OrderBy(x => x.ExampleSource, StringComparer.OrdinalIgnoreCase).First().ExampleTarget))
            .Where(x => x.UniqueSuffixPairs >= 3)
            .ToArray();

        // Conflicting root suggestions are actively suppressed, not ranked.
        var unique = raw
            .Where(x => raw.Count(y => string.Equals(x.SourceRoot, y.SourceRoot, StringComparison.OrdinalIgnoreCase)) == 1)
            .Where(x => raw.Count(y => string.Equals(x.TargetRoot, y.TargetRoot, StringComparison.OrdinalIgnoreCase)) == 1)
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

    private static (string Key, string Root)? TrySuffix(string path)
    {
        var parent = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(parent)) return null;
        var root = Path.GetDirectoryName(parent);
        if (string.IsNullOrWhiteSpace(root)) return null;
        var folder = Path.GetFileName(parent.TrimEnd(Path.DirectorySeparatorChar));
        var file = Path.GetFileName(path);
        if (string.IsNullOrWhiteSpace(folder) || string.IsNullOrWhiteSpace(file)) return null;
        return (folder + "\\" + file, root);
    }

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
