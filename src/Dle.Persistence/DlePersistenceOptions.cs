using System.ComponentModel.DataAnnotations;

namespace Dle.Persistence;

/// <summary>
/// Configuration of the control-plane database connection, bound from <c>Dle:Persistence</c>.
/// </summary>
/// <remarks>
/// The connection string itself is not here: it lives in <c>ConnectionStrings:Postgres</c> like
/// every other connection string in the product (§C.4), so that a deployment can supply it the way
/// its platform supplies secrets without knowing anything about this section.
/// </remarks>
public sealed class DlePersistenceOptions
{
    /// <summary>Configuration section this class binds to.</summary>
    public const string SectionName = "Dle:Persistence";

    /// <summary>Name of the connection string entry read from configuration.</summary>
    public const string ConnectionStringName = "Postgres";

    /// <summary>
    /// Timeout for a single command, in seconds. Control-plane work only; the resolve path does not
    /// use this context.
    /// </summary>
    [Range(1, 600)]
    public int CommandTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// How many times a transient failure is retried. Zero disables the retrying execution
    /// strategy.
    /// </summary>
    [Range(0, 20)]
    public int MaxRetryCount { get; set; } = 3;

    /// <summary>Upper bound of the exponential backoff between retries, in seconds.</summary>
    [Range(1, 300)]
    public int MaxRetryDelaySeconds { get; set; } = 5;

    /// <summary>
    /// How many slug counter values a process claims in one go (ADR-007).
    /// </summary>
    /// <remarks>
    /// A larger block means fewer round trips and more values skipped when a process restarts.
    /// Skipping is harmless — the space holds 1.4 × 10^14 values — so the block is sized for the
    /// round trips, not for the waste.
    /// </remarks>
    [Range(1, 1_000_000)]
    public int SlugBlockSize { get; set; } = 1000;

    /// <summary>Name of the slug counter space to draw from.</summary>
    [Required]
    [StringLength(64, MinimumLength = 1)]
    public string SlugSequenceName { get; set; } = "default";

    /// <summary>How long a stored idempotent response stays replayable, in hours (§B.7.3).</summary>
    [Range(1, 720)]
    public int IdempotencyRetentionHours { get; set; } = 24;

    /// <summary>
    /// How long a webhook delivery claimed by a worker stays claimed before another worker may take
    /// it, in seconds.
    /// </summary>
    [Range(5, 3600)]
    public int WebhookLeaseSeconds { get; set; } = 60;

    /// <summary>Table EF Core records applied migrations in.</summary>
    [Required]
    [StringLength(63, MinimumLength = 1)]
    public string MigrationsHistoryTable { get; set; } = "__dle_migrations_history";
}
