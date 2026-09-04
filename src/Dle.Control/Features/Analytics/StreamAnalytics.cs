using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;

using Dle.Analytics.Postgres;
using Dle.Domain.Analytics;
using Dle.Domain.Ports;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Dle.Control.Features.Analytics;

/// <summary>
/// <c>GET /api/v1/analytics/stream</c> — a live view of the current bucket over Server-Sent Events
/// (FR-206).
/// </summary>
/// <remarks>
/// <para>
/// A "could have" requirement implemented honestly rather than impressively. The click stream is
/// written by the <em>edge</em> process, not by the control plane, so there is no in-process event
/// the control plane could forward; a genuinely push-based stream would need the edge to publish to
/// a bus and would put a second piece of infrastructure into a design whose whole premise is that a
/// self-hoster needs only PostgreSQL (§B.3, ADR-006). What this endpoint does instead is poll the
/// analytics store on an interval the client chooses within bounds, and emit a snapshot whenever it
/// changes. For a dashboard tile that is indistinguishable from a push; for anything that needs
/// millisecond latency it is the wrong tool, and it says so here rather than in a support ticket.
/// </para>
/// <para>
/// <b>The seam, if push is wanted later.</b> Replace <see cref="PollAsync"/> with a subscription:
/// the edge already writes through <c>IClickEventSink</c>, so a deployment that adds a bus
/// publishes there and this method consumes from it. Nothing else in this file, and nothing in the
/// endpoint registration, has to change — the shape is already an
/// <see cref="IAsyncEnumerable{T}"/> of snapshots.
/// </para>
/// <para>
/// Payloads are pre-serialized through the module's source generated context and emitted as
/// strings, so no reflection based serializer touches a connection that stays open for hours
/// (SHARED-KERNEL §17.3).
/// </para>
/// </remarks>
public static class StreamAnalytics
{
    /// <summary>Shortest polling interval a client may ask for, in seconds.</summary>
    public const int MinIntervalSeconds = 2;

    /// <summary>Longest polling interval a client may ask for, in seconds.</summary>
    public const int MaxIntervalSeconds = 60;

    /// <summary>Default polling interval, in seconds.</summary>
    public const int DefaultIntervalSeconds = 10;

    /// <summary>Event name of a snapshot of the current bucket.</summary>
    public const string SnapshotEvent = "snapshot";

    /// <summary>Event name of a keep-alive sent when nothing changed.</summary>
    public const string HeartbeatEvent = "heartbeat";

    /// <summary>
    /// Opens the stream.
    /// </summary>
    /// <param name="intervalSeconds">Polling interval, clamped to
    /// [<see cref="MinIntervalSeconds"/>, <see cref="MaxIntervalSeconds"/>].</param>
    /// <param name="context">The request.</param>
    /// <param name="store">The analytics store.</param>
    /// <param name="options">Analytics options.</param>
    /// <param name="timeProvider">Clock.</param>
    /// <returns>The event stream, or a problem document when the query is not valid.</returns>
    /// <exception cref="ArgumentNullException">An argument is <see langword="null"/>.</exception>
    public static IResult Handle(
        int? intervalSeconds,
        HttpContext context,
        IClickAnalyticsStore store,
        IOptionsMonitor<AnalyticsOptions> options,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);

        AnalyticsQueryBinding binding = AnalyticsQueryBinder.Bind(
            context,
            timeProvider,
            options.CurrentValue.MaxBreakdownRows);

        if (binding.Query is not { } query)
        {
            return binding.Problem!;
        }

        TimeSpan interval = TimeSpan.FromSeconds(
            Math.Clamp(intervalSeconds ?? DefaultIntervalSeconds, MinIntervalSeconds, MaxIntervalSeconds));

        return TypedResults.ServerSentEvents(
            PollAsync(query, store, timeProvider, interval, context.RequestAborted));
    }

    /// <summary>
    /// Polls the store and yields a snapshot whenever the numbers move.
    /// </summary>
    /// <param name="query">The bound, tenant scoped query.</param>
    /// <param name="store">The analytics store.</param>
    /// <param name="timeProvider">Clock, also driving the timer so a test can advance it.</param>
    /// <param name="interval">Polling interval.</param>
    /// <param name="cancellationToken">Cancelled when the client disconnects.</param>
    /// <returns>The stream of events.</returns>
    /// <remarks>
    /// A heartbeat is emitted when nothing changed, for the unglamorous reason that a proxy will
    /// close an idle connection and a dashboard will then show stale numbers with no error.
    /// </remarks>
    private static async IAsyncEnumerable<SseItem<string>> PollAsync(
        AnalyticsQuery query,
        IClickAnalyticsStore store,
        TimeProvider timeProvider,
        TimeSpan interval,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        string? previous = null;

        using PeriodicTimer timer = new(interval, timeProvider);

        while (!cancellationToken.IsCancellationRequested)
        {
            FunnelSummary? summary = null;

            try
            {
                summary = await store.GetFunnelAsync(query, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // The client went away. Ending the enumeration is the whole of the cleanup.
                yield break;
            }

            string payload = JsonSerializer.Serialize(summary, AnalyticsJsonContext.Default.FunnelSummary);

            if (!string.Equals(payload, previous, StringComparison.Ordinal))
            {
                previous = payload;
                yield return new SseItem<string>(payload, SnapshotEvent);
            }
            else
            {
                // Ordinal equality on the rendered bytes rather than a field-by-field comparison:
                // the payload is what the client receives, so it is the right thing to compare.
                yield return new SseItem<string>(string.Empty, HeartbeatEvent);
            }

            bool ticked;

            try
            {
                ticked = await timer.WaitForNextTickAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                yield break;
            }

            if (!ticked)
            {
                yield break;
            }
        }
    }
}
