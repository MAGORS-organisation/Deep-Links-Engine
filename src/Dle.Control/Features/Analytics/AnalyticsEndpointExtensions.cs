using Dle.Analytics.Postgres;
using Dle.Control.Features.Analytics;
using Dle.Control.Identity;
using Dle.Domain.Analytics;
using Dle.Domain.Contracts;
using Dle.Domain.Ports;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// The reporting endpoints (SHARED-KERNEL §15, §B.7.3, FR-202, FR-203, FR-206).
/// </summary>
/// <remarks>
/// <para>
/// Every route here is behind <see cref="DlePolicies.AnalyticsRead"/>, and every query is scoped to
/// the caller's own tenant by <see cref="AnalyticsQueryBinder"/> — there is no <c>tenant_id</c>
/// parameter anywhere in this surface and there will not be one.
/// </para>
/// <para>
/// The route worth pointing at is <c>/attribution-quality</c>. It publishes the split between
/// deterministic matches, probabilistic ones and installs that were not matched at all, with the
/// mean confidence of the guesses. Commercial measurement partners report one attributed number and
/// leave the composition unstated; stating it is the argument this product is built on, and it is
/// an endpoint rather than a footnote (ADR-008, §0.2).
/// </para>
/// <para>
/// The tenant data export is mapped from here rather than from <c>MapTenants</c> because it is an
/// export and this is the export surface — SHARED-KERNEL §15 names no <c>MapExports</c>, and adding
/// one would be a change to a contract that other slices already code against.
/// </para>
/// </remarks>
public static class AnalyticsEndpointExtensions
{
    /// <summary>
    /// Maps the reporting routes under <c>/api/v1/analytics</c> and the tenant export under
    /// <c>/api/v1/exports</c>.
    /// </summary>
    /// <param name="app">The endpoint route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is <see langword="null"/>.</exception>
    public static IEndpointRouteBuilder MapAnalytics(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        RouteGroupBuilder analytics = app.MapGroup("/api/v1/analytics")
            .WithTags("Analytics")
            .RequireAuthorization(DlePolicies.AnalyticsRead);

        analytics.MapGet("/clicks", GetAnalytics.TimeSeriesAsync)
            .WithName("GetClickAnalytics")
            .WithSummary("Clicks, installs and conversions over time.")
            .WithDescription(
                "Bucketed by hour, day, week or month, filtered by link, campaign, country or "
                + "platform. Bot traffic is excluded unless include_bots=true is passed explicitly, "
                + "because a crawler's click is a real event that must not inflate a campaign "
                + "(FR-205, TC-106).")
            .Produces<TimeSeriesResponse>(StatusCodes.Status200OK, "application/json")
            .ProducesProblem(StatusCodes.Status400BadRequest);

        analytics.MapGet("/installs", GetAnalytics.TimeSeriesAsync)
            .WithName("GetInstallAnalytics")
            .WithSummary("The same series, from the install side.")
            .WithDescription(
                "Named separately because §B.7.3 names it, and answered by the same query: clicks, "
                + "installs and conversions come out of one pass over one window, and serving them "
                + "from three requests would triple the work to produce numbers that have to agree "
                + "anyway.")
            .Produces<TimeSeriesResponse>(StatusCodes.Status200OK, "application/json")
            .ProducesProblem(StatusCodes.Status400BadRequest);

        analytics.MapGet("/breakdown", static (
                string? dimension,
                HttpContext context,
                IClickAnalyticsStore store,
                IOptionsMonitor<AnalyticsOptions> options,
                TimeProvider timeProvider,
                CancellationToken cancellationToken) =>
                GetAnalytics.BreakdownAsync(dimension, context, store, options, timeProvider, cancellationToken))
            .WithName("GetAnalyticsBreakdown")
            .WithSummary("One dimension of the window, grouped and ranked.")
            .WithDescription(
                "The dimension is resolved against a fixed allowlist before any query is built, so "
                + "an unknown name is a validation error rather than a failed query.")
            .Produces<BreakdownResponse>(StatusCodes.Status200OK, "application/json")
            .ProducesProblem(StatusCodes.Status400BadRequest);

        analytics.MapGet("/funnels", GetAnalytics.FunnelAsync)
            .WithName("GetAnalyticsFunnel")
            .WithSummary("Clicks to installs to attributed installs to conversions.")
            .WithDescription("The totals for the window, and the conversion rate they imply.")
            .Produces<FunnelSummary>(StatusCodes.Status200OK, "application/json")
            .ProducesProblem(StatusCodes.Status400BadRequest);

        analytics.MapGet("/attribution-quality", GetAnalytics.AttributionQualityAsync)
            .WithName("GetAttributionQuality")
            .WithSummary("The deterministic, probabilistic and unmatched split.")
            .WithDescription(
                "How many installs were matched with certainty, how many were guessed and at what "
                + "mean confidence, and how many were not matched at all. The unmatched share is "
                + "published rather than hidden: a measurement product that only shows what it "
                + "caught cannot be checked, and this number is the argument against the commercial "
                + "measurement partners rather than a footnote (ADR-008, §0.2).")
            .Produces<AttributionQualityResponse>(StatusCodes.Status200OK, "application/json")
            .ProducesProblem(StatusCodes.Status400BadRequest);

        analytics.MapGet("/export", static (
                string? report,
                string? format,
                string? dimension,
                HttpContext context,
                IClickAnalyticsStore store,
                IEnumerable<IAnalyticsExporter> exporters,
                IOptionsMonitor<AnalyticsOptions> options,
                TimeProvider timeProvider,
                CancellationToken cancellationToken) =>
                ExportAnalytics.HandleAsync(
                    report,
                    format,
                    dimension,
                    context,
                    store,
                    exporters,
                    options,
                    timeProvider,
                    cancellationToken))
            .WithName("ExportAnalytics")
            .WithSummary("A report as a CSV or Parquet download.")
            .WithDescription(
                "Streams the report rather than buffering it, so a two year breakdown is a download "
                + "and not an outage (FR-203).")
            .Produces(StatusCodes.Status200OK, contentType: "text/csv")
            .ProducesProblem(StatusCodes.Status400BadRequest);

        analytics.MapGet("/stream", static (
                int? intervalSeconds,
                HttpContext context,
                IClickAnalyticsStore store,
                IOptionsMonitor<AnalyticsOptions> options,
                TimeProvider timeProvider) =>
                StreamAnalytics.Handle(intervalSeconds, context, store, options, timeProvider))
            .WithName("StreamAnalytics")
            .WithSummary("A live view of the window over Server-Sent Events.")
            .WithDescription(
                "Emits a snapshot whenever the totals move and a heartbeat when they do not. The "
                + "click stream is written by the edge process, so this polls the analytics store "
                + "rather than forwarding an in-process event; the shape is already a stream of "
                + "snapshots, so replacing the poll with a subscription later changes nothing a "
                + "client can see (FR-206).")
            .Produces(StatusCodes.Status200OK, contentType: "text/event-stream")
            .ProducesProblem(StatusCodes.Status400BadRequest);

        RouteGroupBuilder exports = app.MapGroup("/api/v1/exports")
            .WithTags("Analytics");

        exports.MapGet("/tenant", ExportTenantData.HandleAsync)
            .RequireAuthorization(DlePolicies.KeysWrite)
            .WithName("ExportTenantData")
            .WithSummary("Everything the engine holds for this tenant, as one archive.")
            .WithDescription(
                "GDPR article 20 portability and the DORA article 30 exit plan are the same feature, "
                + "and this is it: newline delimited JSON per table in a ZIP, streamed straight out "
                + "of the database. Credential material — key hashes, webhook secrets, account "
                + "hashes — is never included, and the manifest says so explicitly rather than "
                + "leaving the gap to be discovered (FR-249, §E.6.4).")
            .Produces(StatusCodes.Status200OK, contentType: "application/zip")
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }
}
