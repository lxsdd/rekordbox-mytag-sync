using Microsoft.Data.Sqlite;
using RekordboxMyTagSync.Core;

public static class RekordboxUpdateCounterSelfTest
{
    public static void Run()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE agentRegistry(registry_id TEXT, int_1 INTEGER);
                INSERT INTO agentRegistry(registry_id, int_1) VALUES ('localUpdateCount', 100);
                """;
            command.ExecuteNonQuery();
        }

        if (RekordboxUpdateCounter.Read(connection) != 100)
            throw new InvalidOperationException("local update counter initial value mismatch");
        if (RekordboxUpdateCounter.Advance(connection, 100, 2) != 102)
            throw new InvalidOperationException("local update counter advance mismatch");
        if (RekordboxUpdateCounter.Read(connection) != 102)
            throw new InvalidOperationException("local update counter persisted value mismatch");

        AssertFailsClosed(
            () => RekordboxUpdateCounter.Advance(connection, 100, 1),
            "stale local update counter expectation was accepted");

        using (var transaction = connection.BeginTransaction())
        {
            if (RekordboxUpdateCounter.Read(connection, transaction) != 102)
                throw new InvalidOperationException("transactional local update counter read mismatch");
            if (RekordboxUpdateCounter.Advance(connection, 102, 3, transaction) != 105)
                throw new InvalidOperationException("transactional local update counter advance mismatch");
            if (RekordboxUpdateCounter.Read(connection, transaction) != 105)
                throw new InvalidOperationException("transactional local update counter was not visible inside transaction");
            transaction.Rollback();
        }
        if (RekordboxUpdateCounter.Read(connection) != 102)
            throw new InvalidOperationException("rolled-back local update counter change leaked outside transaction");

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO agentRegistry(registry_id, int_1) VALUES ('localUpdateCount', 103);";
            command.ExecuteNonQuery();
        }
        AssertFailsClosed(
            () => RekordboxUpdateCounter.Read(connection),
            "duplicate local update counter rows were accepted");
    }

    private static void AssertFailsClosed(Action action, string message)
    {
        try
        {
            action();
        }
        catch
        {
            return;
        }
        throw new InvalidOperationException(message);
    }
}
