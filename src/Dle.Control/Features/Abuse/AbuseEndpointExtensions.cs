using Dle.Control.Configuration;
using Dle.Control.Features.Abuse;
using Dle.Control.Identity;

using Microsoft.AspNetCore.RateLimiting;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Maps the abuse handling routes (SHARED-KERNEL §15, FR-245, §E.3).
/// </summary>
/// <remarks>
/// <para>
/// The submission form is anonymous and rate limited. It exists for hygiene and for article 16 of
/// the Digital Services Act, which requires a hosting service to offer a mechanism for notifying it
/// of illegal content and to handle those notices traceably — a link shortener carrying
/// user-supplied targets is very likely such a service. The submission instant, the decision, the
/// decision instant and the reason are all recorded; that record is the compliance artefact, not the
/// form.
/// </para>
/// <para>
/// Triage and quarantine are instance-operator actions, not tenant ones: the party whose link is
/// being withdrawn is not the party who decides. They therefore carry the same policy as tenant
/// administration, which is the policy that recognises the instance operator.
/// </para>
/// </remarks>
public static class AbuseEndpointExtensions
{
    /// <summary>Maps the public abuse form and the operator's triage routes.</summary>
    /// <param name="app">The endpoint route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="app"/> is <see langword="null"/>.</exception>
    public static IEndpointRouteBuilder MapAbuse(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost("/abuse-reports", SubmitAbuseReport.HandleAsync)
            .AllowAnonymous()
            .RequireRateLimiting(AbuseServiceCollectionExtensions.ReportRateLimitPolicy)
            .WithName("SubmitAbuseReport")
            .WithTags("Abuse")
            .WithSummary("Reports a short URL as abusive. Public and rate limited.")
            .WithDescription(
                "The acknowledgement never reveals whether the reported link exists. A form that "
                + "answered \"unknown link\" would tell anyone with a list of candidate slugs which "
                + "ones are live, which is slug enumeration with the rate limit of a public form "
                + "rather than of the resolve path.")
            .Produces<AbuseReportResponse>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        RouteGroupBuilder tenant = app.MapGroup("/api/v1/abuse-reports").WithTags("Abuse");

        tenant.MapGet("/", ListAbuseReports.HandleTenantAsync)
            .RequireAuthorization(DlePolicies.LinksRead)
            .RequireRateLimiting(DleRateLimitPolicies.Read)
            .WithName("ListTenantAbuseReports")
            .WithSummary("Lists the reports filed against this tenant's links.")
            .Produces<PagedResponse<AbuseReportDetailResponse>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        RouteGroupBuilder triage = app.MapGroup("/api/v1/admin/abuse-reports").WithTags("Abuse triage");

        triage.MapGet("/", ListAbuseReports.HandleTriageAsync)
            .RequireAuthorization(DlePolicies.TenantsWrite)
            .RequireRateLimiting(DleRateLimitPolicies.Read)
            .WithName("ListAbuseTriageQueue")
            .WithSummary("The instance operator's triage queue across every tenant.")
            .WithDescription("Oldest first: the reaction target runs from submission, not from triage.")
            .Produces<PagedResponse<AbuseReportDetailResponse>>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden);

        triage.MapPost("/{reportId:guid}/decision", TriageAbuseReport.HandleAsync)
            .RequireAuthorization(DlePolicies.TenantsWrite)
            .RequireRateLimiting(DleRateLimitPolicies.Write)
            .WithName("TriageAbuseReport")
            .WithSummary("Records a decision on one report.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        RouteGroupBuilder quarantine = app.MapGroup("/api/v1/admin/links").WithTags("Abuse triage");

        quarantine.MapPost("/{linkId}/quarantine", QuarantineLink.QuarantineAsync)
            .RequireAuthorization(DlePolicies.TenantsWrite)
            .RequireRateLimiting(DleRateLimitPolicies.Write)
            .WithName("QuarantineLink")
            .WithSummary("Withdraws a link from service.")
            .WithDescription(
                "A quarantined link is not deleted. It keeps answering 410 with an explanation, "
                + "because quiet deletion destroys the forensic trail a later complaint has to be "
                + "answered from.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        // Release is a POST rather than a DELETE on the quarantine resource: the decision carries a
        // stated reason in its body, and a DELETE with a body is refused by proxies, by some client
        // libraries and by the minimal API binder itself.
        quarantine.MapPost("/{linkId}/quarantine/release", QuarantineLink.ReleaseAsync)
            .RequireAuthorization(DlePolicies.TenantsWrite)
            .RequireRateLimiting(DleRateLimitPolicies.Write)
            .WithName("ReleaseLinkFromQuarantine")
            .WithSummary("Returns a wrongly withdrawn link to service.")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status404NotFound);

        return app;
    }
}
