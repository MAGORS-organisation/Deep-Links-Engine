using System.Security.Claims;
using System.Text.Encodings.Web;

using Dle.Control.Infrastructure;
using Dle.Crypto;

using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Dle.Control.Identity;

/// <summary>
/// Authenticates a key that ships inside a customer application (§E.2.1, TB2).
/// </summary>
/// <remarks>
/// <para>
/// This is a separate scheme from the control-plane one, and that is the whole design. An SDK key is
/// distributed inside an APK and an IPA to every device that installs the application; it can be
/// pulled out of the binary with a hex editor, and the threat model assumes it has been. What
/// follows is that it must be incapable of doing damage, not merely unlikely to.
/// </para>
/// <para>
/// The principal this handler issues carries exactly one scope, <see cref="DleScopes.SdkIngest"/>,
/// and no role at all. <see cref="DlePolicies.SdkIngest"/> is the only policy that accepts this
/// scheme, and every configuration policy lists only the control-plane and OIDC schemes — so an
/// extracted key reaches <c>/v1/resolve</c> and <c>/v1/events</c> and is refused everywhere else by
/// the authorization stack, before a single claim is read. There is no code path in which a role
/// mix-up or a scope typo turns it into a credential that can write configuration.
/// </para>
/// <para>
/// The verification itself is the same as for a control-plane key: prefix lookup, Argon2id,
/// constant-time comparison, one shared attempt budget (SHARED-KERNEL §17.6, §E.9).
/// </para>
/// </remarks>
public sealed class DleSdkKeyAuthenticationHandler
    : AuthenticationHandler<DleKeyAuthenticationOptions>
{
    private const string Refused = "The credential was refused.";

    private readonly DleCredentialStore _store;
    private readonly DleCredentialCache _cache;
    private readonly DleAuthAttemptLimiter _limiter;
    private readonly DleDecoyCredential _decoy;

    /// <summary>Creates the handler.</summary>
    /// <param name="options">Scheme options monitor.</param>
    /// <param name="logger">Logger factory.</param>
    /// <param name="encoder">URL encoder.</param>
    /// <param name="store">Cross-tenant credential lookup.</param>
    /// <param name="cache">Short-lived verification cache.</param>
    /// <param name="limiter">The §E.9 authentication attempt limiter.</param>
    /// <param name="decoy">Hash verified against when no candidate row exists.</param>
    public DleSdkKeyAuthenticationHandler(
        IOptionsMonitor<DleKeyAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        DleCredentialStore store,
        DleCredentialCache cache,
        DleAuthAttemptLimiter limiter,
        DleDecoyCredential decoy)
        : base(options, logger, encoder)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(limiter);
        ArgumentNullException.ThrowIfNull(decoy);

        _store = store;
        _cache = cache;
        _limiter = limiter;
        _decoy = decoy;
    }

    /// <inheritdoc />
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        string? presented = DleKeyCredentialReader.Read(Request, Options.HeaderName);

        if (presented is null)
        {
            return AuthenticateResult.NoResult();
        }

        if (!DleKeyCredentialReader.TryReadPrefix(presented, Options.KeyName, out string prefix))
        {
            return AuthenticateResult.Fail(Refused);
        }

        if (_cache.TryGet(Scheme.Name, presented, out SdkKeyCandidate? cached) && cached is not null)
        {
            return Success(cached, prefix);
        }

        if (!_limiter.TryAcquire(
                DleKeyCredentialReader.AddressPrefix(Context.Connection.RemoteIpAddress),
                prefix))
        {
            return AuthenticateResult.Fail(Refused);
        }

        SdkKeyCandidate? candidate = await _store.FindSdkKeyAsync(prefix, Context.RequestAborted);

        bool verified = ApiKeyHasher.Verify(presented, candidate?.Hash ?? _decoy.Hash);

        if (candidate is null || !verified)
        {
            return AuthenticateResult.Fail(Refused);
        }

        if (!string.Equals(candidate.TenantStatus, TenantStatuses.Active, StringComparison.Ordinal))
        {
            return AuthenticateResult.Fail(Refused);
        }

        _cache.Set(Scheme.Name, presented, candidate);

        return Success(candidate, prefix);
    }

    /// <inheritdoc />
    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = DleKeyAuthenticationOptions.BearerScheme;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Builds the deliberately impoverished principal an SDK key gets.
    /// </summary>
    /// <param name="candidate">The verified key row.</param>
    /// <param name="prefix">The non-secret prefix, for logging.</param>
    /// <returns>A ticket carrying a tenant, an application, and one scope.</returns>
    /// <remarks>
    /// There is no role claim. <see cref="DleRoles.Rank"/> ranks an absent role below every
    /// requirement, so even if a configuration policy were misconfigured to accept this scheme, the
    /// role floor would still refuse it. Two independent reasons, because this is the credential that
    /// is assumed to be in the wrong hands.
    /// </remarks>
    private AuthenticateResult Success(SdkKeyCandidate candidate, string prefix)
    {
        Claim[] claims =
        [
            new(DleClaimTypes.TenantId, candidate.TenantId.ToString()),
            new(DleClaimTypes.TenantCreatedAt, candidate.TenantCreatedAt.ToString("O", CultureInfo.InvariantCulture)),
            new(DleClaimTypes.ActorId, candidate.KeyId.ToString()),
            new(DleClaimTypes.ActorType, DleCaller.SdkKeyActor),
            new(DleClaimTypes.AppId, candidate.AppId.ToString()),
            new(DleClaimTypes.KeyPrefix, prefix),
            new(DleClaimTypes.Scope, DleScopes.SdkIngest),
        ];

        ClaimsPrincipal principal = new(new ClaimsIdentity(claims, Scheme.Name));

        return AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name));
    }
}
