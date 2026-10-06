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
}
