using Microsoft.AspNetCore.Diagnostics;
using Npgsql;

namespace Dle.Control.Infrastructure;

/// <summary>
/// Turns a PostgreSQL that cannot be reached into a <c>503</c> problem document rather than the
/// <c>500</c> the default exception handler would produce (§D.6: "control plane vracia 503;
/// žiadny 500").
/// </summary>
/// <remarks>
/// <para>
/// The distinction matters to the caller. A 500 says "the request is broken, do not repeat it"; a
/// 503 with <c>Retry-After</c> says "the request is fine, the service is not, try again shortly",
/// which is exactly what a client or an orchestrator should do while the database is being failed
/// over. The two are told apart by <em>what</em> failed: an exception that carries a server answer
/// is a genuine error, an exception that never got one is an outage.
/// </para>
/// <para>
/// The document itself follows SHARED-KERNEL §17.5: a problem type, a title and a sentence of
/// detail, and nothing that names a host, a driver or a stack frame.
/// </para>
/// </remarks>
public sealed partial class DependencyUnavailableExceptionHandler : IExceptionHandler
{
    /// <summary>How long a client should wait before retrying, in seconds.</summary>
    private const string RetryAfterSeconds = "5";

    private readonly ILogger<DependencyUnavailableExceptionHandler> _logger;

    /// <summary>Creates the handler.</summary>
    /// <param name="logger">The logger.</param>
    /// <exception cref="ArgumentNullException"><paramref name="logger"/> is <see langword="null"/>.</exception>
    public DependencyUnavailableExceptionHandler(ILogger<DependencyUnavailableExceptionHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;
    }

    /// <inheritdoc />
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        ArgumentNullException.ThrowIfNull(exception);

        if (!IsDependencyUnavailable(exception))
        {
            return false;
        }

        LogDependencyUnavailable(_logger, httpContext.Request.Path.Value ?? "/", exception);

        httpContext.Response.Headers.RetryAfter = RetryAfterSeconds;

        await DleProblem.DependencyUnavailable(
                "The control-plane database did not answer. The request was not completed; retry it shortly.")
            .ExecuteAsync(httpContext);

        return true;
    }

    /// <summary>
    /// Whether the exception is a lost dependency rather than a genuine error: a connection that
    /// could not be opened or that broke, a pool that timed out, or a server that answered with a
    /// connection or shutdown condition instead of a result.
    /// </summary>
    /// <param name="exception">The exception, at any depth of wrapping.</param>
    /// <returns><see langword="true"/> for an outage; <see langword="false"/> for an error.</returns>
    internal static bool IsDependencyUnavailable(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case PostgresException postgres:
                    // The server spoke, so this is a real answer. Only the SQLSTATE classes that
                    // describe the connection itself or an administrative shutdown count as an
                    // outage; a constraint violation or a bad query is the request's own fault.
                    return IsConnectivitySqlState(postgres.SqlState);

                case NpgsqlException:
                    // No server answer at all: refused connection, broken stream, pool exhausted.
                    return true;

                case AggregateException aggregate:
                    foreach (Exception inner in aggregate.InnerExceptions)
                    {
                        if (IsDependencyUnavailable(inner))
                        {
                            return true;
                        }
                    }

                    return false;

                default:
                    break;
            }
        }

        return false;
    }

    private static bool IsConnectivitySqlState(string sqlState) =>
        sqlState.StartsWith("08", StringComparison.Ordinal)  // connection exception
        || sqlState is "57P01" or "57P02" or "57P03"          // admin shutdown, crash shutdown, cannot connect now
        || sqlState is "53300";                               // too many connections

    [LoggerMessage(
        EventId = 5901,
        Level = LogLevel.Warning,
        Message = "The control-plane database was unavailable while serving {Path}; answered 503 (§D.6).")]
    private static partial void LogDependencyUnavailable(ILogger logger, string path, Exception exception);
}
