using System.Diagnostics;

namespace RekordboxMyTagSync.Core;

public static class RekordboxProcessGuard
{
    private static readonly string[] RekordboxProcessNames = ["rekordbox"];

    public static bool IsRunning()
    {
        foreach (var processName in RekordboxProcessNames)
        {
            Process[] processes;
            try { processes = Process.GetProcessesByName(processName); }
            catch { return true; }

            try
            {
                if (processes.Length != 0) return true;
            }
            finally
            {
                foreach (var process in processes) process.Dispose();
            }
        }
        return false;
    }
}

public enum MyTagMutationKind
{
    AddAssignment,
    RemoveAssignment
}

public sealed record MyTagMutationOperation(
    MyTagMutationKind Kind,
    string ContentId,
    string MyTagId,
    MyTagAssignment Tag);

public sealed record MyTagMutationPlan(
    bool IsValid,
    string PreviewFingerprintSha256,
    IReadOnlyList<MyTagMutationOperation> Operations,
    IReadOnlyList<string> Errors);

public static class MyTagMutationPlanner
{
    private static readonly ManagedKeyComparer KeyComparer = new();

    public static MyTagMutationPlan Create(
        PreviewRequest currentRequest,
        PreviewResult approvedPreview,
        IReadOnlyList<RekordboxMyTagDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(currentRequest);
        ArgumentNullException.ThrowIfNull(approvedPreview);
        ArgumentNullException.ThrowIfNull(definitions);

        var errors = new List<string>();
        var currentPreview = PreviewEngine.Create(currentRequest);

        if (!approvedPreview.IsValid)
            errors.Add("Approved preview is invalid and cannot authorize a mutation plan.");
        if (!currentPreview.IsValid)
            errors.Add("Current preview is invalid and cannot authorize a mutation plan.");
        if (string.IsNullOrWhiteSpace(approvedPreview.FingerprintSha256) ||
            !string.Equals(approvedPreview.FingerprintSha256, currentPreview.FingerprintSha256, StringComparison.Ordinal))
            errors.Add("Approved preview is stale for the current database, source, mapping, path-alias or provenance state.");

        var definitionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in definitions)
        {
            if (string.IsNullOrWhiteSpace(definition.Id))
            {
                errors.Add("Active MyTag definition has an empty ID.");
                continue;
            }
            if (string.IsNullOrWhiteSpace(definition.Name))
            {
                errors.Add($"Active MyTag definition '{definition.Id}' has an empty name.");
                continue;
            }
            if (!definitionIds.Add(definition.Id))
                errors.Add($"Duplicate active MyTag definition ID '{definition.Id}'.");
        }

        if (errors.Count != 0)
            return Invalid(currentPreview.FingerprintSha256, errors);

        var parentIds = definitions
            .Where(x => !string.IsNullOrWhiteSpace(x.ParentId))
            .Select(x => x.ParentId!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var managed = new HashSet<ManagedKey>(KeyComparer);
        foreach (var item in currentRequest.ManagedAssignments)
        {
            if (string.IsNullOrWhiteSpace(item.ContentId) || item.Tag is null ||
                string.IsNullOrWhiteSpace(item.Tag.Group) || string.IsNullOrWhiteSpace(item.Tag.Value))
                continue;
            managed.Add(new ManagedKey(
                item.ContentId.Trim(),
                item.Tag.Group.Trim(),
                item.Tag.Value.Trim()));
        }

        var operations = new List<MyTagMutationOperation>();
        foreach (var detail in currentPreview.Details)
        {
            if (detail.Kind is not (PreviewDetailKind.Add or PreviewDetailKind.Remove)) continue;

            if (string.IsNullOrWhiteSpace(detail.ContentId) || detail.Tag is null ||
                string.IsNullOrWhiteSpace(detail.Tag.Group) || string.IsNullOrWhiteSpace(detail.Tag.Value))
            {
                errors.Add($"Preview {detail.Kind} detail lacks a complete ContentId/MyTag identity.");
                continue;
            }

            var contentId = detail.ContentId.Trim();
            var tag = new MyTagAssignment(detail.Tag.Group.Trim(), detail.Tag.Value.Trim());

            if (detail.Kind == PreviewDetailKind.Remove &&
                !managed.Contains(new ManagedKey(contentId, tag.Group, tag.Value)))
            {
                errors.Add($"Removal '{contentId}' → '{tag.Group}/{tag.Value}' is not proven tool-owned by current provenance.");
                continue;
            }

            var groups = definitions
                .Where(x => parentIds.Contains(x.Id) &&
                            string.Equals(x.Name.Trim(), tag.Group, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (groups.Length == 0)
            {
                errors.Add($"No existing MyTag group matches '{tag.Group}'. Definition creation is intentionally not inferred by the planner.");
                continue;
            }
            if (groups.Length != 1)
            {
                errors.Add($"MyTag group '{tag.Group}' is ambiguous across {groups.Length} active definitions.");
                continue;
            }

            var group = groups[0];
            var values = definitions
                .Where(x => string.Equals(x.ParentId, group.Id, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(x.Name.Trim(), tag.Value, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (values.Length == 0)
            {
                errors.Add($"No existing MyTag value matches '{tag.Group}/{tag.Value}'. Definition creation is intentionally not inferred by the planner.");
                continue;
            }
            if (values.Length != 1)
            {
                errors.Add($"MyTag value '{tag.Group}/{tag.Value}' is ambiguous across {values.Length} active definitions.");
                continue;
            }

            operations.Add(new MyTagMutationOperation(
                detail.Kind == PreviewDetailKind.Add ? MyTagMutationKind.AddAssignment : MyTagMutationKind.RemoveAssignment,
                contentId,
                values[0].Id,
                tag));
        }

        var duplicates = operations
            .GroupBy(x => new OperationKey(x.Kind, x.ContentId, x.MyTagId), OperationKeyComparer.Instance)
            .Where(x => x.Count() > 1)
            .Select(x => x.Key)
            .ToArray();
        foreach (var duplicate in duplicates)
            errors.Add($"Duplicate mutation operation detected: {duplicate.Kind} {duplicate.ContentId} → {duplicate.MyTagId}.");

        if (errors.Count != 0)
            return Invalid(currentPreview.FingerprintSha256, errors);

        var ordered = operations
            .OrderBy(x => x.Kind)
            .ThenBy(x => x.ContentId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Tag.Group, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Tag.Value, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.MyTagId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new MyTagMutationPlan(true, currentPreview.FingerprintSha256, ordered, Array.Empty<string>());
    }

    private static MyTagMutationPlan Invalid(string fingerprint, IReadOnlyList<string> errors) =>
        new(false, fingerprint, Array.Empty<MyTagMutationOperation>(), errors.ToArray());

    private sealed record ManagedKey(string ContentId, string Group, string Value);

    private sealed class ManagedKeyComparer : IEqualityComparer<ManagedKey>
    {
        public bool Equals(ManagedKey? x, ManagedKey? y) =>
            ReferenceEquals(x, y) ||
            (x is not null && y is not null &&
             string.Equals(x.ContentId, y.ContentId, StringComparison.OrdinalIgnoreCase) &&
             string.Equals(x.Group, y.Group, StringComparison.OrdinalIgnoreCase) &&
             string.Equals(x.Value, y.Value, StringComparison.OrdinalIgnoreCase));

        public int GetHashCode(ManagedKey obj) => HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.ContentId),
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Group),
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Value));
    }

    private sealed record OperationKey(MyTagMutationKind Kind, string ContentId, string MyTagId);

    private sealed class OperationKeyComparer : IEqualityComparer<OperationKey>
    {
        public static readonly OperationKeyComparer Instance = new();

        public bool Equals(OperationKey? x, OperationKey? y) =>
            ReferenceEquals(x, y) ||
            (x is not null && y is not null && x.Kind == y.Kind &&
             string.Equals(x.ContentId, y.ContentId, StringComparison.OrdinalIgnoreCase) &&
             string.Equals(x.MyTagId, y.MyTagId, StringComparison.OrdinalIgnoreCase));

        public int GetHashCode(OperationKey obj) => HashCode.Combine(
            obj.Kind,
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.ContentId),
            StringComparer.OrdinalIgnoreCase.GetHashCode(obj.MyTagId));
    }
}
