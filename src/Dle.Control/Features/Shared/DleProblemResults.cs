using Dle.Domain.Contracts;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Dle.Control.Features.Shared;

/// <summary>
/// Builds the RFC 9457 problem documents the control plane answers with.
/// </summary>
/// <remarks>
/// <para>
/// Every failure leaves through here so that the <c>type</c> member is always one of the stable
/// identifiers in <see cref="ProblemCodes"/> and never an ad hoc string. Integrators branch on
/// that member, which makes it API surface.
/// </para>
/// <para>
/// The document is written through <see cref="IProblemDetailsService"/> when the host registered
/// one, so a deployment that customises problem documents once — adding a trace identifier, a
/// support contact — customises them everywhere, including here. When no service is registered the
/// result falls back to writing the same document directly, because an error path that depends on
/// optional configuration is an error path that fails when it is needed most.
/// </para>
/// </remarks>
internal static class DleProblemResults
{
    /// <summary>Answers 400 with a validation problem.</summary>
    /// <param name="detail">What was wrong, in terms the caller can act on.</param>
    /// <param name="errors">Per field messages, published as the <c>errors</c> extension.</param>
    /// <returns>The result.</returns>
    internal static IResult ValidationFailed(
        string detail,
        IReadOnlyDictionary<string, string[]>? errors = null)
    {
        Dictionary<string, object?>? extensions = errors is null || errors.Count == 0
            ? null
            : new Dictionary<string, object?>(StringComparer.Ordinal) { ["errors"] = errors };

        return Create(
            StatusCodes.Status400BadRequest,
            ProblemCodes.ValidationFailed,
            "The request failed validation.",
            detail,
            extensions);
    }

    /// <summary>Answers 401 when no usable credential established a tenant.</summary>
    /// <param name="detail">What the caller has to supply.</param>
    /// <returns>The result.</returns>
    internal static IResult Unauthorized(string detail) => Create(
        StatusCodes.Status401Unauthorized,
        ProblemCodes.Unauthorized,
        "The request carries no usable credential.",
        detail);

    /// <summary>
    /// Answers 404. Also the answer for a resource that belongs to another tenant: a 403 there
    /// would confirm that the identifier exists, which is exactly what SHARED-KERNEL §17.7 and
    /// TC-166 forbid.
    /// </summary>
    /// <param name="detail">What was not found, without echoing anything the caller did not send.</param>
    /// <returns>The result.</returns>
    internal static IResult NotFound(string detail) => Create(
        StatusCodes.Status404NotFound,
        ProblemCodes.Base + "not-found",
        "The resource does not exist.",
        detail);

    /// <summary>Answers 409 when a request conflicts with the current state of the resource.</summary>
    /// <param name="type">One of the identifiers on <see cref="ProblemCodes"/>.</param>
    /// <param name="detail">What conflicts.</param>
    /// <returns>The result.</returns>
    internal static IResult Conflict(string type, string detail) => Create(
        StatusCodes.Status409Conflict,
        type,
        "The request conflicts with the current state.",
        detail);

    /// <summary>Answers 503 when a dependency the request needs is unavailable (§D.6).</summary>
    /// <param name="detail">Which dependency failed, without leaking connection details.</param>
    /// <returns>The result.</returns>
    internal static IResult DependencyUnavailable(string detail) => Create(
        StatusCodes.Status503ServiceUnavailable,
        ProblemCodes.DependencyUnavailable,
        "A dependency of this operation is unavailable.",
        detail);

    /// <summary>Builds a problem document with an explicit status and type.</summary>
    /// <param name="status">HTTP status code.</param>
    /// <param name="type">Problem type identifier, from <see cref="ProblemCodes"/>.</param>
    /// <param name="title">Short, stable, human readable summary.</param>
    /// <param name="detail">Explanation of this occurrence.</param>
    /// <param name="extensions">Extra members, or <see langword="null"/>.</param>
    /// <returns>The result.</returns>
    internal static IResult Create(
        int status,
        string type,
        string title,
        string? detail = null,
        IDictionary<string, object?>? extensions = null)
    {
        ProblemDetails details = new()
        {
            Status = status,
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

        return new ProblemDocumentResult(details);
    }

    /// <summary>An <see cref="IResult"/> that prefers the host's problem details writer.</summary>
    private sealed class ProblemDocumentResult : IResult
    {
        private readonly ProblemDetails _details;

        internal ProblemDocumentResult(ProblemDetails details) => _details = details;

        public async Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);

            httpContext.Response.StatusCode = _details.Status ?? StatusCodes.Status500InternalServerError;

            IProblemDetailsService? service =
                httpContext.RequestServices.GetService<IProblemDetailsService>();

            if (service is not null)
            {
                ProblemDetailsContext context = new()
                {
                    HttpContext = httpContext,
                    ProblemDetails = _details,
                };

                if (await service.TryWriteAsync(context))
                {
                    return;
                }
            }

            await Results.Problem(_details).ExecuteAsync(httpContext);
        }
    }
}
