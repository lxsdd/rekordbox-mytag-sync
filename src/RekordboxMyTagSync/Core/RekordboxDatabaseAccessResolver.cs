using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace RekordboxMyTagSync.Core;

internal sealed record RekordboxDatabaseKeyCandidate(
    string Source,
    string Value);

internal sealed record RekordboxResolvedDatabaseAccess(
    string Key,
    string KeySource,
    RekordboxDatabaseReadPolicy Policy,
    RekordboxDatabaseSnapshot Snapshot);

internal static class RekordboxDatabaseAccessResolver
{
    private sealed record RemoteKeySource(
        string Name,
        Uri Uri,
        Regex Pattern);

    private static readonly RemoteKeySource[] RemoteSources =
    [
        new(
            "pinned CueGen compatibility source",
            new Uri(
                "https://raw.githubusercontent.com/mganss/CueGen/" +
                "19878e6eb3f586dee0eb3eb4f2ce3ef18309de9d/CueGen/Generator.cs"),
            new Regex(
                @"Config\.UseSqlCipher\s*\?\s*""(?<key>[^""]+)""\s*:\s*null",
                RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)),
        new(
            "pinned go-rekordbox compatibility source",
            new Uri(
                "https://raw.githubusercontent.com/dvcrn/go-rekordbox/" +
                "8be6191ba198ed7abd4ad6406d177ed7b4f749b5/cmd/getencryptionkey/main.go"),
            new Regex(
                @"fmt\.Print\(""(?<key>[^""]+)""\)",
                RegexOptions.CultureInvariant))
    ];

    internal static async Task<RekordboxResolvedDatabaseAccess> ResolveAsync(
        RekordboxLibraryCandidate library,
        string? roamingAppDataRoot = null,
        HttpClient? httpClient = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(library);
        ValidateLibraryEvidence(library);

        var localCandidates = ReadLocalCandidates(roamingAppDataRoot);
        var local = QualifyCandidates(library, localCandidates);
        if (local is not null)
            return local;

        using var ownedClient = httpClient is null
            ? new HttpClient { Timeout = TimeSpan.FromSeconds(8) }
            : null;
        var client = httpClient ?? ownedClient!;

        var remoteFailures = new List<string>();
        foreach (var source in RemoteSources)
        {
            string payload;
            try
            {
                payload = await client.GetStringAsync(source.Uri, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (
                ex is HttpRequestException or TaskCanceledException or IOException)
            {
                remoteFailures.Add($"{source.Name}: unavailable");
                continue;
            }

            var candidates = ParsePinnedSource(source.Name, payload);
            if (candidates.Count == 0)
            {
                remoteFailures.Add($"{source.Name}: no candidate");
                continue;
            }

            var qualified = QualifyCandidates(library, candidates);
            if (qualified is not null)
                return qualified;

            remoteFailures.Add($"{source.Name}: candidate rejected");
        }

        var suffix = remoteFailures.Count == 0
            ? string.Empty
            : " Sources: " + string.Join("; ", remoteFailures) + ".";
        throw new InvalidDataException(
            "Could not resolve and verify rekordbox database access automatically. " +
            "No database key was persisted or logged." + suffix);
    }

    internal static async Task<IReadOnlyList<string>> ProbePinnedSourcesAsync(
        HttpClient? httpClient = null,
        CancellationToken cancellationToken = default)
    {
        using var ownedClient = httpClient is null
            ? new HttpClient { Timeout = TimeSpan.FromSeconds(8) }
            : null;
        var client = httpClient ?? ownedClient!;

        var available = new List<string>();
        foreach (var source in RemoteSources)
        {
            try
            {
                var payload = await client.GetStringAsync(source.Uri, cancellationToken)
                    .ConfigureAwait(false);
                if (ParsePinnedSource(source.Name, payload).Count != 0)
                    available.Add(source.Name);
            }
            catch (Exception ex) when (
                ex is HttpRequestException or TaskCanceledException or IOException)
            {
                // A second pinned source may still keep automatic first-run
                // resolution operational. No remote payload is logged.
            }
        }

        if (available.Count == 0)
            throw new InvalidDataException(
                "No pinned automatic database-access compatibility source is currently reachable and parseable.");

        return available;
    }

    internal static IReadOnlyList<RekordboxDatabaseKeyCandidate> ParsePinnedSource(
        string sourceName,
        string payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceName);
        ArgumentNullException.ThrowIfNull(payload);

        var source = RemoteSources.SingleOrDefault(x =>
            string.Equals(x.Name, sourceName, StringComparison.Ordinal));
        if (source is null)
            throw new ArgumentException(
                $"Unknown pinned database-key source '{sourceName}'.",
                nameof(sourceName));

        return source.Pattern.Matches(payload)
            .Select(x => x.Groups["key"].Value)
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .Select(x => new RekordboxDatabaseKeyCandidate(
                source.Name,
                x.Trim()))
            .ToArray();
    }

    internal static RekordboxResolvedDatabaseAccess? QualifyCandidates(
        RekordboxLibraryCandidate library,
        IEnumerable<RekordboxDatabaseKeyCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(candidates);
        ValidateLibraryEvidence(library);

        var policy = RekordboxDatabaseReadPolicy.SchemaQualifiedRuntime();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var candidate in candidates)
        {
            if (candidate is null ||
                string.IsNullOrWhiteSpace(candidate.Source) ||
                string.IsNullOrWhiteSpace(candidate.Value))
                continue;

            var key = candidate.Value.Trim();
            if (!seen.Add(key))
                continue;
            if (!CanOpenDatabase(library.DatabasePath, key))
                continue;

            var snapshot = RekordboxSqlCipherDatabase.ReadSnapshot(
                library.DatabasePath,
                key,
                policy);
            return new RekordboxResolvedDatabaseAccess(
                key,
                candidate.Source.Trim(),
                policy,
                snapshot);
        }

        return null;
    }

    private static void ValidateLibraryEvidence(RekordboxLibraryCandidate library)
    {
        if (!library.Safe || !string.IsNullOrWhiteSpace(library.Error))
            throw new InvalidDataException(
                "Selected rekordbox library is not qualified as safe.");
        if (string.IsNullOrWhiteSpace(library.DatabasePath) ||
            !File.Exists(Path.GetFullPath(library.DatabasePath)))
            throw new FileNotFoundException(
                "Selected rekordbox master.db does not exist.",
                library.DatabasePath);

        var supportedInstallations = library.UsedBy
            .Where(x => x.MajorVersion is 6 or 7)
            .ToArray();
        if (supportedInstallations.Length == 0)
            throw new InvalidDataException(
                "Automatic database access requires discovered rekordbox 6/7 installation evidence.");

        if (RekordboxProcessGuard.IsRunning())
            throw new InvalidOperationException(
                "rekordbox is running. Database access qualification is blocked until rekordbox is closed.");
    }

    private static IReadOnlyList<RekordboxDatabaseKeyCandidate> ReadLocalCandidates(
        string? roamingAppDataRoot)
    {
        var root = string.IsNullOrWhiteSpace(roamingAppDataRoot)
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
            : roamingAppDataRoot;
        if (string.IsNullOrWhiteSpace(root))
            return Array.Empty<RekordboxDatabaseKeyCandidate>();

        var cachePath = Path.Combine(root, "pyrekordbox", "rb.cache");
        if (!File.Exists(cachePath))
            return Array.Empty<RekordboxDatabaseKeyCandidate>();

        try
        {
            var result = new List<RekordboxDatabaseKeyCandidate>();
            foreach (var line in File.ReadLines(cachePath))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("dp:", StringComparison.OrdinalIgnoreCase))
                {
                    var value = trimmed[3..].Trim();
                    if (value.Length != 0)
                        result.Add(new RekordboxDatabaseKeyCandidate(
                            "local pyrekordbox cache",
                            value));
                }
            }
            return result;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<RekordboxDatabaseKeyCandidate>();
        }
    }

    private static bool CanOpenDatabase(string databasePath, string key)
    {
        RekordboxSqlCipherDatabase.EnsureSqliteInitialized();
        using var connection = new SqliteConnection(
            RekordboxSqlCipherDatabase.BuildConnectionString(
                databasePath,
                key,
                SqliteOpenMode.ReadOnly));
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM sqlite_master;";
            _ = command.ExecuteScalar();
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
    }
}
