using System.Security.Cryptography;
using System.Text;

namespace RekordboxMyTagSync.Core;

public sealed record MyTagAssignment(string Group, string Value);

public sealed record RekordboxTrackSnapshot(
    string ContentId,
    string Path,
    IReadOnlyList<MyTagAssignment> Assignments);

public sealed record ManagedAssignment(string ContentId, MyTagAssignment Tag);

public enum PreviewDetailKind
{
    Add,
    Remove,
    AlreadyCorrect,
    Conflict,
    Unmatched
}

public sealed record PreviewDetail(
    PreviewDetailKind Kind,
    string? ContentId,
    string? Path,
    MyTagAssignment? Tag,
    string Message);

public sealed record PreviewCounts(
    int Additions,
    int Removals,
    int AlreadyCorrect,
    int Conflicts,
    int Unmatched);

public sealed record PreviewRequest(
    string DatabaseIdentity,
    IReadOnlyList<BridgeTrack> BridgeTracks,
    IReadOnlyList<MappingRule> Mappings,
    IReadOnlyList<RekordboxTrackSnapshot> RekordboxTracks,
    IReadOnlyList<ManagedAssignment> ManagedAssignments,
    IReadOnlyList<PathAlias>? PathAliases = null);

public sealed record PreviewResult(
    bool IsValid,
    string FingerprintSha256,
    PreviewCounts Counts,
    IReadOnlyList<PreviewDetail> Details);

public static class PreviewEngine
{
    private static readonly MyTagAssignmentComparer TagComparer = new();

    public static PreviewResult Create(PreviewRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DatabaseIdentity);
        ArgumentNullException.ThrowIfNull(request.BridgeTracks);
        ArgumentNullException.ThrowIfNull(request.Mappings);
        ArgumentNullException.ThrowIfNull(request.RekordboxTracks);
        ArgumentNullException.ThrowIfNull(request.ManagedAssignments);

        var aliases = request.PathAliases ?? Array.Empty<PathAlias>();
        var details = new List<PreviewDetail>();

        var normalizedTargets = new List<NormalizedTarget>();
        var targetContentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var track in request.RekordboxTracks)
        {
            if (string.IsNullOrWhiteSpace(track.ContentId))
            {
                details.Add(Conflict(null, track.Path, null, "Rekordbox track has an empty ContentId."));
                continue;
            }
            if (!targetContentIds.Add(track.ContentId))
            {
                details.Add(Conflict(track.ContentId, track.Path, null, "Duplicate rekordbox ContentId."));
                continue;
            }

            if (!TryNormalizePath(track.Path, aliases, out var canonicalPath, out var pathError))
            {
                details.Add(Conflict(track.ContentId, track.Path, null, $"Unsafe rekordbox path: {pathError}"));
                continue;
            }

            var current = new HashSet<MyTagAssignment>(TagComparer);
            var duplicateCurrent = false;
            foreach (var rawTag in track.Assignments ?? Array.Empty<MyTagAssignment>())
            {
                if (!TryNormalizeTag(rawTag, out var normalizedTag))
                {
                    details.Add(Conflict(track.ContentId, canonicalPath, rawTag, "Current rekordbox MyTag assignment contains an empty group or value."));
                    continue;
                }
                if (!current.Add(normalizedTag)) duplicateCurrent = true;
            }
            if (duplicateCurrent)
                details.Add(Conflict(track.ContentId, canonicalPath, null, "Duplicate current Track↔MyTag assignment detected."));

            normalizedTargets.Add(new NormalizedTarget(track.ContentId, canonicalPath, current));
        }

        var targetsByPath = normalizedTargets
            .GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.OrdinalIgnoreCase);
        var ambiguousTargetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in targetsByPath.Where(x => x.Value.Length > 1))
        {
            ambiguousTargetPaths.Add(pair.Key);
            details.Add(Conflict(
                null,
                pair.Key,
                null,
                $"Multiple rekordbox ContentIds resolve to the same canonical path: {string.Join(", ", pair.Value.Select(x => x.ContentId).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))}."));
        }

        var normalizedBridge = new List<NormalizedBridge>();
        foreach (var track in request.BridgeTracks)
        {
            if (!TryNormalizePath(track.Path, aliases, out var canonicalPath, out var pathError))
            {
                details.Add(Conflict(null, track.Path, null, $"Unsafe bridge path: {pathError}"));
                continue;
            }
            normalizedBridge.Add(new NormalizedBridge(track, canonicalPath));
        }

        var bridgeByPath = normalizedBridge
            .GroupBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.OrdinalIgnoreCase);
        var ambiguousBridgePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in bridgeByPath.Where(x => x.Value.Length > 1))
        {
            ambiguousBridgePaths.Add(pair.Key);
            var identities = pair.Value
                .Select(x => $"{x.Track.Path}#{x.Track.Subsong}")
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase);
            details.Add(Conflict(null, pair.Key, null,
                $"Multiple bridge (path, subsong) identities resolve to one target path: {string.Join(", ", identities)}."));
        }

        var managedByContent = BuildManagedIndex(request.ManagedAssignments, normalizedTargets, details);
        var matchedContentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var bridge in normalizedBridge.OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Track.Subsong))
        {
            if (ambiguousBridgePaths.Contains(bridge.Path) || ambiguousTargetPaths.Contains(bridge.Path)) continue;
            if (!targetsByPath.TryGetValue(bridge.Path, out var targetCandidates) || targetCandidates.Length != 1)
            {
                details.Add(new PreviewDetail(
                    PreviewDetailKind.Unmatched,
                    null,
                    bridge.Path,
                    null,
                    $"Bridge track is not matched to a rekordbox ContentId (subsong {bridge.Track.Subsong})."));
                continue;
            }

            var target = targetCandidates[0];
            matchedContentIds.Add(target.ContentId);
            var desired = BuildDesiredAssignments(bridge, request.Mappings, target.ContentId, details);

            foreach (var tag in desired.OrderBy(x => x.Group, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Value, StringComparer.OrdinalIgnoreCase))
            {
                if (target.Current.Contains(tag))
                    details.Add(new PreviewDetail(PreviewDetailKind.AlreadyCorrect, target.ContentId, target.Path, tag, "Assignment already correct."));
                else
                    details.Add(new PreviewDetail(PreviewDetailKind.Add, target.ContentId, target.Path, tag, "Assignment will be added."));
            }

            if (!managedByContent.TryGetValue(target.ContentId, out var managed)) continue;
            foreach (var tag in managed.OrderBy(x => x.Group, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Value, StringComparer.OrdinalIgnoreCase))
            {
                if (!target.Current.Contains(tag))
                {
                    details.Add(Conflict(target.ContentId, target.Path, tag,
                        "Managed-assignment provenance does not correspond to a current rekordbox assignment."));
                    continue;
                }
                if (!desired.Contains(tag))
                    details.Add(new PreviewDetail(PreviewDetailKind.Remove, target.ContentId, target.Path, tag,
                        "Tool-owned stale assignment will be removed; unmanaged/manual assignments are preserved."));
            }
        }

        foreach (var pair in managedByContent)
        {
            if (matchedContentIds.Contains(pair.Key)) continue;
            var target = normalizedTargets.FirstOrDefault(x => string.Equals(x.ContentId, pair.Key, StringComparison.OrdinalIgnoreCase));
            if (target is null) continue;
            foreach (var tag in pair.Value)
            {
                if (!target.Current.Contains(tag))
                    details.Add(Conflict(target.ContentId, target.Path, tag,
                        "Managed-assignment provenance does not correspond to a current rekordbox assignment."));
            }
        }

        var ordered = details
            .OrderBy(x => x.Kind)
            .ThenBy(x => x.Path ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.ContentId ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Tag?.Group ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Tag?.Value ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Message, StringComparer.Ordinal)
            .ToArray();

        var counts = new PreviewCounts(
            ordered.Count(x => x.Kind == PreviewDetailKind.Add),
            ordered.Count(x => x.Kind == PreviewDetailKind.Remove),
            ordered.Count(x => x.Kind == PreviewDetailKind.AlreadyCorrect),
            ordered.Count(x => x.Kind == PreviewDetailKind.Conflict),
            ordered.Count(x => x.Kind == PreviewDetailKind.Unmatched));

        var fingerprint = BuildFingerprint(request, aliases, normalizedBridge, normalizedTargets, ordered);
        return new PreviewResult(counts.Conflicts == 0, fingerprint, counts, ordered);
    }

    private static Dictionary<string, HashSet<MyTagAssignment>> BuildManagedIndex(
        IReadOnlyList<ManagedAssignment> managedAssignments,
        IReadOnlyList<NormalizedTarget> targets,
        List<PreviewDetail> details)
    {
        var targetIds = targets.Select(x => x.ContentId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<string, HashSet<MyTagAssignment>>(StringComparer.OrdinalIgnoreCase);
        foreach (var managed in managedAssignments)
        {
            if (string.IsNullOrWhiteSpace(managed.ContentId))
            {
                details.Add(Conflict(null, null, managed.Tag, "Managed-assignment provenance has an empty ContentId."));
                continue;
            }
            if (!TryNormalizeTag(managed.Tag, out var normalizedTag))
            {
                details.Add(Conflict(managed.ContentId, null, managed.Tag, "Managed-assignment provenance contains an empty group or value."));
                continue;
            }
            if (!targetIds.Contains(managed.ContentId))
            {
                details.Add(Conflict(managed.ContentId, null, normalizedTag, "Managed-assignment provenance references a missing rekordbox ContentId."));
                continue;
            }
            if (!result.TryGetValue(managed.ContentId, out var set))
                result[managed.ContentId] = set = new HashSet<MyTagAssignment>(TagComparer);
            if (!set.Add(normalizedTag))
                details.Add(Conflict(managed.ContentId, null, normalizedTag, "Duplicate managed-assignment provenance entry."));
        }
        return result;
    }

    private static HashSet<MyTagAssignment> BuildDesiredAssignments(
        NormalizedBridge bridge,
        IReadOnlyList<MappingRule> mappings,
        string contentId,
        List<PreviewDetail> details)
    {
        var desired = new HashSet<MyTagAssignment>(TagComparer);
        foreach (var rule in mappings)
        {
            IReadOnlyList<string> values;
            try
            {
                values = MappingEngine.Apply(rule, bridge.Track.GetFieldValues(rule.SourceField));
            }
            catch (Exception ex) when (ex is ArgumentException or RegexMatchTimeoutException)
            {
                details.Add(Conflict(contentId, bridge.Path, null,
                    $"Mapping '{rule.SourceField}' → '{rule.TargetMyTag}' failed: {ex.Message}"));
                continue;
            }

            foreach (var value in values)
            {
                var rawTag = new MyTagAssignment(rule.TargetMyTag, value);
                if (!TryNormalizeTag(rawTag, out var tag))
                {
                    details.Add(Conflict(contentId, bridge.Path, rawTag,
                        $"Mapping '{rule.SourceField}' produced an empty MyTag group or value."));
                    continue;
                }
                desired.Add(tag);
            }
        }
        return desired;
    }

    private static bool TryNormalizeTag(MyTagAssignment? tag, out MyTagAssignment normalized)
    {
        if (tag is null || string.IsNullOrWhiteSpace(tag.Group) || string.IsNullOrWhiteSpace(tag.Value))
        {
            normalized = new MyTagAssignment(string.Empty, string.Empty);
            return false;
        }
        normalized = new MyTagAssignment(tag.Group.Trim(), tag.Value.Trim());
        return true;
    }

    private static bool TryNormalizePath(
        string path,
        IReadOnlyList<PathAlias> aliases,
        out string normalized,
        out string error)
    {
        try
        {
            normalized = WindowsPathMatcher.Normalize(path, aliases);
            error = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            normalized = path ?? string.Empty;
            error = ex.Message;
            return false;
        }
    }

    private static PreviewDetail Conflict(string? contentId, string? path, MyTagAssignment? tag, string message) =>
        new(PreviewDetailKind.Conflict, contentId, path, tag, message);

    private static string BuildFingerprint(
        PreviewRequest request,
        IReadOnlyList<PathAlias> aliases,
        IReadOnlyList<NormalizedBridge> bridgeTracks,
        IReadOnlyList<NormalizedTarget> targets,
        IReadOnlyList<PreviewDetail> details)
    {
        var writer = new FingerprintWriter();
        writer.Add("db", request.DatabaseIdentity.Trim());

        foreach (var alias in aliases
                     .Select(x => (Source: SafeCanonical(x.SourceRoot), Target: SafeCanonical(x.TargetRoot)))
                     .OrderBy(x => x.Source, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(x => x.Target, StringComparer.OrdinalIgnoreCase))
            writer.Add("alias", alias.Source, alias.Target);

        foreach (var rule in request.Mappings
                     .OrderBy(x => x.SourceField, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(x => x.TargetMyTag, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(x => x.Transform)
                     .ThenBy(x => x.PerValue)
                     .ThenBy(x => x.IgnoreEmpty)
                     .ThenBy(x => x.Prefix, StringComparer.Ordinal)
                     .ThenBy(x => x.Pattern ?? string.Empty, StringComparer.Ordinal)
                     .ThenBy(x => x.Replacement, StringComparer.Ordinal))
            writer.Add("mapping", rule.SourceField, rule.TargetMyTag, rule.Transform.ToString(), rule.PerValue.ToString(),
                rule.IgnoreEmpty.ToString(), rule.Prefix, rule.Pattern ?? string.Empty, rule.Replacement);

        foreach (var bridge in bridgeTracks
                     .OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(x => x.Track.Subsong))
        {
            writer.Add("bridge", bridge.Path, bridge.Track.Subsong.ToString());
            AddFields(writer, "core", bridge.Track.Core);
            AddFields(writer, "extra", bridge.Track.Extra);
        }

        foreach (var target in targets
                     .OrderBy(x => x.ContentId, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase))
        {
            writer.Add("target", target.ContentId, target.Path);
            foreach (var tag in target.Current.OrderBy(x => x.Group, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Value, StringComparer.OrdinalIgnoreCase))
                writer.Add("current", target.ContentId, tag.Group, tag.Value);
        }

        foreach (var managed in request.ManagedAssignments
                     .OrderBy(x => x.ContentId, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(x => x.Tag?.Group ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(x => x.Tag?.Value ?? string.Empty, StringComparer.OrdinalIgnoreCase))
            writer.Add("managed", managed.ContentId, managed.Tag?.Group ?? string.Empty, managed.Tag?.Value ?? string.Empty);

        foreach (var detail in details)
            writer.Add("detail", detail.Kind.ToString(), detail.ContentId ?? string.Empty, detail.Path ?? string.Empty,
                detail.Tag?.Group ?? string.Empty, detail.Tag?.Value ?? string.Empty, detail.Message);

        return writer.Hash();
    }

    private static void AddFields(FingerprintWriter writer, string kind, IReadOnlyDictionary<string, IReadOnlyList<string>> fields)
    {
        foreach (var pair in fields.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            writer.Add(kind + "-field", pair.Key);
            for (var i = 0; i < pair.Value.Count; i++) writer.Add(kind + "-value", pair.Key, i.ToString(), pair.Value[i]);
        }
    }

    private static string SafeCanonical(string path)
    {
        try { return WindowsPathMatcher.Normalize(path); }
        catch { return path ?? string.Empty; }
    }

    private sealed record NormalizedBridge(BridgeTrack Track, string Path);
    private sealed record NormalizedTarget(string ContentId, string Path, HashSet<MyTagAssignment> Current);

    private sealed class MyTagAssignmentComparer : IEqualityComparer<MyTagAssignment>
    {
        public bool Equals(MyTagAssignment? x, MyTagAssignment? y) =>
            ReferenceEquals(x, y) ||
            (x is not null && y is not null &&
             string.Equals(x.Group, y.Group, StringComparison.OrdinalIgnoreCase) &&
             string.Equals(x.Value, y.Value, StringComparison.OrdinalIgnoreCase));

        public int GetHashCode(MyTagAssignment obj) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Group), StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Value));
    }

    private sealed class FingerprintWriter
    {
        private readonly StringBuilder _builder = new();

        public void Add(params string[] parts)
        {
            foreach (var part in parts)
            {
                var value = part ?? string.Empty;
                _builder.Append(value.Length).Append(':').Append(value).Append('|');
            }
            _builder.Append('\n');
        }

        public string Hash()
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(_builder.ToString()));
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }
    }
}
