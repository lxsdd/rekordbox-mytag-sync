using System.IO;

namespace RekordboxMyTagSync.Core;

public sealed record VerifiedPreviewScope(
    IReadOnlyList<BridgeTrack> Sources,
    IReadOnlyList<RekordboxTrackSnapshot> Targets,
    IReadOnlyList<PreviewDetail> ExcludedTargets,
    PathAlias TemporaryAlias)
{
    public static VerifiedPreviewScope Build(
        IReadOnlyList<BridgeTrack> bridge,
        IReadOnlyList<RekordboxTrackSnapshot> target,
        PathAlias alias,
        IReadOnlyList<VerifiedPhysicalPair> verified)
    {
        ArgumentNullException.ThrowIfNull(bridge);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(verified);

        var files = bridge.Where(x => x.Subsong == 0)
            .GroupBy(x => WindowsPathMatcher.Normalize(x.Path), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.OrdinalIgnoreCase);
        var destinations = target
            .GroupBy(x => WindowsPathMatcher.Normalize(x.Path), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.OrdinalIgnoreCase);
        var includedSource = new List<BridgeTrack>();
        var includedTarget = new List<RekordboxTrackSnapshot>();
        var sourceKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targetKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var contentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in verified)
        {
            var left = WindowsPathMatcher.Normalize(item.SourcePath);
            var right = WindowsPathMatcher.Normalize(item.TargetPath);
            if (!WindowsPathMatcher.Equivalent(left, right, new[] { alias }) ||
                !files.TryGetValue(left, out var sourceItems) || sourceItems.Length != 1 ||
                !destinations.TryGetValue(right, out var targetItems) || targetItems.Length != 1 ||
                !sourceKeys.Add(left) || !targetKeys.Add(right) ||
                string.IsNullOrWhiteSpace(targetItems[0].ContentId) ||
                !contentIds.Add(targetItems[0].ContentId))
                throw new InvalidDataException("Physical file evidence cannot be mapped to a unique ContentId.");
            includedSource.Add(sourceItems[0]);
            includedTarget.Add(targetItems[0]);
        }

        var excluded = target.Where(x => !contentIds.Contains(x.ContentId))
            .Select(x => new PreviewDetail(
                PreviewDetailKind.Unmatched, x.ContentId, x.Path, null,
                destinations[WindowsPathMatcher.Normalize(x.Path)].Length > 1
                    ? "Duplicate target path: physical identity not qualified."
                    : "No unique physically verified source track."))
            .OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.ContentId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new VerifiedPreviewScope(includedSource, includedTarget, excluded, alias);
    }
}
