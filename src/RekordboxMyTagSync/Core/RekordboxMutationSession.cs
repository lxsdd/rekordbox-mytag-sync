using System.IO;
using Microsoft.Data.Sqlite;

namespace RekordboxMyTagSync.Core;

internal sealed class RekordboxMutationSession : IDisposable
{
    private readonly SqliteConnection _connection;

    private RekordboxMutationSession(SqliteConnection connection, string canonicalPath)
    {
        _connection = connection;
        CanonicalPath = canonicalPath;
    }

    internal string CanonicalPath { get; }
    internal SqliteConnection Connection => _connection;

    internal static RekordboxMutationSession Open(string databasePath, string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (RekordboxProcessGuard.IsRunning())
            throw new InvalidOperationException("rekordbox is running. Database mutation is blocked until rekordbox is closed.");

        var canonicalPath = Path.GetFullPath(databasePath);
        if (!File.Exists(canonicalPath))
            throw new FileNotFoundException("rekordbox master.db not found.", canonicalPath);

        RekordboxSqlCipherDatabase.EnsureSqliteInitialized();
        var connection = new SqliteConnection(
            RekordboxSqlCipherDatabase.BuildConnectionString(canonicalPath, key, SqliteOpenMode.ReadWrite));
        try
        {
            connection.Open();
            return new RekordboxMutationSession(connection, canonicalPath);
        }
        catch (SqliteException ex)
        {
            connection.Dispose();
            throw new InvalidDataException(
                "Could not open rekordbox master.db for mutation with the configured SQLCipher profile.", ex);
        }
    }

    internal SqliteTransaction BeginTransaction() => _connection.BeginTransaction();

    public void Dispose() => _connection.Dispose();
}
