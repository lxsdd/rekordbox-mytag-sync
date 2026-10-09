namespace RekordboxMyTagSync.Core;

public sealed record MyTagLinkAuditRow(
    string AssignmentRowId,
    string ContentId,
    string MyTagId,
    string Group,
    string Value);

public sealed record DuplicateMyTagLinkEvidence(
    string ContentId,
    string Group,
    string Value,
    IReadOnlyList<string> AssignmentRowIds,
    IReadOnlyList<string> MyTagIds)
{
    public string Reason => MyTagIds.Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1
        ? "DUPLICATE_SAME_MYTAG_ID: duplicate active assignment rows for one ContentId and MyTagID."
        : "DUPLICATE_DISPLAY_VALUE: different MyTagIDs resolve to the same group/value.";
}

public static class MyTagLinkAudit
{
    // Pure read-only classification. Never deduplicates or modifies the DB.
    public static IReadOnlyList<DuplicateMyTagLinkEvidence> FindDuplicates(
        IEnumerable<MyTagLinkAuditRow> rows) =>
        rows.GroupBy(x => (
            ContentId: x.ContentId.Trim().ToUpperInvariant(),
            Group: x.Group.Trim().ToUpperInvariant(),
            Value: x.Value.Trim().ToUpperInvariant()))
        .Where(x => x.Count() > 1)
        .Select(group =>
        {
            var ordered = group.OrderBy(x => x.AssignmentRowId, StringComparer.OrdinalIgnoreCase).ToArray();
            return new DuplicateMyTagLinkEvidence(
                ordered[0].ContentId, ordered[0].Group, ordered[0].Value,
                ordered.Select(x => x.AssignmentRowId).ToArray(),
                ordered.Select(x => x.MyTagId).ToArray());
        })
        .OrderBy(x => x.ContentId, StringComparer.OrdinalIgnoreCase)
        .ThenBy(x => x.Group, StringComparer.OrdinalIgnoreCase)
        .ThenBy(x => x.Value, StringComparer.OrdinalIgnoreCase)
        .ToArray();
}
