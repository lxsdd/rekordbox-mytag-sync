using Microsoft.Data.Sqlite;
using RekordboxMyTagSync.Core;

public static class RekordboxMyTagDefinitionWriterSelfTest
{
    public static void Run()
    {
        QualifyAndCreate();
        RejectMissingDefault();
        RejectAmbiguousAttribute();
        RejectMixedIdScheme();
        RejectInconsistentChildSequenceStart();
    }

    private static void QualifyAndCreate()
    {
        using var connection = NewConnection(QualifiedSchema);
        SeedQualified(connection);

        var profile = RekordboxMyTagDefinitionWriter.Qualify(connection);
        if (profile.DataStatus != 256 ||
            profile.LocalDataStatus != 0 ||
            profile.RootAttribute != 10 ||
            profile.ChildAttribute != 20 ||
            profile.FirstChildSequence != 1 ||
            profile.IdKind != RekordboxDefinitionIdKind.Numeric)
            throw new InvalidOperationException("MyTag definition write profile was not derived from fixture evidence");

        using var transaction = connection.BeginTransaction();
        var result = RekordboxMyTagDefinitionWriter.EnsureDefinitions(
            connection,
            transaction,
            new[]
            {
                new MyTagAssignment("Genre", "House"),
                new MyTagAssignment("Genre", "Techno"),
                new MyTagAssignment("Energy", "Peak"),
                new MyTagAssignment("energy", "peak")
            },
            profile,
            101);

        if (result.Created.Count != 3 || result.NextLocalUsn != 104)
            throw new InvalidOperationException("MyTag definition writer created unexpected row count/USN range");

        transaction.Commit();

        AssertDefinition(connection, "Energy", null, 3, 10, 101, 256, 0, 0, 0);
        var energyId = ScalarString(connection, "SELECT ID FROM djmdMyTag WHERE ParentID IS NULL AND Name='Energy';");
        AssertDefinition(connection, "Peak", energyId, 1, 20, 102, 256, 0, 0, 0);

        var genreId = ScalarString(connection, "SELECT ID FROM djmdMyTag WHERE ParentID IS NULL AND Name='Genre';");
        AssertDefinition(connection, "Techno", genreId, 2, 20, 103, 256, 0, 0, 0);

        if (ScalarLong(connection, "SELECT COUNT(*) FROM djmdMyTag WHERE Name='House';") != 1)
            throw new InvalidOperationException("existing MyTag definition was duplicated");
        if (ScalarLong(connection, "SELECT COUNT(DISTINCT UUID) FROM djmdMyTag;") !=
            ScalarLong(connection, "SELECT COUNT(*) FROM djmdMyTag;"))
            throw new InvalidOperationException("created MyTag UUIDs are not unique");

        using var uuidCommand = connection.CreateCommand();
        uuidCommand.CommandText = "SELECT UUID FROM djmdMyTag;";
        using var reader = uuidCommand.ExecuteReader();
        while (reader.Read())
            if (!Guid.TryParse(reader.GetString(0), out _))
                throw new InvalidOperationException("created/existing MyTag UUID is not a GUID");
    }

    private static void RejectMissingDefault()
    {
        var schema = QualifiedSchema.Replace(
            "usn INTEGER NOT NULL DEFAULT 0,",
            "usn INTEGER NOT NULL,",
            StringComparison.Ordinal);
        using var connection = NewConnection(schema);
        SeedQualified(connection);
        AssertBlocked(
            () => RekordboxMyTagDefinitionWriter.Qualify(connection),
            "no schema default");
    }

    private static void RejectAmbiguousAttribute()
    {
        using var connection = NewConnection(QualifiedSchema);
        SeedQualified(connection);
        Execute(connection,
            "INSERT INTO djmdMyTag(ID,UUID,Seq,Name,Attribute,ParentID,rb_local_usn) VALUES('5',$uuid,3,'OtherRoot',11,NULL,5);",
            ("$uuid", Guid.NewGuid().ToString()));
        AssertBlocked(
            () => RekordboxMyTagDefinitionWriter.Qualify(connection),
            "Attribute");
    }

    private static void RejectMixedIdScheme()
    {
        using var connection = NewConnection(QualifiedSchema);
        SeedQualified(connection);
        Execute(connection,
            "INSERT INTO djmdMyTag(ID,UUID,Seq,Name,Attribute,ParentID,rb_local_usn) VALUES('custom-id',$uuid,3,'OtherRoot',10,NULL,5);",
            ("$uuid", Guid.NewGuid().ToString()));
        AssertBlocked(
            () => RekordboxMyTagDefinitionWriter.Qualify(connection),
            "ID scheme");
    }

    private static void RejectInconsistentChildSequenceStart()
    {
        using var connection = NewConnection(QualifiedSchema);
        SeedQualified(connection);
        Execute(connection,
            "INSERT INTO djmdMyTag(ID,UUID,Seq,Name,Attribute,ParentID,rb_local_usn) VALUES('5',$uuid,2,'Calm',20,'2',5);",
            ("$uuid", Guid.NewGuid().ToString()));
        AssertBlocked(
            () => RekordboxMyTagDefinitionWriter.Qualify(connection),
            "starting sequences");
    }

    private static SqliteConnection NewConnection(string schema)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = schema;
        command.ExecuteNonQuery();
        return connection;
    }

    private static void SeedQualified(SqliteConnection connection)
    {
        Insert(connection, "1", "Genre", null, 1, 10);
        Insert(connection, "2", "Mood", null, 2, 10);
        Insert(connection, "3", "House", "1", 1, 20);
        Insert(connection, "4", "Euphoric", "2", 1, 20);
    }

    private static void Insert(
        SqliteConnection connection,
        string id,
        string name,
        string? parent,
        int seq,
        int attribute)
    {
        Execute(
            connection,
            """
            INSERT INTO djmdMyTag(
                ID,UUID,Seq,Name,Attribute,ParentID,rb_local_usn)
            VALUES($id,$uuid,$seq,$name,$attribute,$parent,$localUsn);
            """,
            ("$id", id),
            ("$uuid", Guid.NewGuid().ToString()),
            ("$seq", seq),
            ("$name", name),
            ("$attribute", attribute),
            ("$parent", parent),
            ("$localUsn", long.Parse(id, System.Globalization.CultureInfo.InvariantCulture)));
    }

    private static void AssertDefinition(
        SqliteConnection connection,
        string name,
        string? parentId,
        long seq,
        long attribute,
        long localUsn,
        long dataStatus,
        long localDataStatus,
        long localDeleted,
        long localSynced)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Seq, Attribute, ParentID, rb_local_usn,
                   rb_data_status, rb_local_data_status, rb_local_deleted, rb_local_synced
            FROM djmdMyTag
            WHERE Name=$name AND (($parent IS NULL AND ParentID IS NULL) OR ParentID=$parent);
            """;
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$parent", parentId is null ? DBNull.Value : parentId);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            throw new InvalidOperationException($"created MyTag definition '{name}' missing");
        if (reader.GetInt64(0) != seq ||
            reader.GetInt64(1) != attribute ||
            reader.GetInt64(3) != localUsn ||
            reader.GetInt64(4) != dataStatus ||
            reader.GetInt64(5) != localDataStatus ||
            reader.GetInt64(6) != localDeleted ||
            reader.GetInt64(7) != localSynced)
            throw new InvalidOperationException($"created MyTag definition '{name}' values mismatch");
        if (parentId is null ? !reader.IsDBNull(2) : reader.GetString(2) != parentId)
            throw new InvalidOperationException($"created MyTag definition '{name}' parent mismatch");
        if (reader.Read())
            throw new InvalidOperationException($"created MyTag definition '{name}' is duplicated");
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
            $"MyTag definition qualification did not fail closed for '{expected}'");
    }

    private static void Execute(
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

    private static long ScalarLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string ScalarString(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture)
               ?? throw new InvalidOperationException("scalar string fixture query returned NULL");
    }

    private const string QualifiedSchema = """
        CREATE TABLE djmdMyTag(
            ID TEXT PRIMARY KEY,
            UUID TEXT NOT NULL,
            Seq INTEGER NOT NULL,
            Name TEXT NOT NULL,
            Attribute INTEGER NOT NULL,
            ParentID TEXT,
            rb_data_status INTEGER NOT NULL DEFAULT 256,
            rb_local_data_status INTEGER NOT NULL DEFAULT 0,
            rb_local_deleted INTEGER NOT NULL DEFAULT 0,
            rb_local_synced INTEGER NOT NULL DEFAULT 0,
            usn INTEGER NOT NULL DEFAULT 0,
            rb_local_usn INTEGER NOT NULL,
            created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
            updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP);
        """;
}
