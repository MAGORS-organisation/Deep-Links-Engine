using System.Security.Cryptography;
using System.Text;

using Dle.Control.Infrastructure;

using Microsoft.AspNetCore.Routing.Patterns;

namespace Dle.Control.Features.Shared;

/// <summary>
/// Makes a write replayable through the <c>Idempotency-Key</c> header (§B.7.3).
/// </summary>
/// <remarks>
/// <para>
/// A client that times out on <c>POST /api/v1/links</c> has no way to know whether the link was
/// created. Retrying without a key creates a second one; not retrying may lose the first. The
/// header resolves that: the first attempt reserves the key, and a retry carrying the same key and
/// the same body is answered with the stored response instead of running again.
/// </para>
/// <para>
/// The body hash is what makes the mechanism trustworthy rather than merely convenient. The same
/// key with a different body is a client bug — two different requests sharing one key — and
/// replaying the first response would hide it, so it is answered with
/// <see cref="ProblemCodes.IdempotencyConflict"/>. The hash covers the method and the route pattern
/// as well as the bytes, so one key reused across two endpoints conflicts rather than replaying the
/// wrong resource.
/// </para>
/// <para>
/// The header is honoured, not required. A caller that does not send one gets ordinary
/// at-most-once-per-request semantics, which is what every interactive client already expects; the
/// key is the tool a machine client reaches for when it retries.
/// </para>
/// <para>
/// Only responses below 500 are stored. A request that failed because a dependency was unavailable
/// must stay retryable, so the reservation is released rather than completed, and the very next
/// attempt with the same key proceeds normally.
/// </para>
/// </remarks>
internal sealed class IdempotencyFilter : IEndpointFilter
{
    /// <summary>The header this filter reads.</summary>
    internal const string HeaderName = "Idempotency-Key";

    /// <summary>Longest key accepted. Long enough for a UUID or a hash, short enough to index.</summary>
    private const int MaxKeyLength = 255;

    /// <summary>Status below which a response is worth storing for replay.</summary>
    private const int ServerErrorFloor = 500;

    /// <inheritdoc />
    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        HttpContext http = context.HttpContext;

        if (!TryReadKey(http.Request, out string key, out IResult? malformed))
        {
            return malformed ?? await next(context);
        }

        IdempotencyStore store = http.RequestServices.GetRequiredService<IdempotencyStore>();
        string endpoint = DescribeEndpoint(http);
        byte[] requestHash = await ComputeRequestHashAsync(http, endpoint);

        IdempotencyLookup lookup =
            await store.BeginAsync(key, endpoint, requestHash, http.RequestAborted);

        switch (lookup.Outcome)
        {
            case IdempotencyOutcome.Replay:
                return new StoredResponseResult(lookup.ResponseStatus, lookup.ResponseBody);

            case IdempotencyOutcome.Conflict:
                return DleProblem.Conflict(
                    ProblemCodes.IdempotencyConflict,
                    "The idempotency key has already been used for a different request.",
                    "This Idempotency-Key was first seen with a different body or on a different "
                    + "route. Reusing one key for two requests is a client mistake, so the stored "
                    + "response is deliberately not replayed. Use a fresh key.");

            case IdempotencyOutcome.InProgress:
                return DleProblem.Conflict(
                    ProblemCodes.IdempotencyConflict,
                    "The first attempt with this idempotency key has not finished.",
                    "An earlier request carrying this Idempotency-Key is still being handled. "
                    + "Retry shortly; the stored response will be replayed once it completes.");

            case IdempotencyOutcome.Proceed:
            default:
                break;
        }

        return await RunAndStoreAsync(context, next, store, key, endpoint, http);
    }

    /// <summary>Runs the handler, captures its response and stores it for replay.</summary>
    /// <param name="context">The filter invocation context.</param>
    /// <param name="next">The rest of the pipeline.</param>
    /// <param name="store">The idempotency store holding the reservation.</param>
    /// <param name="key">The presented key.</param>
    /// <param name="endpoint">The route the key was reserved against.</param>
    /// <param name="http">The request.</param>
    /// <returns>A result that has already been written.</returns>
    /// <remarks>
    /// The handler's result is executed here rather than returned, because a result that the
    /// framework executes after the filter chain has already left this method cannot be captured.
    /// Execution writes into a buffer instead of the response stream, so nothing reaches the client
    /// until the same bytes have been stored — a client that receives a response is therefore always
    /// a client whose retry will be answered from the store.
    /// </remarks>
    private static async ValueTask<object?> RunAndStoreAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next,
        IdempotencyStore store,
        string key,
        string endpoint,
        HttpContext http)
    {
        object? produced;

        try
        {
            produced = await next(context);
        }
        catch (OperationCanceledException)
        {
            await store.AbandonAsync(key, endpoint, CancellationToken.None);
            throw;
        }
        catch (Exception)
        {
            // The reservation is released before the exception is allowed to propagate, so a
            // failure never leaves a key permanently poisoned. The exception itself is not
            // swallowed; the host's exception handler still turns it into a problem document
            // (SHARED-KERNEL §17.9).
            await store.AbandonAsync(key, endpoint, CancellationToken.None);
            throw;
        }

        if (produced is not IResult result)
        {
            // A handler that did not produce a result cannot have its response replayed. The
            // reservation is released so the caller may retry rather than being locked out.
            await store.AbandonAsync(key, endpoint, http.RequestAborted);
            return produced;
        }

        Stream original = http.Response.Body;
        byte[] captured;

        using (MemoryStream buffer = new())
        {
            http.Response.Body = buffer;

            try
            {
                await result.ExecuteAsync(http);
            }
            catch (Exception)
            {
                http.Response.Body = original;
                await store.AbandonAsync(key, endpoint, CancellationToken.None);
                throw;
            }

            http.Response.Body = original;
            captured = buffer.ToArray();
        }

        int status = http.Response.StatusCode;

        if (status < ServerErrorFloor)
        {
            await store.CompleteAsync(
                key,
                endpoint,
                status,
                captured.Length == 0 ? null : Encoding.UTF8.GetString(captured),
                http.RequestAborted);
        }
        else
        {
            await store.AbandonAsync(key, endpoint, http.RequestAborted);
        }

        if (captured.Length > 0)
        {
            // The body feature has been restored by now, so this writes through the ordinary
            // response pipeline rather than around it.
            await http.Response.Body.WriteAsync(captured, http.RequestAborted);
        }

        return Results.Empty;
    }

    /// <summary>Reads and validates the header.</summary>
    /// <param name="request">The request.</param>
    /// <param name="key">The key, when one was presented.</param>
    /// <param name="malformed">A problem document when the header is present but unusable.</param>
    /// <returns><see langword="true"/> when a usable key was presented.</returns>
    private static bool TryReadKey(HttpRequest request, out string key, out IResult? malformed)
    {
        key = string.Empty;
        malformed = null;

        if (!request.Headers.TryGetValue(HeaderName, out Microsoft.Extensions.Primitives.StringValues values))
        {
            return false;
        }

        string candidate = values.ToString().Trim();

        if (candidate.Length == 0)
        {
            return false;
        }

        if (candidate.Length > MaxKeyLength)
        {
            malformed = DleProblem.Validation(
                HeaderName,
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"An idempotency key may be at most {MaxKeyLength} characters."));

            return false;
        }

        key = candidate;
        return true;
    }

    /// <summary>Names the route a key is reserved against.</summary>
    /// <param name="http">The request.</param>
    /// <returns>The method and the route pattern, for example <c>POST /api/v1/links</c>.</returns>
    /// <remarks>
    /// The route pattern rather than the concrete path, so that <c>PATCH /links/{id}</c> is one
    /// endpoint for reservation purposes while the identifier still enters the hash through the
    /// request line. Two different links patched under one key therefore conflict, which is the
    /// intended answer.
    /// </remarks>
    private static string DescribeEndpoint(HttpContext http)
    {
        string pattern = http.GetEndpoint() is RouteEndpoint routeEndpoint
            ? routeEndpoint.RoutePattern.RawText ?? http.Request.Path.Value ?? "/"
            : http.Request.Path.Value ?? "/";

        return string.Create(CultureInfo.InvariantCulture, $"{http.Request.Method} {pattern}");
    }

    /// <summary>Hashes the request line and the body.</summary>
    /// <param name="http">The request.</param>
    /// <param name="endpoint">The route description.</param>
    /// <returns>A SHA-256 digest of everything that identifies this request.</returns>
    /// <remarks>
    /// The body has already been bound to the handler's parameters by the time a filter runs, so it
    /// is read from the buffered copy the request-buffering middleware left behind. When there is no
    /// seekable body — a request without one — the digest covers the request line alone, which is
    /// exactly right for a <c>DELETE</c>.
    /// </remarks>
    private static async Task<byte[]> ComputeRequestHashAsync(HttpContext http, string endpoint)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        hash.AppendData(Encoding.UTF8.GetBytes(endpoint));
        hash.AppendData(Encoding.UTF8.GetBytes(http.Request.Path.Value ?? string.Empty));
        hash.AppendData(Encoding.UTF8.GetBytes(http.Request.QueryString.Value ?? string.Empty));

        if (http.Request.Body.CanSeek)
        {
            http.Request.Body.Position = 0;

            byte[] rented = new byte[8192];
            int read;

            while ((read = await http.Request.Body.ReadAsync(rented, http.RequestAborted)) > 0)
            {
                hash.AppendData(rented.AsSpan(0, read));
            }

            http.Request.Body.Position = 0;
        }

        return hash.GetHashAndReset();
    }

    /// <summary>Replays a stored response byte for byte.</summary>
    private sealed class StoredResponseResult : IResult
    {
        private readonly int _status;
        private readonly string? _body;

        internal StoredResponseResult(int status, string? body)
        {
            _status = status;
            _body = body;
        }

        /// <inheritdoc />
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);

            httpContext.Response.StatusCode = _status;
            httpContext.Response.Headers["Idempotency-Replayed"] = "true";

            if (string.IsNullOrEmpty(_body))
            {
                return;
            }

            httpContext.Response.ContentType = "application/json; charset=utf-8";
            await httpContext.Response.WriteAsync(_body, Encoding.UTF8, httpContext.RequestAborted);
        }
    }
}

/// <summary>
/// Attaches <see cref="IdempotencyFilter"/> to an endpoint and documents the header.
/// </summary>
internal static class IdempotencyEndpointExtensions
{
    /// <summary>Makes a write endpoint replayable through <c>Idempotency-Key</c>.</summary>
    /// <param name="builder">The endpoint being built.</param>
    /// <returns>The same builder, for chaining.</returns>
    internal static RouteHandlerBuilder WithIdempotency(this RouteHandlerBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddEndpointFilter<IdempotencyFilter>();

        return builder;
    }
}

/// <summary>
/// Leaves the request body readable a second time, so that <see cref="IdempotencyFilter"/> can hash
/// what the handler was given.
/// </summary>
/// <remarks>
/// Minimal APIs bind the body before endpoint filters run, which consumes a non-seekable stream.
/// Buffering has to be switched on before that happens, which means before routing, and it is
/// deliberately limited to the requests that can carry an idempotency key: a write, on an API path,
/// with a body small enough that keeping it in memory is free.
/// </remarks>
internal sealed class IdempotencyBufferingMiddleware
{
    /// <summary>Bodies above this size spill to a temporary file rather than to memory.</summary>
    private const int MemoryThresholdBytes = 64 * 1024;

    private readonly RequestDelegate _next;

    /// <summary>Creates the middleware.</summary>
    /// <param name="next">The next component in the pipeline.</param>
    public IdempotencyBufferingMiddleware(RequestDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);
        _next = next;
    }

    /// <summary>Enables buffering for a request that may carry an idempotency key.</summary>
    /// <param name="context">The request.</param>
    /// <returns>A task that completes when the request has been handled.</returns>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (ShouldBuffer(context.Request))
        {
            context.Request.EnableBuffering(MemoryThresholdBytes, bufferLimit: long.MaxValue);
        }

        await _next(context);
    }

    /// <summary>Whether this request's body has to stay readable.</summary>
    /// <param name="request">The request.</param>
    /// <returns><see langword="true"/> when buffering is needed.</returns>
    /// <remarks>
    /// The streamed bulk import is excluded by name. Its whole point is that ten thousand rows never
    /// have to exist at once, and buffering the request to hash it would undo that; the endpoint
    /// reserves its key without a body hash instead, and says so in its own documentation (FR-103).
    /// </remarks>
    private static bool ShouldBuffer(HttpRequest request)
    {
        if (!request.Headers.ContainsKey(IdempotencyFilter.HeaderName))
        {
            return false;
        }

        if (request.ContentLength is null or 0)
        {
            return false;
        }

        return !request.Path.StartsWithSegments("/api/v1/links/bulk", StringComparison.OrdinalIgnoreCase);
    }
}
