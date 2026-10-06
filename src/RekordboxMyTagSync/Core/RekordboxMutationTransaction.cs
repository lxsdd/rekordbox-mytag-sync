using Microsoft.Data.Sqlite;

namespace RekordboxMyTagSync.Core;

internal static class RekordboxMutationTransaction
{
    internal static TResult Execute<TResult>(
        SqliteConnection connection,
        Func<SqliteTransaction, TResult> mutate,
        Action<SqliteTransaction, TResult> validateBeforeCommit)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(mutate);
        ArgumentNullException.ThrowIfNull(validateBeforeCommit);
        if (connection.State != System.Data.ConnectionState.Open)
            throw new InvalidOperationException("SQLite connection must be open before starting a mutation transaction.");

        using var transaction = connection.BeginTransaction();
        try
        {
            var result = mutate(transaction);
            validateBeforeCommit(transaction, result);
            transaction.Commit();
            return result;
        }
        catch
        {
            try
            {
                transaction.Rollback();
            }
            catch (InvalidOperationException)
            {
                // The provider may report that the transaction is already completed after a failed commit.
                // Preserve the original mutation/validation/commit exception.
            }
            catch (SqliteException)
            {
                // Preserve the original exception. A caller must treat the operation as failed and use
                // the pre-write backup if database integrity cannot subsequently be established.
            }
            throw;
        }
    }
}
