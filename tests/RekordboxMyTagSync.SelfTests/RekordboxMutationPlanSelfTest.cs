using RekordboxMyTagSync.Core;

public static class RekordboxMutationPlanSelfTest
{
    public static void Run()
    {
        var identity = new RekordboxDatabaseIdentity(
            "db", "6", @"C:\fixture\master.db", 1, DateTime.UnixEpoch, "fixture");
        var definitions = new[]
        {
            new RekordboxMyTagDefinition("G1", "Genre", null, 1, 0),
            new RekordboxMyTagDefinition("T1", "House", "G1", 1, 0)
        };
        var snapshot = new RekordboxDatabaseSnapshot(
            identity,
            new[] { new RekordboxTrackSnapshot("C1", @"C:\Music\one.mp3", Array.Empty<MyTagAssignment>()) },
            definitions);

        var valid = new PreviewResult(
            true,
            new PreviewCounts(1, 0, 0, 0, 0),
            new[]
            {
                new PreviewDetail(PreviewDetailKind.Add, @"C:\Music\one.mp3", "C1",
                    new MyTagAssignment("Genre", "House"), null)
            },
            "fixture-fingerprint");
        var plan = RekordboxMutationPlan.Resolve(valid, snapshot);
        if (plan.Count != 1 || plan[0].ContentId != "C1" || plan[0].MyTagId != "T1")
            throw new InvalidOperationException("valid mutation plan did not resolve the existing MyTag definition");

        var missing = valid with
        {
            Details = new[]
            {
                new PreviewDetail(PreviewDetailKind.Add, @"C:\Music\one.mp3", "C1",
                    new MyTagAssignment("Genre", "Techno"), null)
            }
        };
        AssertBlocked(() => RekordboxMutationPlan.Resolve(missing, snapshot), "definition creation");

        var duplicate = valid with
        {
            Counts = new PreviewCounts(2, 0, 0, 0, 0),
            Details = new[] { valid.Details[0], valid.Details[0] }
        };
        AssertBlocked(() => RekordboxMutationPlan.Resolve(duplicate, snapshot), "duplicate");
    }

    private static void AssertBlocked(Action action, string expected)
    {
        try
        {
            action();
        }
        catch (InvalidDataException ex) when (ex.Message.Contains(expected, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        throw new InvalidOperationException($"mutation plan did not fail closed for {expected}");
    }
}
