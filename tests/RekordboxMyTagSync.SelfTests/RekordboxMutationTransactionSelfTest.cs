using Microsoft.Data.Sqlite;
using RekordboxMyTagSync.Core;

public static class RekordboxMutationTransactionSelfTest
{
    public static void Run()
    {
        RekordboxSqlCipherDatabase.EnsureSqliteInitialized();
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var setup = connection.CreateCommand())
        {
            setup.CommandText = "CREATE TABLE fixture (id INTEGER PRIMARY KEY, value TEXT NOT NULL); INSERT INTO fixture(id, value) VALUES (1, 'before');";
            setup.ExecuteNonQuery();
        }

        var validationFailureObserved = false;
        try
        {
            _ = RekordboxMutationTransaction.Execute(
                connection,
                transaction =>
                {
                    using var command = connection.CreateCommand();
                    command.Transaction = transaction;
                    command.CommandText = "UPDATE fixture SET value = 'mutated' WHERE id = 1;";
                    if (command.ExecuteNonQuery() != 1)
                        throw new InvalidOperationException("transaction rollback fixture update count mismatch");
                    return "mutated";
                },
                (_, result) =>
                {
                    if (!string.Equals(result, "mutated", StringComparison.Ordinal))
                        throw new InvalidOperationException("unexpected transaction result");
                    throw new InvalidDataException("intentional pre-commit validation failure");
                });
        }
        catch (InvalidDataException ex) when (ex.Message.Contains("intentional", StringComparison.Ordinal))
        {
            validationFailureObserved = true;
        }
        if (!validationFailureObserved)
            throw new InvalidOperationException("pre-commit validation failure did not propagate");
        if (!string.Equals(ReadValue(connection), "before", StringComparison.Ordinal))
            throw new InvalidOperationException("failed mutation transaction was not rolled back");

        var committed = RekordboxMutationTransaction.Execute(
            connection,
            transaction =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "UPDATE fixture SET value = 'after' WHERE id = 1;";
                if (command.ExecuteNonQuery() != 1)
                    throw new InvalidOperationException("transaction commit fixture update count mismatch");
                return "after";
            },
            (transaction, result) =>
            {
                if (!string.Equals(result, "after", StringComparison.Ordinal))
                    throw new InvalidOperationException("unexpected successful transaction result");
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = "SELECT value FROM fixture WHERE id = 1;";
                var current = command.ExecuteScalar() as string;
                if (!string.Equals(current, "after", StringComparison.Ordinal))
                    throw new InvalidDataException("pre-commit postimage validation did not observe the mutation");
            });
        if (!string.Equals(committed, "after", StringComparison.Ordinal) ||
            !string.Equals(ReadValue(connection), "after", StringComparison.Ordinal))
            throw new InvalidOperationException("validated mutation transaction was not committed");
    }

    private static string? ReadValue(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM fixture WHERE id = 1;";
        return command.ExecuteScalar() as string;
    }
}
