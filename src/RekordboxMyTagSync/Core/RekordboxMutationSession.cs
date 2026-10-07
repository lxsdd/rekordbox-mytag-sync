using System.IO;
using Microsoft.Data.Sqlite;

namespace RekordboxMyTagSync.Core;

internal sealed class RekordboxMutationSession : IDisposable
{
    private readonly SqliteConnection _connection;

    private RekordboxMutationSession(SqliteConnection connection, string canonicalPath)
    {
        _connection = connection;
        CanonicalPath = canonicalPath;
    }

    internal string CanonicalPath { get; }
    internal SqliteConnection Connection => _connection;

    internal static RekordboxMutationSession Open(string databasePath, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (RekordboxProcessGuard.IsRunning())
            throw new InvalidOperationException("rekordbox is running. Database mutation is blocked until rekordbox is closed.");

        var canonicalPath = Path.GetFullPath(databasePath);
        if (!File.Exists(canonicalPath))
            throw new FileNotFoundException("rekordbox master.db not found.", canonicalPath);

        RekordboxSqlCipherDatabase.EnsureSqliteInitialized();
        var connection = new SqliteConnection(
            RekordboxSqlCipherDatabase.BuildConnectionString(canonicalPath, key, SqliteOpenMode.ReadWrite));
        try
        {
            connection.Open();
            return new RekordboxMutationSession(connection, canonicalPath);
        }
        catch (SqliteException ex)
        {
            connection.Dispose();
            throw new InvalidDataException(
                "Could not open rekordbox master.db for mutation with the configured SQLCipher profile.", ex);
        }
    }

    internal SqliteTransaction BeginTransaction() => _connection.BeginTransaction();

    public void Dispose() => _connection.Dispose();
}


internal sealed record RekordboxAssignmentMutation(
    PreviewDetailKind Kind,
    string ContentId,
    string MyTagId,
    string Group,
    string Value);

internal static class RekordboxMutationPlan
{
    internal static IReadOnlyList<RekordboxMyTagDefinition> BuildPostDefinitionState(
        RekordboxDatabaseSnapshot snapshot,
        IReadOnlyList<RekordboxCreatedMyTagDefinition> created)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(created);

        var result = snapshot.MyTagDefinitions.ToList();
        var ids = result.Select(x => x.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var item in created)
        {
            if (string.IsNullOrWhiteSpace(item.Id) ||
                string.IsNullOrWhiteSpace(item.Name))
                throw new InvalidDataException("Created MyTag definition contains an empty ID or name.");

            var id = item.Id.Trim();
            var name = item.Name.Trim();
            var parentId = string.IsNullOrWhiteSpace(item.ParentId)
                ? null
                : item.ParentId.Trim();

            if (!ids.Add(id))
                throw new InvalidDataException($"Created MyTag definition ID '{id}' already exists.");

            result.Add(new RekordboxMyTagDefinition(
                id,
                name,
                parentId,
                item.Sequence,
                item.Attribute));
        }

        var allIds = result.Select(x => x.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in result.Where(x => x.ParentId is not null))
        {
            if (!allIds.Contains(item.ParentId!))
                throw new InvalidDataException(
                    $"MyTag definition '{item.Id}' references missing parent '{item.ParentId}'.");
        }

        return result;
    }

    internal static IReadOnlyList<RekordboxAssignmentMutation> Resolve(
        PreviewResult preview,
        RekordboxDatabaseSnapshot snapshot) =>
        Resolve(preview, snapshot, snapshot.MyTagDefinitions);

    internal static IReadOnlyList<RekordboxAssignmentMutation> Resolve(
        PreviewResult preview,
        RekordboxDatabaseSnapshot snapshot,
        IReadOnlyList<RekordboxMyTagDefinition> definitions)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(definitions);
        if (!preview.IsValid || preview.Counts.Conflicts != 0)
            throw new InvalidOperationException("Only a valid conflict-free preview can become a mutation plan.");

        var parents = definitions
            .Where(x => x.ParentId is null)
            .GroupBy(x => x.Name.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.OrdinalIgnoreCase);
        var children = definitions
            .Where(x => x.ParentId is not null)
            .GroupBy(x => x.ParentId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.OrdinalIgnoreCase);
        var contentIds = snapshot.Tracks.Select(x => x.ContentId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var result = new List<RekordboxAssignmentMutation>();
        var links = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var detail in preview.Details.Where(x =>
                     x.Kind is PreviewDetailKind.Add or PreviewDetailKind.Remove))
        {
            if (string.IsNullOrWhiteSpace(detail.ContentId) || detail.Tag is null)
                throw new InvalidDataException("Mutation preview detail is missing ContentId or MyTag.");
            if (!contentIds.Contains(detail.ContentId))
                throw new InvalidDataException($"Mutation references missing ContentID '{detail.ContentId}'.");

            var groupName = detail.Tag.Group.Trim();
            var valueName = detail.Tag.Value.Trim();
            if (!parents.TryGetValue(groupName, out var parentMatches) || parentMatches.Length != 1)
                throw new InvalidDataException($"MyTag group '{groupName}' is missing or ambiguous; definition creation remains fail-closed.");
            var parent = parentMatches[0];
            if (!children.TryGetValue(parent.Id, out var childCandidates))
                throw new InvalidDataException($"MyTag value '{groupName}/{valueName}' does not exist; definition creation remains fail-closed.");
            var childMatches = childCandidates
                .Where(x => string.Equals(x.Name.Trim(), valueName, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (childMatches.Length != 1)
                throw new InvalidDataException($"MyTag value '{groupName}/{valueName}' is missing or ambiguous; definition creation remains fail-closed.");

            var child = childMatches[0];
            var link = detail.ContentId.Trim() + "\0" + child.Id.Trim();
            if (!links.Add(link))
                throw new InvalidDataException("Preview contains duplicate mutation for the same ContentID/MyTagID link.");
            result.Add(new RekordboxAssignmentMutation(
                detail.Kind, detail.ContentId.Trim(), child.Id.Trim(), groupName, valueName));
        }

        if (result.Count != preview.Counts.Additions + preview.Counts.Removals)
            throw new InvalidDataException("Mutation-plan cardinality does not match preview counts.");
        return result;
    }
}

internal sealed record RekordboxMutationPreflightResult(
    PreviewResult CurrentPreview,
    IReadOnlyList<RekordboxAssignmentMutation> Mutations,
    IReadOnlyList<MyTagAssignment> MissingDefinitions)
{
    internal bool RequiresDefinitionCreation => MissingDefinitions.Count != 0;
    internal bool HasAssignmentMutations => Mutations.Count != 0;
    internal bool HasWork => RequiresDefinitionCreation || HasAssignmentMutations;
}

internal sealed record RekordboxCombinedMutationResult(
    IReadOnlyList<RekordboxCreatedMyTagDefinition> CreatedDefinitions,
    IReadOnlyList<RekordboxAssignmentMutation> AssignmentMutations,
    int TotalChangeCount,
    long FinalLocalUpdateCount);

internal static class RekordboxCombinedMutationWriter
{
    internal static RekordboxCombinedMutationResult Apply(
        SqliteConnection connection,
        RekordboxDatabaseSnapshot snapshot,
        RekordboxMutationPreflightResult preflight,
        RekordboxMyTagDefinitionWriteProfile? definitionProfile,
        RekordboxSongMyTagWriteProfile assignmentProfile,
        long currentCounter,
        long firstLocalUsn)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(preflight);
        ArgumentNullException.ThrowIfNull(assignmentProfile);
        if (!preflight.HasWork)
            throw new InvalidOperationException("Combined mutation writer requires definition or assignment work.");
        if (preflight.RequiresDefinitionCreation && definitionProfile is null)
            throw new InvalidDataException("MyTag definition creation was requested without qualified write semantics.");

        return RekordboxMutationTransaction.Execute(
            connection,
            transaction =>
            {
                var definitionResult = preflight.RequiresDefinitionCreation
                    ? RekordboxMyTagDefinitionWriter.EnsureDefinitions(
                        connection,
                        transaction,
                        preflight.MissingDefinitions,
                        definitionProfile!,
                        firstLocalUsn)
                    : new RekordboxDefinitionWriteResult(
                        Array.Empty<RekordboxCreatedMyTagDefinition>(),
                        firstLocalUsn);

                var postDefinitions = RekordboxMutationPlan.BuildPostDefinitionState(
                    snapshot,
                    definitionResult.Created);
                var mutations = RekordboxMutationPlan.Resolve(
                    preflight.CurrentPreview,
                    snapshot,
                    postDefinitions);

                var changedAssignments = RekordboxSongMyTagWriter.Apply(
                    connection,
                    transaction,
                    mutations,
                    assignmentProfile,
                    definitionResult.NextLocalUsn);
                if (changedAssignments != mutations.Count)
                    throw new InvalidDataException(
                        "SongMyTag writer changed-count does not match the post-definition mutation plan.");

                int totalChangeCount;
                try
                {
                    totalChangeCount = checked(definitionResult.Created.Count + changedAssignments);
                }
                catch (OverflowException ex)
                {
                    throw new InvalidDataException("Combined MyTag mutation change-count overflow.", ex);
                }
                if (totalChangeCount < 1)
                    throw new InvalidDataException("Combined MyTag mutation unexpectedly produced no database changes.");

                var finalCounter = RekordboxUpdateCounter.Advance(
                    connection,
                    currentCounter,
                    totalChangeCount,
                    transaction);

                return new RekordboxCombinedMutationResult(
                    definitionResult.Created,
                    mutations,
                    totalChangeCount,
                    finalCounter);
            },
            (_, result) =>
            {
                if (result.TotalChangeCount !=
                    result.CreatedDefinitions.Count + result.AssignmentMutations.Count)
                    throw new InvalidDataException(
                        "Pre-commit combined mutation count does not match definition and assignment changes.");
            });
    }
}

internal static class RekordboxMutationPreflight
{
    internal static RekordboxMutationPreflightResult Recheck(
        PreviewResult approvedPreview,
        RekordboxDatabaseSnapshot freshSnapshot,
        ProvenanceDocument freshProvenance,
        IReadOnlyList<BridgeTrack> bridgeTracks,
        IReadOnlyList<MappingRule> mappings,
        IReadOnlyList<PathAlias>? pathAliases = null)
    {
        ArgumentNullException.ThrowIfNull(approvedPreview);
        ArgumentNullException.ThrowIfNull(freshSnapshot);
        ArgumentNullException.ThrowIfNull(freshProvenance);
        ArgumentNullException.ThrowIfNull(bridgeTracks);
        ArgumentNullException.ThrowIfNull(mappings);

        if (!approvedPreview.IsValid || approvedPreview.Counts.Conflicts != 0)
            throw new InvalidOperationException("Only a valid conflict-free approved preview may authorize database mutation.");
        if (string.IsNullOrWhiteSpace(approvedPreview.FingerprintSha256))
            throw new InvalidDataException("Approved preview fingerprint is empty.");

        var managedAssignments = ProvenanceStore.ToManagedAssignments(freshProvenance, freshSnapshot);
        var request = new PreviewRequest(
            freshSnapshot.Identity.PreviewIdentity,
            bridgeTracks,
            mappings,
            freshSnapshot.Tracks,
            managedAssignments,
            pathAliases,
            freshSnapshot.MyTagDefinitions);
        var currentPreview = PreviewEngine.Create(request);

        if (!currentPreview.IsValid || currentPreview.Counts.Conflicts != 0)
            throw new InvalidOperationException("Fresh preview is invalid; database mutation is blocked.");
        if (!string.Equals(
                approvedPreview.FingerprintSha256,
                currentPreview.FingerprintSha256,
                StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Approved preview is stale for the current database, source, mapping, path-alias or provenance state.");

        var missingDefinitions = (currentPreview.MissingDefinitions ?? Array.Empty<MyTagAssignment>()).ToArray();
        var mutations = missingDefinitions.Length == 0
            ? RekordboxMutationPlan.Resolve(currentPreview, freshSnapshot)
            : Array.Empty<RekordboxAssignmentMutation>();
        return new RekordboxMutationPreflightResult(
            currentPreview,
            mutations,
            missingDefinitions);
    }
}
