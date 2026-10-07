using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

public static class EncryptedMutationFixture
{
    public static readonly string Key = Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes("rekordbox-mytag-sync-executor-fixture-material")));

    public const string DbVersion = "fixture-v1";

    public static void Create(
        string path,
        string trackOne,
        string trackTwo)
    {
        if (File.Exists(path))
            File.Delete(path);

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        SQLitePCL.Batteries_V2.Init();
        using var connection = Open(path, SqliteOpenMode.ReadWriteCreate);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = SchemaSql;
            command.ExecuteNonQuery();
        }

        Insert(connection,
            "INSERT INTO djmdProperty(DBID, DBVersion, created_at, updated_at) VALUES ($dbid, $version, $now, $now);",
            ("$dbid", "db-executor-fixture"), ("$version", DbVersion), ("$now", "2026-10-07 00:00:00.000 +00:00"));
        Insert(connection,
            "INSERT INTO djmdContent(ID, FolderPath, rb_local_deleted) VALUES ($id, $path, 0);",
            ("$id", "C1"), ("$path", trackOne));
        Insert(connection,
            "INSERT INTO djmdContent(ID, FolderPath, rb_local_deleted) VALUES ($id, $path, 0);",
            ("$id", "C2"), ("$path", trackTwo));
        Insert(connection,
            "INSERT INTO agentRegistry(registry_id, int_1) VALUES ('localUpdateCount', 100);");

        InsertMyTag(connection, "G1", "Genre", null, 1);
        InsertMyTag(connection, "T1", "House", "G1", 1);
        InsertMyTag(connection, "T2", "Techno", "G1", 2);
        InsertMyTag(connection, "G2", "Mood", null, 2);
        InsertMyTag(connection, "T3", "Euphoric", "G2", 1);

        InsertSongMyTag(connection, "1", "T1", "C1", 1, 256, 0, false, 90);
        InsertSongMyTag(connection, "2", "T3", "C1", 1, 256, 0, false, 91);
        InsertSongMyTag(connection, "3", "T2", "C2", 1, 256, 0, false, 92);
        InsertSongMyTag(connection, "4", "T2", "C1", 2, 262, 0, true, 93);
    }

    public static SqliteConnection Open(string path, SqliteOpenMode mode = SqliteOpenMode.ReadWrite)
    {
        SQLitePCL.Batteries_V2.Init();
        var uri = new Uri(Path.GetFullPath(path)).AbsoluteUri + "?cipher=sqlcipher&legacy=4";
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = uri,
            Mode = mode,
            Password = Key,
            Pooling = false
        };
        var connection = new SqliteConnection(builder.ToString());
        connection.Open();
        return connection;
    }

    private static void InsertMyTag(
        SqliteConnection connection,
        string id,
        string name,
        string? parentId,
        int seq)
    {
        Insert(connection,
            """
            INSERT INTO djmdMyTag(
                ID, UUID, Seq, Name, Attribute, ParentID,
                rb_data_status, rb_local_data_status, rb_local_deleted, rb_local_synced,
                usn, rb_local_usn, created_at, updated_at)
            VALUES($id, $uuid, $seq, $name, 0, $parent, 0, 0, 0, 0, 1, 1, $now, $now);
            """,
            ("$id", id),
            ("$uuid", "uuid-" + id),
            ("$seq", seq),
            ("$name", name),
            ("$parent", parentId),
            ("$now", "2026-10-07 00:00:00.000 +00:00"));
    }

    private static void InsertSongMyTag(
        SqliteConnection connection,
        string id,
        string myTagId,
        string contentId,
        int trackNo,
        long dataStatus,
        long localDataStatus,
        bool deleted,
        long localUsn)
    {
        Insert(connection,
            """
            INSERT INTO djmdSongMyTag(
                ID, UUID, MyTagID, ContentID, TrackNo,
                rb_data_status, rb_local_data_status, rb_local_deleted, rb_local_synced,
                usn, rb_local_usn, created_at, updated_at)
            VALUES(
                $id, $uuid, $tag, $content, $track,
                $dataStatus, $localDataStatus, $deleted, 0,
                1, $localUsn, $now, $now);
            """,
            ("$id", id),
            ("$uuid", "uuid-" + id),
            ("$tag", myTagId),
            ("$content", contentId),
            ("$track", trackNo),
            ("$dataStatus", dataStatus),
            ("$localDataStatus", localDataStatus),
            ("$deleted", deleted ? 1 : 0),
            ("$localUsn", localUsn),
            ("$now", "2026-10-07 00:00:00.000 +00:00"));
    }

    private static void Insert(
        SqliteConnection connection,
        string sql,
        params (string Name, object? Value)[] values)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in values)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private const string SchemaSql = """
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
}
