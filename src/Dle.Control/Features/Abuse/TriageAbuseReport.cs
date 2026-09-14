using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;

using Dle.Control.Features.Shared;
using Dle.Domain.Serialization;
using Dle.Persistence.Repositories;
using Dle.Persistence.Tenancy;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Dle.Control.Features.Abuse;

/// <summary>
/// <c>PATCH /api/v1/abuse-reports/{id}</c> — the operator's decision on a notice (§E.3 step 9).
/// </summary>
/// <remarks>
/// <para>
/// The decision, its reason and the instant it was taken are all written, because a notice and
/// action mechanism that cannot show what was decided and when is not a mechanism. The Digital
/// Services Act asks for a statement of reasons; the resolution note is where it lives, and it is
/// mandatory for every closing status including a rejection.
/// </para>
/// <para>
/// Closing a report is not the same act as withdrawing the link. They are separate endpoints on
/// purpose: a confirmed report about a link that has already been quarantined should not quarantine
/// it twice, and a rejected report must not touch it at all.
/// </para>
/// </remarks>
public static class TriageAbuseReport
{
    /// <summary>Log category of this use case.</summary>
    private const string LogCategory = "Dle.Control.Features.Abuse.TriageAbuseReport";

    /// <summary>Records a decision on one report.</summary>
    /// <param name="reportId">The report.</param>
    /// <param name="request">The decision.</param>
    /// <param name="user">The operator taking the decision.</param>
    /// <param name="reports">Report storage.</param>
    /// <param name="locator">Used to find the tenant the report belongs to, for the audit entry.</param>
    /// <param name="audit">Audit trail.</param>
    /// <param name="tenantContext">Tenant scope, entered for the reported link's tenant.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>204 when the decision was recorded, 404 when the report does not exist.</returns>
    public static async Task<IResult> HandleAsync(
        Guid reportId,
        TriageDecisionRequest request,
        ClaimsPrincipal user,
        AbuseRepository reports,
        AbuseLinkLocator locator,
        AuditLogWriter audit,
        ITenantContext tenantContext,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(reports);
        ArgumentNullException.ThrowIfNull(locator);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(tenantContext);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        ILogger logger = loggerFactory.CreateLogger(LogCategory);

        if (!AbuseReasonNames.TryNormalizeClosingStatus(request.Status, out string status))
        {
            return DleProblemResults.ValidationFailed(
                "The decision does not name a status an operator may set.",
                new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["status"] =
                    [
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"Allowed: {string.Join(", ", AbuseReasonNames.ClosingStatuses)}."),
                    ],
                });
        }

        if (string.IsNullOrWhiteSpace(request.ResolutionNote))
        {
            return DleProblemResults.ValidationFailed(
                "A decision has to carry a statement of reasons.",
                new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    ["resolution_note"] =
                        ["Say what was decided and why. This is the record the notice is answered from."],
                });
        }

        bool updated = await reports.ResolveReportAsync(
            reportId,
            status,
            request.ResolutionNote,
            cancellationToken);

        if (!updated)
        {
            return DleProblemResults.NotFound("There is no abuse report with that identifier.");
        }

        RequestActor actor = RequestActor.FromPrincipal(user);

        Guid? tenantId = await locator.FindReportTenantAsync(reportId, cancellationToken);

        if (tenantId is Guid owner)
        {
            using (tenantContext.BeginScope(owner))
            {
                Dictionary<string, string> metadata = new(StringComparer.Ordinal)
                {
                    ["status"] = status,
                    ["note"] = request.ResolutionNote,
                };

                await audit.WriteAsync(
                    "abuse_report.decided",
                    "abuse_report",
                    reportId.ToString("D", CultureInfo.InvariantCulture),
                    actor.ActorType,
                    actor.ActorId,
                    JsonSerializer.Serialize(metadata, DleDomainJsonContext.Default.DictionaryStringString),
                    cancellationToken);
            }
        }

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Abuse report {ReportId} was decided as {Status}.",
                reportId,
                status);
        }

        return TypedResults.NoContent();
    }
}

/// <summary>Body of <c>PATCH /api/v1/abuse-reports/{id}</c>.</summary>
public sealed record TriageDecisionRequest
{
    /// <summary>The status to move the report to: <c>triaged</c>, <c>confirmed</c>,
    /// <c>rejected</c> or <c>resolved</c>.</summary>
    [Required(AllowEmptyStrings = false)]
    public required string Status { get; init; }

    /// <summary>
    /// What was decided and why. Mandatory: this is the statement of reasons the notice is answered
    /// from, and it is what makes the decision auditable months later.
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    [StringLength(4000, MinimumLength = 1)]
    public required string ResolutionNote { get; init; }
}
