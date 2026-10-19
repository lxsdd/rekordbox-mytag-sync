using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace RekordboxMyTagSync.Core;

public static class EncryptedLibraryScalePreviewSelfTest
{
    // Synthetic metadata only: no 52k audio file writes, no real user DB,
    // and no synthetic pair list is ever used as live write authorization.
    private const int TotalContentIds = 52784;
    private const int VirtualCueOnly = 670;
    private const int DuplicatePathContentIds = 6;
    private const int MissingFromBridge = 2;
    private const int ExpectedMatched = TotalContentIds -
        VirtualCueOnly - DuplicatePathContentIds - MissingFromBridge;

    public static void Run(string temp)
    {
        var root = Path.Combine(temp, "Encrypted52784Scale");
        var bridgeRoot = Path.Combine(root, "foobar");
        var targetRoot = Path.Combine(root, "rekordbox-files");
        var sources = new[]
        {
            Path.Combine(bridgeRoot, "Singles"),
            Path.Combine(bridgeRoot, "Albums")
        };
        var targets = new[]
        {
            Path.Combine(targetRoot, "Singles"),
            Path.Combine(targetRoot, "Albums")
        };
        var aliases = new[]
        {
            new PathAlias(sources[0], targets[0]),
            new PathAlias(sources[1], targets[1])
        };

        string Name(int i) => i >= TotalContentIds - MissingFromBridge - DuplicatePathContentIds &&
            i < TotalContentIds - MissingFromBridge
                ? "Duplicate" + ((i - (TotalContentIds - MissingFromBridge - DuplicatePathContentIds)) / 2).ToString("D2") + ".mp3"
                : "Track" + i.ToString("D6") + ".mp3";
        string Destination(int i) =>
            Path.Combine(targets[i % 2], Name(i));
        string Source(int i) =>
            Path.Combine(sources[i % 2], Name(i));

        var databasePath = Path.Combine(root, "master.db");
        EncryptedMutationFixture.Create(databasePath, Destination(0), Destination(1));

        // One transaction and a reused prepared command preserve realistic
        // 52k SQLCipher snapshot semantics without spending minutes on setup.
        using (var connection = EncryptedMutationFixture.Open(databasePath))
        using (var transaction = connection.BeginTransaction())
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO djmdContent(ID, FolderPath, rb_local_deleted)
                VALUES($id, $path, 0);
                """;
            var idParameter = insert.Parameters.Add("$id", SqliteType.Text);
            var pathParameter = insert.Parameters.Add("$path", SqliteType.Text);
            for (var i = 2; i < TotalContentIds; i++)
            {
                idParameter.Value = "C" + i.ToString("D6");
                pathParameter.Value = Destination(i);
                if (insert.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException(
                        "Encrypted 52,784-ContentID fixture could not insert synthetic track.");
            }
            transaction.Commit();
        }
        var preimageSha = SHA256.HashData(File.ReadAllBytes(databasePath));

        var genre = new Dictionary<string, IReadOnlyList<string>>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["GENRE"] = new[] { "House" }
        };
        var empty = new Dictionary<string, IReadOnlyList<string>>(
            StringComparer.OrdinalIgnoreCase);
        var bridge = new BridgeTrack[TotalContentIds - DuplicatePathContentIds - MissingFromBridge];
        for (var i = 0; i < bridge.Length; i++)
            bridge[i] = new BridgeTrack(
                Source(i), i < VirtualCueOnly ? 1U : 0U, genre, empty);

        var stopwatch = Stopwatch.StartNew();
        var snapshot = RekordboxSqlCipherDatabase.ReadSnapshot(
            databasePath, EncryptedMutationFixture.Key,
            new RekordboxDatabaseReadPolicy(
                new HashSet<string>(StringComparer.Ordinal) { EncryptedMutationFixture.DbVersion },
                RequireRekordboxClosed: false));
        if (snapshot.Tracks.Count != TotalContentIds)
            throw new InvalidOperationException(
                "Encrypted fixture lost a real rekordbox ContentID.");

        // CollectPairs is the production alias/path/collision algorithm.
        // The next list is SYNTHETIC path evidence for exercising scope
        // and mapping scale only; actual write authority independently
        // requires fresh native Windows VolumeSerial/FileIndex handles.
        var candidates = PathPairVerifier.CollectPairs(
            bridge, snapshot.Tracks, aliases);
        if (candidates.Pairs.Count != ExpectedMatched ||
            candidates.ExcludedSubsongs != VirtualCueOnly ||
            candidates.AmbiguousTargetPaths != 3)
            throw new InvalidOperationException(
                $"Library-scale pairing mismatch: matched={candidates.Pairs.Count}, " +
                $"virtual={candidates.ExcludedSubsongs}, duplicates={candidates.AmbiguousTargetPaths}.");

        var scope = VerifiedPreviewScope.Build(
            bridge, snapshot.Tracks, aliases,
            candidates.Pairs.Select(x =>
                new VerifiedPhysicalPair(x.Source, x.Target)).ToArray());
        if (scope.Sources.Count != ExpectedMatched ||
            scope.Targets.Count != ExpectedMatched ||
            scope.ExcludedTargets.Count != VirtualCueOnly + DuplicatePathContentIds + MissingFromBridge ||
            scope.Sources.Any(x => x.Subsong != 0))
            throw new InvalidOperationException(
                "Library-scale verified scope leaked virtual segments or special ContentIDs.");

        var reasonCounts = scope.ExcludedTargets
            .GroupBy(x => x.Message.Split(':')[0], StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
        if (reasonCounts.GetValueOrDefault("VIRTUAL_SUBSONG_ONLY") != VirtualCueOnly ||
            reasonCounts.GetValueOrDefault("DUPLICATE_TARGET_PATH") != DuplicatePathContentIds ||
            reasonCounts.GetValueOrDefault("NOT_IN_FOOBAR_SOURCE") != MissingFromBridge)
            throw new InvalidOperationException(
                "Large physical exception report conflated 670 cue-only main files or eight unusual ContentIDs.");

        var preview = PreviewEngine.Create(new PreviewRequest(
            snapshot.Identity.PreviewIdentity, scope.Sources,
            new[] { new MappingRule("GENRE", "Genre") }, scope.Targets,
            Array.Empty<ManagedAssignment>(), aliases, snapshot.MyTagDefinitions));
        if (!preview.IsValid || preview.Counts.Additions != ExpectedMatched ||
            preview.Counts.Removals != 0 || preview.Counts.Unmatched != 0 ||
            preview.Counts.Conflicts != 0 ||
            (preview.MissingDefinitions?.Count ?? 0) != 0 ||
            preview.Details.Any(x => x.Kind != PreviewDetailKind.Add ||
                x.ContentId is null))
            throw new InvalidOperationException(
                "52k production preview proposed unsafe, duplicated or missing MyTag assignments.");

        stopwatch.Stop();
        if (stopwatch.Elapsed > TimeSpan.FromSeconds(120))
            throw new InvalidOperationException(
                $"Encrypted 52,784-track read/physical scope/mapping preview exceeded " +
                $"the generous 120-second scale budget: {stopwatch.Elapsed}.");
        if (!preimageSha.AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(databasePath))))
            throw new InvalidOperationException(
                "Library-scale diagnostics modified the encrypted SQLCipher fixture.");
    }
}
