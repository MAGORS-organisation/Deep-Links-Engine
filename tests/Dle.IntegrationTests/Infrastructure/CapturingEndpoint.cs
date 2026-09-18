using System.Collections.ObjectModel;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Dle.IntegrationTests.Infrastructure;

/// <summary>
/// An HTTP endpoint the test owns, for the cases where what matters is the request the product
/// sent rather than the answer it got back.
/// </summary>
/// <remarks>
/// <para>
/// A webhook delivery is only observable from outside: the signature, the algorithm header and the
/// body reach the subscriber and nothing else. Asserting them against a public address would be
/// asserting a third party's behaviour, so the suite listens on loopback and reads what actually
/// arrived. The webhook destination policy refuses loopback by design (T-02); the host under test
/// sets <c>Dle:Webhooks:AllowPrivateDestinations</c> for that reason and for no other.
/// </para>
/// </remarks>
public sealed class CapturingEndpoint : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly TaskCompletionSource<CapturedRequest> _captured;

    private CapturingEndpoint(WebApplication app, TaskCompletionSource<CapturedRequest> captured, string url)
    {
        _app = app;
        _captured = captured;
        Url = url;
    }

    /// <summary>The address a subscription points at, including a path.</summary>
    public string Url { get; }

    /// <summary>
    /// Starts the endpoint on a loopback port the operating system picks.
    /// </summary>
    /// <param name="statusCode">The status every request is answered with.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The endpoint, already listening.</returns>
    public static async Task<CapturingEndpoint> StartAsync(int statusCode, CancellationToken cancellationToken)
    {
        TaskCompletionSource<CapturedRequest> captured =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        WebApplication app = builder.Build();

        app.MapPost(
            "/{**path}",
            async (HttpContext http) =>
            {
                using StreamReader reader = new(http.Request.Body);
                string body = await reader.ReadToEndAsync(http.RequestAborted);

                Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);

                foreach (KeyValuePair<string, Microsoft.Extensions.Primitives.StringValues> header in http.Request.Headers)
                {
                    headers[header.Key] = header.Value.ToString();
                }

                _ = captured.TrySetResult(new CapturedRequest(body, new ReadOnlyDictionary<string, string>(headers)));

                return Results.StatusCode(statusCode);
            });

        await app.StartAsync(cancellationToken);

        string root = app.Urls.First();

        return new CapturingEndpoint(app, captured, root.TrimEnd('/') + "/hook");
    }

    /// <summary>
    /// Waits for the first request to arrive.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>What arrived.</returns>
    /// <exception cref="TimeoutException">Nothing arrived within thirty seconds.</exception>
    public async Task<CapturedRequest> WaitAsync(CancellationToken cancellationToken)
    {
        Task completed = await Task.WhenAny(
            _captured.Task,
            Task.Delay(TimeSpan.FromSeconds(30), cancellationToken));

        if (completed != _captured.Task)
        {
            throw new TimeoutException("No request reached the endpoint the test is listening on.");
        }

        return await _captured.Task;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

/// <summary>One request as it arrived.</summary>
/// <param name="Body">The body, read as UTF-8 text.</param>
/// <param name="Headers">The request headers, matched without regard to case.</param>
public sealed record CapturedRequest(string Body, IReadOnlyDictionary<string, string> Headers);
