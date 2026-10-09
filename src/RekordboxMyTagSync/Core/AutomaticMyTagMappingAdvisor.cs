using System.IO;

namespace RekordboxMyTagSync.Core;

/// <summary>
/// Suggests rules for preview only; it never creates MyTag definitions,
/// updates settings, approves an alias or authorizes a database write.
/// </summary>
public sealed record AutomaticMyTagMappingAdvice(
    IReadOnlyList<MappingRule> Rules,
    IReadOnlyList<string> Messages,
    int YearSourceTracks,
    int GenreSourceTracks)
{
    public bool HasRules => Rules.Count > 0;
}

public static class AutomaticMyTagMappingAdvisor
{
    public static AutomaticMyTagMappingAdvice Suggest(
        IReadOnlyList<BridgeTrack> verifiedPhysicalTracks,
        IReadOnlyList<RekordboxMyTagDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(verifiedPhysicalTracks);
        ArgumentNullException.ThrowIfNull(definitions);

        // These exact standard groups are the ONLY automatically proposed
        // destinations; similarly named groups never qualify.
        var rules = new List<MappingRule>(2);
        var messages = new List<string>();
        var yearTracks = verifiedPhysicalTracks.Count(t =>
            t.GetFieldValues("DATE").Any(v => !string.IsNullOrWhiteSpace(v)));
        var genreTracks = verifiedPhysicalTracks.Count(t =>
            t.GetFieldValues("GENRE").Any(v => !string.IsNullOrWhiteSpace(v)));

        CheckGroup("Year", "DATE", TransformKind.StrictYearFromDate, yearTracks);
        CheckGroup("Genre", "GENRE", TransformKind.Direct, genreTracks);

        return new AutomaticMyTagMappingAdvice(rules, messages, yearTracks, genreTracks);

        void CheckGroup(string group, string field, TransformKind transform, int sourceCount)
        {
            if (sourceCount == 0)
            {
                messages.Add(field + ": No nonempty source metadata; no automatic rule.");
                return;
            }

            var parents = definitions.Where(x =>
                x.ParentId is null &&
                string.Equals(x.Name?.Trim(), group, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (parents.Length != 1)
            {
                messages.Add(field + ": MyTag group '" + group + "' is " +
                    (parents.Length == 0 ? "absent" : "ambiguous") +
                    "; automatic mapping skipped.");
                return;
            }

            var children = definitions.Where(x =>
                string.Equals(x.ParentId, parents[0].Id, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (children.Length == 0 ||
                children.Any(x => string.IsNullOrWhiteSpace(x.Name)) ||
                children.GroupBy(x => x.Name.Trim(), StringComparer.OrdinalIgnoreCase)
                    .Any(x => x.Count() > 1))
            {
                messages.Add(field + ": MyTag values under '" + group +
                    "' are missing or ambiguous; automatic mapping skipped.");
                return;
            }

            rules.Add(new MappingRule(field, group, transform));
            messages.Add(field + " → " + group +
                " suggested for read-only preview (" + sourceCount.ToString("N0") +
                " source tracks contain the field; existing values are validated separately).");
        }
    }
}
