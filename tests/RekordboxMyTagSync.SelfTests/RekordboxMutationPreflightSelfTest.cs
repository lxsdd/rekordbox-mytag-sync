using RekordboxMyTagSync.Core;

public static class RekordboxMutationPreflightSelfTest
{
    public static void Run(string temp)
    {
        var trackPath = Path.Combine(temp, "PreflightMusic", "Track.mp3");
        var databasePath = Path.Combine(temp, "PreflightLibrary", "master.db");
        var identity = new RekordboxDatabaseIdentity(
            "db-preflight",
            "6.0",
            Path.GetFullPath(databasePath),
            12345,
            new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc),
            "fixture");

        var definitions = new RekordboxMyTagDefinition[]
        {
            new("G1", "Mood", null, 0, 0),
            new("V1", "Old", "G1", 0, 0),
            new("V2", "Euphoric", "G1", 1, 0),
            new("V3", "Manual", "G1", 2, 0)
        };
        var track = new RekordboxTrackSnapshot(
            "C1",
            trackPath,
            new[]
            {
                new MyTagAssignment("Mood", "Old"),
                new MyTagAssignment("Mood", "Manual")
            });
        var snapshot = new RekordboxDatabaseSnapshot(identity, new[] { track }, definitions);
        var provenance = new ProvenanceDocument(
            ProvenanceStore.CurrentSchemaVersion,
            identity.DbId,
            identity.CanonicalPath,
            new[]
            {
                new OwnedAssignment("C1", "V1", "Mood", "Old")
            });
        var bridge = new BridgeTrack(
            trackPath,
            0,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["MOOD"] = new[] { "Euphoric" }
            });
        var mappings = new[] { new MappingRule("MOOD", "Mood") };
        var managed = ProvenanceStore.ToManagedAssignments(provenance, snapshot);
        var approved = PreviewEngine.Create(new PreviewRequest(
            identity.PreviewIdentity,
            new[] { bridge },
            mappings,
            snapshot.Tracks,
            managed,
            MyTagDefinitions: snapshot.MyTagDefinitions));
        if (!approved.IsValid || approved.Counts.Additions != 1 || approved.Counts.Removals != 1)
            throw new InvalidOperationException("preflight baseline preview is not the expected Add/Remove plan");

        var preflight = RekordboxMutationPreflight.Recheck(
            approved,
            snapshot,
            provenance,
            new[] { bridge },
            mappings);
        if (preflight.Mutations.Count != 2)
            throw new InvalidOperationException($"expected 2 preflight mutations, got {preflight.Mutations.Count}");
        if (!preflight.Mutations.Any(x =>
                x.Kind == PreviewDetailKind.Add &&
                string.Equals(x.ContentId, "C1", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.MyTagId, "V2", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("preflight did not resolve the expected Add mutation");
        if (!preflight.Mutations.Any(x =>
                x.Kind == PreviewDetailKind.Remove &&
                string.Equals(x.ContentId, "C1", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.MyTagId, "V1", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("preflight did not resolve the expected tool-owned Remove mutation");
        if (preflight.Mutations.Any(x => string.Equals(x.MyTagId, "V3", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("manual/unmanaged assignment entered preflight mutation plan");

        var postDefinitions = RekordboxMutationPlan.BuildPostDefinitionState(
            snapshot,
            new[]
            {
                new RekordboxCreatedMyTagDefinition("G2", "Energy", null, 1, 0, 101),
                new RekordboxCreatedMyTagDefinition("V4", "Peak", "G2", 0, 0, 102)
            });
        if (postDefinitions.Count != snapshot.MyTagDefinitions.Count + 2 ||
            !postDefinitions.Any(x =>
                x.Id == "G2" &&
                x.Name == "Energy" &&
                x.ParentId is null) ||
            !postDefinitions.Any(x =>
                x.Id == "V4" &&
                x.Name == "Peak" &&
                x.ParentId == "G2"))
            throw new InvalidOperationException("post-definition state did not preserve and append expected definitions");

        var duplicateIdBlocked = false;
        try
        {
            _ = RekordboxMutationPlan.BuildPostDefinitionState(
                snapshot,
                new[] { new RekordboxCreatedMyTagDefinition("G1", "Duplicate", null, 3, 0, 103) });
        }
        catch (InvalidDataException)
        {
            duplicateIdBlocked = true;
        }
        if (!duplicateIdBlocked)
            throw new InvalidOperationException("post-definition state accepted a duplicate definition ID");

        var missingParentBlocked = false;
        try
        {
            _ = RekordboxMutationPlan.BuildPostDefinitionState(
                snapshot,
                new[] { new RekordboxCreatedMyTagDefinition("V5", "Orphan", "MISSING", 0, 0, 104) });
        }
        catch (InvalidDataException)
        {
            missingParentBlocked = true;
        }
        if (!missingParentBlocked)
            throw new InvalidOperationException("post-definition state accepted a child with a missing parent");

        var driftedSnapshot = snapshot with
        {
            Tracks = new[]
            {
                track with
                {
                    Assignments = new[]
                    {
                        new MyTagAssignment("Mood", "Old"),
                        new MyTagAssignment("Mood", "Manual"),
                        new MyTagAssignment("Mood", "Euphoric")
                    }
                }
            }
        };
        var staleBlocked = false;
        try
        {
            _ = RekordboxMutationPreflight.Recheck(
                approved,
                driftedSnapshot,
                provenance,
                new[] { bridge },
                mappings);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("stale", StringComparison.OrdinalIgnoreCase))
        {
            staleBlocked = true;
        }
        if (!staleBlocked)
            throw new InvalidOperationException("database assignment drift did not invalidate the approved preview");

        var mappingDriftBlocked = false;
        try
        {
            _ = RekordboxMutationPreflight.Recheck(
                approved,
                snapshot,
                provenance,
                new[] { bridge },
                new[] { new MappingRule("MOOD", "Mood", Prefix: "Changed: ") });
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("stale", StringComparison.OrdinalIgnoreCase))
        {
            mappingDriftBlocked = true;
        }
        if (!mappingDriftBlocked)
            throw new InvalidOperationException("mapping drift did not invalidate the approved preview");
    }
}
