using Microsoft.Data.Sqlite;
using RekordboxMyTagSync.Core;

public static class RekordboxSongMyTagWriterSelfTest
{
    public static void Run()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var setup = connection.CreateCommand())
        {
            setup.CommandText = """
                CREATE TABLE djmdSongMyTag(
                    ID TEXT PRIMARY KEY, UUID TEXT, MyTagID TEXT, ContentID TEXT, TrackNo INTEGER,
                    rb_data_status INTEGER, rb_local_data_status INTEGER,
                    rb_local_deleted INTEGER, rb_local_synced INTEGER,
                    rb_local_usn INTEGER, created_at TEXT, updated_at TEXT);
                INSERT INTO djmdSongMyTag VALUES ('1','u1','T1','C1',1,256,0,0,0,90,'before','before');
                INSERT INTO djmdSongMyTag VALUES ('2','u2','T2','C1',1,256,0,0,0,91,'before','before');
                INSERT INTO djmdSongMyTag VALUES ('3','u3','T3','C2',4,262,0,1,0,92,'before','before');
                INSERT INTO djmdSongMyTag VALUES ('4','u4','T4','C3',1,256,0,0,0,93,'before','before');
                """;
            setup.ExecuteNonQuery();
        }

        var profile = new RekordboxSongMyTagWriteProfile(
            new RekordboxSongMyTagStatusTuple(256, 0),
            new RekordboxSongMyTagStatusTuple(262, 0));
        var mutations = new RekordboxAssignmentMutation[]
        {
            new(PreviewDetailKind.Add, "C2", "T3", "Mood", "Reactivated"),
            new(PreviewDetailKind.Add, "C2", "T1", "Genre", "House"),
            new(PreviewDetailKind.Remove, "C3", "T4", "Mood", "Old")
        };

        using (var transaction = connection.BeginTransaction())
        {
            if (RekordboxSongMyTagWriter.Apply(connection, transaction, mutations, profile, 101) != 3)
                throw new InvalidOperationException("SongMyTag writer changed-count mismatch");
            transaction.Commit();
        }

        AssertRow(connection, "3", "T3", "C2", 4, 256, 0, 0, 0, 101);
        AssertRow(connection, "5", "T1", "C2", 2, 256, 0, 0, 0, 102);
        AssertRow(connection, "4", "T4", "C3", 1, 262, 0, 1, 0, 103);
        if (ScalarLong(connection, "SELECT COUNT(*) FROM djmdSongMyTag WHERE MyTagID='T3' AND ContentID='C2';") != 1)
            throw new InvalidOperationException("SongMyTag add duplicated a reusable tombstone row");
        if (ScalarLong(connection, "SELECT COUNT(*) FROM djmdSongMyTag WHERE rb_local_deleted=0;") != 4)
            throw new InvalidOperationException("SongMyTag active-row count mismatch after Add/Remove");

        using var mixed = new SqliteConnection("Data Source=:memory:");
        mixed.Open();
        using (var setup = mixed.CreateCommand())
        {
            setup.CommandText = """
                CREATE TABLE djmdSongMyTag(
                    ID TEXT PRIMARY KEY, UUID TEXT, MyTagID TEXT, ContentID TEXT, TrackNo INTEGER,
                    rb_data_status INTEGER, rb_local_data_status INTEGER,
                    rb_local_deleted INTEGER, rb_local_synced INTEGER,
                    rb_local_usn INTEGER, created_at TEXT, updated_at TEXT);
                INSERT INTO djmdSongMyTag VALUES ('1','u1','T1','C1',1,256,0,0,0,1,'x','x');
                INSERT INTO djmdSongMyTag VALUES ('custom-id','u2','T2','C2',1,256,0,0,0,2,'x','x');
                """;
            setup.ExecuteNonQuery();
        }
        using (var transaction = mixed.BeginTransaction())
        {
            var blocked = false;
            try
            {
                RekordboxSongMyTagWriter.Apply(
                    mixed,
                    transaction,
                    new[] { new RekordboxAssignmentMutation(PreviewDetailKind.Add, "C3", "T1", "Genre", "New") },
                    new RekordboxSongMyTagWriteProfile(new RekordboxSongMyTagStatusTuple(256, 0), null),
                    3);
            }
            catch (InvalidDataException ex) when (ex.Message.Contains("ID scheme", StringComparison.OrdinalIgnoreCase))
            {
                blocked = true;
            }
            transaction.Rollback();
            if (!blocked)
                throw new InvalidOperationException("mixed SongMyTag ID scheme was accepted");
        }
    }

    private static void AssertRow(
        SqliteConnection connection,
        string id,
        string tag,
        string content,
        long trackNo,
        long dataStatus,
        long localDataStatus,
        long deleted,
        long synced,
        long localUsn)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT MyTagID, ContentID, TrackNo, rb_data_status, rb_local_data_status,
                   rb_local_deleted, rb_local_synced, rb_local_usn
            FROM djmdSongMyTag WHERE ID=$id;
            """;
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            throw new InvalidOperationException($"SongMyTag row '{id}' missing");
        var actual = new object[]
        {
            reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3),
            reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7)
        };
        var expected = new object[] { tag, content, trackNo, dataStatus, localDataStatus, deleted, synced, localUsn };
        if (!actual.SequenceEqual(expected))
            throw new InvalidOperationException($"SongMyTag row '{id}' values mismatch");
    }

    private static long ScalarLong(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
