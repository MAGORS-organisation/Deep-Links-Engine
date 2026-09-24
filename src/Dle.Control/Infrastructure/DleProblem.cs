using Microsoft.AspNetCore.Mvc;

namespace Dle.Control.Infrastructure;

/// <summary>
/// Every error the control plane returns, as an RFC 9457 problem document.
/// </summary>
/// <remarks>
/// <para>
/// The <c>type</c> member always comes from <see cref="ProblemCodes"/>. Those strings are part of
/// the public contract — integrators branch on them — which is why nothing here composes a type URI
/// on the fly.
/// </para>
/// <para>
/// Writing goes through <see cref="IProblemDetailsService"/> so that whatever the host has
/// configured (the trace identifier, an instance URI, a correlation header) is applied uniformly to
/// hand-written failures and to framework-produced ones alike. The direct write is only a fallback
/// for the case where no registered writer accepts the request, which happens when a browser asks
/// for HTML; answering with the document anyway is better than answering with nothing.
/// </para>
/// </remarks>
public static class DleProblem
{
    /// <summary>Media type of an RFC 9457 problem document.</summary>
    public const string ContentType = "application/problem+json";

    /// <summary>Extension member carrying the per-field validation errors.</summary>
    public const string ErrorsExtension = "errors";

    /// <summary>Builds a problem result with a caller-chosen status and type.</summary>
    /// <param name="statusCode">HTTP status code.</param>
    /// <param name="type">Problem type from <see cref="ProblemCodes"/>.</param>
    /// <param name="title">Short, stable, human-readable summary.</param>
    /// <param name="detail">Explanation of this occurrence.</param>
    /// <param name="extensions">Additional members, for example the validation error map.</param>
    /// <returns>A result that writes the document.</returns>
    public static IResult Create(
        int statusCode,
        string type,
        string title,
        string? detail = null,
        IReadOnlyDictionary<string, object?>? extensions = null)
    {
        ProblemDetails details = new()
        {
            Status = statusCode,
            Type = type,
            Title = title,
            Detail = detail,
        };

        if (extensions is not null)
        {
            foreach (KeyValuePair<string, object?> extension in extensions)
            {
                details.Extensions[extension.Key] = extension.Value;
            }
        }

        return new DleProblemResult(details);
    }

    /// <summary>The request body or query failed validation.</summary>
    /// <param name="errors">Field path to messages.</param>
    /// <param name="detail">Optional summary.</param>
    /// <returns>A 400 problem document carrying an <c>errors</c> member.</returns>
    public static IResult Validation(
        IReadOnlyDictionary<string, string[]> errors,
        string? detail = null)
    {
        ArgumentNullException.ThrowIfNull(errors);

        return Create(
            StatusCodes.Status400BadRequest,
            ProblemCodes.ValidationFailed,
            "The request failed validation.",
            detail,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [ErrorsExtension] = errors,
            });
    }

    /// <summary>The request body or query failed validation on a single field.</summary>
    /// <param name="field">Field path, for example <c>routing_rules[0].then.url</c>.</param>
    /// <param name="message">What is wrong with it.</param>
    /// <returns>A 400 problem document.</returns>
    public static IResult Validation(string field, string message) =>
        Validation(
            new Dictionary<string, string[]>(StringComparer.Ordinal) { [field] = [message] },
            message);

    /// <summary>
    /// The addressed object does not exist, is not the caller's, or has been withdrawn.
    /// </summary>
    /// <param name="detail">What was not found, in terms that reveal nothing about other tenants.</param>
    /// <returns>A 404 problem document.</returns>
    /// <remarks>
    /// There is deliberately no "belongs to another tenant" answer. A foreign identifier is filtered
    /// out by the tenant query filter before any handler sees it, so it arrives here as an ordinary
    /// miss — which is exactly the response TC-166 requires, reached by construction rather than by
    /// a special case somebody could forget (SHARED-KERNEL §17.7).
    /// </remarks>
    public static IResult NotFound(string detail) =>
        Create(
            StatusCodes.Status404NotFound,
            ProblemCodes.Base + "not-found",
            "The requested resource does not exist.",
            detail);

    /// <summary>The credential is missing, malformed or expired.</summary>
    /// <param name="detail">Why the credential was refused, without echoing it.</param>
    /// <returns>A 401 problem document.</returns>
    public static IResult Unauthorized(string detail) =>
        Create(
            StatusCodes.Status401Unauthorized,
            ProblemCodes.Unauthorized,
            "Authentication is required.",
            detail);

    /// <summary>The credential is valid but lacks the role or scope this operation needs.</summary>
    /// <param name="detail">Which capability is missing.</param>
    /// <returns>A 403 problem document.</returns>
    /// <remarks>
    /// This answer is only ever given about a capability, never about ownership. "You may not create
    /// links" is a 403; "this link is not yours" is a 404.
    /// </remarks>
    public static IResult Forbidden(string detail) =>
        Create(
            StatusCodes.Status403Forbidden,
            ProblemCodes.Forbidden,
            "The credential lacks the required scope.",
            detail);

    /// <summary>A conflicting state prevents the write.</summary>
    /// <param name="type">Problem type from <see cref="ProblemCodes"/>.</param>
    /// <param name="title">Short summary.</param>
    /// <param name="detail">Explanation.</param>
    /// <returns>A 409 problem document.</returns>
    public static IResult Conflict(string type, string title, string detail) =>
        Create(StatusCodes.Status409Conflict, type, title, detail);

    /// <summary>A dependency the request needs is not available.</summary>
    /// <param name="detail">Which dependency, and what the caller can do about it.</param>
    /// <returns>A 503 problem document.</returns>
    /// <remarks>
    /// Used where a missing dependency would otherwise weaken a security decision — a target URL that
    /// cannot be checked against the reputation feed is refused rather than accepted unchecked
    /// (SHARED-KERNEL §17.9).
    /// </remarks>
    public static IResult DependencyUnavailable(string detail) =>
        Create(
            StatusCodes.Status503ServiceUnavailable,
            ProblemCodes.DependencyUnavailable,
            "A required dependency is unavailable.",
            detail);

    /// <summary>A rate limit was exceeded (§E.9).</summary>
    /// <param name="retryAfterSeconds">Seconds to put into <c>Retry-After</c>.</param>
    /// <param name="detail">Which limit was hit.</param>
    /// <returns>A 429 problem document.</returns>
    public static IResult RateLimited(int retryAfterSeconds, string detail) =>
        new DleProblemResult(
            new ProblemDetails
            {
                Status = StatusCodes.Status429TooManyRequests,
                Type = ProblemCodes.RateLimited,
                Title = "Too many requests.",
                Detail = detail,
            },
            retryAfterSeconds);

    /// <summary>
    /// A problem document written through <see cref="IProblemDetailsService"/>.
    /// </summary>
    private sealed class DleProblemResult : IResult, IStatusCodeHttpResult
    {
        private readonly ProblemDetails _details;
        private readonly int? _retryAfterSeconds;

        internal DleProblemResult(ProblemDetails details, int? retryAfterSeconds = null)
        {
            _details = details;
            _retryAfterSeconds = retryAfterSeconds;
        }

        /// <inheritdoc />
        public int? StatusCode => _details.Status;

        /// <inheritdoc />
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);

            httpContext.Response.StatusCode = _details.Status ?? StatusCodes.Status500InternalServerError;

            if (_retryAfterSeconds is int retryAfter)
            {
                httpContext.Response.Headers.RetryAfter =
                    retryAfter.ToString(CultureInfo.InvariantCulture);
            }

            IProblemDetailsService? service =
                httpContext.RequestServices.GetService<IProblemDetailsService>();

            if (service is not null)
            {
                bool written = await service.TryWriteAsync(new ProblemDetailsContext
                {
                    HttpContext = httpContext,
                    ProblemDetails = _details,
                });

                if (written)
                {
                    return;
                }
            }

            // No registered writer accepted the request — a browser asking for HTML, typically. The
            // document is still the most useful thing to return, so it is written directly.
            httpContext.Response.ContentType = ContentType;
            await httpContext.Response.WriteAsJsonAsync(
                _details,
                options: null,
                contentType: ContentType,
                cancellationToken: httpContext.RequestAborted);
        }
    }
}
