using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace RekordboxMyTagSync.Core;

internal sealed record RekordboxSongMyTagStatusTuple(
    long DataStatus,
    long LocalDataStatus);

internal sealed record RekordboxSongMyTagWriteProfile(
    RekordboxSongMyTagStatusTuple Active,
    RekordboxSongMyTagStatusTuple? Tombstone);

internal static class RekordboxSongMyTagWriteSemantics
{
    internal static RekordboxSongMyTagWriteProfile Qualify(
        SqliteConnection connection,
        SqliteTransaction? transaction = null,
        bool requireTombstone = false)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.State != System.Data.ConnectionState.Open)
            throw new InvalidOperationException("SQLite connection must be open before qualifying SongMyTag write semantics.");

        var active = ReadUniqueStatusTuple(
            connection,
            transaction,
            """
            SELECT DISTINCT rb_data_status, rb_local_data_status
            FROM djmdSongMyTag
            WHERE COALESCE(rb_local_deleted, 0) = 0;
            """,
            "active djmdSongMyTag rows");

        var tombstone = ReadOptionalUniqueStatusTuple(
            connection,
            transaction,
            """
            SELECT DISTINCT rb_data_status, rb_local_data_status
            FROM djmdSongMyTag
            WHERE COALESCE(rb_local_deleted, 0) <> 0
              AND COALESCE(rb_local_synced, 0) = 0
              AND ID IS NOT NULL AND TRIM(ID) <> ''
              AND MyTagID IS NOT NULL AND TRIM(MyTagID) <> ''
              AND ContentID IS NOT NULL AND TRIM(ContentID) <> ''
              AND TrackNo IS NOT NULL;
            """,
            "pending local djmdSongMyTag tombstones");

        if (requireTombstone && tombstone is null)
            throw new InvalidDataException(
                "Removal is fail-closed because this database contains no unambiguous local SongMyTag tombstone semantics.");
        if (tombstone is not null && tombstone == active)
            throw new InvalidDataException(
                "SongMyTag active and tombstone status tuples are identical; removal semantics are ambiguous.");

        return new RekordboxSongMyTagWriteProfile(active, tombstone);
    }

    internal static void ApplyActiveStatus(
        SqliteCommand command,
        RekordboxSongMyTagStatusTuple active,
        string dataStatusParameter = "$dataStatus",
        string localDataStatusParameter = "$localDataStatus")
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(active);
        command.Parameters.AddWithValue(dataStatusParameter, active.DataStatus);
        command.Parameters.AddWithValue(localDataStatusParameter, active.LocalDataStatus);
    }

    internal static void ApplyTombstoneStatus(
        SqliteCommand command,
        RekordboxSongMyTagStatusTuple tombstone,
        string dataStatusParameter = "$dataStatus",
        string localDataStatusParameter = "$localDataStatus")
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(tombstone);
        command.Parameters.AddWithValue(dataStatusParameter, tombstone.DataStatus);
        command.Parameters.AddWithValue(localDataStatusParameter, tombstone.LocalDataStatus);
    }

    private static RekordboxSongMyTagStatusTuple ReadUniqueStatusTuple(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        string label)
    {
        var rows = ReadStatusTuples(connection, transaction, sql, label);
        if (rows.Count == 0)
            throw new InvalidDataException($"Cannot qualify {label}: no evidence rows exist.");
        if (rows.Count != 1)
            throw new InvalidDataException(
                $"Cannot qualify {label}: observed {rows.Count} distinct status tuples.");
        return rows[0];
    }

    private static RekordboxSongMyTagStatusTuple? ReadOptionalUniqueStatusTuple(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        string label)
    {
        var rows = ReadStatusTuples(connection, transaction, sql, label);
        if (rows.Count == 0)
            return null;
        if (rows.Count != 1)
            throw new InvalidDataException(
                $"Cannot qualify {label}: observed {rows.Count} distinct status tuples.");
        return rows[0];
    }

    private static List<RekordboxSongMyTagStatusTuple> ReadStatusTuples(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string sql,
        string label)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var result = new List<RekordboxSongMyTagStatusTuple>();
        while (reader.Read())
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1))
                throw new InvalidDataException($"Cannot qualify {label}: status column is NULL.");
            try
            {
                result.Add(new RekordboxSongMyTagStatusTuple(
                    Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture),
                    Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture)));
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            {
                throw new InvalidDataException($"Cannot qualify {label}: status column is not an integer.", ex);
            }
        }
        return result;
    }
}
