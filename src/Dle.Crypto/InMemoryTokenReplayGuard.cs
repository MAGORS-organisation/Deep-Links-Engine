using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;

namespace Dle.Crypto;

/// <summary>
/// Process local replay guard: a bounded map of token identifier to expiry.
/// </summary>
/// <remarks>
/// <para>
/// Correct for a single instance and for tests, and wrong for a scaled deployment, where a token
/// consumed on one instance would still be accepted by the others. Register a Valkey backed
/// <see cref="ITokenReplayGuard"/> there.
/// </para>
/// <para>
/// The map is bounded, so it cannot be turned into a memory exhaustion attack by minting
/// identifiers. When the bound is reached, expired entries are dropped first; if that frees
/// nothing, the guard refuses rather than forgetting something it promised to remember, which is
/// the default deny that SHARED-KERNEL §17.9 asks for. The refusal is logged at warning level,
/// because reaching it means either an attack or an undersized bound, and both need looking at.
/// </para>
/// </remarks>
public sealed class InMemoryTokenReplayGuard : ITokenReplayGuard
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _consumed = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<InMemoryTokenReplayGuard> _logger;
    private readonly int _capacity;

    /// <summary>
    /// Creates a guard.
    /// </summary>
    /// <param name="capacity">Maximum number of identifiers held at once.</param>
    /// <param name="timeProvider">Clock used to expire entries.</param>
    /// <param name="logger">Logger.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="capacity"/> is not positive.</exception>
    public InMemoryTokenReplayGuard(int capacity, TimeProvider timeProvider, ILogger<InMemoryTokenReplayGuard> logger)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _capacity = capacity;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    /// <summary>Number of identifiers currently remembered.</summary>
    public int Count => _consumed.Count;

    /// <inheritdoc />
    public ValueTask<bool> TryConsumeAsync(string jti, DateTimeOffset expiresAt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrEmpty(jti);

        DateTimeOffset now = _timeProvider.GetUtcNow();

        if (expiresAt <= now)
        {
            // Nothing to remember: the token is already refused on its own expiry.
            return ValueTask.FromResult(false);
        }

        if (_consumed.Count >= _capacity)
        {
            Prune(now);

            if (_consumed.Count >= _capacity)
            {
                _logger.LogWarning(
                    "The in-memory token replay guard is full at {Capacity} entries and refused a token. Either the " +
                    "capacity is undersized or identifiers are being minted deliberately.",
                    _capacity);

                return ValueTask.FromResult(false);
            }
        }

        return ValueTask.FromResult(_consumed.TryAdd(jti, expiresAt));
    }

    /// <summary>Drops every entry whose token has expired.</summary>
    private void Prune(DateTimeOffset now)
    {
        foreach (KeyValuePair<string, DateTimeOffset> entry in _consumed)
        {
            if (entry.Value <= now)
            {
                _consumed.TryRemove(entry);
            }
        }
    }
}
