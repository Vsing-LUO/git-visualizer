using GitVisualizer.Core;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace GitVisualizer.Infrastructure.Persistence;

public sealed class OperationLogStore : IOperationLogStore
{
    private readonly SemaphoreSlim gate = new(1, 1);

    private static string ConnectionString =>
        new SqliteConnectionStringBuilder { DataSource = LocalPaths.DatabaseFile }.ToString();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        LocalPaths.EnsureCreated();
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE IF NOT EXISTS operation_log (
                    id TEXT PRIMARY KEY,
                    timestamp TEXT NOT NULL,
                    repository_path TEXT NOT NULL,
                    operation TEXT NOT NULL,
                    success INTEGER NOT NULL,
                    risk INTEGER NOT NULL,
                    summary TEXT NOT NULL,
                    equivalent_command TEXT NOT NULL,
                    recovery_point_id TEXT NULL,
                    error_code TEXT NULL,
                    details_json TEXT NOT NULL DEFAULT '[]'
                );
                CREATE INDEX IF NOT EXISTS ix_operation_log_repo_time
                    ON operation_log(repository_path, timestamp DESC);
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            var hasDetailsColumn = false;
            var schemaCommand = connection.CreateCommand();
            schemaCommand.CommandText = "PRAGMA table_info(operation_log);";
            await using (var reader = await schemaCommand.ExecuteReaderAsync(cancellationToken)
                             .ConfigureAwait(false))
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (string.Equals(
                            reader.GetString(reader.GetOrdinal("name")),
                            "details_json",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        hasDetailsColumn = true;
                        break;
                    }
                }
            }

            if (!hasDetailsColumn)
            {
                var migrationCommand = connection.CreateCommand();
                migrationCommand.CommandText =
                    "ALTER TABLE operation_log ADD COLUMN details_json TEXT NOT NULL DEFAULT '[]';";
                await migrationCommand.ExecuteNonQueryAsync(cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task AddAsync(OperationLogEntry entry, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT OR REPLACE INTO operation_log
                (id, timestamp, repository_path, operation, success, risk, summary,
                 equivalent_command, recovery_point_id, error_code, details_json)
                VALUES
                ($id, $timestamp, $repository, $operation, $success, $risk, $summary,
                 $command, $recovery, $error, $details);
                """;
            command.Parameters.AddWithValue("$id", entry.Id);
            command.Parameters.AddWithValue("$timestamp", entry.Timestamp.ToString("O"));
            command.Parameters.AddWithValue("$repository", entry.RepositoryPath);
            command.Parameters.AddWithValue("$operation", entry.Operation);
            command.Parameters.AddWithValue("$success", entry.Success ? 1 : 0);
            command.Parameters.AddWithValue("$risk", (int)entry.Risk);
            command.Parameters.AddWithValue("$summary", Redact(entry.Summary));
            command.Parameters.AddWithValue("$command", Redact(entry.EquivalentCommand));
            command.Parameters.AddWithValue("$recovery", (object?)entry.RecoveryPointId ?? DBNull.Value);
            command.Parameters.AddWithValue("$error", (object?)entry.ErrorCode ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "$details",
                JsonSerializer.Serialize(
                    (entry.Details ?? []).Select(Redact).ToArray()));
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<IReadOnlyList<OperationLogEntry>> GetRecentAsync(
        string? repositoryPath, int count, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            var command = connection.CreateCommand();
            command.CommandText = repositoryPath is null
                ? "SELECT * FROM operation_log ORDER BY timestamp DESC LIMIT $count;"
                : "SELECT * FROM operation_log WHERE repository_path = $repository ORDER BY timestamp DESC LIMIT $count;";
            command.Parameters.AddWithValue("$count", Math.Clamp(count, 1, 1000));
            if (repositoryPath is not null)
            {
                command.Parameters.AddWithValue("$repository", repositoryPath);
            }

            var entries = new List<OperationLogEntry>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                entries.Add(new OperationLogEntry(
                    reader.GetString(reader.GetOrdinal("id")),
                    DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("timestamp"))),
                    reader.GetString(reader.GetOrdinal("repository_path")),
                    reader.GetString(reader.GetOrdinal("operation")),
                    reader.GetInt64(reader.GetOrdinal("success")) == 1,
                    (GitOperationRisk)reader.GetInt64(reader.GetOrdinal("risk")),
                    reader.GetString(reader.GetOrdinal("summary")),
                    reader.GetString(reader.GetOrdinal("equivalent_command")),
                    reader.IsDBNull(reader.GetOrdinal("recovery_point_id"))
                        ? null
                        : reader.GetString(reader.GetOrdinal("recovery_point_id")),
                    reader.IsDBNull(reader.GetOrdinal("error_code"))
                        ? null
                        : reader.GetString(reader.GetOrdinal("error_code")),
                    JsonSerializer.Deserialize<string[]>(
                        reader.GetString(reader.GetOrdinal("details_json"))) ?? []));
            }

            return entries;
        }
        finally
        {
            gate.Release();
        }
    }

    private static string Redact(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var at = value.IndexOf("://", StringComparison.Ordinal);
        if (at < 0)
        {
            return value;
        }

        var credentialEnd = value.IndexOf('@', at + 3);
        return credentialEnd < 0
            ? value
            : value[..(at + 3)] + "***@" + value[(credentialEnd + 1)..];
    }
}
