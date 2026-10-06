using System.Globalization;
using Microsoft.Data.Sqlite;

namespace RekordboxMyTagSync.Core;

internal static class RekordboxSongMyTagWriter
{
    internal static int Apply(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<RekordboxAssignmentMutation> mutations,
        RekordboxSongMyTagWriteProfile profile,
        long firstLocalUsn)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(mutations);
        ArgumentNullException.ThrowIfNull(profile);
        if (connection.State != System.Data.ConnectionState.Open)
            throw new InvalidOperationException("SQLite connection must be open before applying SongMyTag mutations.");
        if (firstLocalUsn < 0)
            throw new ArgumentOutOfRangeException(nameof(firstLocalUsn));

        var now = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);
        for (var index = 0; index < mutations.Count; index++)
        {
            long localUsn;
            try
            {
                localUsn = checked(firstLocalUsn + index);
            }
            catch (OverflowException ex)
            {
                throw new InvalidDataException("SongMyTag rb_local_usn overflow.", ex);
            }

            var mutation = mutations[index];
            switch (mutation.Kind)
            {
                case PreviewDetailKind.Add:
                    ApplyAdd(connection, transaction, mutation, profile.Active, localUsn, now);
                    break;
                case PreviewDetailKind.Remove:
                    if (profile.Tombstone is null)
                        throw new InvalidDataException(
                            "SongMyTag removal is fail-closed because tombstone semantics were not qualified.");
                    ApplyRemove(connection, transaction, mutation, profile.Tombstone, localUsn, now);
                    break;
                default:
                    throw new InvalidDataException($"Unsupported SongMyTag mutation kind '{mutation.Kind}'.");
            }
        }

        return mutations.Count;
    }

    private static void ApplyAdd(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RekordboxAssignmentMutation mutation,
        RekordboxSongMyTagStatusTuple active,
        long localUsn,
        string now)
    {
        var matches = ReadLinkRows(connection, transaction, mutation.ContentId, mutation.MyTagId);
        var activeRows = matches.Where(x => !x.LocalDeleted).ToArray();
        if (activeRows.Length != 0)
            throw new InvalidDataException(
                $"Add mutation already has an active SongMyTag row for ContentID '{mutation.ContentId}' and MyTagID '{mutation.MyTagId}'.");

        var tombstones = matches.Where(x => x.LocalDeleted).ToArray();
        if (tombstones.Length > 1)
            throw new InvalidDataException(
                $"Add mutation found multiple SongMyTag tombstones for ContentID '{mutation.ContentId}' and MyTagID '{mutation.MyTagId}'.");

        if (tombstones.Length == 1)
        {
            using var reactivate = connection.CreateCommand();
            reactivate.Transaction = transaction;
            reactivate.CommandText = """
                UPDATE djmdSongMyTag
                SET rb_data_status = $dataStatus,
                    rb_local_data_status = $localDataStatus,
                    rb_local_deleted = 0,
                    rb_local_synced = 0,
                    rb_local_usn = $localUsn,
                    updated_at = $updated
                WHERE ID = $id AND COALESCE(rb_local_deleted, 0) <> 0;
                """;
            reactivate.Parameters.AddWithValue("$dataStatus", active.DataStatus);
            reactivate.Parameters.AddWithValue("$localDataStatus", active.LocalDataStatus);
            reactivate.Parameters.AddWithValue("$localUsn", localUsn);
            reactivate.Parameters.AddWithValue("$updated", now);
            reactivate.Parameters.AddWithValue("$id", tombstones[0].Id);
            if (reactivate.ExecuteNonQuery() != 1)
                throw new InvalidDataException("SongMyTag tombstone reactivation did not update exactly one row.");
            return;
        }

        var id = GenerateNewId(connection, transaction);
        var trackNo = NextTrackNo(connection, transaction, mutation.MyTagId);
        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO djmdSongMyTag(
                ID, UUID, MyTagID, ContentID, TrackNo,
                rb_data_status, rb_local_data_status, rb_local_deleted, rb_local_synced,
                rb_local_usn, created_at, updated_at)
            VALUES(
                $id, $uuid, $tag, $content, $trackNo,
                $dataStatus, $localDataStatus, 0, 0,
                $localUsn, $created, $updated);
            """;
        insert.Parameters.AddWithValue("$id", id);
        insert.Parameters.AddWithValue("$uuid", Guid.NewGuid().ToString());
        insert.Parameters.AddWithValue("$tag", mutation.MyTagId);
        insert.Parameters.AddWithValue("$content", mutation.ContentId);
        insert.Parameters.AddWithValue("$trackNo", trackNo);
        insert.Parameters.AddWithValue("$dataStatus", active.DataStatus);
        insert.Parameters.AddWithValue("$localDataStatus", active.LocalDataStatus);
        insert.Parameters.AddWithValue("$localUsn", localUsn);
        insert.Parameters.AddWithValue("$created", now);
        insert.Parameters.AddWithValue("$updated", now);
        if (insert.ExecuteNonQuery() != 1)
            throw new InvalidDataException("SongMyTag insert did not create exactly one row.");
    }

    private static void ApplyRemove(
        SqliteConnection connection,
        SqliteTransaction transaction,
        RekordboxAssignmentMutation mutation,
        RekordboxSongMyTagStatusTuple tombstone,
        long localUsn,
        string now)
    {
        var matches = ReadLinkRows(connection, transaction, mutation.ContentId, mutation.MyTagId);
        var activeRows = matches.Where(x => !x.LocalDeleted).ToArray();
        if (activeRows.Length != 1)
            throw new InvalidDataException(
                $"Remove mutation requires exactly one active SongMyTag row for ContentID '{mutation.ContentId}' and MyTagID '{mutation.MyTagId}', observed {activeRows.Length}.");

        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE djmdSongMyTag
            SET rb_data_status = $dataStatus,
                rb_local_data_status = $localDataStatus,
                rb_local_deleted = 1,
                rb_local_synced = 0,
                rb_local_usn = $localUsn,
                updated_at = $updated
            WHERE ID = $id AND COALESCE(rb_local_deleted, 0) = 0;
            """;
        update.Parameters.AddWithValue("$dataStatus", tombstone.DataStatus);
        update.Parameters.AddWithValue("$localDataStatus", tombstone.LocalDataStatus);
        update.Parameters.AddWithValue("$localUsn", localUsn);
        update.Parameters.AddWithValue("$updated", now);
        update.Parameters.AddWithValue("$id", activeRows[0].Id);
        if (update.ExecuteNonQuery() != 1)
            throw new InvalidDataException("SongMyTag tombstone update did not affect exactly one row.");
    }

    private static long NextTrackNo(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string myTagId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT MAX(TrackNo) FROM djmdSongMyTag WHERE MyTagID = $tag;";
        command.Parameters.AddWithValue("$tag", myTagId);
        var value = command.ExecuteScalar();
        if (value is null or DBNull)
            return 1;

        long maximum;
        try
        {
            maximum = Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            throw new InvalidDataException("Existing SongMyTag TrackNo is not a valid integer.", ex);
        }
        if (maximum < 0)
            throw new InvalidDataException("Existing SongMyTag TrackNo is negative.");
        try
        {
            return checked(maximum + 1);
        }
        catch (OverflowException ex)
        {
            throw new InvalidDataException("SongMyTag TrackNo overflow.", ex);
        }
    }

    private static string GenerateNewId(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT ID FROM djmdSongMyTag WHERE ID IS NOT NULL AND TRIM(ID) <> '';";
        using var reader = command.ExecuteReader();
        var ids = new List<string>();
        while (reader.Read())
            ids.Add(reader.GetString(0).Trim());
        if (ids.Count == 0)
            throw new InvalidDataException("Cannot infer SongMyTag ID scheme from an empty table.");

        var numeric = ids.All(x => ulong.TryParse(x, NumberStyles.None, CultureInfo.InvariantCulture, out _));
        var guids = ids.All(x => Guid.TryParse(x, out _));
        if (numeric == guids)
            throw new InvalidDataException("SongMyTag ID scheme is mixed or ambiguous.");

        if (numeric)
        {
            var maximum = ids.Max(x => ulong.Parse(x, CultureInfo.InvariantCulture));
            if (maximum == ulong.MaxValue)
                throw new InvalidDataException("SongMyTag numeric ID space is exhausted.");
            return checked(maximum + 1).ToString(CultureInfo.InvariantCulture);
        }

        var existing = ids.ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var attempt = 0; attempt < 16; attempt++)
        {
            var candidate = Guid.NewGuid().ToString();
            if (!existing.Contains(candidate))
                return candidate;
        }
        throw new InvalidDataException("Could not allocate a unique SongMyTag GUID ID.");
    }

    private static List<LinkRow> ReadLinkRows(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string contentId,
        string myTagId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT ID, TrackNo, COALESCE(rb_local_deleted, 0)
            FROM djmdSongMyTag
            WHERE ContentID = $content AND MyTagID = $tag;
            """;
        command.Parameters.AddWithValue("$content", contentId);
        command.Parameters.AddWithValue("$tag", myTagId);
        using var reader = command.ExecuteReader();
        var result = new List<LinkRow>();
        while (reader.Read())
        {
            if (reader.IsDBNull(0) || reader.IsDBNull(1))
                throw new InvalidDataException("SongMyTag link identity or TrackNo is NULL.");
            var id = reader.GetString(0).Trim();
            if (id.Length == 0)
                throw new InvalidDataException("SongMyTag link ID is empty.");
            long trackNo;
            long deleted;
            try
            {
                trackNo = Convert.ToInt64(reader.GetValue(1), CultureInfo.InvariantCulture);
                deleted = Convert.ToInt64(reader.GetValue(2), CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
            {
                throw new InvalidDataException("SongMyTag TrackNo/deleted state is not an integer.", ex);
            }
            result.Add(new LinkRow(id, trackNo, deleted != 0));
        }
        return result;
    }

    private sealed record LinkRow(string Id, long TrackNo, bool LocalDeleted);
}
