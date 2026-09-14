using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Configuration;

namespace Dle.Persistence.Fast.Configuration;

/// <summary>
/// Expiration policy for the two-level link cache (ADR-005), read from <c>Dle:Edge:Cache</c>.
/// </summary>
/// <remarks>
/// <para>
/// The two levels answer different questions. L1 is per process and its expiration is how long an
/// instance may serve a control-plane change it has not been told about; it is therefore short. L2 is
/// shared through Valkey, survives a restart and absorbs the cold start of a scaled-out replica, so it
/// is measured in minutes.
/// </para>
/// <para>
/// The negative entry has its own, shorter expiration. Caching misses is not optional — an enumeration
/// scan is mostly misses, and re-querying Postgres for each one turns the scan into a denial of service
/// — but a miss must not outlive the creation of the link a user is about to publish.
/// </para>
/// </remarks>
public sealed class EdgeCacheOptions
{
    /// <summary>Configuration section these options are read from (SHARED-KERNEL §16).</summary>
    public const string SectionName = "Dle:Edge:Cache";

    /// <summary>Lifetime of an entry in the in-process L1 cache, in seconds.</summary>
    public int L1Seconds { get; init; } = 30;

    /// <summary>Lifetime of an entry in the shared Valkey L2 cache, in minutes.</summary>
    public int L2Minutes { get; init; } = 10;

    /// <summary>Lifetime of a cached miss, in seconds.</summary>
    public int NegativeSeconds { get; init; } = 15;

    // Both option objects are built once and reused. The edge passes them to GetOrCreateAsync on every
    // request, so returning a fresh instance from the getter would allocate on the hot path for a value
    // that never changes after start-up.
    private HybridCacheEntryOptions? _defaultEntryOptions;
    private HybridCacheEntryOptions? _negativeEntryOptions;

    /// <summary>Entry options for a link that exists.</summary>
    public HybridCacheEntryOptions DefaultEntryOptions => _defaultEntryOptions ??= new HybridCacheEntryOptions
    {
        LocalCacheExpiration = TimeSpan.FromSeconds(L1Seconds),
        Expiration = TimeSpan.FromMinutes(L2Minutes),
    };

    /// <summary>
    /// Entry options for a miss. The shared level is given the same short window as the local one, so a
    /// newly created link becomes visible everywhere within <see cref="NegativeSeconds"/>.
    /// </summary>
    public HybridCacheEntryOptions NegativeEntryOptions => _negativeEntryOptions ??= new HybridCacheEntryOptions
    {
        LocalCacheExpiration = TimeSpan.FromSeconds(NegativeSeconds),
        Expiration = TimeSpan.FromSeconds(NegativeSeconds),
    };

    /// <summary>
    /// Reads the options from configuration and validates them.
    /// </summary>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>The validated options.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="configuration"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A value is malformed or outside its permitted range.</exception>
    public static EdgeCacheOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        IConfiguration section = configuration.GetSection(SectionName);

        var options = new EdgeCacheOptions
        {
            L1Seconds = ConfigurationValues.Int32(section, nameof(L1Seconds), 30),
            L2Minutes = ConfigurationValues.Int32(section, nameof(L2Minutes), 10),
            NegativeSeconds = ConfigurationValues.Int32(section, nameof(NegativeSeconds), 15),
        };

        options.Validate();
        return options;
    }

    /// <summary>
    /// Checks every value against the range it has to be in for the cache to behave.
    /// </summary>
    /// <exception cref="InvalidOperationException">A value is outside its permitted range.</exception>
    public void Validate()
    {
        _ = ConfigurationValues.InRange(L1Seconds, 1, 86_400, $"{SectionName}:{nameof(L1Seconds)}");
        _ = ConfigurationValues.InRange(L2Minutes, 1, 1_440, $"{SectionName}:{nameof(L2Minutes)}");
        _ = ConfigurationValues.InRange(NegativeSeconds, 1, 3_600, $"{SectionName}:{nameof(NegativeSeconds)}");
    }
}
