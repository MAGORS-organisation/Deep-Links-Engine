using Dle.Control.Infrastructure;

namespace Dle.Control.Identity;

/// <summary>
/// Refuses a request that presents more than one credential.
/// </summary>
/// <remarks>
/// <para>
/// A credential can arrive in three places: <c>Authorization</c> (a control-plane key, an SDK key or
/// an identity-provider token), <c>X-Api-Key</c> and <c>X-Dle-Sdk-Key</c>. Each authentication scheme
/// reads only its own, and an endpoint's policy may authenticate a different scheme than the default
/// one. A request carrying an SDK key of tenant A next to a control-plane key of tenant B would then
/// be one principal to the code that opens the tenant scope and another to the code that authorises
/// it. The SDK key ships inside every copy of the customer's application, so it must be assumed to be
/// public; such a request is a cross-tenant write with nothing but an extracted APK.
/// </para>
/// <para>
/// No legitimate client sends two credentials: the SDKs send one SDK key, the console sends one key or
/// one token. So the rule is the simplest one that closes the class of bug rather than this instance:
/// at most one credential per request, checked before any scheme runs. The tenant scope is
/// additionally derived from the principal authorization accepted (see
/// <see cref="DleTenantScopeMiddleware"/>), and a principal naming two tenants has no tenant at all
/// (see <see cref="DleCallerExtensions.GetDleCaller"/>) — three independent barriers.
/// </para>
/// </remarks>
public sealed class DleSingleCredentialMiddleware
{
    private readonly RequestDelegate _next;

    /// <summary>Creates the middleware.</summary>
    /// <param name="next">The next component in the pipeline.</param>
    public DleSingleCredentialMiddleware(RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);
        _next = next;
    }

    /// <summary>Lets a request with at most one credential through and refuses any other.</summary>
    /// <param name="context">The request.</param>
    /// <returns>A task that completes when the request has been handled.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (CountCredentials(context.Request) > 1)
        {
            await DleProblem
                .Validation(
                    "credentials",
                    "Send exactly one credential: an Authorization header, an X-Api-Key header or an "
                    + "X-Dle-Sdk-Key header, never more than one.")
                .ExecuteAsync(context);

            return;
        }

        await _next(context);
    }

    /// <summary>Counts the credential-bearing headers that carry a non-empty value.</summary>
    /// <param name="request">The request.</param>
    /// <returns>How many credentials the request presents.</returns>
    internal static int CountCredentials(HttpRequest request)
    {
        int count = 0;

        if (HasValue(request, "Authorization"))
        {
            count++;
        }

        if (HasValue(request, DleKeyAuthenticationOptions.ApiKeyHeader))
        {
            count++;
        }

        if (HasValue(request, DleKeyAuthenticationOptions.SdkKeyHeader))
        {
            count++;
        }

        return count;
    }

    private static bool HasValue(HttpRequest request, string header) =>
        request.Headers.TryGetValue(header, out Microsoft.Extensions.Primitives.StringValues values)
        && !string.IsNullOrWhiteSpace(values.ToString());
}
