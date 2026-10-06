using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using RekordboxMyTagSync.Core;

public static class RekordboxDatabaseSelfTest
{
    private static readonly string Key = Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes("rekordbox-mytag-sync-fixture-material")));
    private const string DbVersion = "fixture-v1";

    public static void Run(string temp)
    {
        var root = Path.Combine(temp, "EncryptedDatabase");
        Directory.CreateDirectory(root);
        var databasePath = Path.Combine(root, "master.db");
        var trackOne = Path.Combine(temp, "CanonicalMusic", "Artist", "One.mp3");
        var trackTwo = Path.Combine(temp, "CanonicalMusic", "Artist", "Two.mp3");
        CreateFixture(databasePath, Key, DbVersion, trackOne, trackTwo, compatibleSchema: true);

        AssertEncrypted(databasePath);
        var hashBefore = HashFile(databasePath);
        var writeTimeBefore = File.GetLastWriteTimeUtc(databasePath);
        var policy = new RekordboxDatabaseReadPolicy(
            new HashSet<string>(StringComparer.Ordinal) { DbVersion },
            RequireRekordboxClosed: false);
        var snapshot = RekordboxSqlCipherDatabase.ReadSnapshot(databasePath, Key, policy);
        var hashAfter = HashFile(databasePath);
        var writeTimeAfter = File.GetLastWriteTimeUtc(databasePath);

        if (!hashBefore.SequenceEqual(hashAfter))
            throw new InvalidOperationException("read-only encrypted database reader mutated master.db bytes");
        if (writeTimeBefore != writeTimeAfter)
            throw new InvalidOperationException("read-only encrypted database reader changed master.db write time");
        if (snapshot.Identity.DbId != "db-fixture-1" || snapshot.Identity.DbVersion != DbVersion)
            throw new InvalidOperationException("encrypted database identity mismatch");
        if (string.IsNullOrWhiteSpace(snapshot.Identity.SqliteCipherVersion))
            throw new InvalidOperationException("SQLite3MC version was not reported");
        if (snapshot.Tracks.Count != 2 || snapshot.MyTagDefinitions.Count != 4)
            throw new InvalidOperationException("encrypted database snapshot row counts mismatch");

        var c1 = snapshot.Tracks.Single(x => x.ContentId == "C1");
        AssertAssignment(c1, "Genre", "House");
        AssertAssignment(c1, "Mood", "Euphoric");
        if (c1.Assignments.Count != 2)
            throw new InvalidOperationException("unexpected encrypted database assignments for C1");

        var bridge = new BridgeTrack(
            trackOne,
            0,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["genre"] = new[] { "House" }
            },
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["MOOD"] = new[] { "Euphoric" }
            });
        var preview = PreviewEngine.Create(new PreviewRequest(
            snapshot.Identity.PreviewIdentity,
            new[] { bridge },
            new[] { new MappingRule("GENRE", "Genre"), new MappingRule("MOOD", "Mood") },
            snapshot.Tracks,
            Array.Empty<ManagedAssignment>()));
        if (!preview.IsValid || preview.Counts.AlreadyCorrect != 2 || preview.Counts.Additions != 0 ||
            preview.Counts.Removals != 0 || preview.Counts.Conflicts != 0 || preview.Counts.Unmatched != 0)
            throw new InvalidOperationException("encrypted database snapshot did not feed preview deterministically");

        AssertFailsClosed(
            () => RekordboxSqlCipherDatabase.ReadSnapshot(databasePath, "wrong-key", policy),
            "wrong SQLCipher key was accepted");
        AssertFailsClosed(
            () => RekordboxSqlCipherDatabase.ReadSnapshot(
                databasePath,
                Key,
                new RekordboxDatabaseReadPolicy(
                    new HashSet<string>(StringComparer.Ordinal) { "some-other-version" },
                    RequireRekordboxClosed: false)),
            "unknown DBVersion was accepted");

        var incompatiblePath = Path.Combine(root, "incompatible.db");
        CreateFixture(incompatiblePath, Key, DbVersion, trackOne, trackTwo, compatibleSchema: false);
        AssertFailsClosed(
            () => RekordboxSqlCipherDatabase.ReadSnapshot(incompatiblePath, Key, policy),
            "schema missing required rekordbox column was accepted");
    }

    private static void CreateFixture(
        string path,
        string key,
        string dbVersion,
        string trackOne,
        string trackTwo,
        bool compatibleSchema)
    {
        if (File.Exists(path)) File.Delete(path);
        SQLitePCL.Batteries_V2.Init();
        var uri = new Uri(Path.GetFullPath(path)).AbsoluteUri + "?cipher=sqlcipher&legacy=4";
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = uri,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Password = key,
            Pooling = false
        };
        using var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = compatibleSchema ? CompatibleSchemaSql : IncompatibleSchemaSql;
        command.ExecuteNonQuery();

        Insert(connection,
            "INSERT INTO djmdProperty(DBID, DBVersion, created_at, updated_at) VALUES ($dbid, $version, $now, $now);",
            ("$dbid", "db-fixture-1"), ("$version", dbVersion), ("$now", "2026-10-04 00:00:00.000 +00:00"));
        Insert(connection,
            "INSERT INTO djmdContent(ID, FolderPath, rb_local_deleted) VALUES ($id, $path, 0);",
            ("$id", "C1"), ("$path", trackOne));
        Insert(connection,
            "INSERT INTO djmdContent(ID, FolderPath, rb_local_deleted) VALUES ($id, $path, 0);",
            ("$id", "C2"), ("$path", trackTwo));
        Insert(connection,
            "INSERT INTO agentRegistry(registry_id, int_1) VALUES ($registry, $value);",
            ("$registry", "localUpdateCount"), ("$value", 100L));

        if (!compatibleSchema) return;
        InsertMyTag(connection, "G1", "Genre", null, 1);
        InsertMyTag(connection, "T1", "House", "G1", 1);
        InsertMyTag(connection, "G2", "Mood", null, 2);
        InsertMyTag(connection, "T2", "Euphoric", "G2", 1);
        InsertSongMyTag(connection, "S1", "T1", "C1", 1);
        InsertSongMyTag(connection, "S2", "T2", "C1", 2);
    }

    private static void InsertMyTag(SqliteConnection connection, string id, string name, string? parentId, int seq)
    {
        Insert(connection,
            """
            INSERT INTO djmdMyTag(
                ID, UUID, Seq, Name, Attribute, ParentID,
                rb_data_status, rb_local_data_status, rb_local_deleted, rb_local_synced,
                usn, rb_local_usn, created_at, updated_at)
            VALUES($id, $uuid, $seq, $name, 0, $parent, 0, 0, 0, 0, 1, 1, $now, $now);
            """,
            ("$id", id), ("$uuid", "uuid-" + id), ("$seq", seq), ("$name", name),
            ("$parent", parentId), ("$now", "2026-10-04 00:00:00.000 +00:00"));
    }

    private static void InsertSongMyTag(SqliteConnection connection, string id, string myTagId, string contentId, int trackNo)
    {
        Insert(connection,
            """
            INSERT INTO djmdSongMyTag(
                ID, UUID, MyTagID, ContentID, TrackNo,
                rb_data_status, rb_local_data_status, rb_local_deleted, rb_local_synced,
                usn, rb_local_usn, created_at, updated_at)
            VALUES($id, $uuid, $tag, $content, $track, 0, 0, 0, 0, 1, 1, $now, $now);
            """,
            ("$id", id), ("$uuid", "uuid-" + id), ("$tag", myTagId), ("$content", contentId),
            ("$track", trackNo), ("$now", "2026-10-04 00:00:00.000 +00:00"));
    }

    private static void Insert(SqliteConnection connection, string sql, params (string Name, object? Value)[] values)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in values)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static void AssertAssignment(RekordboxTrackSnapshot track, string group, string value)
    {
        if (!track.Assignments.Any(x =>
                string.Equals(x.Group, group, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.Value, value, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"missing encrypted database assignment {group}/{value}");
    }

    private static void AssertEncrypted(string path)
    {
        var header = File.ReadAllBytes(path).Take(16).ToArray();
        var sqliteHeader = Encoding.ASCII.GetBytes("SQLite format 3\0");
        if (header.SequenceEqual(sqliteHeader))
            throw new InvalidOperationException("SQLCipher fixture is plaintext SQLite");
    }

    private static byte[] HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return SHA256.HashData(stream);
    }

    private static void AssertFailsClosed(Action action, string message)
    {
        try
        {
            action();
        }
        catch
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private const string CompatibleSchemaSql = """
        CREATE TABLE djmdProperty(
            DBID TEXT PRIMARY KEY, DBVersion TEXT, created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
        CREATE TABLE djmdContent(
            ID TEXT PRIMARY KEY, FolderPath TEXT, rb_local_deleted INTEGER DEFAULT 0);
        CREATE TABLE agentRegistry(
            registry_id TEXT PRIMARY KEY, int_1 INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE djmdMyTag(
            ID TEXT PRIMARY KEY, UUID TEXT, Seq INTEGER, Name TEXT, Attribute INTEGER, ParentID TEXT,
            rb_data_status INTEGER DEFAULT 0, rb_local_data_status INTEGER DEFAULT 0,
            rb_local_deleted INTEGER DEFAULT 0, rb_local_synced INTEGER DEFAULT 0,
            usn INTEGER, rb_local_usn INTEGER, created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
        CREATE TABLE djmdSongMyTag(
            ID TEXT PRIMARY KEY, UUID TEXT, MyTagID TEXT, ContentID TEXT, TrackNo INTEGER,
            rb_data_status INTEGER DEFAULT 0, rb_local_data_status INTEGER DEFAULT 0,
            rb_local_deleted INTEGER DEFAULT 0, rb_local_synced INTEGER DEFAULT 0,
            usn INTEGER, rb_local_usn INTEGER, created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
        """;

    private const string IncompatibleSchemaSql = """
        CREATE TABLE djmdProperty(
            DBID TEXT PRIMARY KEY, DBVersion TEXT, created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
        CREATE TABLE djmdContent(
            ID TEXT PRIMARY KEY, FolderPath TEXT, rb_local_deleted INTEGER DEFAULT 0);
        CREATE TABLE agentRegistry(
            registry_id TEXT PRIMARY KEY, int_1 INTEGER NOT NULL DEFAULT 0);
        CREATE TABLE djmdMyTag(
            ID TEXT PRIMARY KEY, UUID TEXT, Seq INTEGER, Name TEXT, Attribute INTEGER, ParentID TEXT,
            rb_data_status INTEGER DEFAULT 0, rb_local_data_status INTEGER DEFAULT 0,
            rb_local_deleted INTEGER DEFAULT 0, rb_local_synced INTEGER DEFAULT 0,
            usn INTEGER, rb_local_usn INTEGER, created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
        CREATE TABLE djmdSongMyTag(
            ID TEXT PRIMARY KEY, MyTagID TEXT, ContentID TEXT, TrackNo INTEGER,
            rb_data_status INTEGER DEFAULT 0, rb_local_data_status INTEGER DEFAULT 0,
            rb_local_deleted INTEGER DEFAULT 0, rb_local_synced INTEGER DEFAULT 0,
            usn INTEGER, rb_local_usn INTEGER, created_at TEXT NOT NULL, updated_at TEXT NOT NULL);
        """;
}
