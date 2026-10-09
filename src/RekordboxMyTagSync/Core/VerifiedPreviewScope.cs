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

        var sourceByMappedPath = bridge
            .GroupBy(x => WindowsPathMatcher.Normalize(x.Path, new[] { alias }),
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.OrdinalIgnoreCase);

        // Every excluded rekordbox ContentId gets its own explainable reason;
        // virtual cuesheet tracks do not count as physical file matches.
        var excluded = target.Where(x => !contentIds.Contains(x.ContentId))
            .Select(x =>
            {
                var normalized = WindowsPathMatcher.Normalize(x.Path);
                string reason;
                if (destinations[normalized].Length > 1)
                    reason = "DUPLICATE_TARGET_PATH: ContentIds=[" +
                        string.Join(", ", destinations[normalized]
                            .Select(y => y.ContentId)
                            .OrderBy(y => y, StringComparer.OrdinalIgnoreCase)) +
                        "] point to the SAME path (not necessarily different audio files).";
                else if (!sourceByMappedPath.TryGetValue(normalized, out var candidates))
                    reason = "NOT_IN_FOOBAR_SOURCE: no matching path in the inspected foobar bridge.";
                else if (candidates.All(y => y.Subsong != 0))
                    reason = "VIRTUAL_SUBSONG_ONLY: foobar has no file-level subsong-0 entry; cue segments cannot authorize MyTag changes.";
                else if (candidates.Count(y => y.Subsong == 0) > 1)
                    reason = "MULTIPLE_PHYSICAL_SOURCE: several subsong-0 records collide at this path; no unique file-level source.";
                else
                    reason = "FILE_IDENTITY_UNCONFIRMED: a unique physical subsong-0 candidate exists but its file ID was not verified.";
                return new PreviewDetail(
                    PreviewDetailKind.Unmatched, x.ContentId, x.Path, null, reason);
            })
            .OrderBy(x => x.Message, StringComparer.Ordinal)
            .ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.ContentId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new VerifiedPreviewScope(includedSource, includedTarget, excluded, alias);
    }
}
