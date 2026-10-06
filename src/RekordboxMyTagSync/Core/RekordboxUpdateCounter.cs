using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace RekordboxMyTagSync.Core;

internal static class RekordboxUpdateCounter
{
    internal static long Read(SqliteConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT int_1 FROM agentRegistry WHERE registry_id = 'localUpdateCount';";
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.IsDBNull(0))
            throw new InvalidDataException("agentRegistry localUpdateCount row is missing or NULL.");

        long count;
        try
        {
            count = Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            throw new InvalidDataException("agentRegistry localUpdateCount is not a valid integer.", ex);
        }

        if (count < 0)
            throw new InvalidDataException("agentRegistry localUpdateCount is negative.");
        if (reader.Read())
            throw new InvalidDataException("agentRegistry contains multiple localUpdateCount rows.");
        return count;
    }

    internal static long Advance(SqliteConnection connection, long expectedCurrent, int changeCount)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (expectedCurrent < 0)
            throw new ArgumentOutOfRangeException(nameof(expectedCurrent));
        if (changeCount < 1)
            throw new ArgumentOutOfRangeException(nameof(changeCount));

        var current = Read(connection);
        if (current != expectedCurrent)
            throw new InvalidDataException(
                $"agentRegistry localUpdateCount changed from expected {expectedCurrent} to {current}.");

        long next;
        try
        {
            next = checked(current + changeCount);
        }
        catch (OverflowException ex)
        {
            throw new InvalidDataException("agentRegistry localUpdateCount overflow.", ex);
        }

        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE agentRegistry
            SET int_1 = $next
            WHERE registry_id = 'localUpdateCount' AND int_1 = $current;
            """;
        command.Parameters.AddWithValue("$next", next);
        command.Parameters.AddWithValue("$current", current);
        if (command.ExecuteNonQuery() != 1)
            throw new InvalidDataException("agentRegistry localUpdateCount compare-and-set failed.");
        return next;
    }
}
