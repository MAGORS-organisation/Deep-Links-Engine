using System.Security.Claims;
using System.Text.Encodings.Web;

using Dle.Control.Infrastructure;
using Dle.Crypto;

using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Dle.Control.Identity;

/// <summary>
/// Authenticates a control-plane API key: public prefix for the lookup, Argon2id for the
/// verification (FR-242, K5).
/// </summary>
/// <remarks>
/// <para>
/// The order matters. The prefix is not a secret and is indexed, so one lookup finds the candidate
/// row without hashing anything; only then is the presented value verified against the stored
/// Argon2id hash, and that comparison ends in
/// <see cref="System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(ReadOnlySpan{byte}, ReadOnlySpan{byte})"/>
/// inside <see cref="Argon2PasswordHasher"/> — never in an <c>==</c> (SHARED-KERNEL §17.6).
/// </para>
/// <para>
/// Every refusal is the same refusal. An unknown prefix, a wrong secret, an expired key, a suspended
/// tenant and an exhausted attempt budget all produce one message and one status, because telling
/// them apart would turn the endpoint into an oracle for which keys exist (T-07).
/// </para>
/// <para>
/// Nothing here logs the presented value, the <c>Authorization</c> header or the caller's address.
/// The non-secret prefix is the identifier that appears in logs and in the audit trail
/// (SHARED-KERNEL §17.5).
/// </para>
/// </remarks>
public sealed class DleApiKeyAuthenticationHandler
    : AuthenticationHandler<DleKeyAuthenticationOptions>
{
    private const string Refused = "The credential was refused.";

    private readonly DleCredentialStore _store;
    private readonly DleCredentialCache _cache;
    private readonly DleAuthAttemptLimiter _limiter;
    private readonly DleDecoyCredential _decoy;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the handler.</summary>
    /// <param name="options">Scheme options monitor.</param>
    /// <param name="logger">Logger factory.</param>
    /// <param name="encoder">URL encoder.</param>
    /// <param name="store">Cross-tenant credential lookup.</param>
    /// <param name="cache">Short-lived verification cache.</param>
    /// <param name="limiter">The §E.9 authentication attempt limiter.</param>
    /// <param name="decoy">Hash verified against when no candidate row exists.</param>
    /// <param name="timeProvider">Clock used for expiry and for the last-used stamp.</param>
    public DleApiKeyAuthenticationHandler(
        IOptionsMonitor<DleKeyAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        DleCredentialStore store,
        DleCredentialCache cache,
        DleAuthAttemptLimiter limiter,
        DleDecoyCredential decoy,
        TimeProvider timeProvider)
        : base(options, logger, encoder)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(limiter);
        ArgumentNullException.ThrowIfNull(decoy);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _store = store;
        _cache = cache;
        _limiter = limiter;
        _decoy = decoy;
        _timeProvider = timeProvider;
    }

    /// <inheritdoc />
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? presented = DleKeyCredentialReader.Read(Request, Options.HeaderName);

        if (presented is null)
        {
            // No credential of this kind at all: not a failure, just nothing to say. Another scheme
            // may still authenticate the request, and the fallback policy denies it if none does.
            return AuthenticateResult.NoResult();
        }

        if (!DleKeyCredentialReader.TryReadPrefix(presented, Options.KeyName, out string prefix))
        {
            return AuthenticateResult.Fail(Refused);
        }

        if (_cache.TryGet(Scheme.Name, presented, out ApiKeyCandidate? cached) && cached is not null)
        {
            return Success(cached, prefix);
        }

        if (!_limiter.TryAcquire(
                DleKeyCredentialReader.AddressPrefix(Context.Connection.RemoteIpAddress),
                prefix))
        {
            Logger.LogWarning(
                "Authentication attempts exhausted for key prefix {KeyPrefix}.", prefix);

            return AuthenticateResult.Fail(Refused);
        }

        ApiKeyCandidate? candidate = await _store.FindApiKeyAsync(prefix, Context.RequestAborted);

        // The verification runs even when the prefix is unknown, against a hash that cannot match,
        // so that a present key and an absent one take the same time to refuse (T-07).
        bool verified = ApiKeyHasher.Verify(presented, candidate?.Hash ?? _decoy.Hash);

        if (candidate is null || !verified)
        {
            return AuthenticateResult.Fail(Refused);
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();

        if (candidate.ExpiresAt is DateTimeOffset expiry && expiry <= now)
        {
            if (Logger.IsEnabled(LogLevel.Information))
            {
                Logger.LogInformation("Expired API key presented, prefix {KeyPrefix}.", prefix);
            }

            return AuthenticateResult.Fail(Refused);
        }

        if (!string.Equals(candidate.TenantStatus, TenantStatuses.Active, StringComparison.Ordinal))
        {
            if (Logger.IsEnabled(LogLevel.Information))
            {
                Logger.LogInformation(
                    "API key {KeyPrefix} belongs to a tenant that is not active.", prefix);
            }

            return AuthenticateResult.Fail(Refused);
        }

        if (!DleRoles.IsKnown(candidate.Role))
        {
            // A role this build does not understand grants nothing, so the credential is refused
            // rather than admitted with an empty set of capabilities (SHARED-KERNEL §17.9).
            Logger.LogWarning(
                "API key {KeyPrefix} carries the unknown role {Role}.", prefix, candidate.Role);

            return AuthenticateResult.Fail(Refused);
        }

        _cache.Set(Scheme.Name, presented, candidate);
        await _store.TouchApiKeyAsync(candidate.KeyId, now, Context.RequestAborted);

        return Success(candidate, prefix);
    }

    /// <inheritdoc />
    /// <remarks>
    /// The challenge names the scheme without echoing anything the caller sent, and carries no
    /// <c>realm</c>: a realm string is a free hint about what lives behind the endpoint.
    /// </remarks>
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = DleKeyAuthenticationOptions.BearerScheme;
        return Task.CompletedTask;
    }

    private AuthenticateResult Success(ApiKeyCandidate candidate, string prefix)
    {
        List<Claim> claims =
        [
            new(DleClaimTypes.TenantId, candidate.TenantId.ToString()),
            new(DleClaimTypes.TenantCreatedAt, candidate.TenantCreatedAt.ToString("O", CultureInfo.InvariantCulture)),
            new(DleClaimTypes.ActorId, candidate.KeyId.ToString()),
            new(DleClaimTypes.ActorType, DleCaller.ApiKeyActor),
            new(DleClaimTypes.Role, candidate.Role),
            new(DleClaimTypes.KeyPrefix, prefix),
        ];

        foreach (string scope in candidate.Scopes)
        {
            if (!string.IsNullOrWhiteSpace(scope))
            {
                claims.Add(new Claim(DleClaimTypes.Scope, scope));
            }
        }

        ClaimsPrincipal principal = new(new ClaimsIdentity(claims, Scheme.Name));

        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }
}
