using Microsoft.Data.Sqlite;
using RekordboxMyTagSync.Core;

public static class RekordboxSongMyTagWriteSemanticsSelfTest
{
    public static void Run()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var setup = connection.CreateCommand())
        {
            setup.CommandText = """
                CREATE TABLE djmdSongMyTag(
                    ID TEXT, MyTagID TEXT, ContentID TEXT, TrackNo INTEGER,
                    rb_data_status INTEGER, rb_local_data_status INTEGER,
                    rb_local_deleted INTEGER, rb_local_synced INTEGER);
                INSERT INTO djmdSongMyTag VALUES ('A1','T1','C1',1,256,0,0,0);
                INSERT INTO djmdSongMyTag VALUES ('A2','T2','C1',2,256,0,0,0);
                INSERT INTO djmdSongMyTag VALUES ('D1','T3','C2',3,262,0,1,0);
                """;
            setup.ExecuteNonQuery();
        }

        var profile = RekordboxSongMyTagWriteSemantics.Qualify(connection, requireTombstone: true);
        if (profile.Active.DataStatus != 256 || profile.Active.LocalDataStatus != 0)
            throw new InvalidOperationException("active SongMyTag status tuple was not derived from evidence rows");
        if (profile.Tombstone is null || profile.Tombstone.DataStatus != 262 || profile.Tombstone.LocalDataStatus != 0)
            throw new InvalidOperationException("tombstone SongMyTag status tuple was not derived from evidence rows");

        using (var transaction = connection.BeginTransaction())
        {
            var transactional = RekordboxSongMyTagWriteSemantics.Qualify(connection, transaction, requireTombstone: true);
            if (transactional != profile)
                throw new InvalidOperationException("transactional SongMyTag semantics differed from base evidence");
            transaction.Rollback();
        }

        using (var delete = connection.CreateCommand())
        {
            delete.CommandText = "DELETE FROM djmdSongMyTag WHERE rb_local_deleted <> 0;";
            delete.ExecuteNonQuery();
        }
        var addOnly = RekordboxSongMyTagWriteSemantics.Qualify(connection);
        if (addOnly.Tombstone is not null)
            throw new InvalidOperationException("missing tombstone evidence produced a tombstone profile");
        AssertFailsClosed(
            () => RekordboxSongMyTagWriteSemantics.Qualify(connection, requireTombstone: true),
            "removal semantics were accepted without tombstone evidence");

        using (var conflicting = connection.CreateCommand())
        {
            conflicting.CommandText = "INSERT INTO djmdSongMyTag VALUES ('A3','T3','C3',1,257,0,0,0);";
            conflicting.ExecuteNonQuery();
        }
        AssertFailsClosed(
            () => RekordboxSongMyTagWriteSemantics.Qualify(connection),
            "ambiguous active status tuples were accepted");
    }

    private static void AssertFailsClosed(Action action, string message)
    {
        try
        {
            action();
        }
        catch (InvalidDataException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }
}
