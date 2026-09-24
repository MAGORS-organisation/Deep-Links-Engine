using System.Globalization;
using System.Net;

using Dle.Domain.Contracts;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Dle.Control.Features.Attribution;

/// <summary>
/// The RFC 9457 problem documents this module returns.
/// </summary>
/// <remarks>
/// <para>
/// Every failure of an SDK endpoint is a problem document carrying one of the stable
/// <see cref="ProblemCodes"/> URIs, because an SDK has to branch on the failure rather than parse a
/// sentence: a 429 means back off, an expired claim code means offer the user a new one, a
/// validation failure means the integration is wrong and retrying will not help.
/// </para>
/// <para>
/// The documents are written through <see cref="IProblemDetailsService"/> so that whatever the host
/// configured — a trace identifier, an instance URI, a customisation callback — applies to these
/// responses too.
/// </para>
/// </remarks>
public static class AttributionProblem
{
    /// <summary>Extension member naming the machine readable reason of a claim code failure.</summary>
    public const string ReasonExtension = "reason";

    /// <summary>Extension member telling the client whether asking for a new claim code helps (TC-148).</summary>
    public const string CanReissueExtension = "can_reissue";

    /// <summary>The request body failed validation.</summary>
    /// <param name="detail">What was wrong, in terms the integrator can act on.</param>
    /// <returns>A 400 problem document.</returns>
    public static IResult Validation(string detail) => new ProblemResult(
        HttpStatusCode.BadRequest,
        ProblemCodes.ValidationFailed,
        "Validation failed",
        detail);

    /// <summary>The SDK key is missing, malformed, unknown or inactive.</summary>
    /// <returns>A 401 problem document with the <c>WWW-Authenticate</c> challenge.</returns>
    /// <remarks>
    /// The detail is identical for every cause on purpose. Telling an attacker that a key exists but
    /// is inactive is a free oracle, and §E.9 already pairs this endpoint with a per-address limit on
    /// verification attempts.
    /// </remarks>
    public static IResult Unauthenticated() => new ProblemResult(
        HttpStatusCode.Unauthorized,
        ProblemCodes.Unauthorized,
        "Unauthorized",
        "A valid SDK key must be presented as 'Authorization: Bearer <key>'.")
    {
        Challenge = "Bearer realm=\"dle\"",
    };

    /// <summary>A rate limit was exceeded (§E.9).</summary>
    /// <param name="retryAfter">How long the caller should wait, when the limiter could say.</param>
    /// <param name="detail">Which limit was hit.</param>
    /// <returns>A 429 problem document carrying <c>Retry-After</c>.</returns>
    public static IResult RateLimited(TimeSpan? retryAfter, string detail) => new ProblemResult(
        HttpStatusCode.TooManyRequests,
        ProblemCodes.RateLimited,
        "Too many requests",
        detail)
    {
        RetryAfter = retryAfter,
    };

    /// <summary>The claim code is unknown, already consumed or past its time to live (TC-148).</summary>
    /// <param name="reason">Machine readable reason: <c>expired</c>, <c>consumed</c>,
    /// <c>unknown</c> or <c>disabled</c>.</param>
    /// <param name="detail">Sentence for a human reading the log.</param>
    /// <param name="canReissue">Whether obtaining a fresh code is a sensible next step.</param>
    /// <returns>A 400 problem document.</returns>
    public static IResult ClaimCodeInvalid(string reason, string detail, bool canReissue)
    {
        var problem = new ProblemResult(
            HttpStatusCode.BadRequest,
            ProblemCodes.ClaimCodeInvalid,
            "Claim code cannot be redeemed",
            detail);

        problem.Extensions[ReasonExtension] = reason;
        problem.Extensions[CanReissueExtension] = canReissue;

        return problem;
    }

    /// <summary>The request body is larger than the endpoint accepts.</summary>
    /// <param name="maximumBytes">The limit, in bytes.</param>
    /// <returns>A 413 problem document.</returns>
    public static IResult PayloadTooLarge(int maximumBytes) => new ProblemResult(
        HttpStatusCode.RequestEntityTooLarge,
        ProblemCodes.ValidationFailed,
        "Payload too large",
        string.Create(
            CultureInfo.InvariantCulture,
            $"The request body must not exceed {maximumBytes} bytes."));

    /// <summary>A dependency the request needs is unavailable.</summary>
    /// <param name="detail">What was unavailable, without naming internals.</param>
    /// <returns>A 503 problem document.</returns>
    public static IResult DependencyUnavailable(string detail) => new ProblemResult(
        HttpStatusCode.ServiceUnavailable,
        ProblemCodes.DependencyUnavailable,
        "Dependency unavailable",
        detail);

    /// <summary>
    /// An <see cref="IResult"/> that renders itself through <see cref="IProblemDetailsService"/>.
    /// </summary>
    private sealed class ProblemResult : IResult
    {
        private readonly HttpStatusCode _status;
        private readonly string _type;
        private readonly string _title;
        private readonly string _detail;

        internal ProblemResult(HttpStatusCode status, string type, string title, string detail)
        {
            _status = status;
            _type = type;
            _title = title;
            _detail = detail;
        }

        /// <summary>Members added to the document beyond the RFC 9457 core.</summary>
        internal Dictionary<string, object?> Extensions { get; } = new(StringComparer.Ordinal);

        /// <summary>Value of the <c>WWW-Authenticate</c> header, when the failure is a challenge.</summary>
        internal string? Challenge { get; init; }

        /// <summary>Value of the <c>Retry-After</c> header, when the failure is a rate limit.</summary>
        internal TimeSpan? RetryAfter { get; init; }

        public async Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);

            httpContext.Response.StatusCode = (int)_status;

            if (Challenge is { Length: > 0 } challenge)
            {
                httpContext.Response.Headers.WWWAuthenticate = challenge;
            }

            if (RetryAfter is { } retryAfter)
            {
                int seconds = (int)Math.Ceiling(Math.Max(retryAfter.TotalSeconds, 1));
                httpContext.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
            }

            var problem = new ProblemDetails
            {
                Status = (int)_status,
                Type = _type,
                Title = _title,
                Detail = _detail,
                Instance = httpContext.Request.Path.Value,
            };

            foreach (KeyValuePair<string, object?> extension in Extensions)
            {
                problem.Extensions[extension.Key] = extension.Value;
            }

            IProblemDetailsService service = httpContext.RequestServices
                .GetRequiredService<IProblemDetailsService>();

            await service.WriteAsync(new ProblemDetailsContext
            {
                HttpContext = httpContext,
                ProblemDetails = problem,
            });
        }
    }
}
