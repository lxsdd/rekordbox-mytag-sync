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
        var value = command.ExecuteScalar();
        if (value is null || value is DBNull)
            throw new InvalidDataException("agentRegistry localUpdateCount row is missing or NULL.");
        try
        {
            var count = Convert.ToInt64(value, CultureInfo.InvariantCulture);
            if (count < 0) throw new InvalidDataException("agentRegistry localUpdateCount is negative.");
            return count;
        }
        catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
        {
            throw new InvalidDataException("agentRegistry localUpdateCount is not a valid integer.", ex);
        }
    }
}
