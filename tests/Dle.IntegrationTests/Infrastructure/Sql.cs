using System.Data;
using System.Globalization;
using System.Text;

using Npgsql;

namespace Dle.IntegrationTests.Infrastructure;

/// <summary>
/// Small, explicit helpers for talking to the test database directly.
/// </summary>
/// <remarks>
/// Deliberately not a micro-ORM. Half of what this suite asserts is that a particular column has a
/// particular PostgreSQL type and a particular value in it, so the reads stay at the level where the
/// type is visible: read by ordinal, cast explicitly, and let the driver's own mapping be part of
/// what is under test.
/// </remarks>
public static class Sql
{
    /// <summary>Runs a statement that returns nothing.</summary>
    /// <param name="dataSource">The data source.</param>
    /// <param name="sql">The statement.</param>
    /// <param name="parameters">Parameters, by name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of rows affected.</returns>
    public static async Task<int> ExecuteAsync(
        NpgsqlDataSource dataSource,
        string sql,
        object?[]? parameters = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        await using NpgsqlCommand command = dataSource.CreateCommand(sql);
        AddParameters(command, parameters);

        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Runs a statement that returns a single value.</summary>
    /// <typeparam name="T">The value's type.</typeparam>
    /// <param name="dataSource">The data source.</param>
    /// <param name="sql">The statement.</param>
    /// <param name="parameters">Parameters, positional, referenced as <c>$1</c>, <c>$2</c>, ….</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The value, or <see langword="default"/> when the result is null or empty.</returns>
    public static async Task<T?> ScalarAsync<T>(
        NpgsqlDataSource dataSource,
        string sql,
        object?[]? parameters = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        await using NpgsqlCommand command = dataSource.CreateCommand(sql);
        AddParameters(command, parameters);

        object? value = await command.ExecuteScalarAsync(cancellationToken);

        return value is null or DBNull ? default : (T)value;
    }

    /// <summary>Reads the first column of every row as a string.</summary>
    /// <param name="dataSource">The data source.</param>
    /// <param name="sql">The statement.</param>
    /// <param name="parameters">Parameters, positional.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The values, in row order. A null becomes an empty string.</returns>
    public static async Task<IReadOnlyList<string>> StringsAsync(
        NpgsqlDataSource dataSource,
        string sql,
        object?[]? parameters = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        List<string> values = [];

        await using NpgsqlCommand command = dataSource.CreateCommand(sql);
        AddParameters(command, parameters);

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(
            CommandBehavior.SingleResult,
            cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            values.Add(reader.IsDBNull(0) ? string.Empty : reader.GetValue(0)?.ToString() ?? string.Empty);
        }

        return values;
    }

    /// <summary>
    /// Runs <c>EXPLAIN</c> over a statement and returns the plan as one string.
    /// </summary>
    /// <param name="dataSource">The data source.</param>
    /// <param name="sql">The statement to explain, without the <c>EXPLAIN</c> prefix.</param>
    /// <param name="options">
    /// The <c>EXPLAIN</c> option list, for example <c>ANALYZE, BUFFERS</c>. §B.5.2 asks for exactly
    /// those two, because only <c>ANALYZE</c> proves the plan was executed and only <c>BUFFERS</c>
    /// shows whether the heap was touched.
    /// </param>
    /// <param name="parameters">Parameters, positional.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The plan text, newline separated.</returns>
    public static async Task<string> ExplainAsync(
        NpgsqlDataSource dataSource,
        string sql,
        string options = "ANALYZE, BUFFERS",
        object?[]? parameters = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        string statement = string.Create(CultureInfo.InvariantCulture, $"EXPLAIN ({options}) {sql}");

        IReadOnlyList<string> lines = await StringsAsync(dataSource, statement, parameters, cancellationToken);

        StringBuilder plan = new();

        foreach (string line in lines)
        {
            plan.AppendLine(line);
        }

        return plan.ToString();
    }

    /// <summary>Whether a relation of any kind exists in the <c>public</c> schema.</summary>
    /// <param name="dataSource">The data source.</param>
    /// <param name="name">The relation name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><see langword="true"/> when it exists.</returns>
    public static async Task<bool> RelationExistsAsync(
        NpgsqlDataSource dataSource,
        string name,
        CancellationToken cancellationToken = default) =>
        await ScalarAsync<bool>(
            dataSource,
            "SELECT to_regclass('public.' || quote_ident($1)) IS NOT NULL",
            [name],
            cancellationToken);

    /// <summary>Adds positional parameters, so a statement can spell them <c>$1</c>, <c>$2</c>, ….</summary>
    private static void AddParameters(NpgsqlCommand command, object?[]? parameters)
    {
        if (parameters is null)
        {
            return;
        }

        foreach (object? value in parameters)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value ?? DBNull.Value });
        }
    }
}
