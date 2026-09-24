using Microsoft.Data.Sqlite;

namespace CleanLens.Data;

public sealed class CleanLensDatabase
{
    private readonly string connectionString;

    public CleanLensDatabase(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath, Pooling = false }.ToString();
        Initialize();
    }

    public async Task RecordOperationAsync(string applicationName, string operation, string result, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO history (application_name, operation, result, created_utc) VALUES ($name, $operation, $result, $created);";
        command.Parameters.AddWithValue("$name", applicationName);
        command.Parameters.AddWithValue("$operation", operation);
        command.Parameters.AddWithValue("$result", result);
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<HistoryEntry>> GetHistoryAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, application_name, operation, result, created_utc FROM history ORDER BY id DESC LIMIT 500;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var entries = new List<HistoryEntry>();
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(new HistoryEntry(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), DateTimeOffset.Parse(reader.GetString(4))));
        }
        return entries;
    }

    public async Task RecordQuarantineAsync(string operationId, string originalPath, string quarantinePath, string applicationName, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO quarantine (operation_id, application_name, original_path, quarantine_path, created_utc) VALUES ($id, $name, $original, $quarantine, $created);";
        command.Parameters.AddWithValue("$id", operationId);
        command.Parameters.AddWithValue("$name", applicationName);
        command.Parameters.AddWithValue("$original", originalPath);
        command.Parameters.AddWithValue("$quarantine", quarantinePath);
        command.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<QuarantineEntry>> GetQuarantineAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT operation_id, application_name, original_path, quarantine_path, created_utc FROM quarantine ORDER BY created_utc DESC;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var entries = new List<QuarantineEntry>();
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(new QuarantineEntry(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), DateTimeOffset.Parse(reader.GetString(4))));
        }
        return entries;
    }

    public async Task RemoveQuarantineAsync(string operationId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM quarantine WHERE operation_id = $id;";
        command.Parameters.AddWithValue("$id", operationId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private void Initialize()
    {
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            CREATE TABLE IF NOT EXISTS history (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                application_name TEXT NOT NULL,
                operation TEXT NOT NULL,
                result TEXT NOT NULL,
                created_utc TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS quarantine (
                operation_id TEXT PRIMARY KEY,
                application_name TEXT NOT NULL,
                original_path TEXT NOT NULL,
                quarantine_path TEXT NOT NULL,
                created_utc TEXT NOT NULL
            );
            """;
        command.ExecuteNonQuery();
    }
}

public sealed record HistoryEntry(long Id, string ApplicationName, string Operation, string Result, DateTimeOffset CreatedAt);

public sealed record QuarantineEntry(string OperationId, string ApplicationName, string OriginalPath, string QuarantinePath, DateTimeOffset CreatedAt);
