using RekordboxMyTagSync.Core;

public static class ProvenanceSelfTest
{
    public static void Run(string temp)
    {
        var dbPath = Path.Combine(temp, "Provenance", "master.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        File.WriteAllBytes(dbPath, new byte[] { 1 });
        var identity = new RekordboxDatabaseIdentity(
            "db-provenance-1",
            "fixture-v1",
            dbPath,
            1,
            File.GetLastWriteTimeUtc(dbPath),
            "fixture-cipher");
        var snapshot = new RekordboxDatabaseSnapshot(
            identity,
            new[]
            {
                new RekordboxTrackSnapshot(
                    "C1",
                    Path.Combine(temp, "Music", "One.mp3"),
                    new[] { new MyTagAssignment("Mood", "Euphoric") })
            },
            new[]
            {
                new RekordboxMyTagDefinition("G1", "Mood", null, 1, 0),
                new RekordboxMyTagDefinition("T1", "Euphoric", "G1", 1, 0)
            });

        var statePath = Path.Combine(temp, "state", "provenance.json");
        var empty = ProvenanceStore.Load(statePath, identity);
        if (empty.Assignments.Count != 0 || empty.DbId != identity.DbId)
            throw new InvalidOperationException("missing provenance file did not produce DB-bound empty state");

        var owned = new OwnedAssignment("C1", "T1", "Mood", "Euphoric");
        var document = ProvenanceStore.ReplaceAssignments(empty, identity, new[] { owned });
        ProvenanceStore.SaveAtomic(statePath, document, identity);
        var loaded = ProvenanceStore.Load(statePath, identity);
        if (loaded.Assignments.Count != 1 || loaded.Assignments[0] != owned)
            throw new InvalidOperationException("provenance atomic round-trip failed");

        var managed = ProvenanceStore.ToManagedAssignments(loaded, snapshot);
        if (managed.Count != 1 || managed[0].ContentId != "C1" ||
            managed[0].Tag.Group != "Mood" || managed[0].Tag.Value != "Euphoric")
            throw new InvalidOperationException("provenance did not project to preview ownership correctly");

        var replacement = ProvenanceStore.ReplaceAssignments(loaded, identity, Array.Empty<OwnedAssignment>());
        ProvenanceStore.SaveAtomic(statePath, replacement, identity);
        if (ProvenanceStore.Load(statePath, identity).Assignments.Count != 0)
            throw new InvalidOperationException("provenance rolling replacement retained stale assignments");
        if (Directory.EnumerateFiles(Path.GetDirectoryName(statePath)!, "*.tmp-*", SearchOption.TopDirectoryOnly).Any())
            throw new InvalidOperationException("provenance atomic save left temporary files behind");

        AssertFailsClosed(
            () => ProvenanceStore.Load(
                statePath,
                identity with { DbId = "other-db" }),
            "provenance DBID mismatch was accepted");
        AssertFailsClosed(
            () => ProvenanceStore.Load(
                statePath,
                identity with { CanonicalPath = Path.Combine(temp, "Other", "master.db") }),
            "provenance database path mismatch was accepted");

        ProvenanceStore.SaveAtomic(statePath, document, identity);
        var renamedSnapshot = snapshot with
        {
            MyTagDefinitions = new[]
            {
                new RekordboxMyTagDefinition("G1", "Mood", null, 1, 0),
                new RekordboxMyTagDefinition("T1", "Dark", "G1", 1, 0)
            },
            Tracks = new[]
            {
                new RekordboxTrackSnapshot(
                    "C1",
                    Path.Combine(temp, "Music", "One.mp3"),
                    new[] { new MyTagAssignment("Mood", "Dark") })
            }
        };
        AssertFailsClosed(
            () => ProvenanceStore.ToManagedAssignments(document, renamedSnapshot),
            "provenance MyTag rename drift was accepted");

        var missingLinkSnapshot = snapshot with
        {
            Tracks = new[]
            {
                new RekordboxTrackSnapshot("C1", Path.Combine(temp, "Music", "One.mp3"), Array.Empty<MyTagAssignment>())
            }
        };
        AssertFailsClosed(
            () => ProvenanceStore.ToManagedAssignments(document, missingLinkSnapshot),
            "provenance missing Track↔MyTag link was accepted");

        var duplicate = new ProvenanceDocument(
            ProvenanceStore.CurrentSchemaVersion,
            identity.DbId,
            identity.CanonicalPath,
            new[] { owned, owned });
        AssertFailsClosed(
            () => ProvenanceStore.SaveAtomic(Path.Combine(temp, "duplicate.json"), duplicate, identity),
            "duplicate provenance link was accepted");
    }

    private static void AssertFailsClosed(Action action, string message)
    {
        try { action(); }
        catch { return; }
        throw new InvalidOperationException(message);
    }
}
