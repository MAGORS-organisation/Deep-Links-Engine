using Microsoft.Extensions.Configuration;

namespace Dle.Persistence.Fast.Configuration;

/// <summary>
/// Settings that shape the hot-path connection pools and the batched event writers.
/// </summary>
/// <remarks>
/// <para>
/// Bound from the configuration section <c>Dle:Persistence:Fast</c>, with the connection strings read
/// from the standard <c>ConnectionStrings</c> section named in SHARED-KERNEL §16:
/// <c>ConnectionStrings:Postgres</c> for the primary and the optional
/// <c>ConnectionStrings:PostgresRead</c> for the read replica of §B.8 profile B.
/// </para>
/// <para>
/// The four pool and timeout settings are owned by this section, not by the connection string: they
/// are written onto the connection string when the data source is built, so a value for
/// <c>Maximum Pool Size</c>, <c>Minimum Pool Size</c>, <c>Command Timeout</c> or
/// <c>Application Name</c> inside the connection string is overwritten. Having one place that decides
/// them is worth more than the flexibility of having two.
/// </para>
/// <para>
/// Validation runs eagerly in <c>AddDleFastPersistence</c> rather than through
/// <c>ValidateOnStart</c>, because the data annotations validator is reflective and this assembly
/// stays free of reflection (ADR-012). The failure mode is the same: the process refuses to start.
/// </para>
/// </remarks>
public sealed class FastPersistenceOptions
{
    /// <summary>Configuration section these options are read from.</summary>
    public const string SectionName = "Dle:Persistence:Fast";

    /// <summary>Name of the connection string for the primary, writable Postgres instance.</summary>
    public const string PrimaryConnectionStringName = "Postgres";

    /// <summary>
    /// Name of the optional connection string for a read replica. When it is absent the read data
    /// source is the primary one (§B.8 profile A).
    /// </summary>
    public const string ReadConnectionStringName = "PostgresRead";

    /// <summary>Name of the optional connection string for the Valkey L2 cache.</summary>
    public const string CacheConnectionStringName = "Valkey";

    /// <summary>
    /// Value reported to Postgres as <c>application_name</c>. It is what <c>pg_stat_activity</c> shows,
    /// so it is the difference between a diagnosable slow query and an anonymous one.
    /// </summary>
    public string ApplicationName { get; init; } = "dle-edge";

    /// <summary>
    /// Upper bound of the primary connection pool. The default matches a four vCPU edge instance
    /// serving the throughput target of NFR-03 at a cache hit rate above 95 %, where only misses reach
    /// Postgres at all.
    /// </summary>
    public int MaxPoolSize { get; init; } = 64;

    /// <summary>
    /// Connections kept open when idle. A small non-zero value removes the connection handshake from
    /// the first cache miss after a quiet period, which would otherwise show up in the p99 of NFR-02.
    /// </summary>
    public int MinPoolSize { get; init; } = 4;

    /// <summary>
    /// Command timeout in seconds for the read path. Deliberately short: the resolve budget of NFR-01
    /// is eight milliseconds, so a lookup that has been running for seconds has already failed its
    /// caller and is only holding a connection hostage.
    /// </summary>
    public int CommandTimeoutSeconds { get; init; } = 5;

    /// <summary>
    /// Command timeout in seconds for the batched <c>COPY</c> writers, which move up to
    /// <see cref="ClickEventBatchSize"/> rows at a time and are not on any request's critical path.
    /// </summary>
    public int WriteCommandTimeoutSeconds { get; init; } = 30;

    /// <summary>
    /// Capacity of the bounded click event channel. Once it is full, further events are dropped rather
    /// than delaying a redirect (NFR-06, FR-165).
    /// </summary>
    public int ClickEventChannelCapacity { get; init; } = 100_000;

    /// <summary>Largest batch handed to one <c>COPY</c> operation (§C.3.2).</summary>
    public int ClickEventBatchSize { get; init; } = 5_000;

    /// <summary>
    /// Longest a partially filled batch waits before it is written anyway (§C.3.2). Together with
    /// <see cref="ClickEventBatchSize"/> this is the "5 000 rows or 250 ms, whichever comes first"
    /// rule.
    /// </summary>
    public int ClickEventFlushMilliseconds { get; init; } = 250;

    /// <summary>
    /// How long the writer is given to flush what it still holds when the host is shutting down.
    /// </summary>
    public int ShutdownFlushSeconds { get; init; } = 10;

    /// <summary>Largest batch size for probabilistic attribution candidate lookups.</summary>
    public int MaxCandidateRows { get; init; } = 500;

    /// <summary>The batch flush interval as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan ClickEventFlushInterval => TimeSpan.FromMilliseconds(ClickEventFlushMilliseconds);

    /// <summary>The shutdown flush budget as a <see cref="TimeSpan"/>.</summary>
    public TimeSpan ShutdownFlushTimeout => TimeSpan.FromSeconds(ShutdownFlushSeconds);

    /// <summary>
    /// Reads the options from configuration and validates them.
    /// </summary>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>The validated options.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configuration"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A value is malformed or outside its permitted range.</exception>
    public static FastPersistenceOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        IConfiguration section = configuration.GetSection(SectionName);

        var options = new FastPersistenceOptions
        {
            ApplicationName = ConfigurationValues.OptionalString(section, nameof(ApplicationName)) ?? "dle-edge",
            MaxPoolSize = ConfigurationValues.Int32(section, nameof(MaxPoolSize), 64),
            MinPoolSize = ConfigurationValues.Int32(section, nameof(MinPoolSize), 4),
            CommandTimeoutSeconds = ConfigurationValues.Int32(section, nameof(CommandTimeoutSeconds), 5),
            WriteCommandTimeoutSeconds = ConfigurationValues.Int32(section, nameof(WriteCommandTimeoutSeconds), 30),
            ClickEventChannelCapacity = ConfigurationValues.Int32(section, nameof(ClickEventChannelCapacity), 100_000),
            ClickEventBatchSize = ConfigurationValues.Int32(section, nameof(ClickEventBatchSize), 5_000),
            ClickEventFlushMilliseconds = ConfigurationValues.Int32(section, nameof(ClickEventFlushMilliseconds), 250),
            ShutdownFlushSeconds = ConfigurationValues.Int32(section, nameof(ShutdownFlushSeconds), 10),
            MaxCandidateRows = ConfigurationValues.Int32(section, nameof(MaxCandidateRows), 500),
        };

        options.Validate();
        return options;
    }

    /// <summary>
    /// Checks every value against the range it has to be in for the module to work.
    /// </summary>
    /// <exception cref="InvalidOperationException">A value is outside its permitted range.</exception>
    public void Validate()
    {
        _ = ConfigurationValues.InRange(MaxPoolSize, 1, 1_000, $"{SectionName}:{nameof(MaxPoolSize)}");
        _ = ConfigurationValues.InRange(MinPoolSize, 0, MaxPoolSize, $"{SectionName}:{nameof(MinPoolSize)}");
        _ = ConfigurationValues.InRange(CommandTimeoutSeconds, 1, 600, $"{SectionName}:{nameof(CommandTimeoutSeconds)}");
        _ = ConfigurationValues.InRange(WriteCommandTimeoutSeconds, 1, 3_600, $"{SectionName}:{nameof(WriteCommandTimeoutSeconds)}");
        _ = ConfigurationValues.InRange(ClickEventChannelCapacity, 1, 10_000_000, $"{SectionName}:{nameof(ClickEventChannelCapacity)}");
        _ = ConfigurationValues.InRange(ClickEventBatchSize, 1, ClickEventChannelCapacity, $"{SectionName}:{nameof(ClickEventBatchSize)}");
        _ = ConfigurationValues.InRange(ClickEventFlushMilliseconds, 1, 60_000, $"{SectionName}:{nameof(ClickEventFlushMilliseconds)}");
        _ = ConfigurationValues.InRange(ShutdownFlushSeconds, 1, 300, $"{SectionName}:{nameof(ShutdownFlushSeconds)}");
        _ = ConfigurationValues.InRange(MaxCandidateRows, 1, 10_000, $"{SectionName}:{nameof(MaxCandidateRows)}");

        if (string.IsNullOrWhiteSpace(ApplicationName))
        {
            throw new InvalidOperationException($"Configuration value '{SectionName}:{nameof(ApplicationName)}' must not be blank.");
        }
    }
}
