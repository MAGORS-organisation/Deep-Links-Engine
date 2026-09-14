using System.Reflection;
using System.Text;

namespace Dle.Analytics.ClickHouse;

/// <summary>
/// Access to the embedded ClickHouse DDL (<c>Sql/001_clickhouse_schema.sql</c>): the
/// <c>MergeTree</c> click stream partitioned by month, ordered by
/// <c>(tenant_id, link_id, occurred_at)</c> and expired by a <c>TTL</c> that matches the
/// configured retention window, plus the mirror tables the funnel and attribution-quality
/// reports read (ADR-006, FR-247).
/// </summary>
/// <remarks>
/// Applying the script is an operator action, not a startup action: a deployment that has not
/// selected the ClickHouse provider never calls it, and a deployment that has may prefer to apply
/// the DDL through its own migration tooling. <see cref="Build"/> exists so that both paths use
/// exactly the same text.
/// </remarks>
public static class ClickHouseSchemaScript
{
    private const string ResourceName = "Dle.Analytics.ClickHouse.Sql.001_clickhouse_schema.sql";
    private const string RetentionPlaceholder = "{{RAW_RETENTION_DAYS}}";

    /// <summary>Statement separator used by <see cref="BuildStatements"/>.</summary>
    private const char StatementSeparator = ';';

    /// <summary>Start of a SQL line comment.</summary>
    private const string LineCommentMarker = "--";

    /// <summary>
    /// Returns the DDL with the retention placeholder replaced by <paramref name="rawRetentionDays"/>.
    /// </summary>
    /// <param name="rawRetentionDays">Number of days raw events are kept, mirroring
    /// <c>Dle:Privacy:Retention:RawDays</c>. Must be between 1 and 3650.</param>
    /// <returns>The complete script, still containing several statements.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The retention window is outside the accepted
    /// range. The value is interpolated into DDL, so it is validated rather than trusted.</exception>
    public static string Build(int rawRetentionDays)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rawRetentionDays, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rawRetentionDays, 3650);

        string template = ReadTemplate();
        return template.Replace(
            RetentionPlaceholder,
            rawRetentionDays.ToString(CultureInfo.InvariantCulture),
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns the DDL split into individual statements, because the ClickHouse HTTP interface
    /// executes one statement per request.
    /// </summary>
    /// <param name="rawRetentionDays">Number of days raw events are kept.</param>
    /// <returns>The statements in application order, with line comments removed.</returns>
    /// <remarks>
    /// Comments are stripped before the split rather than after, because a semicolon inside a
    /// comment would otherwise cut a statement in half — which it silently did until a test
    /// counted the statements. The stripping is line based and assumes no <c>--</c> appears inside
    /// a string literal in this script, which holds and is worth re-checking before adding one.
    /// </remarks>
    public static IReadOnlyList<string> BuildStatements(int rawRetentionDays)
    {
        string script = StripLineComments(Build(rawRetentionDays));
        List<string> statements = [];

        foreach (string part in script.Split(StatementSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = part.Trim();

            if (trimmed.Length == 0)
            {
                continue;
            }

            statements.Add(trimmed);
        }

        return statements;
    }

    /// <summary>Returns the raw template, retention placeholder included.</summary>
    /// <returns>The embedded script exactly as it ships.</returns>
    /// <exception cref="InvalidOperationException">The embedded resource is missing, which means
    /// the assembly was built without its <c>Sql</c> folder.</exception>
    public static string ReadTemplate()
    {
        Assembly assembly = typeof(ClickHouseSchemaScript).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException(
                $"Embedded ClickHouse schema '{ResourceName}' is missing from {assembly.GetName().Name}.");

        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }

    private static string StripLineComments(string script)
    {
        StringBuilder builder = new(script.Length);

        foreach (string line in script.Split('\n'))
        {
            int marker = line.IndexOf(LineCommentMarker, StringComparison.Ordinal);
            string text = marker >= 0 ? line[..marker] : line;

            builder.Append(text.TrimEnd()).Append('\n');
        }

        return builder.ToString();
    }
}
