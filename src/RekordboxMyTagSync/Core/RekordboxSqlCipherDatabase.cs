using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace RekordboxMyTagSync.Core;

public sealed record RekordboxDatabaseIdentity(
    string DbId,
    string DbVersion,
    string CanonicalPath,
    long FileLength,
    DateTime LastWriteUtc,
    string SqliteCipherVersion)
{
    public string PreviewIdentity => string.Join("|", DbId, DbVersion, CanonicalPath, FileLength.ToString(CultureInfo.InvariantCulture), LastWriteUtc.Ticks.ToString(CultureInfo.InvariantCulture));
}

public sealed record RekordboxMyTagDefinition(
    string Id,
    string Name,
    string? ParentId,
    int? Sequence,
    int? Attribute);

public sealed record RekordboxDatabaseSnapshot(
    RekordboxDatabaseIdentity Identity,
    IReadOnlyList<RekordboxTrackSnapshot> Tracks,
    IReadOnlyList<RekordboxMyTagDefinition> MyTagDefinitions);

public sealed record RekordboxDatabaseReadPolicy(
    IReadOnlySet<string> SupportedDbVersions,
    bool RequireRekordboxClosed = true,
    bool AllowSchemaQualifiedDbVersion = false)
{
    public static RekordboxDatabaseReadPolicy SchemaQualifiedRuntime(
        bool requireRekordboxClosed = true) =>
        new(
            new HashSet<string>(StringComparer.Ordinal),
            requireRekordboxClosed,
            AllowSchemaQualifiedDbVersion: true);
}

public static class RekordboxSqlCipherDatabase
{
    private static readonly IReadOnlyDictionary<string, string[]> RequiredColumns =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["djmdProperty"] = ["DBID", "DBVersion"],
            ["djmdContent"] = ["ID", "FolderPath", "rb_local_deleted"],
            ["agentRegistry"] = ["registry_id", "int_1"],
            ["djmdMyTag"] =
            [
                "ID", "UUID", "Seq", "Name", "Attribute", "ParentID",
                "rb_data_status", "rb_local_data_status", "rb_local_deleted", "rb_local_synced",
                "usn", "rb_local_usn", "created_at", "updated_at"
            ],
            ["djmdSongMyTag"] =
            [
                "ID", "UUID", "MyTagID", "ContentID", "TrackNo",
                "rb_data_status", "rb_local_data_status", "rb_local_deleted", "rb_local_synced",
                "usn", "rb_local_usn", "created_at", "updated_at"
            ]
        };

    private static int _sqliteInitialized;

    public static RekordboxDatabaseSnapshot ReadSnapshot(
        string databasePath,
        string key,
        RekordboxDatabaseReadPolicy policy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(policy.SupportedDbVersions);
        if (policy.SupportedDbVersions.Count == 0 &&
            !policy.AllowSchemaQualifiedDbVersion)
            throw new ArgumentException(
                "At least one supported rekordbox DBVersion must be configured unless schema-qualified runtime mode is enabled.",
                nameof(policy));

        if (policy.RequireRekordboxClosed && RekordboxProcessGuard.IsRunning())
            throw new InvalidOperationException("rekordbox is running. Database access is blocked until rekordbox is closed.");

        var canonicalPath = Path.GetFullPath(databasePath);
        if (!File.Exists(canonicalPath)) throw new FileNotFoundException("rekordbox master.db not found.", canonicalPath);
        var file = new FileInfo(canonicalPath);

        EnsureSqliteInitialized();
        using var connection = new SqliteConnection(BuildConnectionString(canonicalPath, key, SqliteOpenMode.ReadOnly));
        try
        {
            connection.Open();
        }
        catch (SqliteException ex)
        {
            throw new InvalidDataException("Could not open rekordbox master.db with the configured SQLCipher key/cipher profile.", ex);
        }

        var cipherVersion = ReadScalarString(connection, "SELECT sqlite3mc_version();", "SQLite3MC version");
        ValidateSchema(connection);
        var (dbId, dbVersion) = ReadIdentity(connection);
        if (!policy.AllowSchemaQualifiedDbVersion &&
            !policy.SupportedDbVersions.Contains(dbVersion))
            throw new InvalidDataException($"Unsupported rekordbox DBVersion '{dbVersion}'.");

        var definitions = ReadMyTagDefinitions(connection);
        var tracks = ReadTracks(connection, definitions);
        return new RekordboxDatabaseSnapshot(
            new RekordboxDatabaseIdentity(dbId, dbVersion, canonicalPath, file.Length, file.LastWriteTimeUtc, cipherVersion),
            tracks,
            definitions);
    }

    internal static string BuildConnectionString(string databasePath, string key, SqliteOpenMode mode)
    {
        var uri = new Uri(Path.GetFullPath(databasePath)).AbsoluteUri + "?cipher=sqlcipher&legacy=4";
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = uri,
            Mode = mode,
            Password = key,
            Pooling = false
        };
        return builder.ToString();
    }

    internal static void EnsureSqliteInitialized()
    {
        if (Interlocked.Exchange(ref _sqliteInitialized, 1) == 0)
            SQLitePCL.Batteries_V2.Init();
    }

    private static void ValidateSchema(SqliteConnection connection)
    {
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type='table';";
            using var reader = command.ExecuteReader();
            while (reader.Read()) tables.Add(reader.GetString(0));
        }

        foreach (var requirement in RequiredColumns)
        {
            if (!tables.Contains(requirement.Key))
                throw new InvalidDataException($"Required rekordbox table '{requirement.Key}' is missing.");

            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info([{requirement.Key}]);";
            using var reader = command.ExecuteReader();
            while (reader.Read()) columns.Add(reader.GetString(1));
            var missing = requirement.Value.Where(x => !columns.Contains(x)).ToArray();
            if (missing.Length != 0)
                throw new InvalidDataException($"Table '{requirement.Key}' is missing required columns: {string.Join(", ", missing)}.");
        }
    }

    private static (string DbId, string DbVersion) ReadIdentity(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DBID, DBVersion FROM djmdProperty;";
        using var reader = command.ExecuteReader();
        if (!reader.Read()) throw new InvalidDataException("djmdProperty contains no database identity row.");
        var dbId = RequiredString(reader, 0, "djmdProperty.DBID");
        var dbVersion = RequiredString(reader, 1, "djmdProperty.DBVersion");
        if (reader.Read()) throw new InvalidDataException("djmdProperty contains multiple database identity rows.");
        return (dbId, dbVersion);
    }

    private static IReadOnlyList<RekordboxMyTagDefinition> ReadMyTagDefinitions(SqliteConnection connection)
    {
        var result = new List<RekordboxMyTagDefinition>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ID, Name, ParentID, Seq, Attribute
            FROM djmdMyTag
            WHERE COALESCE(rb_local_deleted, 0) = 0
            ORDER BY ParentID, Seq, ID;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var id = RequiredString(reader, 0, "djmdMyTag.ID");
            var name = RequiredString(reader, 1, $"djmdMyTag[{id}].Name");
            if (!ids.Add(id)) throw new InvalidDataException($"Duplicate active djmdMyTag ID '{id}'.");
            result.Add(new RekordboxMyTagDefinition(
                id,
                name,
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture),
                reader.IsDBNull(4) ? null : Convert.ToInt32(reader.GetValue(4), CultureInfo.InvariantCulture)));
        }

        var byId = result.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var item in result.Where(x => x.ParentId is not null))
            if (!byId.ContainsKey(item.ParentId!))
                throw new InvalidDataException($"Active MyTag '{item.Id}' references missing/inactive parent '{item.ParentId}'.");
        return result;
    }

    private static IReadOnlyList<RekordboxTrackSnapshot> ReadTracks(
        SqliteConnection connection,
        IReadOnlyList<RekordboxMyTagDefinition> definitions)
    {
        var byId = definitions.ToDictionary(x => x.Id, StringComparer.OrdinalIgnoreCase);
        var assignments = new Dictionary<string, List<MyTagAssignment>>(StringComparer.OrdinalIgnoreCase);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT sm.ContentID, sm.MyTagID
                FROM djmdSongMyTag sm
                WHERE COALESCE(sm.rb_local_deleted, 0) = 0
                ORDER BY sm.ContentID, sm.TrackNo, sm.ID;
                """;
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var contentId = RequiredString(reader, 0, "djmdSongMyTag.ContentID");
                var myTagId = RequiredString(reader, 1, "djmdSongMyTag.MyTagID");
                if (!byId.TryGetValue(myTagId, out var child))
                    throw new InvalidDataException($"Active song MyTag references missing/inactive MyTag '{myTagId}'.");
                if (string.IsNullOrWhiteSpace(child.ParentId) || !byId.TryGetValue(child.ParentId, out var parent))
                    throw new InvalidDataException($"Assigned MyTag '{myTagId}' is not an active child of a MyTag group.");
                if (!assignments.TryGetValue(contentId, out var list)) assignments[contentId] = list = [];
                list.Add(new MyTagAssignment(parent.Name, child.Name));
            }
        }

        var tracks = new List<RekordboxTrackSnapshot>();
        var contentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var trackCommand = connection.CreateCommand();
        trackCommand.CommandText = """
            SELECT ID, FolderPath
            FROM djmdContent
            WHERE COALESCE(rb_local_deleted, 0) = 0
            ORDER BY ID;
            """;
        using var trackReader = trackCommand.ExecuteReader();
        while (trackReader.Read())
        {
            var id = RequiredString(trackReader, 0, "djmdContent.ID");
            var path = RequiredString(trackReader, 1, $"djmdContent[{id}].FolderPath");
            if (!contentIds.Add(id)) throw new InvalidDataException($"Duplicate active djmdContent ID '{id}'.");
            tracks.Add(new RekordboxTrackSnapshot(id, path, assignments.TryGetValue(id, out var list) ? list.ToArray() : []));
        }

        var orphanContentIds = assignments.Keys.Where(x => !contentIds.Contains(x)).ToArray();
        if (orphanContentIds.Length != 0)
            throw new InvalidDataException($"Active song MyTag assignments reference missing/inactive content: {string.Join(", ", orphanContentIds)}.");
        return tracks;
    }

    private static string ReadScalarString(SqliteConnection connection, string sql, string label)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        if (value is null || value is DBNull || string.IsNullOrWhiteSpace(Convert.ToString(value, CultureInfo.InvariantCulture)))
            throw new InvalidDataException($"{label} is empty.");
        return Convert.ToString(value, CultureInfo.InvariantCulture)!;
    }

    private static string RequiredString(SqliteDataReader reader, int ordinal, string label)
    {
        if (reader.IsDBNull(ordinal)) throw new InvalidDataException($"{label} is NULL.");
        var value = reader.GetString(ordinal).Trim();
        if (value.Length == 0) throw new InvalidDataException($"{label} is empty.");
        return value;
    }
}
