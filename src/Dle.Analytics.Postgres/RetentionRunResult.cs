namespace Dle.Analytics.Postgres;

/// <summary>
/// What one pass of <see cref="IRetentionService"/> removed, and when.
/// </summary>
/// <remarks>
/// §E.6.3 requires retention to be automatic <i>and its runs to be auditable</i>. The same values
/// are written to <c>analytics_retention_runs</c> and returned here, so a run leaves evidence in
/// the database, in the log, and in whatever metric the worker publishes — three places that have
/// to agree.
/// </remarks>
public sealed record RetentionRunResult
{
    /// <summary>Status of a run that completed and removed what the policy allowed.</summary>
    public const string StatusOk = "ok";

    /// <summary>Status of a run that only reported what it would have removed.</summary>
    public const string StatusDryRun = "dry_run";

    /// <summary>Status of a run that failed part way through.</summary>
    public const string StatusFailed = "failed";

    /// <summary>Identifier of the run, matching the row in <c>analytics_retention_runs</c>.</summary>
    public required Guid Id { get; init; }

    /// <summary>When the pass started.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>When the pass finished, successfully or not.</summary>
    public required DateTimeOffset FinishedAt { get; init; }

    /// <summary>Raw retention window applied, in days.</summary>
    public required int RawDays { get; init; }

    /// <summary>Aggregate retention window applied, in days.</summary>
    public required int AggregatedDays { get; init; }

    /// <summary>IP prefix window applied, in days, or <see langword="null"/> when disabled.</summary>
    public int? IpPrefixDays { get; init; }

    /// <summary>Whether the pass reported without removing anything.</summary>
    public required bool DryRun { get; init; }

    /// <summary>
    /// Partitions detached and dropped, in the order they were removed. On a dry run, the
    /// partitions that would have been removed.
    /// </summary>
    public required IReadOnlyList<string> PartitionsDropped { get; init; }

    /// <summary>Raw events reduced to hash-only form by clearing their IP prefix.</summary>
    public long RowsAnonymised { get; init; }

    /// <summary>Rollup rows deleted because they fell outside the aggregate window.</summary>
    public long RollupRowsDeleted { get; init; }

    /// <summary>One of <see cref="StatusOk"/>, <see cref="StatusDryRun"/>, <see cref="StatusFailed"/>.</summary>
    public required string Status { get; init; }

    /// <summary>Failure message when <see cref="Status"/> is <see cref="StatusFailed"/>.</summary>
    public string? Error { get; init; }
}
