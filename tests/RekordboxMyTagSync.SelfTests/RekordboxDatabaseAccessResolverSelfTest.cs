using RekordboxMyTagSync.Core;

public static class RekordboxDatabaseAccessResolverSelfTest
{
    public static void Run(string temp)
    {
        var root = Path.Combine(temp, "DatabaseAccessResolver");
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "master.db");
        EncryptedMutationFixture.Create(
            databasePath,
            @"Z:\Music\Singles\Resolver-One.mp3",
            @"Z:\Music\Singles\Resolver-Two.mp3");

        var library = new RekordboxLibraryCandidate(
            databasePath,
            new[]
            {
                new RekordboxInstallation(
                    6,
                    "6.8.5",
                    Path.Combine(root, "rekordbox 6.8.5"))
            },
            new[] { "synthetic fixture" },
            Safe: true,
            Error: null);

        var resolved = RekordboxDatabaseAccessResolver.QualifyCandidates(
            library,
            new[]
            {
                new RekordboxDatabaseKeyCandidate(
                    "wrong synthetic candidate",
                    "not-the-fixture-key"),
                new RekordboxDatabaseKeyCandidate(
                    "verified synthetic candidate",
                    EncryptedMutationFixture.Key)
            });

        if (resolved is null)
            throw new InvalidOperationException(
                "automatic database access did not accept the verified synthetic candidate");
        if (resolved.KeySource != "verified synthetic candidate" ||
            resolved.Key != EncryptedMutationFixture.Key ||
            resolved.Snapshot.Identity.DbVersion != EncryptedMutationFixture.DbVersion ||
            !resolved.Policy.AllowSchemaQualifiedDbVersion)
            throw new InvalidOperationException(
                "automatic database access returned unexpected qualification state");

        var missingInstallationEvidence = library with
        {
            UsedBy = Array.Empty<RekordboxInstallation>()
        };
        AssertBlocked(
            () => RekordboxDatabaseAccessResolver.QualifyCandidates(
                missingInstallationEvidence,
                new[]
                {
                    new RekordboxDatabaseKeyCandidate(
                        "verified synthetic candidate",
                        EncryptedMutationFixture.Key)
                }),
            "installation evidence");

        var unsafeLibrary = library with
        {
            Safe = false,
            Error = "synthetic unsafe state"
        };
        AssertBlocked(
            () => RekordboxDatabaseAccessResolver.QualifyCandidates(
                unsafeLibrary,
                new[]
                {
                    new RekordboxDatabaseKeyCandidate(
                        "verified synthetic candidate",
                        EncryptedMutationFixture.Key)
                }),
            "not qualified as safe");
    }

    private static void AssertBlocked(Action action, string expected)
    {
        try
        {
            action();
        }
        catch (InvalidDataException ex) when (
            ex.Message.Contains(expected, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new InvalidOperationException(
            $"automatic database access did not fail closed for '{expected}'");
    }
}
