using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dle.Crypto;

/// <summary>
/// The default <see cref="ISigningKeyStore"/>: keys come from <c>Dle:Crypto:Keys</c>, and keys
/// added at runtime live in memory for the lifetime of the process.
/// </summary>
/// <remarks>
/// This is the right store for a single instance deployment and for tests. It is explicitly the
/// wrong one for a rotating multi instance deployment, and it says so out loud rather than failing
/// quietly six weeks later: a key generated through <see cref="SaveAsync"/> is logged at warning
/// level because it disappears on restart and is invisible to the other instances, which would
/// leave them unable to verify what this one signed. A deployment that rotates keys registers a
/// database backed store instead.
/// </remarks>
public sealed class ConfigurationSigningKeyStore : ISigningKeyStore
{
    private readonly ConcurrentDictionary<string, SigningKeyMaterial> _keys = new(StringComparer.Ordinal);
    private readonly ILogger<ConfigurationSigningKeyStore> _logger;

    /// <summary>
    /// Creates the store and loads every configured key.
    /// </summary>
    /// <param name="options">The crypto options.</param>
    /// <param name="logger">Logger.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">A configured key cannot be parsed.</exception>
    public ConfigurationSigningKeyStore(IOptions<CryptoOptions> options, ILogger<ConfigurationSigningKeyStore> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;

        foreach (SigningKeyOptions configured in options.Value.Keys)
        {
            SigningKeyMaterial material = SigningKeyFactory.FromOptions(configured);
            _keys[material.Kid] = material;
        }
    }

    /// <inheritdoc />
    public ValueTask<IReadOnlyList<SigningKeyMaterial>> LoadAsync(string purpose, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrEmpty(purpose);

        List<SigningKeyMaterial> result = [];

        foreach (SigningKeyMaterial material in _keys.Values)
        {
            if (string.Equals(material.Purpose, purpose, StringComparison.Ordinal))
            {
                result.Add(material);
            }
        }

        return ValueTask.FromResult<IReadOnlyList<SigningKeyMaterial>>(result);
    }

    /// <inheritdoc />
    public ValueTask SaveAsync(SigningKeyMaterial material, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(material);

        _keys[material.Kid] = material;

        _logger.LogWarning(
            "Signing key {Kid} for purpose {Purpose} was stored in memory only. It will be lost on restart and is " +
            "not visible to other instances. Register a durable ISigningKeyStore before rotating keys in production.",
            material.Kid,
            material.Purpose);

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask RetireAsync(string kid, DateTimeOffset notAfter, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrEmpty(kid);

        if (_keys.TryGetValue(kid, out SigningKeyMaterial? existing))
        {
            _keys[kid] = existing.Retire(notAfter);
        }

        return ValueTask.CompletedTask;
    }
}
