using Microsoft.Data.Sqlite;

namespace DependencyManager.Storage;

internal sealed class HttpCache
{
    private readonly string _databasePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public HttpCache(string databasePath)
    {
        _databasePath = databasePath;
        using SqliteConnection connection = OpenConnection();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS HttpCache
            (
                CacheKey TEXT PRIMARY KEY,
                Content TEXT NOT NULL,
                RetrievedUtc TEXT NOT NULL,
                ExpiresUtc TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_HttpCache_ExpiresUtc ON HttpCache (ExpiresUtc);
            """;
        command.ExecuteNonQuery();
    }

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT Content FROM HttpCache WHERE CacheKey = $key AND ExpiresUtc > $now";
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));
            object? value = await command.ExecuteScalarAsync(cancellationToken);
            return value as string;
        }
        finally { _gate.Release(); }
    }

    public async Task SetAsync(string key, string content, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO HttpCache(CacheKey, Content, RetrievedUtc, ExpiresUtc)
                VALUES($key, $content, $retrieved, $expires)
                ON CONFLICT(CacheKey) DO UPDATE SET
                    Content = excluded.Content,
                    RetrievedUtc = excluded.RetrievedUtc,
                    ExpiresUtc = excluded.ExpiresUtc;
                """;
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$content", content);
            command.Parameters.AddWithValue("$retrieved", DateTime.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$expires", DateTime.UtcNow.Add(lifetime).ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            using SqliteConnection connection = OpenConnection();
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "DELETE FROM HttpCache";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally { _gate.Release(); }
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString());
        connection.Open();
        return connection;
    }
}
