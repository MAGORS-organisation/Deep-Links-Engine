using System.Security.Cryptography;
using System.Text;

using Dle.Crypto;
using Dle.Domain.Entities;
using Dle.Persistence;
using Dle.Persistence.Tenancy;

using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dle.Control.Features.Attribution;

/// <summary>
/// The tenant and application an authenticated SDK call belongs to.
/// </summary>
/// <param name="TenantId">Owning tenant. Every query the request makes is scoped to it.</param>
/// <param name="AppId">Application the key was issued for.</param>
/// <param name="SdkKeyId">The key row, recorded in evidence so a disputed attribution can be
/// traced back to the credential that produced it.</param>
public readonly record struct SdkCaller(Guid TenantId, Guid AppId, Guid SdkKeyId);

/// <summary>
/// Authenticates <c>Authorization: Bearer &lt;sdk_key&gt;</c> on the SDK endpoints (§B.7.2, §E.7 K6).
/// </summary>
/// <remarks>
/// <para>
/// An SDK key ships inside an application binary, so it is a public identifier rather than a
/// secret: anyone who downloads the app can extract it. Its value is that it binds a call to one
/// tenant and one application and that it can be revoked — not that it is hidden. That is why this
/// class refuses to be the only line of defence: §E.9 puts a per-address token bucket on
/// verification attempts, the endpoints put a per-installation limit on top, and an SDK key is
/// never granted control plane rights.
/// </para>
/// <para>
/// Verification is Argon2id and therefore deliberately expensive, which is a problem for an endpoint
/// allowed sixty calls a minute. A successful verification is cached for
/// <see cref="AttributionOptions.SdkKeyCacheSeconds"/>, keyed by a SHA-256 of the presented key so
/// the credential itself never sits in the cache. A revoked key keeps working for at most that long,
/// which is the trade this cache makes explicitly.
/// </para>
/// <para>
/// An unknown key prefix still pays for a full Argon2id verification against a dummy hash. Without
/// it the response time would separate "no such key" from "wrong key" and hand an attacker a free
/// enumeration oracle (T-17).
/// </para>
/// </remarks>
public sealed partial class SdkKeyAuthenticator
{
    private const string CachePrefix = "dle:sdk-key:";

    private readonly DleDbContext _db;
    private readonly Argon2PasswordHasher _hasher;
    private readonly IMemoryCache _cache;
    private readonly AttributionOptions _options;
    private readonly ILogger<SdkKeyAuthenticator> _logger;

    /// <summary>
    /// Creates the authenticator.
    /// </summary>
    /// <param name="db">The control plane context.</param>
    /// <param name="hasher">Argon2id hasher, used both to verify and to build the dummy hash.</param>
    /// <param name="cache">Cache for verified keys.</param>
    /// <param name="options">Attribution options.</param>
    /// <param name="logger">Logger.</param>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public SdkKeyAuthenticator(
        DleDbContext db,
        Argon2PasswordHasher hasher,
        IMemoryCache cache,
        IOptions<AttributionOptions> options,
        ILogger<SdkKeyAuthenticator> logger)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(hasher);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        _db = db;
        _hasher = hasher;
        _cache = cache;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Reads the bearer token from a request.
    /// </summary>
    /// <param name="request">The request.</param>
    /// <returns>The presented key, or <see langword="null"/> when the header is absent or is not a
    /// bearer credential. The value is never logged (SHARED-KERNEL §17.5).</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is <see langword="null"/>.</exception>
    public static string? ReadBearer(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        string? header = request.Headers.Authorization.ToString();

        if (string.IsNullOrWhiteSpace(header))
        {
            return null;
        }

        const string scheme = "Bearer ";

        if (!header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string token = header[scheme.Length..].Trim();

        return token.Length == 0 ? null : token;
    }

    /// <summary>
    /// Authenticates a presented SDK key.
    /// </summary>
    /// <param name="presentedKey">The key as received. Untrusted.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The caller, or <see langword="null"/> when the key is missing, malformed, unknown,
    /// inactive or wrong. The four cases are deliberately indistinguishable to the caller.</returns>
    public async Task<SdkCaller?> AuthenticateAsync(string? presentedKey, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(presentedKey))
        {
            return null;
        }

        string cacheKey = CachePrefix + Fingerprint(presentedKey);

        if (_options.SdkKeyCacheSeconds > 0 && _cache.TryGetValue(cacheKey, out SdkCaller cached))
        {
            return cached;
        }

        if (!ApiKeyHasher.TryReadPrefix(presentedKey, out string prefix))
        {
            return null;
        }

        SdkKey? key = await FindByPrefixAsync(prefix, cancellationToken);

        // A missing row still pays for a verification, so an unknown prefix and a wrong secret cost
        // the same wall-clock time (T-17). The dummy hash is built once per process.
        byte[] storedHash = key?.Hash ?? DummyHash();

        if (!ApiKeyHasher.Verify(presentedKey, storedHash) || key is null || !key.IsActive)
        {
            return null;
        }

        var caller = new SdkCaller(key.TenantId, key.AppId, key.Id);

        if (_options.SdkKeyCacheSeconds > 0)
        {
            _ = _cache.Set(
                cacheKey,
                caller,
                new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(_options.SdkKeyCacheSeconds),
                    Size = 1,
                });
        }

        LogAuthenticated(_logger, key.TenantId, key.AppId);

        return caller;
    }

    /// <summary>
    /// Finds the key row by its public prefix.
    /// </summary>
    /// <param name="prefix">The prefix presented by the caller.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The row, or <see langword="null"/>.</returns>
    /// <remarks>
    /// The lookup crosses tenants because the key is what establishes the tenant in the first
    /// place; this is the same, deliberately greppable exception the control plane's API key
    /// lookup makes.
    /// </remarks>
    private async Task<SdkKey?> FindByPrefixAsync(string prefix, CancellationToken cancellationToken)
    {
        using CrossTenantScope scope = _db.BeginCrossTenantScope(
            "an SDK key is what establishes the tenant, so it cannot be looked up within one");

        return await _db.SdkKeys
            .AsNoTracking()
            .AcrossTenants()
            .FirstOrDefaultAsync(k => k.KeyPrefix == prefix, cancellationToken);
    }

    /// <summary>Non-reversible fingerprint of a presented key, used as the cache key.</summary>
    private static string Fingerprint(string presentedKey)
    {
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes(presentedKey), digest);

        return Convert.ToHexStringLower(digest);
    }

    /// <summary>
    /// An encoded Argon2id hash of a value nobody holds, so that verifying against it always fails
    /// and always costs what a real verification costs.
    /// </summary>
    private byte[] DummyHash() =>
        _dummyHash ??= Encoding.UTF8.GetBytes(_hasher.Hash(Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32))));

    private byte[]? _dummyHash;

    [LoggerMessage(
        EventId = 5101,
        Level = LogLevel.Debug,
        Message = "SDK call authenticated for tenant {TenantId}, application {AppId}.")]
    private static partial void LogAuthenticated(ILogger logger, Guid tenantId, Guid appId);
}
