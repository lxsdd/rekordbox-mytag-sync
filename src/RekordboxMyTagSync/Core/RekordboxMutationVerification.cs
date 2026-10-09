using System.IO;
using Microsoft.Data.Sqlite;

namespace RekordboxMyTagSync.Core;

internal static class RekordboxMutationVerification
{
    internal static ProvenanceDocument VerifyPostimageAndBuildProvenance(
        RekordboxDatabaseSnapshot before,
        RekordboxDatabaseSnapshot after,
        ProvenanceDocument currentProvenance,
        IReadOnlyList<RekordboxAssignmentMutation> mutations,
        IReadOnlyList<RekordboxCreatedMyTagDefinition>? createdDefinitions = null)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        ArgumentNullException.ThrowIfNull(currentProvenance);
        ArgumentNullException.ThrowIfNull(mutations);

        VerifyStableIdentity(before.Identity, after.Identity);
        VerifyDefinitions(
            before.MyTagDefinitions,
            after.MyTagDefinitions,
            createdDefinitions ?? Array.Empty<RekordboxCreatedMyTagDefinition>());

        var beforeTracks = IndexTracks(before.Tracks, "preimage");
        var afterTracks = IndexTracks(after.Tracks, "postimage");
        if (!beforeTracks.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase)
                .SetEquals(afterTracks.Keys))
            throw new InvalidDataException("Track set changed during MyTag assignment mutation.");

        var expected = beforeTracks.ToDictionary(
            x => x.Key,
            x => AssignmentSet(x.Value.Assignments, $"preimage track '{x.Key}'"),
            StringComparer.OrdinalIgnoreCase);

        var mutationLinks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mutation in mutations)
        {
            if (mutation.Kind is not (PreviewDetailKind.Add or PreviewDetailKind.Remove))
                throw new InvalidDataException($"Unsupported postimage mutation kind '{mutation.Kind}'.");
            if (string.IsNullOrWhiteSpace(mutation.ContentId) ||
                string.IsNullOrWhiteSpace(mutation.MyTagId) ||
                string.IsNullOrWhiteSpace(mutation.Group) ||
                string.IsNullOrWhiteSpace(mutation.Value))
                throw new InvalidDataException("Postimage mutation contains an incomplete assignment identity.");
            if (!expected.TryGetValue(mutation.ContentId, out var assignments))
                throw new InvalidDataException($"Mutation references missing preimage ContentID '{mutation.ContentId}'.");

            var link = mutation.ContentId.Trim() + "\0" + mutation.MyTagId.Trim();
            if (!mutationLinks.Add(link))
                throw new InvalidDataException("Mutation list contains duplicate ContentID↔MyTagID operations.");

            var tag = new MyTagAssignment(mutation.Group.Trim(), mutation.Value.Trim());
            if (mutation.Kind == PreviewDetailKind.Add)
            {
                if (!assignments.Add(tag))
                    throw new InvalidDataException(
                        $"Add mutation '{mutation.ContentId}' → '{mutation.Group}/{mutation.Value}' was already present in preimage.");
            }
            else if (!assignments.Remove(tag))
            {
                throw new InvalidDataException(
                    $"Remove mutation '{mutation.ContentId}' → '{mutation.Group}/{mutation.Value}' was absent from preimage.");
            }
        }

        foreach (var pair in beforeTracks)
        {
            var afterTrack = afterTracks[pair.Key];
            if (!string.Equals(pair.Value.Path, afterTrack.Path, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"Track path changed during mutation for ContentID '{pair.Key}'.");

            var actual = AssignmentSet(afterTrack.Assignments, $"postimage track '{pair.Key}'");
            if (!expected[pair.Key].SetEquals(actual))
                throw new InvalidDataException($"Unexpected MyTag assignment delta detected for ContentID '{pair.Key}'.");
        }

        var nextOwned = currentProvenance.Assignments.ToDictionary(
            x => OwnedLink(x.ContentId, x.MyTagId),
            x => x,
            StringComparer.OrdinalIgnoreCase);
        foreach (var mutation in mutations)
        {
            var key = OwnedLink(mutation.ContentId, mutation.MyTagId);
            if (mutation.Kind == PreviewDetailKind.Add)
            {
                if (nextOwned.ContainsKey(key))
                    throw new InvalidDataException(
                        $"Provenance already owns Add mutation '{mutation.ContentId}' → '{mutation.MyTagId}'.");
                nextOwned[key] = new OwnedAssignment(
                    mutation.ContentId.Trim(),
                    mutation.MyTagId.Trim(),
                    mutation.Group.Trim(),
                    mutation.Value.Trim());
            }
            else
            {
                if (!nextOwned.Remove(key))
                    throw new InvalidDataException(
                        $"Remove mutation '{mutation.ContentId}' → '{mutation.MyTagId}' is not tool-owned in provenance.");
            }
        }

        return ProvenanceStore.ReplaceAssignments(
            currentProvenance,
            after.Identity,
            nextOwned.Values);
    }

    internal static void VerifySqliteIntegrity(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.State != System.Data.ConnectionState.Open)
            throw new InvalidOperationException("SQLite connection must be open for integrity validation.");

        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
            rows.Add(reader.IsDBNull(0) ? string.Empty : reader.GetString(0));
        if (rows.Count != 1 || !string.Equals(rows[0], "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "SQLite integrity_check failed: " + (rows.Count == 0 ? "no result" : string.Join(" | ", rows)));
    }

    private static void VerifyStableIdentity(
        RekordboxDatabaseIdentity before,
        RekordboxDatabaseIdentity after)
    {
        if (!string.Equals(before.DbId, after.DbId, StringComparison.Ordinal) ||
            !string.Equals(before.DbVersion, after.DbVersion, StringComparison.Ordinal) ||
            !string.Equals(
                Path.GetFullPath(before.CanonicalPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(after.CanonicalPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("rekordbox database identity changed during mutation.");
    }

    private static void VerifyDefinitions(
        IReadOnlyList<RekordboxMyTagDefinition> before,
        IReadOnlyList<RekordboxMyTagDefinition> after,
        IReadOnlyList<RekordboxCreatedMyTagDefinition> created)
    {
        static string Key(
            string id,
            string name,
            string? parentId,
            int? sequence,
            int? attribute) => string.Join("\0",
                id,
                name,
                RekordboxMyTagHierarchy.NormalizeForSnapshot(parentId) ?? string.Empty,
                sequence?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                attribute?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);

        var expected = before
            .Select(x => Key(x.Id, x.Name, x.ParentId, x.Sequence, x.Attribute))
            .ToList();
        var ids = before
            .Select(x => x.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var item in created)
        {
            if (string.IsNullOrWhiteSpace(item.Id) ||
                string.IsNullOrWhiteSpace(item.Name) ||
                !ids.Add(item.Id.Trim()))
                throw new InvalidDataException(
                    "Expected created MyTag definitions contain an empty or duplicate identity.");
            expected.Add(Key(
                item.Id.Trim(),
                item.Name.Trim(),
                item.ParentId?.Trim(),
                item.Sequence,
                item.Attribute));
        }

        var actual = after
            .Select(x => Key(x.Id, x.Name, x.ParentId, x.Sequence, x.Attribute))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();
        var orderedExpected = expected
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToArray();

        if (!orderedExpected.SequenceEqual(actual, StringComparer.Ordinal))
            throw new InvalidDataException(
                created.Count == 0
                    ? "MyTag definitions changed during assignment-only mutation."
                    : "MyTag definition postimage does not match the expected created definitions.");
    }

    private static Dictionary<string, RekordboxTrackSnapshot> IndexTracks(
        IReadOnlyList<RekordboxTrackSnapshot> tracks,
        string label)
    {
        var result = new Dictionary<string, RekordboxTrackSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var track in tracks)
        {
            if (string.IsNullOrWhiteSpace(track.ContentId) || !result.TryAdd(track.ContentId.Trim(), track))
                throw new InvalidDataException($"{label} contains an empty or duplicate ContentID.");
        }
        return result;
    }

    private static HashSet<MyTagAssignment> AssignmentSet(
        IReadOnlyList<MyTagAssignment> assignments,
        string label)
    {
        var result = new HashSet<MyTagAssignment>(MyTagComparer.Instance);
        foreach (var assignment in assignments ?? Array.Empty<MyTagAssignment>())
        {
            if (assignment is null || string.IsNullOrWhiteSpace(assignment.Group) || string.IsNullOrWhiteSpace(assignment.Value))
                throw new InvalidDataException($"{label} contains an incomplete MyTag assignment.");
            var normalized = new MyTagAssignment(assignment.Group.Trim(), assignment.Value.Trim());
            if (!result.Add(normalized))
                throw new InvalidDataException($"{label} contains a duplicate MyTag assignment.");
        }
        return result;
    }

    private static string OwnedLink(string contentId, string myTagId) =>
        contentId.Trim() + "\0" + myTagId.Trim();

    private sealed class MyTagComparer : IEqualityComparer<MyTagAssignment>
    {
        internal static readonly MyTagComparer Instance = new();

        public bool Equals(MyTagAssignment? x, MyTagAssignment? y) =>
            ReferenceEquals(x, y) ||
            (x is not null && y is not null &&
             string.Equals(x.Group, y.Group, StringComparison.OrdinalIgnoreCase) &&
             string.Equals(x.Value, y.Value, StringComparison.OrdinalIgnoreCase));

        public int GetHashCode(MyTagAssignment obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Group),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Value));
    }
}
