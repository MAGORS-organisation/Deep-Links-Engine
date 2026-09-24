using System.Globalization;
using System.Text.Json;

using Dle.Control.Features.Shared;
using Dle.Domain.Contracts;
using Dle.Domain.Entities;
using Dle.Domain.Serialization;
using Dle.Persistence.Repositories;
using Dle.Persistence.Tenancy;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Dle.Control.Features.Abuse;

/// <summary>
/// <c>POST /api/v1/abuse-reports</c> — the public notice and action form (FR-245, §E.3 step 7).
/// </summary>
/// <remarks>
/// <para>
/// Anonymous and rate limited. The endpoint exists for hygiene and for the Digital Services Act:
/// article 16 requires a hosting service to offer a mechanism for notifying it of illegal content
/// and to handle those notices in a traceable way. A link shortener carrying user supplied targets
/// is very likely such a service, so the submission instant, the decision, the decision instant and
/// the reason are all recorded — that record is the compliance artefact, not the form itself.
/// </para>
/// <para>
/// The answer never reveals whether the reported link exists. A form that answers "unknown link"
/// is an oracle: it tells anyone with a list of candidate slugs which ones are live, which is slug
/// enumeration (T-07) with the rate limit of a public form rather than of the resolve path.
/// </para>
/// </remarks>
public static class SubmitAbuseReport
{
    /// <summary>Log category of this use case.</summary>
    private const string LogCategory = "Dle.Control.Features.Abuse.SubmitAbuseReport";

    /// <summary>Handles a submission.</summary>
    /// <param name="request">The report.</param>
    /// <param name="locator">Resolves the reported URL to a link.</param>
    /// <param name="reports">Report storage.</param>
    /// <param name="audit">Audit trail.</param>
    /// <param name="tenantContext">Tenant scope, entered for the reported link's tenant.</param>
    /// <param name="hasher">Turns a reporter address into the stored hash.</param>
    /// <param name="metrics">Instruments.</param>
    /// <param name="options">Abuse options.</param>
    /// <param name="timeProvider">Clock.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>202 with an acknowledgement, or 400 when the submission is unusable.</returns>
    public static async Task<IResult> HandleAsync(
        CreateAbuseReportRequest request,
        AbuseLinkLocator locator,
        AbuseRepository reports,
        AuditLogWriter audit,
        ITenantContext tenantContext,
        ReporterEmailHasher hasher,
        AbuseMetrics metrics,
        IOptionsMonitor<AbuseOptions> options,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(locator);
        ArgumentNullException.ThrowIfNull(reports);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(tenantContext);
        ArgumentNullException.ThrowIfNull(hasher);
        ArgumentNullException.ThrowIfNull(metrics);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        ILogger logger = loggerFactory.CreateLogger(LogCategory);

        AbuseOptions current = options.CurrentValue;
        Dictionary<string, string[]> errors = new(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(request.Url))
        {
            errors["url"] = ["The reported URL is required."];
        }

        if (!AbuseReasonNames.TryNormalizeReason(request.Reason, out string reason))
        {
            errors["reason"] =
            [
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The reason must be one of: {string.Join(", ", AbuseReasonNames.Reasons)}."),
            ];
        }

        if (request.Details is { Length: > 0 } details && details.Length > current.MaxDetailsLength)
        {
            errors["details"] =
            [
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"The detail text must not exceed {current.MaxDetailsLength} characters."),
            ];
        }

        if (errors.Count > 0)
        {
            // Refusing a malformed submission is not an oracle: nothing here depends on whether the
            // link exists, only on the shape of what was sent.
            return DleProblemResults.ValidationFailed(
                "The abuse report could not be accepted as submitted.",
                errors);
        }

        DateTimeOffset now = timeProvider.GetUtcNow();
        LocatedLink? located = await locator.FindAsync(request.Url, cancellationToken);

        if (located is null)
        {
            metrics.ReportAccepted(reason, matched: false);

            // Logged at information without the reported URL: the submission is attacker controlled
            // text, and a full URL in an operational log is a query string in an operational log
            // (SHARED-KERNEL §17.5).
            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.LogInformation(
                    "An abuse report was submitted for a URL that is not a link of this instance. Reason: {Reason}.",
                    reason);
            }

            return Accepted(new AbuseReportResponse
            {
                Id = Guid.CreateVersion7(now),
                Status = AbuseReasonNames.StatusNew,
                CreatedAt = now,
            });
        }

        byte[]? reporterHash = hasher.Hash(request.ReporterEmail);

        AbuseReport? stored = await reports.CreateReportAsync(
            located.LinkId,
            reason,
            request.Details,
            reporterHash,
            cancellationToken);

        if (stored is null)
        {
            // The link was removed between the lookup and the write. The reporter still gets the
            // same acknowledgement, for the same reason as above.
            metrics.ReportAccepted(reason, matched: false);

            return Accepted(new AbuseReportResponse
            {
                Id = Guid.CreateVersion7(now),
                Status = AbuseReasonNames.StatusNew,
                CreatedAt = now,
            });
        }

        using (tenantContext.BeginScope(located.TenantId))
        {
            Dictionary<string, string> metadata = new(StringComparer.Ordinal)
            {
                ["reason"] = reason,
                ["link_id"] = stored.LinkId.ToString(CultureInfo.InvariantCulture),
                ["has_reporter_contact"] = reporterHash is null ? "false" : "true",
            };

            await audit.WriteAsync(
                "abuse_report.received",
                "abuse_report",
                stored.Id.ToString(),
                "system",
                actorId: null,
                JsonSerializer.Serialize(metadata, DleDomainJsonContext.Default.DictionaryStringString),
                cancellationToken);
        }

        metrics.ReportAccepted(reason, matched: true);

        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation(
                "Abuse report {ReportId} accepted for link {LinkId}. Reason: {Reason}.",
                stored.Id,
                stored.LinkId,
                reason);
        }

        return Accepted(new AbuseReportResponse
        {
            Id = stored.Id,
            Status = stored.Status,
            CreatedAt = stored.CreatedAt,
        });
    }

    /// <summary>Builds the acknowledgement, identical for a matched and an unmatched report.</summary>
    /// <param name="response">The acknowledgement body.</param>
    /// <returns>202 Accepted.</returns>
    /// <remarks>
    /// 202 rather than 201: the report has been received, and whether anything is created — or
    /// acted upon — is the operator's decision, not the reporter's.
    /// </remarks>
    private static Accepted<AbuseReportResponse> Accepted(AbuseReportResponse response) =>
        TypedResults.Accepted((string?)null, response);
}
