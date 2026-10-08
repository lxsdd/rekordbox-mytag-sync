using System.Net;
using System.Net.Http;
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

        QualifyCandidateVerification(library);
        QualifyPinnedSourceParsers();
        QualifyLocalCacheResolution(root, library);

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

    private static void QualifyCandidateVerification(
        RekordboxLibraryCandidate library)
    {
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
    }

    private static void QualifyPinnedSourceParsers()
    {
        const string syntheticKey = "synthetic-parser-candidate";

        var cueGen = RekordboxDatabaseAccessResolver.ParsePinnedSource(
            "pinned CueGen compatibility source",
            $"key: Config.UseSqlCipher ? \"{syntheticKey}\" : null,");
        if (cueGen.Count != 1 ||
            cueGen[0].Value != syntheticKey)
            throw new InvalidOperationException(
                "CueGen compatibility-source parser did not return the expected candidate");

        var goRekordbox = RekordboxDatabaseAccessResolver.ParsePinnedSource(
            "pinned go-rekordbox compatibility source",
            $"fmt.Print(\"{syntheticKey}\")");
        if (goRekordbox.Count != 1 ||
            goRekordbox[0].Value != syntheticKey)
            throw new InvalidOperationException(
                "go-rekordbox compatibility-source parser did not return the expected candidate");
    }

    private static void QualifyLocalCacheResolution(
        string root,
        RekordboxLibraryCandidate library)
    {
        var roaming = Path.Combine(root, "Roaming");
        var cacheDirectory = Path.Combine(roaming, "pyrekordbox");
        Directory.CreateDirectory(cacheDirectory);
        File.WriteAllText(
            Path.Combine(cacheDirectory, "rb.cache"),
            "version: 2" + Environment.NewLine +
            "dp: " + EncryptedMutationFixture.Key);

        using var client = new HttpClient(new NoNetworkHandler());
        var resolved = RekordboxDatabaseAccessResolver.ResolveAsync(
                library,
                roaming,
                client)
            .GetAwaiter()
            .GetResult();

        if (resolved.KeySource != "local pyrekordbox cache" ||
            resolved.Key != EncryptedMutationFixture.Key)
            throw new InvalidOperationException(
                "automatic database access did not prefer the verified local cache");
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

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(
                new HttpRequestException(
                    "network access is forbidden in this deterministic self-test"));
    }
}
