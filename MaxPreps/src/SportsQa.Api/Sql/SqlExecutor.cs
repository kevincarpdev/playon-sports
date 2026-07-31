using Microsoft.Data.Sqlite;
using SportsQa.Api.Configuration;
using SportsQa.Api.Data;

namespace SportsQa.Api.Sql;

public sealed record ResultSet(
    IReadOnlyList<string> Columns,
    IReadOnlyList<IReadOnlyList<object?>> Rows,
    bool Truncated)
{
    public bool IsEmpty => Rows.Count == 0;

    /// <summary>The single cell of a 1x1 result, which is what most questions reduce to.</summary>
    public object? Scalar => Rows is [[var only]] ? only : null;
}

public sealed record ExecutionResult(ResultSet? Data, string? ErrorCode, string? ErrorDetail)
{
    public bool Succeeded => Data is not null;

    public static ExecutionResult Success(ResultSet data) => new(data, null, null);
    public static ExecutionResult Failure(string code, string detail) => new(null, code, detail);
}

/// <summary>
/// Bounded execution of already-validated SQL. Read-only connection, statement timeout, and
/// a row ceiling enforced while reading rather than trusted to the query — so one bad plan
/// cannot exhaust the process.
/// </summary>
public sealed class SqlExecutor(SportsQaOptions options)
{
    public async Task<ExecutionResult> ExecuteAsync(
        string sql,
        int maxRows,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, object?>? parameters = null)
    {
        try
        {
            await using var connection = new SqliteConnection(
                $"Data Source={options.DatabasePath};Mode=ReadOnly");
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText = SqlGuard.EnforceRowLimit(sql, maxRows);
            command.CommandTimeout = options.Execution.CommandTimeoutSeconds;

            foreach (var (name, value) in parameters ?? new Dictionary<string, object?>())
            {
                command.Parameters.AddWithValue(name, value ?? DBNull.Value);
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return ExecutionResult.Success(await ReadBoundedAsync(reader, maxRows, cancellationToken));
        }
        catch (SqliteException exception)
        {
            // The database rejected it. Expected whenever the model invents schema, so this
            // is a normal outcome to report, not an incident.
            return ExecutionResult.Failure("sql_execution_failed", exception.Message);
        }
    }

    private static async Task<ResultSet> ReadBoundedAsync(
        SqliteDataReader reader, int maxRows, CancellationToken cancellationToken)
    {
        var columns = Enumerable.Range(0, reader.FieldCount)
            .Select(reader.GetName)
            .ToList();

        var rows = new List<IReadOnlyList<object?>>();
        var truncated = false;

        while (await reader.ReadAsync(cancellationToken))
        {
            if (rows.Count >= maxRows)
            {
                truncated = true;
                break;
            }

            rows.Add(Enumerable.Range(0, reader.FieldCount)
                .Select(index => reader.IsDBNull(index) ? null : reader.GetValue(index))
                .ToList());
        }

        return new ResultSet(columns, rows, truncated);
    }
}
